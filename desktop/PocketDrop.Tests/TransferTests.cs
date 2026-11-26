using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PocketDrop.Core;
using PocketDrop.Transport;
using Xunit;

namespace PocketDrop.Tests
{
    internal sealed class FakeUi : IReceiverUi
    {
        public bool Accept { get; set; } = true;
        public int Asked;
        public TaskCompletionSource<ReceiveSession> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ConfirmAsync(TransferOffer offer, string folderName, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            return Task.FromResult(Accept);
        }

        public void TransferStarted(ReceiveSession session) { }
        public void TransferFinished(ReceiveSession session) => Finished.TrySetResult(session);
    }

    public sealed class TransferTests : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "pocketdrop-tests-" + Guid.NewGuid().ToString("N"));
        private readonly Settings _settings;

        public TransferTests()
        {
            _settings = new Settings { BaseFolder = _base, DeviceName = "TEST-PC" };
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, true); } catch { }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static async Task Send(Stream s, object msg)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(msg);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
            await s.WriteAsync(header);
            await s.WriteAsync(body);
        }

        private static async Task<JsonObject> Receive(Stream s)
        {
            var msg = await Frames.ReadAsync(s, CancellationToken.None);
            Assert.NotNull(msg);
            return msg!;
        }

        [Fact]
        public async Task Tcp_full_transfer_saves_files_with_safe_names()
        {
            var ui = new FakeUi();
            var manager = new TransferManager(() => _settings, ui);
            var port = FreePort();
            using var tcp = new TcpTransport(new StreamProtocolHandler(manager, () => _settings), port);
            tcp.Start();

            var a = Encoding.UTF8.GetBytes("hello world");
            var b = new byte[3_000_000];
            new Random(1).NextBytes(b);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var s = client.GetStream();

            await Send(s, new { type = "hello", v = 1, device = "Test Phone" });
            var hello = await Receive(s);
            Assert.Equal("hello", (string?)hello["type"]);
            Assert.Equal("TEST-PC", (string?)hello["name"]);

            await Send(s, new
            {
                type = "offer",
                folder = new { mode = "new", name = "../Trip" },
                files = new object[]
                {
                    new { name = "../../evil.txt", size = a.Length },
                    new { name = "big.bin", size = b.Length, modified = 1_700_000_000_000L },
                    new { name = "empty.txt", size = 0 },
                },
            });
            var accept = await Receive(s);
            Assert.Equal("accept", (string?)accept["type"]);
            Assert.Equal("Trip", (string?)accept["folder"]);

            await s.WriteAsync(a);
            await s.WriteAsync(b);
            var done = await Receive(s);
            Assert.Equal("done", (string?)done["type"]);
            Assert.Equal(3, (int?)done["saved"]);

            var folder = Path.Combine(_base, "Trip");
            Assert.Equal(a, await File.ReadAllBytesAsync(Path.Combine(folder, "evil.txt")));
            Assert.Equal(b, await File.ReadAllBytesAsync(Path.Combine(folder, "big.bin")));
            Assert.Equal(0, new FileInfo(Path.Combine(folder, "empty.txt")).Length);
            Assert.Equal(2023, File.GetLastWriteTimeUtc(Path.Combine(folder, "big.bin")).Year);
            Assert.False(File.Exists(Path.Combine(_base, "..", "evil.txt")));

            var session = await ui.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SessionState.Completed, session.State);

            // Same folder name again → "Trip (2)"; existing mode reuses "Trip" and renames clashing files.
            await Send(s, new { type = "list_folders" });
            var folders = await Receive(s);
            Assert.Contains("Trip", folders["folders"]!.AsArray().Select(f => (string?)f!["name"]));

            await Send(s, new
            {
                type = "offer",
                folder = new { mode = "existing", name = "Trip" },
                files = new object[] { new { name = "evil.txt", size = a.Length } },
            });
            Assert.Equal("accept", (string?)(await Receive(s))["type"]);
            await s.WriteAsync(a);
            Assert.Equal("done", (string?)(await Receive(s))["type"]);
            Assert.True(File.Exists(Path.Combine(folder, "evil (1).txt")));
        }

        [Fact]
        public async Task Tcp_declined_offer_is_rejected()
        {
            var ui = new FakeUi { Accept = false };
            var manager = new TransferManager(() => _settings, ui);
            var port = FreePort();
            using var tcp = new TcpTransport(new StreamProtocolHandler(manager, () => _settings), port);
            tcp.Start();

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var s = client.GetStream();
            await Send(s, new { type = "offer", folder = new { mode = "new", name = "X" }, files = new[] { new { name = "a", size = 1 } } });
            var reply = await Receive(s);
            Assert.Equal("reject", (string?)reply["type"]);
            Assert.Equal("declined", (string?)reply["reason"]);
            Assert.False(Directory.Exists(Path.Combine(_base, "X")));
        }

        [Fact]
        public async Task Tcp_disconnect_mid_file_deletes_partial_file_and_keeps_finished_ones()
        {
            var ui = new FakeUi();
            var manager = new TransferManager(() => _settings, ui);
            var port = FreePort();
            using var tcp = new TcpTransport(new StreamProtocolHandler(manager, () => _settings), port);
            tcp.Start();

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var s = client.GetStream();
                await Send(s, new
                {
                    type = "offer",
                    folder = new { mode = "new", name = "Cut" },
                    files = new[] { new { name = "one.txt", size = 3 }, new { name = "two.txt", size = 1000 } },
                });
                Assert.Equal("accept", (string?)(await Receive(s))["type"]);
                await s.WriteAsync(Encoding.ASCII.GetBytes("abc"));
                await s.WriteAsync(new byte[10]);
                await s.FlushAsync();
                await Task.Delay(200);
            }

            var session = await ui.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SessionState.Failed, session.State);
            Assert.True(File.Exists(Path.Combine(_base, "Cut", "one.txt")));
            Assert.False(File.Exists(Path.Combine(_base, "Cut", "two.txt")));

            // The receiver is free again.
            var again = await manager.OfferAsync(new TransferOffer { Files = { new OfferedFile { Name = "x", Size = 1 } } }, CancellationToken.None);
            Assert.True(again.Accepted);
        }

        private const string RemoteKey = "0123456789abcdef0123456789abcdef";

        [Fact]
        public async Task Remote_port_requires_the_key()
        {
            var ui = new FakeUi();
            var manager = new TransferManager(() => _settings, ui);
            var port = FreePort();
            await using var web = new WebServer(manager, () => _settings, 0, port, () => RemoteKey);
            await web.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            // The page itself is public; the API is not.
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/info")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/info?k=ffffffffffffffffffffffffffffffff")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/offer", new { files = new[] { new { name = "a", size = 1 } } })).StatusCode);

            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/info");
            req.Headers.Add(WebServer.KeyHeader, RemoteKey);
            var info = await (await http.SendAsync(req)).Content.ReadFromJsonAsync<JsonObject>();
            Assert.True((bool?)info!["remote"]);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/folders?k={RemoteKey}")).StatusCode);

            // The installed home-screen app keeps the key.
            var manifest = await http.GetStringAsync($"/manifest.webmanifest?k={RemoteKey}");
            Assert.Contains($"\"start_url\": \"/?k={RemoteKey}\"", manifest);
            Assert.Equal(0, ui.Asked); // the refused offer never reached the PC's screen
        }

        [Fact]
        public async Task Remote_port_refuses_everything_while_the_tunnel_is_off()
        {
            var manager = new TransferManager(() => _settings, new FakeUi());
            var port = FreePort();
            await using var web = new WebServer(manager, () => _settings, 0, port, () => null);
            await web.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/api/info?k={RemoteKey}")).StatusCode);
        }

        [Fact]
        public async Task Remote_polled_offer_and_pieces_with_a_resent_piece()
        {
            var ui = new FakeUi();
            var manager = new TransferManager(() => _settings, ui);
            var port = FreePort();
            await using var web = new WebServer(manager, () => _settings, 0, port, () => RemoteKey);
            await web.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            http.DefaultRequestHeaders.Add(WebServer.KeyHeader, RemoteKey);

            var data = new byte[250_000];
            new Random(7).NextBytes(data);
            var empty = Array.Empty<byte>();

            var start = await (await http.PostAsJsonAsync("/api/offer?wait=0", new
            {
                folder = new { mode = "new", name = "From the road" },
                files = new[] { new { name = "video.mov", size = data.Length }, new { name = "empty.txt", size = 0 } },
            })).Content.ReadFromJsonAsync<JsonObject>();
            Assert.True((bool?)start!["pending"]);

            var answer = await http.GetFromJsonAsync<JsonObject>($"/api/offer/{(string)start["ticket"]!}");
            Assert.True((bool?)answer!["accepted"]);
            var id = (string)answer["id"]!;

            async Task<HttpResponseMessage> Piece(int index, int offset, byte[] source, int length) =>
                await http.PostAsync($"/api/upload/{id}/{index}?offset={offset}", new ByteArrayContent(source, offset, length));

            Assert.Equal(HttpStatusCode.OK, (await Piece(0, 0, data, 100_000)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Piece(0, 100_000, data, 100_000)).StatusCode);
            // The answer to the second piece got lost: the phone sends it again.
            Assert.Equal(HttpStatusCode.OK, (await Piece(0, 100_000, data, 100_000)).StatusCode);
            // A gap is refused.
            Assert.NotEqual(HttpStatusCode.OK, (await Piece(0, 240_000, data, 10_000)).StatusCode);
            var last = await (await Piece(0, 200_000, data, 50_000)).Content.ReadFromJsonAsync<JsonObject>();
            Assert.True((bool?)last!["complete"]);
            // ... and the last piece once more.
            Assert.Equal(HttpStatusCode.OK, (await Piece(0, 200_000, data, 50_000)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Piece(1, 0, empty, 0)).StatusCode);

            var fin = await (await http.PostAsync($"/api/finish/{id}", null)).Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(2, (int?)fin!["saved"]);
            var session = await ui.Finished.Task;
            Assert.Equal("Internet", session.Offer.Transport);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_base, "From the road", "video.mov")));
            Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(_base, "From the road", "empty.txt")));
        }

        [Fact]
        public async Task Cancelled_polled_offer_frees_the_receiver()
        {
            var ui = new SlowUi();
            var manager = new TransferManager(() => _settings, ui);
            var port = FreePort();
            await using var web = new WebServer(manager, () => _settings, port);
            await web.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var start = await (await http.PostAsJsonAsync("/api/offer?wait=0", new { files = new[] { new { name = "a", size = 1 } } }))
                .Content.ReadFromJsonAsync<JsonObject>();
            var ticket = (string)start!["ticket"]!;
            await ui.Asked.Task;
            Assert.True(manager.IsBusy);

            await http.PostAsync($"/api/offer/{ticket}/cancel", null);
            await ui.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 50 && manager.IsBusy; i++) await Task.Delay(20);
            Assert.False(manager.IsBusy);
        }

        [Fact]
        public async Task A_drop_that_stops_getting_data_gives_up()
        {
            var old = ReceiveSession.IdleTimeout;
            ReceiveSession.IdleTimeout = TimeSpan.FromMilliseconds(600);
            try
            {
                var ui = new FakeUi();
                var manager = new TransferManager(() => _settings, ui);
                var r = await manager.OfferAsync(new TransferOffer { Files = { new OfferedFile { Name = "big.bin", Size = 10 } } }, CancellationToken.None);
                await r.Session!.ReceiveChunkAsync(0, 0, 4, new MemoryStream(new byte[4]), CancellationToken.None);

                var finished = await ui.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(SessionState.Failed, finished.State);
                Assert.False(manager.IsBusy);
                Assert.Empty(Directory.GetFiles(finished.FolderPath)); // the half file is gone
            }
            finally
            {
                ReceiveSession.IdleTimeout = old;
            }
        }

        private sealed class SlowUi : IReceiverUi
        {
            public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<bool> ConfirmAsync(TransferOffer offer, string folderName, CancellationToken ct)
            {
                Asked.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Closed.TrySetResult(); }
                return false;
            }

            public void TransferStarted(ReceiveSession session) { }
            public void TransferFinished(ReceiveSession session) { }
        }

        [Fact]
        public async Task Http_full_transfer()
        {
            var ui = new FakeUi();
            var manager = new TransferManager(() => _settings, ui);
            var port = FreePort();
            await using var web = new WebServer(manager, () => _settings, port);
            await web.StartAsync();

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var page = await http.GetStringAsync("/");
            Assert.Contains("PocketDrop", page);
            var icon = await http.GetAsync("/icon.png");
            Assert.Equal(HttpStatusCode.OK, icon.StatusCode);

            var info = await http.GetFromJsonAsync<JsonObject>("/api/info");
            Assert.Equal("TEST-PC", (string?)info!["name"]);

            var data = Encoding.UTF8.GetBytes("photo bytes");
            var offer = await http.PostAsJsonAsync("/api/offer", new
            {
                folder = new { mode = "new", name = "" },
                files = new[] { new { name = "IMG_0001.HEIC", size = data.Length } },
            });
            var o = await offer.Content.ReadFromJsonAsync<JsonObject>();
            Assert.True((bool?)o!["accepted"]);
            var id = (string)o["id"]!;
            var folderName = (string)o["folder"]!;
            Assert.StartsWith("PocketDrop ", folderName);

            var up = await http.PostAsync($"/api/upload/{id}/0", new ByteArrayContent(data));
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);

            var fin = await (await http.PostAsync($"/api/finish/{id}", null)).Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(1, (int?)fin!["saved"]);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_base, folderName, "IMG_0001.HEIC")));

            var folders = await http.GetFromJsonAsync<JsonObject>("/api/folders");
            Assert.Single(folders!["folders"]!.AsArray());
        }

        [Fact]
        public async Task Busy_while_another_transfer_runs()
        {
            var ui = new FakeUi();
            var manager = new TransferManager(() => _settings, ui);
            var first = await manager.OfferAsync(new TransferOffer { Files = { new OfferedFile { Name = "a", Size = 1 } } }, CancellationToken.None);
            Assert.True(first.Accepted);
            var second = await manager.OfferAsync(new TransferOffer { Files = { new OfferedFile { Name = "b", Size = 1 } } }, CancellationToken.None);
            Assert.False(second.Accepted);
            Assert.Equal("busy", second.ReasonCode);
        }

        [Theory]
        [InlineData("../../x.txt", "x.txt")]
        [InlineData("C:\\Windows\\win.ini", "win.ini")]
        [InlineData("..", "fallback")]
        [InlineData("   ", "fallback")]
        [InlineData("CON", "_CON")]
        [InlineData("a<b>c?.jpg", "a_b_c_.jpg")]
        [InlineData("name.  ", "name")]
        public void SafeNames_clean(string input, string expected) =>
            Assert.Equal(expected, SafeNames.Clean(input, "fallback"));
    }
}
