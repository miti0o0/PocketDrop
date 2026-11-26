using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PocketDrop.Core
{
    /// <summary>
    /// Turns names coming from the phone into safe single path segments, so that nothing can be
    /// written outside the receive folder (no "..", no separators, no reserved device names).
    /// </summary>
    public static class SafeNames
    {
        private static readonly string[] Reserved =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static readonly char[] Invalid =
            Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();

        /// <summary>Returns a safe file/folder name, or <paramref name="fallback"/> if nothing usable is left.</summary>
        public static string Clean(string? name, string fallback, int maxLength = 120)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;

            // Keep only the last segment in case the client sent a path.
            var last = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

            var sb = new StringBuilder(last.Length);
            foreach (var ch in last)
            {
                if (char.IsControl(ch) || Array.IndexOf(Invalid, ch) >= 0) sb.Append('_');
                else sb.Append(ch);
            }

            var result = sb.ToString().Trim().TrimEnd('.', ' ');
            while (result.StartsWith("..", StringComparison.Ordinal)) result = result[1..];
            result = result.Trim();

            if (result.Length == 0 || result == ".") return fallback;

            var stem = Path.GetFileNameWithoutExtension(result);
            if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) result = "_" + result;

            if (result.Length > maxLength)
            {
                var ext = Path.GetExtension(result);
                if (ext.Length > 20) ext = "";
                result = result[..(maxLength - ext.Length)].TrimEnd('.', ' ') + ext;
            }
            return result;
        }

        /// <summary>"photo.jpg" → "photo (1).jpg" … until the path is free.</summary>
        public static string UniqueFilePath(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;

            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            for (var i = 1; ; i++)
            {
                path = Path.Combine(directory, $"{stem} ({i}){ext}");
                if (!File.Exists(path) && !Directory.Exists(path)) return path;
            }
        }

        /// <summary>"Trip" → "Trip (2)" … until the folder name is free.</summary>
        public static string UniqueDirectoryName(string parent, string name)
        {
            if (!Directory.Exists(Path.Combine(parent, name)) && !File.Exists(Path.Combine(parent, name))) return name;
            for (var i = 2; ; i++)
            {
                var candidate = $"{name} ({i})";
                if (!Directory.Exists(Path.Combine(parent, candidate)) && !File.Exists(Path.Combine(parent, candidate)))
                    return candidate;
            }
        }

        /// <summary>True when <paramref name="path"/> is inside <paramref name="root"/>.</summary>
        public static bool IsInside(string root, string path)
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var p = Path.GetFullPath(path);
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
    }
}
