package app.pocketdrop.net

import org.json.JSONArray
import org.json.JSONObject
import java.io.Closeable
import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.UUID

/** Constants shared with the PC app. See docs/PROTOCOL.md. */
object PocketDrop {
    const val APP = "PocketDrop"
    const val PROTOCOL_VERSION = 1
    const val DISCOVERY_PORT = 45454
    const val TCP_PORT = 45455
    const val HTTP_PORT = 45456
    const val DISCOVERY_REQUEST = "POCKETDROP_DISCOVER_V1"
    val BLUETOOTH_UUID: UUID = UUID.fromString("7a1c3e52-5d0b-4b8e-9f43-2c6a0d1e8b71")
}

/** Length-prefixed (4-byte big-endian) UTF-8 JSON frames. */
object Frames {
    private const val MAX_FRAME = 1 shl 20

    fun write(out: OutputStream, message: JSONObject) {
        val body = message.toString().toByteArray(Charsets.UTF_8)
        val frame = ByteArray(4 + body.size)
        frame[0] = (body.size ushr 24).toByte()
        frame[1] = (body.size ushr 16).toByte()
        frame[2] = (body.size ushr 8).toByte()
        frame[3] = body.size.toByte()
        System.arraycopy(body, 0, frame, 4, body.size)
        out.write(frame)
        out.flush()
    }

    /** @return the next frame, or null if the PC closed the connection cleanly. */
    fun read(input: InputStream): JSONObject? {
        val data = DataInputStream(input)
        val first = data.read()
        if (first < 0) return null
        val length = (first shl 24) or (data.readUnsignedByte() shl 16) or
            (data.readUnsignedByte() shl 8) or data.readUnsignedByte()
        if (length <= 0 || length > MAX_FRAME) throw IOException("Bad frame length $length")
        val body = ByteArray(length)
        data.readFully(body)
        return JSONObject(String(body, Charsets.UTF_8))
    }
}

/** A connection to the PC: TCP socket or Bluetooth RFCOMM socket. */
interface Connection : Closeable {
    val input: InputStream
    val output: OutputStream
}

data class RemoteFolder(val name: String, val files: Int)

data class SendFile(val name: String, val size: Long, val modified: Long?)

data class FolderChoice(val existing: Boolean, val name: String)

sealed interface SendResult {
    data class Done(val saved: Int, val folder: String) : SendResult
    /** reason: declined, busy, timeout, invalid, no_space */
    data class Rejected(val reason: String) : SendResult
}

sealed interface SendPhase {
    data object WaitingForAccept : SendPhase
    data class Sending(val folder: String) : SendPhase
}

/**
 * Talks the stream protocol over a [Connection]. Blocking: call it from a background thread.
 * Closing the connection from another thread aborts any call in progress.
 */
class SenderClient(private val conn: Connection, private val deviceName: String) {

    /** @return the PC's display name. */
    fun hello(): String {
        Frames.write(conn.output, JSONObject()
            .put("type", "hello")
            .put("v", PocketDrop.PROTOCOL_VERSION)
            .put("device", deviceName))
        val reply = expect("hello")
        return reply.optString("name", "PC")
    }

    fun listFolders(): List<RemoteFolder> {
        Frames.write(conn.output, JSONObject().put("type", "list_folders"))
        val reply = expect("folders")
        val arr = reply.optJSONArray("folders") ?: JSONArray()
        return (0 until arr.length()).map {
            val o = arr.getJSONObject(it)
            RemoteFolder(o.getString("name"), o.optInt("files"))
        }
    }

    fun send(
        folder: FolderChoice,
        files: List<SendFile>,
        open: (index: Int) -> InputStream,
        onPhase: (SendPhase) -> Unit,
        onProgress: (sentBytes: Long, fileIndex: Int) -> Unit,
    ): SendResult {
        val list = JSONArray()
        files.forEach { f ->
            val o = JSONObject().put("name", f.name).put("size", f.size)
            if (f.modified != null && f.modified > 0) o.put("modified", f.modified)
            list.put(o)
        }
        Frames.write(conn.output, JSONObject()
            .put("type", "offer")
            .put("device", deviceName)
            .put("folder", JSONObject()
                .put("mode", if (folder.existing) "existing" else "new")
                .put("name", folder.name))
            .put("files", list))

        onPhase(SendPhase.WaitingForAccept)
        val answer = Frames.read(conn.input) ?: throw EOFException("The PC closed the connection.")
        when (answer.optString("type")) {
            "accept" -> Unit
            "reject" -> return SendResult.Rejected(answer.optString("reason", "declined"))
            else -> throw IOException("Unexpected reply: ${answer.optString("type")}")
        }
        val folderName = answer.optString("folder", folder.name)
        onPhase(SendPhase.Sending(folderName))

        val buffer = ByteArray(256 * 1024)
        var sent = 0L
        files.forEachIndexed { index, file ->
            onProgress(sent, index)
            open(index).use { input ->
                var remaining = file.size
                while (remaining > 0) {
                    val read = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                    if (read < 0) throw IOException("\"${file.name}\" is shorter than expected.")
                    conn.output.write(buffer, 0, read)
                    remaining -= read
                    sent += read
                    onProgress(sent, index)
                }
            }
        }
        conn.output.flush()

        val done = expect("done")
        return SendResult.Done(done.optInt("saved", files.size), done.optString("folder", folderName))
    }

    private fun expect(type: String): JSONObject {
        val msg = Frames.read(conn.input) ?: throw EOFException("The PC closed the connection.")
        if (msg.optString("type") != type) throw IOException("Expected $type, got ${msg.optString("type")}")
        return msg
    }
}
