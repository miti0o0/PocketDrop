using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PocketDrop.Core
{
    public enum SessionState { Receiving, Completed, Failed, Cancelled }

    /// <summary>One accepted drop: a target folder and the files flowing into it.</summary>
    public sealed class ReceiveSession
    {
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

        /// <summary>
        /// A drop that gets no data for this long is failed, so a phone that vanishes between files or
        /// pieces (closed browser, lost mobile signal) doesn't keep the receiver busy forever.
        /// </summary>
        internal static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

        private sealed class Partial
        {
            public required string Path;
            public required FileStream Stream;
            public long Written;
            public bool Busy;
        }

        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private readonly bool[] _done;
        private readonly List<string> _saved = new();
        private readonly Dictionary<int, Partial> _partials = new();
        private readonly Timer _watchdog;
        private long _received;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;
        private int _reading;
        private int _finished;

        public ReceiveSession(TransferOffer offer, string folderName, string folderPath)
        {
            Offer = offer;
            FolderName = folderName;
            FolderPath = folderPath;
            TotalBytes = offer.Files.Sum(f => f.Size);
            _done = new bool[offer.Files.Count];
            var check = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(200).Ticks, IdleTimeout.Ticks / 6));
            _watchdog = new Timer(_ => CheckIdle(), null, check, check);
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public TransferOffer Offer { get; }
        public string FolderName { get; }
        public string FolderPath { get; }
        public long TotalBytes { get; }
        public long ReceivedBytes => Interlocked.Read(ref _received);
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public int CurrentIndex { get; private set; }
        public string CurrentFileName { get; private set; } = "";
        public SessionState State { get; private set; } = SessionState.Receiving;
        public string? Error { get; private set; }
        public CancellationToken Token => _cts.Token;

        public int SavedCount { get { lock (_gate) return _saved.Count; } }
        public IReadOnlyList<string> SavedPaths { get { lock (_gate) return _saved.ToList(); } }
        public bool AllDone { get { lock (_gate) return _done.All(d => d); } }

        /// <summary>Raised once, when the session completes, fails or is cancelled.</summary>
        public event Action<ReceiveSession>? Finished;

        /// <summary>Cancel from the PC (the overlay's cancel button).</summary>
        public void Cancel()
        {
            if (Finish(SessionState.Cancelled, null)) _cts.Cancel();
        }

        public void Complete() => Finish(SessionState.Completed, null);

        public void Fail(string message)
        {
            if (Finish(SessionState.Failed, message)) _cts.Cancel();
        }

        private bool Finish(SessionState state, string? error)
        {
            if (Interlocked.Exchange(ref _finished, 1) == 1) return false;
            State = state;
            Error = error;
            _watchdog.Dispose();
            DropPartials();
            Log.Info($"Session {Id} {state}: {SavedCount}/{Offer.Files.Count} files in \"{FolderPath}\"{(error != null ? " - " + error : "")}");
            try { Finished?.Invoke(this); }
            catch (Exception ex) { Log.Error("Finished handler failed", ex); }
            return true;
        }

        private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        private void CheckIdle()
        {
            if (State != SessionState.Receiving) return;
            if (Volatile.Read(ref _reading) > 0) return; // a running read has its own stall timeout
            var idle = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
            if (idle > IdleTimeout) Fail("The phone stopped sending.");
        }

        /// <summary>
        /// Reads exactly <c>Files[index].Size</c> bytes from <paramref name="source"/> into a new file.
        /// A partially written file is deleted on failure.
        /// </summary>
        /// <param name="abortTransport">Called if the sender stops sending for too long, to unblock the read.</param>
        public async Task ReceiveFileAsync(int index, Stream source, CancellationToken ct, Action? abortTransport = null)
        {
            if (index < 0 || index >= Offer.Files.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (State != SessionState.Receiving) throw new OperationCanceledException("Session is no longer receiving.");

            var file = Offer.Files[index];
            var name = SafeNames.Clean(file.Name, $"file-{index + 1}");
            string path;
            FileStream output;

            lock (_gate)
            {
                if (_done[index] || _partials.ContainsKey(index)) throw new InvalidOperationException($"File {index} was already received.");
                Directory.CreateDirectory(FolderPath);
                path = SafeNames.UniqueFilePath(FolderPath, name);
                if (!SafeNames.IsInside(FolderPath, path)) throw new InvalidOperationException("Unsafe file name.");
                output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            }

            CurrentIndex = index;
            CurrentFileName = Path.GetFileName(path);

            var ok = false;
            try
            {
                await CopyExactAsync(source, output, file.Size, ct, abortTransport);
                await output.FlushAsync(ct);
                ok = true;
            }
            finally
            {
                await output.DisposeAsync();
                if (!ok) TryDelete(path);
            }

            MarkDone(index, path);
        }

        /// <summary>
        /// Receives one piece of a file. The browser sends big files in pieces, which works through
        /// proxies such as Cloudflare that limit the size of one request. Pieces must arrive in order;
        /// a piece may be sent again (after a dropped connection), which rewinds the file to its offset.
        /// </summary>
        /// <returns>true when this piece completed the file.</returns>
        public async Task<bool> ReceiveChunkAsync(int index, long offset, long length, Stream source, CancellationToken ct,
            Action? abortTransport = null)
        {
            if (index < 0 || index >= Offer.Files.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (State != SessionState.Receiving) throw new OperationCanceledException("Session is no longer receiving.");
            var file = Offer.Files[index];
            if (offset < 0 || length < 0 || offset + length > file.Size) throw new InvalidDataException("Piece is outside the file.");
            if (length == 0 && file.Size != 0) throw new InvalidDataException("Empty piece.");

            Partial part;
            lock (_gate)
            {
                if (_done[index])
                {
                    // The last piece arrived but its answer got lost, so the phone sends it again.
                    if (offset + length == file.Size) return true;
                    throw new InvalidOperationException($"File {index} was already received.");
                }
                if (!_partials.TryGetValue(index, out part!))
                {
                    if (offset != 0) throw new InvalidDataException("The first piece must start at 0.");
                    Directory.CreateDirectory(FolderPath);
                    var path = SafeNames.UniqueFilePath(FolderPath, SafeNames.Clean(file.Name, $"file-{index + 1}"));
                    if (!SafeNames.IsInside(FolderPath, path)) throw new InvalidOperationException("Unsafe file name.");
                    part = new Partial
                    {
                        Path = path,
                        Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true),
                    };
                    _partials[index] = part;
                }
                if (part.Busy) throw new InvalidOperationException("Another piece of this file is being received.");
                if (offset > part.Written) throw new InvalidDataException($"Missing data before {offset} (have {part.Written}).");
                part.Busy = true;
            }

            try
            {
                if (offset < part.Written)
                {
                    // Resent piece: forget what arrived of it the first time.
                    Interlocked.Add(ref _received, offset - part.Written);
                    part.Stream.SetLength(offset);
                    part.Written = offset;
                }
                part.Stream.Position = offset;
                CurrentIndex = index;
                CurrentFileName = Path.GetFileName(part.Path);

                try
                {
                    await CopyExactAsync(source, part.Stream, length, ct, abortTransport);
                }
                finally
                {
                    try { part.Written = part.Stream.Position; } catch (ObjectDisposedException) { }
                }

                if (part.Written < file.Size) return false;

                await part.Stream.FlushAsync(ct);
                await part.Stream.DisposeAsync();
                lock (_gate) _partials.Remove(index);
                MarkDone(index, part.Path);
                return true;
            }
            finally
            {
                lock (_gate) part.Busy = false;
            }
        }

        private async Task CopyExactAsync(Stream source, Stream output, long count, CancellationToken ct, Action? abortTransport)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            using var reg = stall.Token.Register(() =>
            {
                if (!linked.IsCancellationRequested) abortTransport?.Invoke();
            });

            Interlocked.Increment(ref _reading);
            Touch();
            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                var remaining = count;
                while (remaining > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), stall.Token);
                    if (read <= 0) throw new EndOfStreamException("The sender stopped before the file was complete.");
                    await output.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                    remaining -= read;
                    Interlocked.Add(ref _received, read);
                    Touch();
                }
            }
            catch (OperationCanceledException) when (stall.IsCancellationRequested && !linked.IsCancellationRequested)
            {
                throw new TimeoutException("The sender stopped sending.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                Interlocked.Decrement(ref _reading);
                Touch();
            }
        }

        private void MarkDone(int index, string path)
        {
            var file = Offer.Files[index];
            if (file.Modified is long ms && ms > 0)
            {
                try
                {
                    var time = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                    File.SetLastWriteTimeUtc(path, time);
                    File.SetCreationTimeUtc(path, time);
                }
                catch { /* cosmetic only */ }
            }

            lock (_gate)
            {
                _done[index] = true;
                _saved.Add(path);
            }
            Touch();
        }

        /// <summary>Half-received files of a failed or cancelled drop are deleted.</summary>
        private void DropPartials()
        {
            List<Partial> parts;
            lock (_gate)
            {
                parts = _partials.Values.ToList();
                _partials.Clear();
            }
            foreach (var p in parts)
            {
                // A piece may still be writing: closing the stream makes it fail, then the file goes.
                try { p.Stream.Dispose(); } catch { }
                TryDelete(p.Path);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log.Error($"Could not delete partial file {path}", ex); }
        }
    }
}
