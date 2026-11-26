package app.pocketdrop.ui

import android.Manifest
import android.content.Intent
import android.os.Build
import android.provider.Settings
import android.text.format.Formatter
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.PhotoLibrary
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.pocketdrop.MainViewModel
import app.pocketdrop.PickedFile
import app.pocketdrop.R
import app.pocketdrop.SendState
import app.pocketdrop.TransportKind
import app.pocketdrop.UiState
import app.pocketdrop.net.BluetoothState
import app.pocketdrop.net.Target

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainScreen(vm: MainViewModel) {
    val state by vm.state.collectAsStateWithLifecycle()
    val context = LocalContext.current

    val pickMedia = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia()) { vm.addUris(it) }
    val pickFiles = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { vm.addUris(it) }
    val btPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { vm.refreshBluetooth() }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.app_name), fontWeight = FontWeight.Bold) },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
            )
        },
        bottomBar = {
            Surface(color = MaterialTheme.colorScheme.background) {
                Button(
                    onClick = vm::send,
                    enabled = state.canSend,
                    modifier = Modifier
                        .fillMaxWidth()
                        .navigationBarsPadding()
                        .padding(16.dp)
                        .height(56.dp),
                    shape = RoundedCornerShape(16.dp),
                ) {
                    Icon(Icons.AutoMirrored.Filled.Send, contentDescription = null)
                    Spacer(Modifier.width(10.dp))
                    Text(
                        text = state.selected?.let { stringResource(R.string.send_to, it.name) } ?: stringResource(R.string.send),
                        fontWeight = FontWeight.Bold,
                        style = MaterialTheme.typography.titleMedium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
        },
        containerColor = MaterialTheme.colorScheme.background,
    ) { padding ->
        LazyColumn(
            contentPadding = PaddingValues(
                start = 16.dp, end = 16.dp,
                top = padding.calculateTopPadding() + 4.dp,
                bottom = padding.calculateBottomPadding() + 16.dp,
            ),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            item {
                FilesCard(
                    state = state,
                    onPickMedia = {
                        pickMedia.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))
                    },
                    onPickFiles = { pickFiles.launch(arrayOf("*/*")) },
                    onRemove = vm::removeFile,
                    onClear = vm::clearFiles,
                )
            }
            item {
                TargetCard(
                    state = state,
                    onTransport = vm::setTransport,
                    onRefresh = vm::refreshTargets,
                    onSelect = vm::select,
                    onManual = vm::addManualHost,
                    onBluetoothPermission = {
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) btPermission.launch(Manifest.permission.BLUETOOTH_CONNECT)
                    },
                    onBluetoothSettings = {
                        context.startActivity(Intent(Settings.ACTION_BLUETOOTH_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    },
                )
            }
            item {
                FolderCard(
                    state = state,
                    onMode = vm::setExistingFolder,
                    onName = vm::setFolderName,
                    onChoose = vm::chooseFolder,
                    onReload = vm::loadFolders,
                )
            }
        }
    }

    if (state.send !is SendState.Idle) {
        SendDialog(state.send, onCancel = vm::cancelSend, onDismiss = vm::dismissResult)
    }
}

// ---- Cards ---------------------------------------------------------------------------------

@Composable
private fun SectionCard(title: String, action: (@Composable () -> Unit)? = null, content: @Composable () -> Unit) {
    Card(
        shape = RoundedCornerShape(16.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                action?.invoke()
            }
            Spacer(Modifier.height(10.dp))
            content()
        }
    }
}

@Composable
private fun FilesCard(
    state: UiState,
    onPickMedia: () -> Unit,
    onPickFiles: () -> Unit,
    onRemove: (PickedFile) -> Unit,
    onClear: () -> Unit,
) {
    val context = LocalContext.current
    SectionCard(
        title = stringResource(R.string.files),
        action = if (state.files.isNotEmpty()) ({ TextButton(onClick = onClear) { Text(stringResource(R.string.clear)) } }) else null,
    ) {
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            FilledTonalButton(onClick = onPickMedia, modifier = Modifier.weight(1f), shape = RoundedCornerShape(12.dp)) {
                Icon(Icons.Filled.PhotoLibrary, null, Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text(stringResource(R.string.pick_photos), maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            FilledTonalButton(onClick = onPickFiles, modifier = Modifier.weight(1f), shape = RoundedCornerShape(12.dp)) {
                Icon(Icons.Filled.Description, null, Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text(stringResource(R.string.pick_files), maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }

        if (state.loadingFiles) {
            LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 12.dp))
        }

        if (state.files.isEmpty() && !state.loadingFiles) {
            Text(
                stringResource(R.string.no_files),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 10.dp),
            )
        }

        state.files.take(MAX_VISIBLE_FILES).forEach { file ->
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 8.dp)) {
                Icon(
                    imageVector = fileIcon(file.mimeType),
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(20.dp),
                )
                Spacer(Modifier.width(10.dp))
                Text(file.name, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.bodyMedium)
                Text(Formatter.formatShortFileSize(context, file.size), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                IconButton(onClick = { onRemove(file) }, modifier = Modifier.size(32.dp)) {
                    Icon(Icons.Filled.Close, stringResource(R.string.remove), Modifier.size(16.dp))
                }
            }
        }
        if (state.files.size > MAX_VISIBLE_FILES) {
            Text(
                stringResource(R.string.and_more, state.files.size - MAX_VISIBLE_FILES),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 6.dp),
            )
        }
        if (state.files.isNotEmpty()) {
            Text(
                pluralStringResource(R.plurals.file_count, state.files.size, state.files.size) +
                    " · " + Formatter.formatShortFileSize(context, state.totalBytes),
                style = MaterialTheme.typography.bodySmall,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier.padding(top = 10.dp),
            )
        }
    }
}

private const val MAX_VISIBLE_FILES = 8

private fun fileIcon(mime: String?): ImageVector = when {
    mime == null -> Icons.Filled.Description
    mime.startsWith("image/") -> Icons.Filled.Image
    mime.startsWith("video/") -> Icons.Filled.Movie
    else -> Icons.Filled.Description
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TargetCard(
    state: UiState,
    onTransport: (TransportKind) -> Unit,
    onRefresh: () -> Unit,
    onSelect: (Target) -> Unit,
    onManual: (String) -> Unit,
    onBluetoothPermission: () -> Unit,
    onBluetoothSettings: () -> Unit,
) {
    var showManual by rememberSaveable { mutableStateOf(false) }

    SectionCard(
        title = stringResource(R.string.send_to_title),
        action = {
            if (state.scanning) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
            else IconButton(onClick = onRefresh) { Icon(Icons.Filled.Refresh, stringResource(R.string.refresh)) }
        },
    ) {
        SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
            SegmentedButton(
                selected = state.transport == TransportKind.WIFI,
                onClick = { onTransport(TransportKind.WIFI) },
                shape = SegmentedButtonDefaults.itemShape(0, 2),
                icon = { Icon(Icons.Filled.Wifi, null, Modifier.size(18.dp)) },
            ) { Text(stringResource(R.string.wifi)) }
            SegmentedButton(
                selected = state.transport == TransportKind.BLUETOOTH,
                onClick = { onTransport(TransportKind.BLUETOOTH) },
                shape = SegmentedButtonDefaults.itemShape(1, 2),
                icon = { Icon(Icons.Filled.Bluetooth, null, Modifier.size(18.dp)) },
            ) { Text(stringResource(R.string.bluetooth)) }
        }
        Spacer(Modifier.height(8.dp))

        if (state.transport == TransportKind.BLUETOOTH) {
            when (state.btState) {
                BluetoothState.NO_ADAPTER -> Hint(stringResource(R.string.bt_no_adapter))
                BluetoothState.NEEDS_PERMISSION -> {
                    Hint(stringResource(R.string.bt_permission))
                    TextButton(onClick = onBluetoothPermission) { Text(stringResource(R.string.allow)) }
                }
                BluetoothState.TURNED_OFF -> {
                    Hint(stringResource(R.string.bt_off))
                    TextButton(onClick = onBluetoothSettings) { Text(stringResource(R.string.open_bt_settings)) }
                }
                BluetoothState.READY -> if (state.btTargets.isEmpty()) Hint(stringResource(R.string.bt_none_paired))
            }
        } else if (state.wifiTargets.isEmpty() && !state.scanning) {
            Hint(stringResource(R.string.wifi_none_found))
        }

        state.targets.forEach { target ->
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(10.dp))
                    .clickable { onSelect(target) }
                    .padding(vertical = 2.dp),
            ) {
                RadioButton(selected = state.selectedKey == target.key, onClick = { onSelect(target) })
                Icon(
                    if (target is Target.Bluetooth && !target.isComputer) Icons.Filled.Bluetooth else Icons.Filled.Computer,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.size(20.dp),
                )
                Spacer(Modifier.width(10.dp))
                Column(Modifier.weight(1f)) {
                    Text(target.name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    if (target is Target.Wifi) {
                        Text(target.host, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }

        if (state.transport == TransportKind.WIFI) {
            TextButton(onClick = { showManual = true }) { Text(stringResource(R.string.enter_address)) }
        } else if (state.btState == BluetoothState.READY) {
            TextButton(onClick = onBluetoothSettings) { Text(stringResource(R.string.pair_pc)) }
        }
    }

    if (showManual) ManualAddressDialog(onDismiss = { showManual = false }, onAdd = { onManual(it); showManual = false })
}

@Composable
private fun ManualAddressDialog(onDismiss: () -> Unit, onAdd: (String) -> Unit) {
    var text by rememberSaveable { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.enter_address)) },
        text = {
            Column {
                Text(stringResource(R.string.enter_address_hint), style = MaterialTheme.typography.bodySmall)
                Spacer(Modifier.height(10.dp))
                OutlinedTextField(value = text, onValueChange = { text = it }, singleLine = true,
                    placeholder = { Text("192.168.1.20") })
            }
        },
        confirmButton = { TextButton(onClick = { onAdd(text) }, enabled = text.isNotBlank()) { Text(stringResource(R.string.add)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun FolderCard(
    state: UiState,
    onMode: (Boolean) -> Unit,
    onName: (String) -> Unit,
    onChoose: (String) -> Unit,
    onReload: () -> Unit,
) {
    SectionCard(title = stringResource(R.string.folder_title)) {
        SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
            SegmentedButton(
                selected = !state.existingFolder,
                onClick = { onMode(false) },
                shape = SegmentedButtonDefaults.itemShape(0, 2),
                icon = { Icon(Icons.Filled.CreateNewFolder, null, Modifier.size(18.dp)) },
            ) { Text(stringResource(R.string.new_folder)) }
            SegmentedButton(
                selected = state.existingFolder,
                onClick = { onMode(true) },
                shape = SegmentedButtonDefaults.itemShape(1, 2),
                icon = { Icon(Icons.Filled.Folder, null, Modifier.size(18.dp)) },
            ) { Text(stringResource(R.string.existing_folder)) }
        }
        Spacer(Modifier.height(12.dp))

        if (!state.existingFolder) {
            OutlinedTextField(
                value = state.folderName,
                onValueChange = onName,
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
                label = { Text(stringResource(R.string.folder_name)) },
                placeholder = { Text(stringResource(R.string.folder_name_placeholder)) },
                shape = RoundedCornerShape(12.dp),
            )
            Hint(stringResource(R.string.folder_name_hint))
        } else when {
            state.selected == null -> Hint(stringResource(R.string.pick_pc_first))
            state.foldersLoading -> Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(10.dp))
                Hint(stringResource(R.string.loading_folders))
            }
            state.foldersError -> {
                Hint(stringResource(R.string.folders_error))
                TextButton(onClick = onReload) { Text(stringResource(R.string.retry)) }
            }
            state.remoteFolders.isEmpty() -> Hint(stringResource(R.string.no_folders))
            else -> state.remoteFolders.forEach { folder ->
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(10.dp))
                        .clickable { onChoose(folder.name) },
                ) {
                    RadioButton(selected = state.selectedFolder == folder.name, onClick = { onChoose(folder.name) })
                    Text(folder.name, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(
                        pluralStringResource(R.plurals.file_count, folder.files, folder.files),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(end = 8.dp),
                    )
                }
            }
        }
    }
}

@Composable
private fun Hint(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(vertical = 4.dp),
    )
}

// ---- Send dialog ---------------------------------------------------------------------------

@Composable
private fun SendDialog(send: SendState, onCancel: () -> Unit, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val view = LocalView.current
    val busy = send is SendState.Connecting || send is SendState.WaitingForAccept || send is SendState.Sending

    // Keep the screen (and with it the connection) awake while sending.
    DisposableEffect(busy) {
        view.keepScreenOn = busy
        onDispose { view.keepScreenOn = false }
    }

    Dialog(
        onDismissRequest = { if (!busy) onDismiss() },
        properties = DialogProperties(dismissOnBackPress = !busy, dismissOnClickOutside = false),
    ) {
        Surface(shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surfaceContainer) {
            Column(
                Modifier
                    .fillMaxWidth()
                    .padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                when (send) {
                    is SendState.Connecting -> {
                        CircularProgressIndicator(Modifier.size(44.dp))
                        DialogTitle(stringResource(R.string.connecting, send.pc))
                    }
                    is SendState.WaitingForAccept -> {
                        CircularProgressIndicator(Modifier.size(44.dp))
                        DialogTitle(stringResource(R.string.waiting_title))
                        DialogText(stringResource(R.string.waiting_text, send.pc))
                    }
                    is SendState.Sending -> {
                        val fraction = if (send.total > 0) send.sent.toFloat() / send.total else 1f
                        Box(
                            Modifier
                                .size(56.dp)
                                .clip(CircleShape)
                                .background(MaterialTheme.colorScheme.primaryContainer),
                            contentAlignment = Alignment.Center,
                        ) {
                            Text("${(fraction * 100).toInt()}%", fontWeight = FontWeight.Bold,
                                color = MaterialTheme.colorScheme.onPrimaryContainer)
                        }
                        DialogTitle(stringResource(R.string.sending_to, send.pc))
                        DialogText(stringResource(R.string.into_folder, send.folder))
                        Spacer(Modifier.height(14.dp))
                        LinearProgressIndicator(
                            progress = { fraction },
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(8.dp)
                                .clip(RoundedCornerShape(4.dp)),
                        )
                        Spacer(Modifier.height(8.dp))
                        val speed = if (send.bytesPerSecond > 0)
                            " · " + Formatter.formatShortFileSize(context, send.bytesPerSecond.toLong()) + "/s" else ""
                        DialogText(
                            stringResource(R.string.progress_files, send.fileIndex + 1, send.fileCount) + " · " +
                                stringResource(
                                    R.string.progress_bytes,
                                    Formatter.formatShortFileSize(context, send.sent),
                                    Formatter.formatShortFileSize(context, send.total),
                                ) + speed
                        )
                    }
                    is SendState.Done -> {
                        Icon(Icons.Filled.CheckCircle, null, tint = Success, modifier = Modifier.size(56.dp))
                        DialogTitle(stringResource(R.string.done_title))
                        DialogText(
                            stringResource(
                                R.string.done_text,
                                pluralStringResource(R.plurals.file_count, send.saved, send.saved),
                                send.pc,
                                send.folder,
                            )
                        )
                    }
                    is SendState.Failed -> {
                        Icon(Icons.Filled.ErrorOutline, null, tint = Danger, modifier = Modifier.size(56.dp))
                        val (title, text) = failureText(send.reason)
                        DialogTitle(title)
                        DialogText(text)
                        if (send.reason == "failed" && !send.detail.isNullOrBlank()) {
                            Text(send.detail, style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 6.dp))
                        }
                    }
                    SendState.Idle -> Unit
                }

                Spacer(Modifier.height(20.dp))
                if (busy) {
                    TextButton(onClick = onCancel) { Text(stringResource(R.string.cancel)) }
                } else {
                    Button(onClick = onDismiss, shape = RoundedCornerShape(12.dp),
                        colors = ButtonDefaults.buttonColors()) { Text(stringResource(R.string.ok)) }
                }
            }
        }
    }
}

@Composable
private fun failureText(reason: String): Pair<String, String> = when (reason) {
    "declined" -> stringResource(R.string.err_declined_title) to stringResource(R.string.err_declined)
    "busy" -> stringResource(R.string.err_busy_title) to stringResource(R.string.err_busy)
    "timeout" -> stringResource(R.string.err_timeout_title) to stringResource(R.string.err_timeout)
    "no_space" -> stringResource(R.string.err_space_title) to stringResource(R.string.err_space)
    "unreachable" -> stringResource(R.string.err_unreachable_title) to stringResource(R.string.err_unreachable)
    else -> stringResource(R.string.err_failed_title) to stringResource(R.string.err_failed)
}

@Composable
private fun DialogTitle(text: String) {
    Spacer(Modifier.height(16.dp))
    Text(text, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold,
        textAlign = androidx.compose.ui.text.style.TextAlign.Center)
}

@Composable
private fun DialogText(text: String) {
    Spacer(Modifier.height(6.dp))
    Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
        textAlign = androidx.compose.ui.text.style.TextAlign.Center)
}
