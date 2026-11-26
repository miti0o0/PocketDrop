using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using PocketDrop.Core;

namespace PocketDrop.Transport
{
    /// <summary>
    /// Serves the browser sender (for iPhone and any phone without the app) and its small HTTP API.
    /// <para>
    /// Two listeners: the home network (<see cref="AppInfo.HttpPort"/>, any address) and, for sending from anywhere,
    /// a loopback-only port that only the Cloudflare tunnel connects to. Every API call on the loopback port must
    /// carry the secret key from the link, so knowing the public address alone is not enough.
    /// </para>
    /// </summary>
    public sealed partial class WebServer : IAsyncDisposable
    {
        public const string KeyHeader = "X-PocketDrop-Key";
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
        private static readonly TimeSpan PollWait = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan AbandonAfter = TimeSpan.FromSeconds(50);

        private readonly TransferManager _manager;
        private readonly Func<Settings> _settings;
        private readonly int _lanPort;
        private readonly int _remotePort;
        private readonly Func<string?> _remoteKey;
        private readonly ConcurrentDictionary<string, PendingOffer> _pending = new();
        private Timer? _sweeper;
        private WebApplication? _app;

        private sealed class PendingOffer
        {
            public required Task<OfferResult> Task;
            public required CancellationTokenSource Cts;
            public long LastSeenTicks = DateTime.UtcNow.Ticks;
        }

        /// <param name="lanPort">Port for the home network, or 0 for none.</param>
        /// <param name="remotePort">Loopback port for the tunnel, or 0 for none.</param>
        /// <param name="remoteKey">The current secret key (null = refuse everything on the remote port).</param>
        public WebServer(TransferManager manager, Func<Settings> settings, int lanPort = AppInfo.HttpPort,
            int remotePort = 0, Func<string?>? remoteKey = null)
        {
            _manager = manager;
            _settings = settings;
            _lanPort = lanPort;
            _remotePort = remotePort;
            _remoteKey = remoteKey ?? (() => null);
        }

        public int LanPort => _lanPort;
        public int RemotePort => _remotePort;

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                if (_lanPort > 0) o.ListenAnyIP(_lanPort);
                if (_remotePort > 0) o.Listen(IPAddress.Loopback, _remotePort);
                o.Limits.MaxRequestBodySize = null;
                o.Limits.MinRequestBodyDataRate = new MinDataRate(1, TimeSpan.FromSeconds(60));
            });

            var app = builder.Build();

            app.Use(async (ctx, next) =>
            {
                if (IsRemote(ctx))
                {
                    ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
                    if (ctx.Request.Path.StartsWithSegments("/api") && !HasValidKey(ctx))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await ctx.Response.WriteAsJsonAsync(new { error = "key" });
                        return;
                    }
                }
                await next();
            });

            app.MapGet("/", () => Page("index.html", "text/html; charset=utf-8"));
            // Icons and manifest for "Add to Home Screen" (iPhone) and installable web app (Android).
            app.MapGet("/manifest.webmanifest", (HttpContext ctx) => Manifest(ctx));
            foreach (var icon in new[] { "icon.png", "icon-192.png", "icon-512.png", "apple-touch-icon.png" })
                app.MapGet("/" + icon, () => Page(icon, "image/png"));
            // iOS also probes these names on its own.
            app.MapGet("/apple-touch-icon-precomposed.png", () => Page("apple-touch-icon.png", "image/png"));
            app.MapGet("/apple-touch-icon-180x180.png", () => Page("apple-touch-icon.png", "image/png"));
            app.MapGet("/favicon.ico", () => Page("icon-192.png", "image/png"));

            app.MapGet("/api/info", (HttpContext ctx) => Results.Json(new
            {
                app = AppInfo.AppName,
                name = _settings().DeviceName,
                v = AppInfo.ProtocolVersion,
                remote = IsRemote(ctx),
            }));

            app.MapGet("/api/folders", () => Results.Json(new { folders = ReceiveFolders.List(_manager.BaseFolder) }));

            app.MapPost("/api/offer", (Func<HttpContext, Task<IResult>>)OfferAsync);
            app.MapGet("/api/offer/{ticket}", (Func<HttpContext, string, Task<IResult>>)PollOfferAsync);
            app.MapPost("/api/offer/{ticket}/cancel", (string ticket) =>
            {
                CancelPending(ticket);
                return Results.Json(new { ok = true });
            });
            app.MapPost("/api/upload/{id}/{index:int}", UploadAsync);
            app.MapPost("/api/finish/{id}", Finish);
            app.MapPost("/api/cancel/{id}", (string id) =>
            {
                _manager.Find(id)?.Fail("Cancelled on the phone.");
                return Results.Json(new { ok = true });
            });

            await app.StartAsync();
            _app = app;
            _sweeper = new Timer(_ => SweepPending(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            Log.Info($"HTTP listening on {(_lanPort > 0 ? _lanPort.ToString() : "-")} (remote {(_remotePort > 0 ? "127.0.0.1:" + _remotePort : "off")})");
        }

        private bool IsRemote(HttpContext ctx) => _remotePort > 0 && ctx.Connection.LocalPort == _remotePort;

        private bool HasValidKey(HttpContext ctx)
        {
            var expected = _remoteKey();
            if (string.IsNullOrEmpty(expected)) return false;
            string? given = ctx.Request.Headers[KeyHeader];
            if (string.IsNullOrEmpty(given)) given = ctx.Request.Query["k"];
            if (string.IsNullOrEmpty(given)) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
        }

        // ---- Offers --------------------------------------------------------------------------
        //
        // The PC waits up to two minutes for the user to accept. Cloudflare drops requests that take longer than
        // 100 seconds, so the browser sends the offer with ?wait=0 and then polls for the answer.

        private async Task<IResult> OfferAsync(HttpContext ctx)
        {
            TransferOffer? offer;
            try { offer = await JsonSerializer.DeserializeAsync<TransferOffer>(ctx.Request.Body, JsonOptions, ctx.RequestAborted); }
            catch (JsonException) { offer = null; }
            if (offer == null) return Results.Json(new { accepted = false, reason = "invalid" }, statusCode: 400);

            offer.Transport = IsRemote(ctx) ? "Internet" : "Browser";
            offer.Device = string.IsNullOrWhiteSpace(offer.Device) ? GuessDevice(ctx) : offer.Device.Trim();
            if (offer.Device.Length > 60) offer.Device = offer.Device[..60];

            if (ctx.Request.Query["wait"] == "0")
            {
                var ticket = Guid.NewGuid().ToString("N");
                var cts = new CancellationTokenSource();
                _pending[ticket] = new PendingOffer { Task = _manager.OfferAsync(offer, cts.Token), Cts = cts };
                return Results.Json(new { pending = true, ticket });
            }

            return OfferJson(await _manager.OfferAsync(offer, ctx.RequestAborted));
        }

        private async Task<IResult> PollOfferAsync(HttpContext ctx, string ticket)
        {
            if (!_pending.TryGetValue(ticket, out var p)) return Results.Json(new { accepted = false, reason = "timeout" });
            Interlocked.Exchange(ref p.LastSeenTicks, DateTime.UtcNow.Ticks);

            try { await p.Task.WaitAsync(PollWait, ctx.RequestAborted); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
            catch (Exception) when (p.Task.IsCompleted) { /* reported below */ }

            Interlocked.Exchange(ref p.LastSeenTicks, DateTime.UtcNow.Ticks);
            if (!p.Task.IsCompleted || ctx.RequestAborted.IsCancellationRequested) return Results.Json(new { pending = true });

            _pending.TryRemove(ticket, out _);
            p.Cts.Dispose();
            OfferResult result;
            try { result = await p.Task; }
            catch (Exception ex)
            {
                Log.Error("Offer failed", ex);
                return Results.Json(new { accepted = false, reason = "failed" });
            }
            return OfferJson(result);
        }

        private void CancelPending(string ticket)
        {
            if (!_pending.TryRemove(ticket, out var p)) return;
            try { p.Cts.Cancel(); } catch (ObjectDisposedException) { }
            // Accepted just before the phone gave up: nobody will send the files.
            _ = p.Task.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result.Session is { } s) s.Fail("Cancelled on the phone.");
                p.Cts.Dispose();
            }, TaskScheduler.Default);
        }

        /// <summary>The phone stopped asking (closed the page, lost the signal): withdraw the question on the PC.</summary>
        private void SweepPending()
        {
            var now = DateTime.UtcNow.Ticks;
            foreach (var (ticket, p) in _pending)
            {
                if (now - Interlocked.Read(ref p.LastSeenTicks) > AbandonAfter.Ticks)
                {
                    Log.Info("The phone stopped waiting for an answer");
                    CancelPending(ticket);
                }
            }
        }

        private static IResult OfferJson(OfferResult result)
        {
            if (!result.Accepted) return Results.Json(new { accepted = false, reason = result.ReasonCode });
            var s = result.Session!;
            return Results.Json(new { accepted = true, id = s.Id, folder = s.FolderName });
        }

        // ---- Files ---------------------------------------------------------------------------

        private async Task<IResult> UploadAsync(HttpContext ctx, string id, int index)
        {
            var session = _manager.Find(id);
            if (session == null) return Results.Json(new { ok = false, error = "cancelled" }, statusCode: 409);
            if (index < 0 || index >= session.Offer.Files.Count) return Results.BadRequest();

            // Pieces: /api/upload/{id}/{index}?offset=N with a Content-Length. Used by the browser for every file.
            if (ctx.Request.Query.TryGetValue("offset", out var offsetText))
            {
                if (!long.TryParse(offsetText, out var offset) || ctx.Request.ContentLength is not long length)
                    return Results.BadRequest();
                try
                {
                    var complete = await session.ReceiveChunkAsync(index, offset, length, ctx.Request.Body, ctx.RequestAborted,
                        () => ctx.Abort());
                    return Results.Json(new { ok = true, complete });
                }
                catch (Exception ex)
                {
                    Log.Info($"Piece {index}@{offset} failed: {ex.Message}");
                    if (session.State != SessionState.Receiving)
                        return Results.Json(new { ok = false, error = session.State == SessionState.Cancelled ? "cancelled" : "failed" },
                            statusCode: 409);
                    // The phone may send the piece again; the session gives up by itself if it never does.
                    return Results.Json(new { ok = false, error = "retry" }, statusCode: 503);
                }
            }

            try
            {
                await session.ReceiveFileAsync(index, ctx.Request.Body, ctx.RequestAborted, () => ctx.Abort());
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                if (session.State == SessionState.Receiving)
                    session.Fail(ctx.RequestAborted.IsCancellationRequested ? "The phone disconnected." : ex.Message);
                Log.Info($"Upload {index} failed: {ex.Message}");
                return Results.Json(new { ok = false, error = session.State == SessionState.Cancelled ? "cancelled" : "failed" },
                    statusCode: 409);
            }
        }

        private IResult Finish(string id)
        {
            var session = _manager.Find(id);
            if (session == null) return Results.Json(new { ok = false, error = "cancelled" }, statusCode: 409);
            if (!session.AllDone) return Results.Json(new { ok = false, error = "incomplete" }, statusCode: 400);
            session.Complete();
            return Results.Json(new { ok = true, saved = session.SavedCount, folder = session.FolderName });
        }

        private static string GuessDevice(HttpContext ctx)
        {
            var ua = ctx.Request.Headers.UserAgent.ToString();
            if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "iPhone";
            if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iPad";
            if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
            var ip = ctx.Connection.RemoteIpAddress;
            return ip == null || IPAddress.IsLoopback(ip) ? "Browser" : ip.ToString();
        }

        [GeneratedRegex("^[0-9a-f]{32}$")]
        private static partial Regex KeyFormat();

        /// <summary>
        /// Opened through a link with a key, the installed home-screen app must start with that key too,
        /// so the manifest's start_url carries it.
        /// </summary>
        private static IResult Manifest(HttpContext ctx)
        {
            string? key = ctx.Request.Query["k"];
            if (string.IsNullOrEmpty(key) || !KeyFormat().IsMatch(key))
                return Page("manifest.webmanifest", "application/manifest+json");

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Web/manifest.webmanifest");
            if (stream == null) return Results.NotFound();
            var json = new StreamReader(stream).ReadToEnd().Replace("\"start_url\": \"/\"", $"\"start_url\": \"/?k={key}\"");
            return Results.Text(json, "application/manifest+json");
        }

        private static IResult Page(string name, string contentType)
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Web/" + name);
            return stream == null ? Results.NotFound() : Results.Stream(stream, contentType);
        }

        public async ValueTask DisposeAsync()
        {
            _sweeper?.Dispose();
            foreach (var ticket in _pending.Keys) CancelPending(ticket);
            if (_app != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _app.StopAsync(cts.Token); } catch { }
                await _app.DisposeAsync();
            }
        }
    }
}
