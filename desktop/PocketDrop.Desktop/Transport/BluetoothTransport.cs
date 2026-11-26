using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PocketDrop.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace PocketDrop.Transport
{
    /// <summary>
    /// Bluetooth transport: advertises an RFCOMM service with <see cref="AppInfo.BluetoothServiceId"/>
    /// and serves the stream protocol on every connection. The phone has to be paired with the PC
    /// first (Windows Settings → Bluetooth &amp; devices).
    /// </summary>
    public sealed class BluetoothTransport : IDisposable
    {
        private const ushort SdpServiceNameAttributeId = 0x100;
        private const byte SdpServiceNameAttributeType = (4 << 3) | 5; // text string, 1-byte length

        private readonly StreamProtocolHandler _handler;
        private readonly CancellationTokenSource _cts = new();
        private RfcommServiceProvider? _provider;
        private StreamSocketListener? _listener;

        public BluetoothTransport(StreamProtocolHandler handler) => _handler = handler;

        public bool IsRunning { get; private set; }

        /// <summary>True when the PC has a Bluetooth adapter that supports classic Bluetooth.</summary>
        public static async Task<bool> IsAdapterPresentAsync()
        {
            try
            {
                var adapter = await BluetoothAdapter.GetDefaultAsync();
                return adapter != null && adapter.IsClassicSupported;
            }
            catch (Exception ex)
            {
                Log.Error("Bluetooth adapter query failed", ex);
                return false;
            }
        }

        /// <returns>null on success, otherwise a short reason.</returns>
        public async Task<string?> StartAsync()
        {
            if (!await IsAdapterPresentAsync()) return "No Bluetooth adapter found.";
            try
            {
                _provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(AppInfo.BluetoothServiceId));
                _listener = new StreamSocketListener();
                _listener.ConnectionReceived += OnConnectionReceived;
                await _listener.BindServiceNameAsync(_provider.ServiceId.AsString(),
                    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

                var writer = new DataWriter();
                var name = Encoding.UTF8.GetBytes(AppInfo.AppName);
                writer.WriteByte(SdpServiceNameAttributeType);
                writer.WriteByte((byte)name.Length);
                writer.WriteBytes(name);
                _provider.SdpRawAttributes.Add(SdpServiceNameAttributeId, writer.DetachBuffer());

                _provider.StartAdvertising(_listener, true);
                IsRunning = true;
                Log.Info("Bluetooth RFCOMM service advertising");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Bluetooth start failed", ex);
                Dispose();
                return ex.Message;
            }
        }

        private async void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            var socket = args.Socket;
            var remote = socket.Information.RemoteHostName?.DisplayName ?? "Bluetooth device";
            try
            {
                using var input = socket.InputStream.AsStreamForRead(64 * 1024);
                using var output = socket.OutputStream.AsStreamForWrite(64 * 1024);
                await _handler.RunAsync(input, output, "Bluetooth", remote, () =>
                {
                    try { socket.Dispose(); } catch { }
                }, _cts.Token);
            }
            catch (Exception ex)
            {
                Log.Error("Bluetooth connection failed", ex);
            }
            finally
            {
                try { socket.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            IsRunning = false;
            try { _provider?.StopAdvertising(); } catch { }
            try { _listener?.Dispose(); } catch { }
            _provider = null;
            _listener = null;
        }
    }
}
