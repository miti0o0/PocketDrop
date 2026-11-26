package app.pocketdrop

import android.app.Application
import android.content.Context
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.pocketdrop.net.Bluetooth
import app.pocketdrop.net.BluetoothState
import app.pocketdrop.net.Connection
import app.pocketdrop.net.Connector
import app.pocketdrop.net.Discovery
import app.pocketdrop.net.FolderChoice
import app.pocketdrop.net.PocketDrop
import app.pocketdrop.net.RemoteFolder
import app.pocketdrop.net.SendFile
import app.pocketdrop.net.SendPhase
import app.pocketdrop.net.SendResult
import app.pocketdrop.net.SenderClient
import app.pocketdrop.net.Target
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException

enum class TransportKind { WIFI, BLUETOOTH }

sealed interface SendState {
    data object Idle : SendState
    data class Connecting(val pc: String) : SendState
    data class WaitingForAccept(val pc: String) : SendState
    data class Sending(
        val pc: String,
        val folder: String,
        val sent: Long,
        val total: Long,
        val fileIndex: Int,
        val fileCount: Int,
        val bytesPerSecond: Double,
    ) : SendState
    data class Done(val pc: String, val saved: Int, val folder: String) : SendState
    /** reason: declined, busy, timeout, no_space, invalid, unreachable, failed */
    data class Failed(val reason: String, val detail: String? = null) : SendState
}

data class UiState(
    val files: List<PickedFile> = emptyList(),
    val loadingFiles: Boolean = false,
    val transport: TransportKind = TransportKind.WIFI,
    val wifiTargets: List<Target.Wifi> = emptyList(),
    val scanning: Boolean = false,
    val btTargets: List<Target.Bluetooth> = emptyList(),
    val btState: BluetoothState = BluetoothState.READY,
    val selectedKey: String? = null,
    val existingFolder: Boolean = false,
    val folderName: String = "",
    val remoteFolders: List<RemoteFolder> = emptyList(),
    val foldersLoading: Boolean = false,
    val foldersError: Boolean = false,
    val selectedFolder: String? = null,
    val send: SendState = SendState.Idle,
) {
    val targets: List<Target> get() = if (transport == TransportKind.WIFI) wifiTargets else btTargets
    val selected: Target? get() = targets.firstOrNull { it.key == selectedKey }
    val totalBytes: Long get() = files.sumOf { it.size }
    val canSend: Boolean
        get() = files.isNotEmpty() && selected != null && !loadingFiles &&
            (!existingFolder || selectedFolder != null)
}

class MainViewModel(app: Application) : AndroidViewModel(app) {

    private val prefs = app.getSharedPreferences("pocketdrop", Context.MODE_PRIVATE)
    private val _state = MutableStateFlow(
        UiState(
            transport = runCatching { TransportKind.valueOf(prefs.getString("transport", "WIFI")!!) }
                .getOrDefault(TransportKind.WIFI),
            selectedKey = prefs.getString("target", null),
        )
    )
    val state: StateFlow<UiState> = _state.asStateFlow()

    private var sendJob: Job? = null
    @Volatile private var activeConnection: Connection? = null
    private val manualTargets = mutableListOf<Target.Wifi>()

    private val context: Context get() = getApplication()

    val deviceName: String by lazy {
        val name = runCatching { Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME) }.getOrNull()
        if (!name.isNullOrBlank()) name else "${Build.MANUFACTURER} ${Build.MODEL}".trim()
    }

    init {
        refreshTargets()
    }

    // ---- Files -----------------------------------------------------------------------------

    fun addUris(uris: List<Uri>) {
        if (uris.isEmpty()) return
        _state.update { it.copy(loadingFiles = true) }
        viewModelScope.launch {
            val resolved = withContext(Dispatchers.IO) {
                val start = _state.value.files.size
                uris.mapIndexedNotNull { i, uri -> PickedFiles.resolve(context.contentResolver, uri, start + i + 1) }
            }
            _state.update { s ->
                val known = s.files.map { it.uri }.toSet()
                s.copy(files = s.files + resolved.filter { it.uri !in known }, loadingFiles = false)
            }
        }
    }

    fun removeFile(file: PickedFile) = _state.update { s -> s.copy(files = s.files - file) }

    fun clearFiles() = _state.update { it.copy(files = emptyList()) }

    // ---- Targets ---------------------------------------------------------------------------

    fun setTransport(kind: TransportKind) {
        prefs.edit().putString("transport", kind.name).apply()
        _state.update { it.copy(transport = kind, remoteFolders = emptyList(), selectedFolder = null) }
        refreshTargets()
    }

    fun refreshTargets() {
        when (_state.value.transport) {
            TransportKind.WIFI -> scanWifi()
            TransportKind.BLUETOOTH -> refreshBluetooth()
        }
    }

    private fun scanWifi() {
        if (_state.value.scanning) return
        _state.update { it.copy(scanning = true) }
        viewModelScope.launch {
            val found = runCatching { Discovery.scan() }.getOrDefault(emptyList())
            val merged = (found + manualTargets.filter { m -> found.none { it.host == m.host } })
            _state.update { s ->
                s.copy(wifiTargets = merged, scanning = false).autoSelect()
            }
            if (_state.value.existingFolder) loadFolders()
        }
    }

    fun refreshBluetooth() {
        val bt = Bluetooth.state(context)
        val list = Bluetooth.pairedTargets(context)
        _state.update { it.copy(btState = bt, btTargets = list).autoSelect() }
    }

    /** Keeps the remembered PC if it is in the list; otherwise picks the only one available. */
    private fun UiState.autoSelect(): UiState {
        if (targets.any { it.key == selectedKey }) return this
        val pick = when {
            targets.size == 1 -> targets[0]
            transport == TransportKind.BLUETOOTH -> btTargets.singleOrNull { it.isComputer }
            else -> null
        }
        return if (pick != null) copy(selectedKey = pick.key) else this
    }

    fun select(target: Target) {
        prefs.edit().putString("target", target.key).apply()
        _state.update { it.copy(selectedKey = target.key, remoteFolders = emptyList(), selectedFolder = null) }
        if (_state.value.existingFolder) loadFolders()
    }

    fun addManualHost(host: String) {
        val h = host.trim().removePrefix("http://").substringBefore('/').substringBefore(':')
        if (h.isEmpty()) return
        val target = Target.Wifi(id = "manual:$h", name = h, host = h, port = PocketDrop.TCP_PORT)
        manualTargets.removeAll { it.host == h }
        manualTargets += target
        _state.update { s -> s.copy(wifiTargets = s.wifiTargets.filter { it.host != h } + target) }
        select(target)
    }

    // ---- Folder ----------------------------------------------------------------------------

    fun setExistingFolder(existing: Boolean) {
        _state.update { it.copy(existingFolder = existing) }
        if (existing && _state.value.remoteFolders.isEmpty()) loadFolders()
    }

    fun setFolderName(name: String) = _state.update { it.copy(folderName = name.take(80)) }

    fun chooseFolder(name: String) = _state.update { it.copy(selectedFolder = name) }

    fun loadFolders() {
        val target = _state.value.selected ?: return
        if (_state.value.foldersLoading) return
        _state.update { it.copy(foldersLoading = true, foldersError = false) }
        viewModelScope.launch {
            val result = runCatching {
                withContext(Dispatchers.IO) {
                    Connector.open(context, target).use { conn ->
                        val client = SenderClient(conn, deviceName)
                        client.hello()
                        client.listFolders()
                    }
                }
            }
            _state.update { s ->
                val folders = result.getOrDefault(emptyList())
                s.copy(
                    foldersLoading = false,
                    foldersError = result.isFailure,
                    remoteFolders = folders,
                    selectedFolder = s.selectedFolder?.takeIf { sel -> folders.any { it.name == sel } }
                        ?: folders.firstOrNull()?.name,
                )
            }
        }
    }

    // ---- Send ------------------------------------------------------------------------------

    fun send() {
        val s = _state.value
        val target = s.selected ?: return
        if (!s.canSend || sendJob?.isActive == true) return

        val files = s.files
        val folder = if (s.existingFolder) FolderChoice(true, s.selectedFolder ?: "")
        else FolderChoice(false, s.folderName.trim())
        val total = files.sumOf { it.size }

        _state.update { it.copy(send = SendState.Connecting(target.name)) }
        sendJob = viewModelScope.launch {
            val result: SendState = try {
                val conn = try {
                    Connector.open(context, target)
                } catch (e: Exception) {
                    throw UnreachableException(e)
                }
                activeConnection = conn
                withContext(Dispatchers.IO) {
                    conn.use {
                        val client = SenderClient(conn, deviceName)
                        val pc = client.hello()
                        var folderName = folder.name
                        val started = System.nanoTime()
                        var lastUpdate = 0L
                        val outcome = client.send(
                            folder = folder,
                            files = files.map { SendFile(it.name, it.size, it.modified) },
                            open = { i ->
                                context.contentResolver.openInputStream(files[i].uri)
                                    ?: throw IOException("Cannot open ${files[i].name}")
                            },
                            onPhase = { phase ->
                                when (phase) {
                                    SendPhase.WaitingForAccept ->
                                        _state.update { it.copy(send = SendState.WaitingForAccept(pc)) }
                                    is SendPhase.Sending -> {
                                        folderName = phase.folder
                                        _state.update {
                                            it.copy(send = SendState.Sending(pc, phase.folder, 0, total, 0, files.size, 0.0))
                                        }
                                    }
                                }
                            },
                            onProgress = { sent, index ->
                                val now = System.nanoTime()
                                if (now - lastUpdate > 100_000_000L || sent == total) {
                                    lastUpdate = now
                                    val secs = (now - started) / 1e9
                                    _state.update {
                                        it.copy(send = SendState.Sending(pc, folderName, sent, total, index, files.size,
                                            if (secs > 0.3) sent / secs else 0.0))
                                    }
                                }
                            },
                        )
                        when (outcome) {
                            is SendResult.Done -> SendState.Done(pc, outcome.saved, outcome.folder)
                            is SendResult.Rejected -> SendState.Failed(outcome.reason)
                        }
                    }
                }
            } catch (e: UnreachableException) {
                SendState.Failed("unreachable", e.cause?.message)
            } catch (e: Exception) {
                if (_state.value.send is SendState.Idle) SendState.Idle
                else SendState.Failed("failed", e.message)
            } finally {
                activeConnection = null
            }
            _state.update { it.copy(send = result) }
            if (result is SendState.Done) {
                _state.update { it.copy(files = emptyList(), remoteFolders = emptyList()) }
            }
        }
    }

    fun cancelSend() {
        _state.update { it.copy(send = SendState.Idle) }
        activeConnection?.close()
        sendJob?.cancel()
    }

    fun dismissResult() = _state.update { it.copy(send = SendState.Idle) }

    override fun onCleared() {
        activeConnection?.close()
    }

    private class UnreachableException(cause: Throwable) : Exception(cause)
}
