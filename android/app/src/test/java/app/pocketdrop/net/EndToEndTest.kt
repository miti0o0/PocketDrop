package app.pocketdrop.net

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.net.Socket
import kotlin.random.Random

/**
 * Runs the Android sender against the real PC receiver.
 *
 * Start the receiver first:
 *   dotnet run --project desktop/PocketDrop.TestReceiver -- <folder> 300
 * then run with POCKETDROP_E2E_FOLDER=<folder>:
 *   gradlew :app:testDebugUnitTest --tests "*EndToEndTest*"
 * Skipped when POCKETDROP_E2E_FOLDER is not set.
 */
class EndToEndTest {
    private val folder: String? = System.getenv("POCKETDROP_E2E_FOLDER")
    private val host = System.getenv("POCKETDROP_E2E_HOST") ?: "127.0.0.1"

    private fun connect(): Connection {
        val socket = Socket(host, PocketDrop.TCP_PORT)
        return object : Connection {
            override val input: InputStream = socket.getInputStream().buffered()
            override val output: OutputStream = socket.getOutputStream().buffered()
            override fun close() = socket.close()
        }
    }

    @Test
    fun discovery_finds_the_pc() = runBlocking {
        assumeTrue(folder != null)
        val found = Discovery.scan(1500)
        assertTrue("PC not discovered: $found", found.any { it.name == "E2E-PC" })
    }

    @Test
    fun sends_files_into_new_and_existing_folders() {
        assumeTrue(folder != null)
        val small = "Здравей от телефона".toByteArray()
        val big = Random(7).nextBytes(5_000_000)
        val files = listOf(
            SendFile("hello.txt", small.size.toLong(), 1_700_000_000_000),
            SendFile("video.mp4", big.size.toLong(), null),
        )
        val data = listOf(small, big)
        val folderName = "E2E " + System.currentTimeMillis()

        connect().use { conn ->
            val client = SenderClient(conn, "JUnit Phone")
            assertEquals("E2E-PC", client.hello())

            var lastProgress = 0L
            val phases = mutableListOf<SendPhase>()
            val result = client.send(
                FolderChoice(existing = false, name = folderName),
                files,
                open = { ByteArrayInputStream(data[it]) },
                onPhase = { phases += it },
                onProgress = { sent, _ -> lastProgress = sent },
            )
            assertEquals(SendResult.Done(2, folderName), result)
            assertEquals(small.size + big.size.toLong(), lastProgress)
            assertEquals(SendPhase.WaitingForAccept, phases.first())

            // Same connection: list folders and add to the existing one.
            assertTrue(client.listFolders().any { it.name == folderName && it.files == 2 })
            val again = client.send(
                FolderChoice(existing = true, name = folderName),
                listOf(SendFile("hello.txt", small.size.toLong(), null)),
                open = { ByteArrayInputStream(small) },
                onPhase = {},
                onProgress = { _, _ -> },
            )
            assertEquals(SendResult.Done(1, folderName), again)
        }

        val dir = File(folder!!, folderName)
        assertArrayEquals(small, File(dir, "hello.txt").readBytes())
        assertArrayEquals(big, File(dir, "video.mp4").readBytes())
        assertTrue(File(dir, "hello (1).txt").exists())
    }

    @Test
    fun declined_offer_returns_rejected() {
        assumeTrue(folder != null)
        connect().use { conn ->
            val client = SenderClient(conn, "JUnit Phone")
            client.hello()
            val result = client.send(
                FolderChoice(existing = false, name = "Decline me"),
                listOf(SendFile("x.txt", 1, null)),
                open = { ByteArrayInputStream(byteArrayOf(1)) },
                onPhase = {},
                onProgress = { _, _ -> },
            )
            assertEquals(SendResult.Rejected("declined"), result)
        }
    }
}
