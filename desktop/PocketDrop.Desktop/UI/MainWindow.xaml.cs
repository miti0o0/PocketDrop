using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PocketDrop.Core;
using QRCoder;

namespace PocketDrop.UI
{
    public partial class MainWindow : Window
    {
        private readonly ReceiverHost _host;
        private bool _loading = true;

        public MainWindow(ReceiverHost host)
        {
            InitializeComponent();
            _host = host;

            var s = host.Settings;
            NameBox.Text = s.DeviceName;
            WifiBox.IsChecked = s.WifiEnabled;
            BtBox.IsChecked = s.BluetoothEnabled;
            RemoteBox.IsChecked = s.RemoteEnabled;
            ExplorerBox.IsChecked = s.OpenExplorerWhenDone;
            AutostartBox.IsChecked = s.StartWithWindows;

            LanguageBox.Items.Add(new ComboBoxItem { Content = Loc.T("opt.lang.auto"), Tag = "auto" });
            LanguageBox.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
            LanguageBox.Items.Add(new ComboBoxItem { Content = "Български", Tag = "bg" });
            LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Language)
                                       ?? LanguageBox.Items[0];

            var version = UpdateService.CurrentVersion.ToString(3);
            VersionText.Text = $"{AppInfo.AppName} {version}";
            CurrentVersionText.Text = Loc.F("update.version", version);

            host.StatusChanged += OnHostStatusChanged;
            Closed += (_, _) => host.StatusChanged -= OnHostStatusChanged;
            if (Application.Current is App app)
            {
                app.UpdateStateChanged += OnUpdateStateChanged;
                Closed += (_, _) => app.UpdateStateChanged -= OnUpdateStateChanged;
            }
            RefreshUpdate();
            Activated += (_, _) => { RefreshNetwork(); RefreshRecent(); };

            RefreshFolder();
            RefreshStatus();
            RefreshNetwork();
            RefreshRecent();
            _loading = false;
        }

        /// <summary>Raised when the window is closed to the tray.</summary>
        public event Action? Hiding;

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            // Minimize = hide from the taskbar and keep receiving in the background.
            // The window comes back from the tray icon or by starting PocketDrop again (e.g. the desktop shortcut).
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                Hiding?.Invoke();
            }
        }

        // ---- Updates -----------------------------------------------------------------------

        private bool _updating;

        private void OnUpdateStateChanged() => Dispatcher.BeginInvoke(RefreshUpdate);

        private void RefreshUpdate()
        {
            var update = (Application.Current as App)?.AvailableUpdate;
            UpdateBanner.Visibility = update == null ? Visibility.Collapsed : Visibility.Visible;
            if (update == null || _updating) return;
            UpdateTitle.Text = Loc.F("update.available", update.Version.ToString(3));
            UpdateHint.Text = UpdateService.IsInstalled
                ? Loc.F("update.hint", UpdateService.CurrentVersion.ToString(3))
                : Loc.T("update.portable");
            UpdateButton.Content = Loc.T(UpdateService.IsInstalled ? "update.button" : "update.download");
        }

        private async void OnUpdate(object sender, RoutedEventArgs e)
        {
            if (Application.Current is not App app || _updating) return;
            _updating = true;
            UpdateButton.IsEnabled = false;
            UpdateError.Visibility = Visibility.Collapsed;
            if (UpdateService.IsInstalled)
            {
                UpdateProgress.Value = 0;
                UpdateProgress.Visibility = Visibility.Visible;
                UpdateHint.Text = Loc.F("update.downloading", 0);
            }

            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p * 100;
                UpdateHint.Text = p >= 1 ? Loc.T("update.installing") : Loc.F("update.downloading", (int)(p * 100));
            });
            var message = await app.InstallUpdateAsync(progress);

            // Only reached if the app is not quitting for the installer.
            _updating = false;
            UpdateButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
            RefreshUpdate();
            if (message != null)
            {
                UpdateError.Text = message;
                UpdateError.Visibility = Visibility.Visible;
            }
        }

        private async void OnCheckUpdates(object sender, RoutedEventArgs e)
        {
            if (Application.Current is not App app) return;
            UpdateStatus.Visibility = Visibility.Visible;
            if (app.Updater == null)
            {
                UpdateStatus.Text = Loc.T("update.notConfigured");
                return;
            }
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatus.Text = Loc.T("update.checking");
            var result = await app.CheckForUpdatesAsync(manual: true);
            CheckUpdatesButton.IsEnabled = true;
            UpdateStatus.Text = result switch
            {
                true => Loc.F("update.available", app.AvailableUpdate!.Version.ToString(3)),
                false => Loc.T("update.latest"),
                null => Loc.T("update.checkFailed"),
            };
        }

        private void OnHostStatusChanged() => Dispatcher.BeginInvoke(() => { RefreshStatus(); RefreshNetwork(); });

        private void RefreshStatus()
        {
            var s = _host.Settings;
            var receiving = (s.WifiEnabled && _host.WifiRunning) || _host.BluetoothRunning || _host.RemoteReady;
            StatusText.Text = Loc.T(receiving ? "status.ready" : "status.off");
            StatusDot.Fill = (System.Windows.Media.Brush)FindResource(receiving ? "Success" : "Danger");

            WifiError.Text = _host.WifiError ?? "";
            WifiError.Visibility = _host.WifiError == null ? Visibility.Collapsed : Visibility.Visible;
            BrowserCard.Visibility = s.WifiEnabled ? Visibility.Visible : Visibility.Collapsed;

            if (_host.BluetoothRunning) BtStatus.Text = Loc.T("bt.running");
            else if (!_host.BluetoothAdapterPresent) BtStatus.Text = Loc.T("bt.noAdapter");
            else if (_host.BluetoothError != null) BtStatus.Text = Loc.F("bt.failed", _host.BluetoothError);
            else BtStatus.Text = Loc.T("bt.disabled");
            BtStatus.Foreground = (System.Windows.Media.Brush)FindResource(_host.BluetoothRunning ? "Success" : "TextSecondary");
            RefreshRemote();
        }

        // ---- From anywhere -------------------------------------------------------------------

        private string? _lastRemoteLink;

        private void RefreshRemote()
        {
            var enabled = _host.Settings.RemoteEnabled;
            RemotePanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled) return;

            var t = _host.Tunnel;
            string? link = null;
            var downloading = false;
            if (_host.RemoteError != null)
                RemoteStatus.Text = _host.RemoteError;
            else
            {
                switch (t?.State)
                {
                    case Transport.RemoteState.Downloading:
                        downloading = true;
                        RemoteProgress.Value = t.DownloadProgress * 100;
                        RemoteStatus.Text = Loc.F("remote.downloading", (int)(t.DownloadProgress * 100));
                        break;
                    case Transport.RemoteState.Ready:
                        link = t.Link;
                        RemoteStatus.Text = Loc.T("remote.ready");
                        break;
                    case Transport.RemoteState.Reconnecting:
                        RemoteStatus.Text = Loc.F("remote.reconnecting", t.Error ?? "");
                        break;
                    default:
                        RemoteStatus.Text = Loc.T("remote.connecting");
                        break;
                }
            }

            RemoteProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            var show = link == null ? Visibility.Collapsed : Visibility.Visible;
            RemoteLink.Visibility = RemoteButtons.Visibility = RemoteQrBorder.Visibility = show;
            RemoteLink.Text = link ?? "";
            if (link == _lastRemoteLink) return;
            _lastRemoteLink = link;
            RemoteQr.Source = link == null ? null : MakeQr(link);
        }

        private async void OnRemoteToggled(object sender, RoutedEventArgs e)
        {
            _host.Settings.RemoteEnabled = RemoteBox.IsChecked == true;
            Save();
            RemoteBox.IsEnabled = false;
            await _host.ApplyRemoteAsync();
            RemoteBox.IsEnabled = true;
            RefreshStatus();
        }

        private async void OnCopyRemote(object sender, RoutedEventArgs e)
        {
            if (_lastRemoteLink == null) return;
            try { Clipboard.SetText(_lastRemoteLink); }
            catch (Exception ex) { Log.Error("Clipboard failed", ex); return; }
            CopyRemoteButton.Content = Loc.T("remote.copied");
            await System.Threading.Tasks.Task.Delay(2000);
            CopyRemoteButton.Content = Loc.T("remote.copy");
        }

        private async void OnNewRemoteLink(object sender, RoutedEventArgs e)
        {
            if (sender is Button b) b.IsEnabled = false;
            await _host.RenewRemoteLinkAsync();
            if (sender is Button b2) b2.IsEnabled = true;
        }

        private string? _lastUrl;

        private void RefreshNetwork()
        {
            var addresses = SystemHelpers.LocalAddresses();
            AddressText.Text = addresses.Count == 0 ? Loc.T("pc.noNetwork") : string.Join("   ", addresses);

            var url = addresses.Count == 0 ? null : $"http://{addresses[0]}:{AppInfo.HttpPort}";
            UrlText.Text = url ?? Loc.T("pc.noNetwork");
            if (url == _lastUrl) return;
            _lastUrl = url;
            QrImage.Source = url == null ? null : MakeQr(url);
        }

        private static BitmapImage MakeQr(string text)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private void RefreshFolder() => FolderText.Text = _host.Settings.BaseFolder;

        public void RefreshRecent()
        {
            var items = ReceiveFolders.List(_host.Settings.BaseFolder).Take(6)
                .Select(f => new { f.Name, Detail = $"{Loc.F("recent.files", f.Files)} · {f.Modified.ToLocalTime():g}" })
                .ToList();
            RecentList.ItemsSource = items;
            RecentEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Save()
        {
            try { _host.Settings.Save(); }
            catch (Exception ex) { Log.Error("Saving settings failed", ex); }
        }

        // ---- Handlers -----------------------------------------------------------------------

        private void OnNameChanged(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();
            if (name.Length == 0) name = Environment.MachineName;
            NameBox.Text = name;
            if (name == _host.Settings.DeviceName) return;
            _host.Settings.DeviceName = name;
            Save();
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(_host.Settings.BaseFolder);
            SystemHelpers.OpenFolder(_host.Settings.BaseFolder);
        }

        private void OnChangeFolder(object sender, RoutedEventArgs e)
        {
            var picked = SetupWindow.PickFolder(this, _host.Settings.BaseFolder);
            if (picked == null) return;
            _host.Settings.BaseFolder = picked;
            Save();
            RefreshFolder();
            RefreshRecent();
        }

        private async void OnWifiToggled(object sender, RoutedEventArgs e)
        {
            _host.Settings.WifiEnabled = WifiBox.IsChecked == true;
            Save();
            WifiBox.IsEnabled = false;
            await _host.ApplyWifiAsync();
            WifiBox.IsEnabled = true;
        }

        private async void OnBluetoothToggled(object sender, RoutedEventArgs e)
        {
            _host.Settings.BluetoothEnabled = BtBox.IsChecked == true;
            Save();
            BtBox.IsEnabled = false;
            await _host.ApplyBluetoothAsync();
            BtBox.IsEnabled = true;
        }

        private void OnBluetoothSettings(object sender, RoutedEventArgs e) => SystemHelpers.OpenUri("ms-settings:bluetooth");

        private void OnUrlClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_lastUrl != null) Clipboard.SetText(_lastUrl);
        }

        private void OnRecentClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string name })
                SystemHelpers.OpenFolder(Path.Combine(_host.Settings.BaseFolder, name));
        }

        private void OnOptionsChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _host.Settings.OpenExplorerWhenDone = ExplorerBox.IsChecked == true;
            _host.Settings.StartWithWindows = AutostartBox.IsChecked == true;
            SystemHelpers.SetAutostart(_host.Settings.StartWithWindows);
            Save();
        }

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string lang }) return;
            _host.Settings.Language = lang;
            Save();
        }
    }
}
