using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PocketDrop.Core;

namespace PocketDrop.Transport
{
    /// <summary>Answers UDP broadcast probes from phones on the same network.</summary>
    public sealed class DiscoveryService : IDisposable
    {
        private readonly Func<Settings> _settings;
        private readonly CancellationTokenSource _cts = new();
        private UdpClient? _udp;

        public DiscoveryService(Func<Settings> settings) => _settings = settings;

        public void Start()
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, AppInfo.DiscoveryPort));
            Log.Info($"Discovery listening on UDP {AppInfo.DiscoveryPort}");
            _ = LoopAsync(_udp, _cts.Token);
        }

        private async Task LoopAsync(UdpClient udp, CancellationToken ct)
        {
            var request = Encoding.ASCII.GetBytes(AppInfo.DiscoveryRequest);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var packet = await udp.ReceiveAsync(ct);
                    if (!packet.Buffer.AsSpan().SequenceEqual(request)) continue;

                    var s = _settings();
                    var reply = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        app = AppInfo.AppName,
                        v = AppInfo.ProtocolVersion,
                        id = s.DeviceId,
                        name = s.DeviceName,
                        tcpPort = AppInfo.TcpPort,
                        httpPort = AppInfo.HttpPort,
                    });
                    await udp.SendAsync(reply, packet.RemoteEndPoint, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex)
                {
                    // e.g. ICMP "port unreachable" from a phone that already left.
                    Log.Info($"Discovery: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp?.Dispose();
        }
    }
}
