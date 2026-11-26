using System;
using System.IO;
using System.Text.Json;

namespace PocketDrop.Core
{
    public sealed class Settings
    {
        public string BaseFolder { get; set; } = DefaultBaseFolder;
        public bool BluetoothEnabled { get; set; }
        public bool WifiEnabled { get; set; } = true;
        /// <summary>Send from anywhere (mobile data) through a Cloudflare tunnel.</summary>
        public bool RemoteEnabled { get; set; }
        public bool OpenExplorerWhenDone { get; set; } = true;
        public bool StartWithWindows { get; set; } = true;
        /// <summary>"auto", "en" or "bg".</summary>
        public string Language { get; set; } = "auto";
        public string DeviceName { get; set; } = Environment.MachineName;
        public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
        public bool SetupDone { get; set; }

        public static string DefaultBaseFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppInfo.AppName);

        public static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.AppName);

        public static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static Settings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath));
                    if (s != null)
                    {
                        if (string.IsNullOrWhiteSpace(s.BaseFolder)) s.BaseFolder = DefaultBaseFolder;
                        if (string.IsNullOrWhiteSpace(s.DeviceName)) s.DeviceName = Environment.MachineName;
                        if (string.IsNullOrWhiteSpace(s.DeviceId)) s.DeviceId = Guid.NewGuid().ToString("N");
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read settings, using defaults", ex);
            }
            return new Settings();
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDir);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
    }
}
