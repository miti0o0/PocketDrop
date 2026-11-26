using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PocketDrop.Core;
using PocketDrop.UI;
using Forms = System.Windows.Forms;

namespace PocketDrop
{
    public partial class App : Application, IReceiverUi
    {
        private const string InstanceName = "PocketDrop.SingleInstance.v1";
        private const string ShowEventName = "PocketDrop.ShowMainWindow.v1";

        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;
        private Forms.NotifyIcon? _tray;
        private MainWindow? _main;
        private ProgressOverlay? _overlay;
        private bool _trayTipShown;

        public ReceiverHost Host { get; private set; } = null!;
        public static new App Current => (App)Application.Current;

        /// <summary>Set by tests that only need the XAML resources, so no receiver/tray/settings are touched.</summary>
        internal static bool ResourcesOnly { get; set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (ResourcesOnly) return;

            _instanceMutex = new Mutex(true, InstanceName, out var isFirst);
            if (!isFirst)
            {
                // Already running: bring the existing window up and exit.
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error("Unhandled UI exception", args.Exception);
                args.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error("Unobserved task exception", args.Exception);
                args.SetObserved();
            };

            var settings = Settings.Load();
            ApplyInstallerDefaults(settings, e.Args);
            Loc.Init(settings.Language);

            if (!settings.SetupDone)
            {
                var setup = new SetupWindow(settings);
                if (setup.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
                settings.SetupDone = true;
                settings.Save();
            }

            SystemHelpers.SetAutostart(settings.StartWithWindows);

            Host = new ReceiverHost(settings, this);
            CreateTray();
            await Host.StartAsync();
            Log.Info($"{AppInfo.AppName} started. Wi-Fi={Host.WifiRunning} Bluetooth={Host.BluetoothRunning}");

            StartShowListener();
            if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) ShowMain();

            if (e.Args.Contains("--updated", StringComparer.OrdinalIgnoreCase))
                _tray?.ShowBalloonTip(4000, AppInfo.AppName, Loc.F("update.done", UpdateService.CurrentVersion.ToString(3)),
                    Forms.ToolTipIcon.Info);
            StartUpdateChecks();
        }

        // ---- Updates -----------------------------------------------------------------------

        private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);
        private DispatcherTimer? _updateTimer;
        private Forms.ToolStripMenuItem? _updateMenuItem;
        private Version? _updateTipShownFor;

        public UpdateService? Updater { get; private set; }
        public UpdateInfo? AvailableUpdate { get; private set; }
        public event Action? UpdateStateChanged;

        private void StartUpdateChecks()
        {
            Updater = UpdateService.ForThisBuild();
            if (Updater == null)
            {
                Log.Info("Update checks disabled: this build has no UpdateRepo.");
                return;
            }
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = UpdateInterval;
                await CheckForUpdatesAsync(manual: false);
            };
            _updateTimer.Start();
        }

        /// <returns>true = update available, false = up to date, null = check failed or not configured.</returns>
        public async Task<bool?> CheckForUpdatesAsync(bool manual)
        {
            if (Updater == null) return null;
            try
            {
                AvailableUpdate = await Updater.CheckAsync(UpdateService.CurrentVersion);
            }
            catch (Exception ex)
            {
                Log.Error("Update check failed", ex);
                return null;
            }

            if (_updateMenuItem != null)
            {
                _updateMenuItem.Visible = AvailableUpdate != null;
                if (AvailableUpdate != null) _updateMenuItem.Text = Loc.F("update.tray", AvailableUpdate.Version.ToString(3));
            }
            if (AvailableUpdate != null && !manual && _updateTipShownFor != AvailableUpdate.Version && _tray != null)
            {
                _updateTipShownFor = AvailableUpdate.Version;
                _tray.ShowBalloonTip(5000, AppInfo.AppName, Loc.F("update.tip", AvailableUpdate.Version.ToString(3)),
                    Forms.ToolTipIcon.Info);
            }
            UpdateStateChanged?.Invoke();
            return AvailableUpdate != null;
        }

        /// <summary>Downloads the new installer, runs it silently and quits; the installer starts the new version.</summary>
        /// <returns>null when the installer was started, otherwise a message for the user.</returns>
        public async Task<string?> InstallUpdateAsync(IProgress<double> progress)
        {
            var update = AvailableUpdate;
            if (Updater == null || update == null) return null;

            if (!UpdateService.IsInstalled)
            {
                SystemHelpers.OpenUri(update.PageUrl);
                return Loc.T("update.portable");
            }
            if (Host.Manager.IsBusy) return Loc.T("update.busy");

            string path;
            try
            {
                path = await Updater.DownloadAsync(update, progress);
            }
            catch (Exception ex)
            {
                Log.Error("Update download failed", ex);
                return Loc.F("update.failed", ex.Message);
            }

            try
            {
                Log.Info($"Starting installer for {update.Version}");
                UpdateService.LaunchInstaller(path);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return Loc.T("update.cancelled"); // the user said No to the Windows permission prompt
            }
            catch (Exception ex)
            {
                Log.Error("Starting the installer failed", ex);
                return Loc.F("update.failed", ex.Message);
            }

            await QuitAsync();
            return null;
        }

        /// <summary>
        /// The installer passes its answers on the command line on first launch:
        /// <c>--bluetooth=yes|no --folder="D:\Drops"</c>.
        /// </summary>
        private static void ApplyInstallerDefaults(Settings settings, string[] args)
        {
            var changed = false;
            foreach (var arg in args)
            {
                if (arg.StartsWith("--bluetooth=", StringComparison.OrdinalIgnoreCase))
                {
                    settings.BluetoothEnabled = arg.EndsWith("=yes", StringComparison.OrdinalIgnoreCase);
                    changed = true;
                }
                else if (arg.StartsWith("--autostart=", StringComparison.OrdinalIgnoreCase))
                {
                    settings.StartWithWindows = arg.EndsWith("=yes", StringComparison.OrdinalIgnoreCase);
                    changed = true;
                }
                else if (arg.StartsWith("--folder=", StringComparison.OrdinalIgnoreCase))
                {
                    var folder = arg["--folder=".Length..].Trim('"');
                    if (folder.Length > 0)
                    {
                        settings.BaseFolder = folder;
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                settings.SetupDone = true;
                settings.Save();
            }
        }

        private void StartShowListener()
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var thread = new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.BeginInvoke(ShowMain);
            }) { IsBackground = true, Name = "ShowListener" };
            thread.Start();
        }

        private void CreateTray()
        {
            var iconStream = GetResourceStream(new Uri("pack://application:,,,/PocketDrop;component/Assets/app.ico"))!.Stream;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(Loc.T("tray.open"), null, (_, _) => ShowMain());
            menu.Items.Add(Loc.T("tray.folder"), null, (_, _) =>
            {
                System.IO.Directory.CreateDirectory(Host.Settings.BaseFolder);
                SystemHelpers.OpenFolder(Host.Settings.BaseFolder);
            });
            _updateMenuItem = new Forms.ToolStripMenuItem("", null, (_, _) => ShowMain()) { Visible = false };
            _updateMenuItem.Font = new System.Drawing.Font(_updateMenuItem.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(_updateMenuItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Loc.T("tray.quit"), null, async (_, _) => await QuitAsync());

            _tray = new Forms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(iconStream),
                Text = $"{AppInfo.AppName} — {Loc.T("status.ready")}",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.BalloonTipClicked += (_, _) => ShowMain();
            _tray.MouseClick += (_, args) =>
            {
                if (args.Button == Forms.MouseButtons.Left) ShowMain();
            };
        }

        public void ShowMain()
        {
            if (_main == null)
            {
                _main = new MainWindow(Host);
                // X = quit completely (receivers, tray icon, process). Minimize = keep running in the tray.
                _main.Closed += async (_, _) =>
                {
                    _main = null;
                    if (!IsQuitting) await QuitAsync();
                };
                _main.Hiding += OnMainHiding;
            }
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Show();
            _main.Activate();
        }

        private void OnMainHiding()
        {
            if (_trayTipShown || _tray == null) return;
            _trayTipShown = true;
            _tray.ShowBalloonTip(3000, AppInfo.AppName, Loc.T("tray.hidden"), Forms.ToolTipIcon.Info);
        }

        public bool IsQuitting { get; private set; }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            IsQuitting = true;
            base.OnSessionEnding(e);
        }

        public async Task QuitAsync()
        {
            IsQuitting = true;
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            if (Host != null) await Host.DisposeAsync();
            Shutdown();
        }

        // ---- IReceiverUi -------------------------------------------------------------------

        Task<bool> IReceiverUi.ConfirmAsync(TransferOffer offer, string folderName, CancellationToken ct) =>
            Dispatcher.InvokeAsync(() => new ConfirmWindow(offer, folderName).AskAsync(ct)).Task.Unwrap();

        void IReceiverUi.TransferStarted(ReceiveSession session) =>
            Dispatcher.BeginInvoke(() =>
            {
                _overlay?.Close();
                _overlay = new ProgressOverlay(session);
                _overlay.Closed += (_, _) => { if (_overlay?.Session == session) _overlay = null; };
                _overlay.Show();
                if (_tray != null) _tray.Text = $"{AppInfo.AppName} — {Loc.T("status.receiving")}";
            });

        void IReceiverUi.TransferFinished(ReceiveSession session) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (_tray != null) _tray.Text = $"{AppInfo.AppName} — {Loc.T("status.ready")}";
                if (_overlay?.Session == session) _overlay.ShowFinished();
                if (session.State == SessionState.Completed && Host.Settings.OpenExplorerWhenDone)
                    SystemHelpers.OpenFolder(session.FolderPath);
                _main?.RefreshRecent();
            });
    }
}
