using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PocketDrop.Core
{
    /// <summary>The sub-folders of the receive folder: one per drop, or reused on request.</summary>
    public static class ReceiveFolders
    {
        public static List<FolderInfo> List(string baseFolder)
        {
            try
            {
                if (!Directory.Exists(baseFolder)) return new List<FolderInfo>();
                return new DirectoryInfo(baseFolder)
                    .EnumerateDirectories()
                    .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .Select(d => new FolderInfo
                    {
                        Name = d.Name,
                        Files = SafeCount(d),
                        Modified = d.LastWriteTimeUtc,
                    })
                    .OrderByDescending(f => f.Modified)
                    .Take(200)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Error("Listing receive folders failed", ex);
                return new List<FolderInfo>();
            }
        }

        private static int SafeCount(DirectoryInfo d)
        {
            try { return d.EnumerateFiles().Count(); }
            catch { return 0; }
        }

        public static string DefaultNewName() => $"{AppInfo.AppName} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";

        /// <summary>
        /// Works out the folder name the drop will land in, without creating anything yet.
        /// </summary>
        public static string ResolveName(string baseFolder, FolderChoice choice)
        {
            var existing = string.Equals(choice.Mode, "existing", StringComparison.OrdinalIgnoreCase);
            var clean = SafeNames.Clean(choice.Name, DefaultNewName(), 80);
            if (existing) return clean;
            return SafeNames.UniqueDirectoryName(baseFolder, clean);
        }
    }
}
