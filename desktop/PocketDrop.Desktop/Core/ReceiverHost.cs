using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using PocketDrop.Transport;

namespace PocketDrop.Core
{
    /// <summary>Owns the transports and starts/stops them when settings change.</summary>
    public sealed class ReceiverHost : IAsyncDisposable
    {
        private readonly StreamProtocolHandler _handler;
        private TcpTransport? _tcp;
        private DiscoveryService? _discovery;
        private WebServer? _web;
        private BluetoothTransport? _bt;
        private CloudflareTunnel? _tunnel;

        public ReceiverHost(Settings settings, IReceiverUi ui)
        {
            Settings = settings;
            Manager = new TransferManager(() => Settings, ui);
            _handler = new StreamProtocolHandler(Manager, () => Settings);
        }

        public Settings Settings { get; }
        public TransferManager Manager { get; }

        public bool WifiRunning => _tcp != null;
        public string? WifiError { get; private set; }
        public bool BluetoothRunning => _bt?.IsRunning == true;
        public string? BluetoothError { get; private set; }
        public bool BluetoothAdapterPresent { get; private set; }

        /// <summary>"Send from anywhere"; null while it is off.</summary>
        public CloudflareTunnel? Tunnel => _tunnel;
        public string? RemoteError { get; private set; }
        public bool RemoteReady => _tunnel?.State == RemoteState.Ready;

        public event Action? StatusChanged;

        public async Task StartAsync()
        {
            await ApplyWifiAsync();
            await ApplyRemoteAsync();
            await ApplyBluetoothAsync();
        }

        public async Task ApplyWifiAsync()
        {
            if (Settings.WifiEnabled && _tcp == null)
            {
                WifiError = null;
                try
                {
                    var tcp = new TcpTransport(_handler);
                    tcp.Start();
                    _tcp = tcp;
                }
                catch (SocketException ex)
                {
                    Log.Error("TCP start failed", ex);
                    WifiError = Loc.F("error.port", AppInfo.TcpPort);
                }

                try
                {
                    var d = new DiscoveryService(() => Settings);
                    d.Start();
                    _discovery = d;
                }
                catch (SocketException ex)
                {
                    Log.Error("Discovery start failed", ex);
                }
            }
            else if (!Settings.WifiEnabled)
            {
                _tcp?.Dispose();
                _tcp = null;
                _discovery?.Dispose();
                _discovery = null;
                WifiError = null;
            }
            await ApplyWebAsync();
            StatusChanged?.Invoke();
        }

        /// <summary>
        /// The web server runs while Wi-Fi or "send from anywhere" is on. Its home-network port follows the Wi-Fi
        /// switch; the loopback port for the tunnel is always there (it refuses everything while the tunnel is off).
        /// </summary>
        private async Task ApplyWebAsync()
        {
            var wantLan = Settings.WifiEnabled ? AppInfo.HttpPort : 0;
            var wanted = Settings.WifiEnabled || Settings.RemoteEnabled;

            if (_web != null && (!wanted || _web.LanPort != wantLan || (_web.RemotePort == 0 && Settings.RemoteEnabled)))
            {
                await _web.DisposeAsync();
                _web = null;
            }
            if (_web != null || !wanted) return;

            RemoteError = null;
            try
            {
                var web = new WebServer(Manager, () => Settings, wantLan, AppInfo.RemotePort, () => _tunnel?.Key);
                await web.StartAsync();
                _web = web;
                return;
            }
            catch (Exception ex)
            {
                Log.Error("HTTP start failed", ex);
            }

            // One of the two ports is taken. Keep whichever works.
            if (wantLan > 0)
            {
                try
                {
                    var web = new WebServer(Manager, () => Settings, wantLan);
                    await web.StartAsync();
                    _web = web;
                    RemoteError = Loc.F("error.port", AppInfo.RemotePort);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("HTTP start failed (home network only)", ex);
                    WifiError ??= Loc.F("error.port", AppInfo.HttpPort);
                }
            }
            if (Settings.RemoteEnabled)
            {
                try
                {
                    var web = new WebServer(Manager, () => Settings, 0, AppInfo.RemotePort, () => _tunnel?.Key);
                    await web.StartAsync();
                    _web = web;
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("HTTP start failed (tunnel port only)", ex);
                }
            }
            RemoteError = Loc.F("error.port", AppInfo.RemotePort);
        }

        public async Task ApplyRemoteAsync()
        {
            await ApplyWebAsync();
            if (Settings.RemoteEnabled && _web?.RemotePort > 0)
            {
                if (_tunnel == null)
                {
                    _tunnel = new CloudflareTunnel(AppInfo.RemotePort);
                    _tunnel.Changed += () => StatusChanged?.Invoke();
                    _tunnel.Start();
                }
            }
            else if (_tunnel != null)
            {
                var t = _tunnel;
                _tunnel = null;
                await t.StopAsync();
            }
            StatusChanged?.Invoke();
        }

        /// <summary>For screenshots only.</summary>
        internal void SetTunnelForPreview(CloudflareTunnel tunnel) => _tunnel = tunnel;

        /// <summary>A new address and key; the old link stops working.</summary>
        public async Task RenewRemoteLinkAsync()
        {
            if (_tunnel == null) return;
            await _tunnel.StopAsync();
            if (_tunnel != null && Settings.RemoteEnabled) _tunnel.Start();
        }

        public async Task ApplyBluetoothAsync()
        {
            BluetoothAdapterPresent = await BluetoothTransport.IsAdapterPresentAsync();
            if (Settings.BluetoothEnabled && _bt == null)
            {
                var bt = new BluetoothTransport(_handler);
                BluetoothError = await bt.StartAsync();
                if (BluetoothError == null) _bt = bt;
            }
            else if (!Settings.BluetoothEnabled)
            {
                _bt?.Dispose();
                _bt = null;
                BluetoothError = null;
            }
            StatusChanged?.Invoke();
        }

        public async ValueTask DisposeAsync()
        {
            if (_tunnel != null) await _tunnel.StopAsync();
            _tunnel = null;
            _tcp?.Dispose();
            _tcp = null;
            _discovery?.Dispose();
            _discovery = null;
            if (_web != null) await _web.DisposeAsync();
            _web = null;
            _bt?.Dispose();
            _bt = null;
        }
    }
}
