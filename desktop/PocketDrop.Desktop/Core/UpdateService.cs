using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PocketDrop.Core
{
    public sealed record UpdateInfo(Version Version, string DownloadUrl, long Size, string? Sha256, string PageUrl);

    /// <summary>
    /// Checks GitHub Releases of the repository this build came from (see <see cref="AppInfo.UpdateRepo"/>)
    /// for a newer <c>PocketDrop-Setup-x.y.z.exe</c>, downloads it and runs it silently.
    /// </summary>
    public sealed class UpdateService
    {
        private readonly HttpClient _http;
        private readonly string _latestReleaseUrl;

        public UpdateService(string latestReleaseUrl, HttpMessageHandler? handler = null)
        {
            _latestReleaseUrl = latestReleaseUrl;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromMinutes(30);
            // GitHub requires a User-Agent; keep it generic.
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.AppName, CurrentVersion.ToString(3)));
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        /// <summary>null when this build doesn't know which repository it came from.</summary>
        public static UpdateService? ForThisBuild() =>
            AppInfo.UpdateRepo == null ? null : new UpdateService($"https://api.github.com/repos/{AppInfo.UpdateRepo}/releases/latest");

        public static Version CurrentVersion => Normalize(
            (Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly).GetName().Version ?? new Version(0, 0, 0));

        /// <summary>Installed with the installer (can update in place) rather than the portable zip.</summary>
        public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

        public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

        /// <summary>"v1.2.3", "1.2.3", "v1.2.3-beta" → 1.2.3</summary>
        public static Version? ParseTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var t = tag.Trim().TrimStart('v', 'V');
            var dash = t.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) t = t[..dash];
            return Version.TryParse(t, out var v) ? Normalize(v) : null;
        }

        /// <returns>The newer release, or null if this is the latest (or there is no release yet).</returns>
        public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
        {
            using var response = await _http.GetAsync(_latestReleaseUrl, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            var version = ParseTag(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null);
            if (version == null || version <= Normalize(current)) return null;

            var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
            if (!root.TryGetProperty("assets", out var assets)) return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith(AppInfo.AppName + "-Setup-", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? sha = null;
                if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
                {
                    var d = digest.GetString() ?? "";
                    if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = d[7..].ToLowerInvariant();
                }
                return new UpdateInfo(version, asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64(), sha, page);
            }
            return null;
        }

        /// <summary>Downloads the installer to %TEMP% and checks its size and SHA-256 (when GitHub provides one).</summary>
        public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
        {
            var dir = Path.Combine(Path.GetTempPath(), AppInfo.AppName + "-update");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{AppInfo.AppName}-Setup-{update.Version.ToString(3)}.exe");

            using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;

            using var sha = SHA256.Create();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                if (update.Size > 0 && done != update.Size)
                    throw new InvalidDataException($"Download incomplete ({done} of {update.Size} bytes).");
            }

            var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (update.Sha256 != null && hash != update.Sha256)
            {
                File.Delete(path);
                throw new InvalidDataException("The downloaded installer is corrupted (checksum mismatch).");
            }
            return path;
        }

        /// <summary>
        /// Runs the installer without its wizard. It keeps the user's settings, replaces the app and starts it again.
        /// Windows asks once for permission because the installer adds the firewall rule.
        /// </summary>
        public static void LaunchInstaller(string path)
        {
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /UPDATE=1")
            {
                UseShellExecute = true,
            });
        }
    }
}
