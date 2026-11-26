using System;
using System.Linq;

namespace PocketDrop.Core
{
    /// <summary>Constants shared with the Android app and the web sender. See docs/PROTOCOL.md.</summary>
    public static class AppInfo
    {
        /// <summary>Product name shown everywhere. Change it here (and in the Android strings) to rebrand.</summary>
        public const string AppName = "PocketDrop";

        public const int ProtocolVersion = 1;
        public const int DiscoveryPort = 45454;
        public const int TcpPort = 45455;
        public const int HttpPort = 45456;
        /// <summary>Loopback-only web port that the Cloudflare tunnel forwards to ("send from anywhere").</summary>
        public const int RemotePort = 45457;
        public const string DiscoveryRequest = "POCKETDROP_DISCOVER_V1";

        /// <summary>RFCOMM service UUID. The Android app connects to this UUID.</summary>
        public static readonly Guid BluetoothServiceId = new("7a1c3e52-5d0b-4b8e-9f43-2c6a0d1e8b71");

        /// <summary>
        /// "owner/repo" on GitHub whose Releases provide updates. Set at build time (CI passes
        /// <c>-p:UpdateRepo=$GITHUB_REPOSITORY</c>, tools\build-release.ps1 uses the git remote).
        /// null in builds that don't know their repository: then the app never checks for updates.
        /// </summary>
        public static readonly string? UpdateRepo = typeof(AppInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepo" && !string.IsNullOrWhiteSpace(a.Value) && a.Value!.Contains('/'))
            ?.Value;

        /// <summary>How long the PC waits for the user to accept or decline an offer.</summary>
        public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(2);
    }
}
