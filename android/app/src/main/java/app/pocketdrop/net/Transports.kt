package app.pocketdrop.net

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothClass
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.BufferedOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.net.SocketTimeoutException

/** A PC the phone can send to. */
sealed interface Target {
    val key: String
    val name: String

    data class Wifi(val id: String, override val name: String, val host: String, val port: Int) : Target {
        override val key get() = "wifi:$id"
    }

    data class Bluetooth(val address: String, override val name: String, val isComputer: Boolean) : Target {
        override val key get() = "bt:$address"
    }
}

private class SocketConnection(private val socket: Socket) : Connection {
    override val input: InputStream = socket.getInputStream().buffered(64 * 1024)
    override val output: OutputStream = BufferedOutputStream(socket.getOutputStream(), 256 * 1024)
    override fun close() = try { socket.close() } catch (_: Exception) {}
}

private class BluetoothConnection(private val socket: BluetoothSocket) : Connection {
    override val input: InputStream = socket.inputStream.buffered(16 * 1024)
    override val output: OutputStream = BufferedOutputStream(socket.outputStream, 64 * 1024)
    override fun close() = try { socket.close() } catch (_: Exception) {}
}

object Connector {
    private const val CONNECT_TIMEOUT_MS = 6000

    /** Long enough for the person at the PC to read the dialog (the PC gives up after 2 minutes). */
    private const val READ_TIMEOUT_MS = 150_000

    suspend fun open(context: Context, target: Target): Connection = withContext(Dispatchers.IO) {
        when (target) {
            is Target.Wifi -> {
                val socket = Socket()
                try {
                    socket.tcpNoDelay = true
                    socket.sendBufferSize = 1 shl 20
                    socket.connect(InetSocketAddress(target.host, target.port), CONNECT_TIMEOUT_MS)
                    socket.soTimeout = READ_TIMEOUT_MS
                    SocketConnection(socket)
                } catch (e: Exception) {
                    socket.close()
                    throw e
                }
            }
            is Target.Bluetooth -> openBluetooth(context, target)
        }
    }

    @SuppressLint("MissingPermission")
    private fun openBluetooth(context: Context, target: Target.Bluetooth): Connection {
        val adapter = Bluetooth.adapter(context) ?: throw IllegalStateException("No Bluetooth")
        val device = adapter.getRemoteDevice(target.address)
        if (Bluetooth.canScan(context)) adapter.cancelDiscovery()
        val socket = device.createRfcommSocketToServiceRecord(PocketDrop.BLUETOOTH_UUID)
        try {
            socket.connect()
        } catch (e: Exception) {
            try { socket.close() } catch (_: Exception) {}
            throw e
        }
        return BluetoothConnection(socket)
    }
}

/** Finds PCs on the local network with a UDP broadcast. */
object Discovery {
    suspend fun scan(timeoutMs: Long = 2000): List<Target.Wifi> = withContext(Dispatchers.IO) {
        val found = LinkedHashMap<String, Target.Wifi>()
        val request = PocketDrop.DISCOVERY_REQUEST.toByteArray(Charsets.US_ASCII)
        val targets = broadcastAddresses()

        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = 200
            val deadline = System.currentTimeMillis() + timeoutMs
            var nextSend = 0L
            val buf = ByteArray(2048)
            while (System.currentTimeMillis() < deadline) {
                if (System.currentTimeMillis() >= nextSend) {
                    for (addr in targets) {
                        try {
                            socket.send(DatagramPacket(request, request.size, addr, PocketDrop.DISCOVERY_PORT))
                        } catch (_: Exception) {
                        }
                    }
                    nextSend = System.currentTimeMillis() + 600
                }
                val packet = DatagramPacket(buf, buf.size)
                try {
                    socket.receive(packet)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                try {
                    val json = JSONObject(String(packet.data, packet.offset, packet.length, Charsets.UTF_8))
                    if (json.optString("app") != PocketDrop.APP) continue
                    val id = json.optString("id", packet.address.hostAddress ?: continue)
                    found[id] = Target.Wifi(
                        id = id,
                        name = json.optString("name", packet.address.hostAddress ?: "PC"),
                        host = packet.address.hostAddress ?: continue,
                        port = json.optInt("tcpPort", PocketDrop.TCP_PORT),
                    )
                } catch (_: Exception) {
                }
            }
        }
        found.values.sortedBy { it.name.lowercase() }
    }

    /** 255.255.255.255 plus the broadcast address of every IPv4 interface (Wi-Fi, hotspot, Ethernet). */
    private fun broadcastAddresses(): List<InetAddress> {
        val result = mutableListOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        try {
            for (nic in NetworkInterface.getNetworkInterfaces()) {
                if (!nic.isUp || nic.isLoopback) continue
                for (ia in nic.interfaceAddresses) {
                    if (ia.address is Inet4Address) ia.broadcast?.let { result += it }
                }
            }
        } catch (_: Exception) {
        }
        return result.distinct()
    }
}

enum class BluetoothState { READY, NO_ADAPTER, TURNED_OFF, NEEDS_PERMISSION }

object Bluetooth {
    fun adapter(context: Context): BluetoothAdapter? =
        context.getSystemService(BluetoothManager::class.java)?.adapter

    fun hasConnectPermission(context: Context): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
            ContextCompat.checkSelfPermission(context, android.Manifest.permission.BLUETOOTH_CONNECT) ==
            PackageManager.PERMISSION_GRANTED

    fun canScan(context: Context): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
            ContextCompat.checkSelfPermission(context, android.Manifest.permission.BLUETOOTH_SCAN) ==
            PackageManager.PERMISSION_GRANTED

    fun state(context: Context): BluetoothState {
        val adapter = adapter(context) ?: return BluetoothState.NO_ADAPTER
        if (!hasConnectPermission(context)) return BluetoothState.NEEDS_PERMISSION
        return if (adapter.isEnabled) BluetoothState.READY else BluetoothState.TURNED_OFF
    }

    /** Paired devices, computers first. The PC must be paired in Android's Bluetooth settings. */
    @SuppressLint("MissingPermission")
    fun pairedTargets(context: Context): List<Target.Bluetooth> {
        if (state(context) != BluetoothState.READY) return emptyList()
        val adapter = adapter(context) ?: return emptyList()
        return try {
            adapter.bondedDevices.orEmpty().map {
                Target.Bluetooth(
                    address = it.address,
                    name = it.name ?: it.address,
                    isComputer = it.bluetoothClass?.majorDeviceClass == BluetoothClass.Device.Major.COMPUTER,
                )
            }.sortedWith(compareByDescending<Target.Bluetooth> { it.isComputer }.thenBy { it.name.lowercase() })
        } catch (_: SecurityException) {
            emptyList()
        }
    }
}
