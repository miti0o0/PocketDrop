using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PocketDrop.Core;

namespace PocketDrop.Transport
{
    public enum RemoteState { Off, Downloading, Connecting, Ready, Reconnecting }

    /// <summary>
    /// "Send from anywhere": a free Cloudflare quick tunnel (no account) that gives the PC a public
    /// <c>https://….trycloudflare.com</c> address forwarding to the loopback-only web port.
    /// The link also carries a random secret key that the web server checks on every API call.
    /// <para>
    /// Uses Cloudflare's own <c>cloudflared.exe</c>, downloaded once from Cloudflare's GitHub releases
    /// (checksum verified), or taken from next to PocketDrop.exe when it is bundled there.
    /// The public address is new every time the tunnel starts.
    /// </para>
    /// </summary>
    public sealed partial class CloudflareTunnel : IAsyncDisposable
    {
        private const string ReleaseApi = "https://api.github.com/repos/cloudflare/cloudflared/releases/latest";
        private const string AssetName = "cloudflared-windows-amd64.exe";
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RefreshBinaryAfter = TimeSpan.FromDays(60);

        private readonly int _localPort;
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private Process? _process;

        public CloudflareTunnel(int localPort)
        {
            _localPort = localPort;
        }

        public RemoteState State { get; private set; }
        /// <summary>0..1 while <see cref="RemoteState.Downloading"/>.</summary>
        public double DownloadProgress { get; private set; }
        /// <summary>Secret for this run; null while stopped.</summary>
        public string? Key { get; private set; }
        public string? PublicUrl { get; private set; }
        /// <summary>The link for the phone: public address plus key.</summary>
        public string? Link => PublicUrl == null || Key == null ? null : $"{PublicUrl}/?k={Key}";
        public string? Error { get; private set; }

        public event Action? Changed;

        public static string ToolDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.AppName, "cloudflared");

        public void Start()
        {
            lock (_gate)
            {
                if (_loop != null) return;
                Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                Error = null;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => RunAsync(token));
            }
        }

        public async Task StopAsync()
        {
            Task? loop;
            lock (_gate)
            {
                loop = _loop;
                _loop = null;
                _cts?.Cancel();
            }
            Kill();
            if (loop != null)
            {
                try { await loop; } catch { }
            }
            _cts?.Dispose();
            _cts = null;
            Key = null;
            PublicUrl = null;
            Error = null;
            Set(RemoteState.Off);
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        /// <summary>For screenshots: looks connected without starting anything.</summary>
        internal void ShowForPreview(string publicUrl, string key)
        {
            PublicUrl = publicUrl;
            Key = key;
            State = RemoteState.Ready;
        }

        private void Set(RemoteState state, string? error = null)
        {
            State = state;
            if (error != null || state is RemoteState.Ready or RemoteState.Off) Error = error;
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Error("Tunnel status handler failed", ex); }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var delay = TimeSpan.FromSeconds(5);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var exe = await EnsureBinaryAsync(ct);
                    Set(RemoteState.Connecting);
                    var connected = await RunOnceAsync(exe, ct);
                    if (connected) delay = TimeSpan.FromSeconds(5);
                    if (ct.IsCancellationRequested) break;
                    Set(RemoteState.Reconnecting, Loc.T("remote.lost"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("Tunnel failed", ex);
                    Set(RemoteState.Reconnecting, ex is HttpRequestException ? Loc.T("remote.noInternet") : ex.Message);
                }

                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }

        [GeneratedRegex(@"https://(?!api\.)[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
        private static partial Regex QuickTunnelUrl();

        /// <returns>true if the tunnel was up at some point.</returns>
        private async Task<bool> RunOnceAsync(string exe, CancellationToken ct)
        {
            Directory.CreateDirectory(ToolDir);
            // An empty config, so a config.yml the user has for their own tunnels doesn't interfere.
            var config = Path.Combine(ToolDir, "pocketdrop-empty.yml");
            if (!File.Exists(config)) await File.WriteAllTextAsync(config, "# Used by PocketDrop for its quick tunnel.\n", ct);

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = ToolDir,
            };
            foreach (var a in new[] { "tunnel", "--no-autoupdate", "--config", config, "--url", $"http://127.0.0.1:{_localPort}" })
                psi.ArgumentList.Add(a);

            var url = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? lastError = null;

            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                var m = QuickTunnelUrl().Match(line);
                if (m.Success) url.TrySetResult(m.Value.ToLowerInvariant());
                if (line.Contains("Registered tunnel connection", StringComparison.OrdinalIgnoreCase)) registered.TrySetResult();
                if (line.Contains(" ERR ", StringComparison.Ordinal))
                {
                    lastError = line;
                    Log.Info("cloudflared: " + line.Trim());
                }
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.Start();
            ChildProcessJob.Add(process);
            lock (_gate) _process = process;
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            try
            {
                var exited = process.WaitForExitAsync(ct);
                var ready = Task.WhenAll(url.Task, registered.Task);
                var first = await Task.WhenAny(ready, exited, Task.Delay(ConnectTimeout, ct));
                ct.ThrowIfCancellationRequested();

                if (first == ready)
                {
                    // Give the new name a moment to reach DNS before showing it, so the phone doesn't cache a miss.
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    PublicUrl = url.Task.Result;
                    Log.Info($"Tunnel ready: {PublicUrl}");
                    Set(RemoteState.Ready);
                    await exited;
                    Log.Info($"cloudflared exited ({process.ExitCode})");
                    return true;
                }
                if (first == exited)
                    throw new InvalidOperationException(lastError != null ? Trim(lastError) : $"cloudflared stopped (code {process.ExitCode}).");
                throw new TimeoutException(Loc.T("remote.timeout"));
            }
            finally
            {
                PublicUrl = null;
                lock (_gate) _process = null;
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
            }
        }

        private static string Trim(string line)
        {
            var i = line.IndexOf(" ERR ", StringComparison.Ordinal);
            var text = i >= 0 ? line[(i + 5)..].Trim() : line.Trim();
            return text.Length > 160 ? text[..160] + "…" : text;
        }

        private void Kill()
        {
            Process? p;
            lock (_gate) p = _process;
            try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }

        // ---- cloudflared.exe ------------------------------------------------------------------

        private async Task<string> EnsureBinaryAsync(CancellationToken ct)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");
            if (File.Exists(bundled)) return bundled;

            var exe = Path.Combine(ToolDir, "cloudflared.exe");
            if (File.Exists(exe))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(exe) < RefreshBinaryAfter) return exe;
                // Cloudflare retires old versions; fetch a fresh copy but keep using the old one if that fails.
                try { await DownloadAsync(exe, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Error("cloudflared refresh failed", ex); }
                return exe;
            }

            await DownloadAsync(exe, ct);
            return exe;
        }

        private async Task DownloadAsync(string target, CancellationToken ct)
        {
            Set(RemoteState.Downloading);
            DownloadProgress = 0;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.AppName, UpdateService.CurrentVersion.ToString(3)));

            string downloadUrl;
            long size;
            string? sha256 = null;
            using (var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                JsonElement? found = null;
                foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                    if (asset.GetProperty("name").GetString() == AssetName) { found = asset; break; }
                if (found is not { } a) throw new InvalidDataException("cloudflared for Windows was not found in Cloudflare's release.");
                downloadUrl = a.GetProperty("browser_download_url").GetString()!;
                size = a.GetProperty("size").GetInt64();
                if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String &&
                    d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha256 = digest[7..].ToLowerInvariant();
            }

            Directory.CreateDirectory(ToolDir);
            var tmp = target + ".download";
            using (var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                using var hash = SHA256.Create();
                long done = 0;
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    var buffer = new byte[1 << 16];
                    int read;
                    var lastReport = 0.0;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        hash.TransformBlock(buffer, 0, read, null, 0);
                        done += read;
                        DownloadProgress = size > 0 ? (double)done / size : 0;
                        if (DownloadProgress - lastReport >= 0.02)
                        {
                            lastReport = DownloadProgress;
                            Set(RemoteState.Downloading);
                        }
                    }
                }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                var actual = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
                if ((size > 0 && done != size) || (sha256 != null && actual != sha256))
                {
                    File.Delete(tmp);
                    throw new InvalidDataException("The downloaded cloudflared is damaged (checksum mismatch).");
                }
            }
            File.Move(tmp, target, overwrite: true);
            Log.Info($"Downloaded cloudflared ({size} bytes{(sha256 != null ? ", sha256 verified" : "")})");
        }
    }
}
