package app.pocketdrop

import android.content.ContentResolver
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap

data class PickedFile(
    val uri: Uri,
    val name: String,
    val size: Long,
    val modified: Long?,
    val mimeType: String?,
)

object PickedFiles {

    /** Reads name, size and date for a content:// or file:// URI. Blocking. Returns null if unreadable. */
    fun resolve(resolver: ContentResolver, uri: Uri, fallbackIndex: Int): PickedFile? {
        var name: String? = null
        var size = -1L
        var modified: Long? = null

        try {
            resolver.query(uri, null, null, null, null)?.use { c ->
                if (c.moveToFirst()) {
                    c.getColumnIndex(OpenableColumns.DISPLAY_NAME).takeIf { it >= 0 }?.let { name = c.getString(it) }
                    c.getColumnIndex(OpenableColumns.SIZE).takeIf { it >= 0 && !c.isNull(it) }?.let { size = c.getLong(it) }
                    c.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED)
                        .takeIf { it >= 0 && !c.isNull(it) }?.let { modified = c.getLong(it) }
                    if (modified == null) {
                        // MediaStore: date_modified is in seconds.
                        c.getColumnIndex("date_modified").takeIf { it >= 0 && !c.isNull(it) }
                            ?.let { modified = c.getLong(it) * 1000 }
                    }
                }
            }
        } catch (_: Exception) {
        }

        if (size < 0) {
            size = try {
                resolver.openAssetFileDescriptor(uri, "r")?.use { it.length } ?: -1L
            } catch (_: Exception) {
                -1L
            }
        }
        if (size < 0) {
            // Last resort: count the bytes.
            size = try {
                resolver.openInputStream(uri)?.use { input ->
                    val buf = ByteArray(256 * 1024)
                    var total = 0L
                    while (true) {
                        val r = input.read(buf)
                        if (r < 0) break
                        total += r
                    }
                    total
                } ?: return null
            } catch (_: Exception) {
                return null
            }
        }

        val mime = try { resolver.getType(uri) } catch (_: Exception) { null }
        if (name.isNullOrBlank()) {
            val ext = mime?.let { MimeTypeMap.getSingleton().getExtensionFromMimeType(it) }
            name = (uri.lastPathSegment?.substringAfterLast('/')?.takeIf { it.isNotBlank() } ?: "file-$fallbackIndex") +
                (if (ext != null && !(uri.lastPathSegment ?: "").endsWith(".$ext")) ".$ext" else "")
        }
        return PickedFile(uri, name!!, size, modified, mime)
    }
}
