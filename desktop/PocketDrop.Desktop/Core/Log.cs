using System;
using System.IO;

namespace PocketDrop.Core
{
    /// <summary>Tiny file logger: %APPDATA%\PocketDrop\pocketdrop.log (rotated at 1 MB).</summary>
    public static class Log
    {
        private static readonly object Gate = new();
        private static string LogPath => Path.Combine(Settings.ConfigDir, "pocketdrop.log");

        public static void Info(string message) => Write("INFO ", message);
        public static void Error(string message, Exception? ex = null) =>
            Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.ConfigDir);
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > 1_000_000)
                        File.Move(LogPath, LogPath + ".old", overwrite: true);
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }
}
