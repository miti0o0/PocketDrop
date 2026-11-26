using System;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PocketDrop.Core;

namespace PocketDrop.UI
{
    public partial class ConfirmWindow : Window
    {
        private const int PreviewCount = 5;
        private bool _accepted;

        public ConfirmWindow(TransferOffer offer, string folderName)
        {
            InitializeComponent();

            var count = offer.Files.Count;
            var files = count == 1 ? Loc.T("confirm.oneFile") : Loc.F("confirm.files", count);
            var size = Loc.Size(offer.Files.Sum(f => f.Size));
            BodyText.Text = Loc.F("confirm.body", offer.Device ?? "?", files, size);
            ViaText.Text = Loc.F("confirm.via", offer.Transport);

            var existing = string.Equals(offer.Folder.Mode, "existing", StringComparison.OrdinalIgnoreCase);
            FolderLabel.Text = Loc.T(existing ? "confirm.existingFolder" : "confirm.newFolder");
            FolderText.Text = folderName;
            FolderText.ToolTip = folderName;

            var preview = offer.Files.Take(PreviewCount).Select(f => $"{SafeNames.Clean(f.Name, "?")}  ·  {Loc.Size(f.Size)}").ToList();
            if (count > PreviewCount) preview.Add(Loc.F("confirm.more", count - PreviewCount));
            FileList.ItemsSource = preview;

            Loaded += (_, _) =>
            {
                PopupPlacement.TopRight(this);
                Activate();
                AcceptButton.Focus();
                SystemSounds.Asterisk.Play();
            };
        }

        /// <summary>Shows the window and completes with the user's answer (false if <paramref name="ct"/> fires).</summary>
        public Task<bool> AskAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reg = ct.Register(() => Dispatcher.BeginInvoke(() => { if (IsVisible) Close(); }));
            Closed += (_, _) =>
            {
                reg.Dispose();
                tcs.TrySetResult(_accepted);
            };
            Show();
            return tcs.Task;
        }

        private void OnAccept(object sender, RoutedEventArgs e)
        {
            _accepted = true;
            Close();
        }

        private void OnDecline(object sender, RoutedEventArgs e) => Close();
    }
}
