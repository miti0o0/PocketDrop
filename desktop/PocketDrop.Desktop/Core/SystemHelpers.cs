using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace PocketDrop.Core
{
    public static class SystemHelpers
    {
        private static readonly string[] VirtualAdapterHints =
            { "virtual", "vmware", "vbox", "hyper-v", "vethernet", "wsl", "docker", "loopback", "tap-", "tailscale", "zerotier", "bluetooth" };

        /// <summary>IPv4 addresses a phone on the LAN can reach, best first.</summary>
        public static List<string> LocalAddresses()
        {
            var result = new List<(string ip, int score)>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                    var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
                    var isVirtual = VirtualAdapterHints.Any(label.Contains);
                    var props = nic.GetIPProperties();
                    var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                                     !g.Address.Equals(System.Net.IPAddress.Any));

                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var ip = addr.Address.ToString();
                        if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                        var score = (hasGateway ? 10 : 0) + (isVirtual ? -20 : 0) +
                                    (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 2 : 0);
                        result.Add((ip, score));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Listing network addresses failed", ex);
            }
            return result.OrderByDescending(r => r.score).Select(r => r.ip).Distinct().ToList();
        }

        public static void OpenFolder(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error($"Could not open {path}", ex);
            }
        }

        public static void OpenUri(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error($"Could not open {uri}", ex); }
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void SetAutostart(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                var exe = Environment.ProcessPath;
                if (enabled && exe != null) key.SetValue(AppInfo.AppName, $"\"{exe}\" --tray");
                else key.DeleteValue(AppInfo.AppName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Log.Error("Updating autostart failed", ex);
            }
        }
    }
}
