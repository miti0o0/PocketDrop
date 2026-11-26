using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PocketDrop.Core;

namespace PocketDrop.Transport
{
    /// <summary>Length-prefixed JSON frames. See docs/PROTOCOL.md.</summary>
    public static class Frames
    {
        public const int MaxFrame = 1 << 20;

        public static async Task<JsonObject?> ReadAsync(Stream s, CancellationToken ct)
        {
            var header = new byte[4];
            if (!await ReadExactAsync(s, header, allowEof: true, ct)) return null;
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length <= 0 || length > MaxFrame) throw new InvalidDataException($"Bad frame length {length}.");
            var body = new byte[length];
            await ReadExactAsync(s, body, allowEof: false, ct);
            return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("Frame is not a JSON object.");
        }

        public static async Task WriteAsync(Stream s, object message, CancellationToken ct)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            var frame = new byte[4 + body.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
            body.CopyTo(frame, 4);
            await s.WriteAsync(frame, ct);
            await s.FlushAsync(ct);
        }

        private static async Task<bool> ReadExactAsync(Stream s, byte[] buffer, bool allowEof, CancellationToken ct)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await s.ReadAsync(buffer.AsMemory(offset), ct);
                if (read <= 0)
                {
                    if (allowEof && offset == 0) return false;
                    throw new EndOfStreamException();
                }
                offset += read;
            }
            return true;
        }
    }

    /// <summary>Serves one phone connection (TCP or Bluetooth) using the stream protocol.</summary>
    public sealed class StreamProtocolHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly TransferManager _manager;
        private readonly Func<Settings> _settings;

        public StreamProtocolHandler(TransferManager manager, Func<Settings> settings)
        {
            _manager = manager;
            _settings = settings;
        }

        /// <param name="abort">Closes the underlying connection (used to unblock stalled reads).</param>
        public async Task RunAsync(Stream input, Stream output, string transport, string remote, Action abort, CancellationToken ct)
        {
            string? device = null;
            using var reg = ct.Register(abort);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var msg = await Frames.ReadAsync(input, ct);
                    if (msg == null) return; // phone closed the connection

                    var type = (string?)msg["type"];
                    switch (type)
                    {
                        case "hello":
                            device = Trim((string?)msg["device"], 60) ?? remote;
                            await Frames.WriteAsync(output, new
                            {
                                type = "hello",
                                v = AppInfo.ProtocolVersion,
                                app = AppInfo.AppName,
                                name = _settings().DeviceName,
                            }, ct);
                            break;

                        case "list_folders":
                            await Frames.WriteAsync(output, new
                            {
                                type = "folders",
                                folders = ReceiveFolders.List(_manager.BaseFolder),
                            }, ct);
                            break;

                        case "offer":
                            await HandleOfferAsync(msg, input, output, transport, device ?? remote, abort, ct);
                            break;

                        default:
                            await Frames.WriteAsync(output, new { type = "error", message = $"unknown type '{type}'" }, ct);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
            {
                Log.Info($"{transport} connection from {remote} closed: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Error($"{transport} connection from {remote} failed", ex);
            }
        }

        private async Task HandleOfferAsync(JsonObject msg, Stream input, Stream output, string transport, string device,
            Action abort, CancellationToken ct)
        {
            TransferOffer? offer;
            try { offer = msg.Deserialize<TransferOffer>(JsonOptions); }
            catch (JsonException) { offer = null; }

            if (offer == null)
            {
                await Frames.WriteAsync(output, new { type = "reject", reason = "invalid" }, ct);
                return;
            }
            offer.Transport = transport;
            offer.Device = Trim(offer.Device, 60) ?? device;

            var result = await _manager.OfferAsync(offer, ct);
            if (!result.Accepted)
            {
                await Frames.WriteAsync(output, new { type = "reject", reason = result.ReasonCode }, ct);
                return;
            }

            var session = result.Session!;
            try
            {
                await Frames.WriteAsync(output, new { type = "accept", folder = session.FolderName }, ct);

                // Closing the socket is the only reliable way to stop a pending read when the
                // user presses Cancel on the PC.
                using var cancelReg = session.Token.Register(abort);
                for (var i = 0; i < offer.Files.Count; i++)
                    await session.ReceiveFileAsync(i, input, ct, abort);

                session.Complete();
                await Frames.WriteAsync(output, new
                {
                    type = "done",
                    saved = session.SavedCount,
                    folder = session.FolderName,
                }, ct);
            }
            catch (Exception ex)
            {
                if (session.State == SessionState.Receiving)
                    session.Fail(ex is EndOfStreamException or IOException or ObjectDisposedException
                        ? "The phone disconnected."
                        : ex.Message);
                throw;
            }
        }

        private static string? Trim(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            return s.Length > max ? s[..max] : s;
        }
    }
}
