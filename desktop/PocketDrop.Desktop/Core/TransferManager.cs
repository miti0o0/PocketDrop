using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PocketDrop.Core
{
    /// <summary>What the receiver needs from the UI. Implemented by the WPF app (and by tests).</summary>
    public interface IReceiverUi
    {
        /// <summary>Ask the user. Must return false when <paramref name="ct"/> is cancelled.</summary>
        Task<bool> ConfirmAsync(TransferOffer offer, string folderName, CancellationToken ct);
        void TransferStarted(ReceiveSession session);
        void TransferFinished(ReceiveSession session);
    }

    /// <summary>
    /// Shared by every transport: validates an offer, asks the user, creates the folder and
    /// keeps track of the one transfer that may run at a time.
    /// </summary>
    public sealed class TransferManager
    {
        public const int MaxFiles = 10_000;

        private readonly Func<Settings> _settings;
        private readonly IReceiverUi _ui;
        private readonly object _gate = new();
        private ReceiveSession? _current;
        private bool _awaitingApproval;

        public TransferManager(Func<Settings> settings, IReceiverUi ui)
        {
            _settings = settings;
            _ui = ui;
        }

        public string BaseFolder => _settings().BaseFolder;

        /// <summary>A transfer is running or waiting for the user to accept.</summary>
        public bool IsBusy { get { lock (_gate) return _current != null || _awaitingApproval; } }

        public ReceiveSession? Find(string id)
        {
            lock (_gate) return _current != null && _current.Id == id ? _current : null;
        }

        public async Task<OfferResult> OfferAsync(TransferOffer offer, CancellationToken ct)
        {
            if (offer.Files == null || offer.Files.Count == 0 || offer.Files.Count > MaxFiles ||
                offer.Files.Any(f => f == null || f.Size < 0))
                return new OfferResult { Reason = RejectReason.Invalid };

            lock (_gate)
            {
                if (_current != null || _awaitingApproval) return new OfferResult { Reason = RejectReason.Busy };
                _awaitingApproval = true;
            }

            try
            {
                var baseFolder = BaseFolder;
                Directory.CreateDirectory(baseFolder);
                offer.Folder ??= new FolderChoice();

                var total = offer.Files.Sum(f => f.Size);
                if (!HasSpace(baseFolder, total)) return new OfferResult { Reason = RejectReason.NoSpace };

                var folderName = ReceiveFolders.ResolveName(baseFolder, offer.Folder);
                Log.Info($"Offer from {offer.Device} via {offer.Transport}: {offer.Files.Count} files, {total} bytes → \"{folderName}\"");

                using var timeout = new CancellationTokenSource(AppInfo.ApprovalTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

                bool accepted;
                try { accepted = await _ui.ConfirmAsync(offer, folderName, linked.Token); }
                catch (OperationCanceledException) { accepted = false; }

                if (!accepted)
                {
                    var reason = timeout.IsCancellationRequested ? RejectReason.Timeout : RejectReason.Declined;
                    Log.Info($"Offer {reason}");
                    return new OfferResult { Reason = reason };
                }
                ct.ThrowIfCancellationRequested();

                // Something may have created the folder while the dialog was open.
                folderName = ReceiveFolders.ResolveName(baseFolder, offer.Folder);
                var folderPath = Path.Combine(baseFolder, folderName);
                if (!SafeNames.IsInside(baseFolder, folderPath)) return new OfferResult { Reason = RejectReason.Invalid };
                Directory.CreateDirectory(folderPath);

                var session = new ReceiveSession(offer, folderName, folderPath);
                session.Finished += OnFinished;
                lock (_gate) _current = session;
                _ui.TransferStarted(session);
                return new OfferResult { Session = session };
            }
            finally
            {
                lock (_gate) _awaitingApproval = false;
            }
        }

        private void OnFinished(ReceiveSession session)
        {
            lock (_gate)
            {
                if (_current == session) _current = null;
            }
            _ui.TransferFinished(session);
        }

        private static bool HasSpace(string folder, long bytes)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return true;
                return new DriveInfo(root).AvailableFreeSpace > bytes + 50L * 1024 * 1024;
            }
            catch
            {
                return true;
            }
        }
    }
}
