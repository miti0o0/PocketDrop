using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PocketDrop.Core;

namespace PocketDrop.Transport
{
    /// <summary>Wi-Fi transport for the Android app (stream protocol over TCP).</summary>
    public sealed class TcpTransport : IDisposable
    {
        private readonly StreamProtocolHandler _handler;
        private readonly int _port;
        private readonly CancellationTokenSource _cts = new();
        private TcpListener? _listener;

        public TcpTransport(StreamProtocolHandler handler, int port = AppInfo.TcpPort)
        {
            _handler = handler;
            _port = port;
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            Log.Info($"TCP listening on {_port}");
            _ = AcceptLoopAsync(_listener, _cts.Token);
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex)
                {
                    Log.Error("TCP accept failed", ex);
                    await Task.Delay(500, CancellationToken.None);
                    continue;
                }
                _ = HandleAsync(client, ct);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                client.NoDelay = true;
                client.ReceiveBufferSize = 1 << 20;
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                var stream = client.GetStream();
                await _handler.RunAsync(stream, stream, "Wi-Fi", remote, () => client.Close(), ct);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener?.Stop(); } catch { }
        }
    }
}
