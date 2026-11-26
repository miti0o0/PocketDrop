using System;
using System.Windows;
using System.Windows.Threading;
using PocketDrop.Core;

namespace PocketDrop.UI
{
    /// <summary>The small "receiving…" card in the top-right corner of the screen.</summary>
    public partial class ProgressOverlay : Window
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(5) };
        private long _lastBytes;
        private DateTime _lastTick = DateTime.UtcNow;
        private double _speed; // bytes per second, smoothed

        public ProgressOverlay(ReceiveSession session)
        {
            InitializeComponent();
            Session = session;
            TitleText.Text = Loc.F("progress.from", session.Offer.Device ?? "?");
            _timer.Tick += (_, _) => Refresh();
            _autoClose.Tick += (_, _) => Close();
            Loaded += (_, _) =>
            {
                PopupPlacement.TopRight(this);
                Refresh();
                _timer.Start();
            };
            SizeChanged += (_, _) => PopupPlacement.TopRight(this);
            Closed += (_, _) =>
            {
                _timer.Stop();
                _autoClose.Stop();
            };
        }

        public ReceiveSession Session { get; }

        private void Refresh()
        {
            var s = Session;
            var received = s.ReceivedBytes;
            var total = Math.Max(1, s.TotalBytes);
            Bar.Value = s.TotalBytes == 0 ? 0 : 1000.0 * received / total;

            var now = DateTime.UtcNow;
            var dt = (now - _lastTick).TotalSeconds;
            if (dt >= 0.5)
            {
                var instant = (received - _lastBytes) / dt;
                _speed = _speed <= 0 ? instant : _speed * 0.7 + instant * 0.3;
                _lastBytes = received;
                _lastTick = now;
            }

            var count = s.Offer.Files.Count;
            FileText.Text = Loc.F("progress.file", Math.Min(s.CurrentIndex + 1, count), count,
                string.IsNullOrEmpty(s.CurrentFileName) ? "…" : s.CurrentFileName);

            var stats = Loc.F("progress.bytes", Loc.Size(received), Loc.Size(s.TotalBytes));
            if (_speed > 1)
            {
                stats += $" · {Loc.Size((long)_speed)}/s";
                var left = TimeSpan.FromSeconds((s.TotalBytes - received) / _speed);
                if (left.TotalSeconds >= 1)
                    stats += " · " + Loc.F("progress.left", left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"m\:ss"));
            }
            StatsText.Text = stats;
        }

        /// <summary>Switches the card to its final state and closes it a few seconds later.</summary>
        public void ShowFinished()
        {
            _timer.Stop();
            Refresh();
            var s = Session;
            CloseButton.ToolTip = null;

            switch (s.State)
            {
                case SessionState.Completed:
                    Bar.Value = Bar.Maximum;
                    Bar.Foreground = (System.Windows.Media.Brush)FindResource("Success");
                    AppIcon.Visibility = Visibility.Collapsed;
                    DoneBadge.Visibility = DoneMark.Visibility = Visibility.Visible;
                    TitleText.Text = Loc.F("progress.done", s.SavedCount == 1 ? Loc.T("confirm.oneFile") : Loc.F("confirm.files", s.SavedCount));
                    FileText.Text = s.FolderName;
                    StatsText.Text = Loc.Size(s.TotalBytes) + " · " + (s.Offer.Device ?? "");
                    OpenFolderButton.Visibility = Host().Settings.OpenExplorerWhenDone ? Visibility.Collapsed : Visibility.Visible;
                    _autoClose.Start();
                    break;

                default:
                    Bar.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
                    TitleText.Text = s.State == SessionState.Cancelled ? Loc.T("progress.cancelled") : Loc.T("progress.failed");
                    FileText.Text = s.Error ?? "";
                    StatsText.Text = s.SavedCount > 0 ? Loc.F("progress.keptFiles", s.SavedCount) : "";
                    OpenFolderButton.Visibility = s.SavedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                    _autoClose.Interval = TimeSpan.FromSeconds(10);
                    _autoClose.Start();
                    break;
            }
        }

        private static ReceiverHost Host() => App.Current.Host;

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            SystemHelpers.OpenFolder(Session.FolderPath);
            Close();
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            if (Session.State == SessionState.Receiving) Session.Cancel();
            else Close();
        }
    }
}
