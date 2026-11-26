using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PocketDrop.Core;
using PocketDrop.Transport;

namespace PocketDrop.UI
{
    /// <summary>First run: "Do you have Bluetooth?" and "Where should files go?".</summary>
    public partial class SetupWindow : Window
    {
        private readonly Settings _settings;

        public SetupWindow(Settings settings)
        {
            InitializeComponent();
            _settings = settings;
            FolderBox.Text = settings.BaseFolder;
            BtNo.IsChecked = true;

            Loaded += async (_, _) =>
            {
                var present = await BluetoothTransport.IsAdapterPresentAsync();
                AdapterText.Text = Loc.T(present ? "setup.btDetected" : "setup.btNotDetected");
                if (present) BtYes.IsChecked = true;
            };
        }

        public static string? PickFolder(Window owner, string current)
        {
            var dialog = new OpenFolderDialog
            {
                Title = Loc.T("folder.pick"),
                InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var picked = PickFolder(this, FolderBox.Text);
            if (picked != null) FolderBox.Text = picked;
        }

        private void OnFinish(object sender, RoutedEventArgs e)
        {
            var folder = FolderBox.Text.Trim();
            if (folder.Length == 0) folder = Settings.DefaultBaseFolder;
            try
            {
                folder = Path.GetFullPath(folder);
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Loc.F("error.folder", ex.Message), AppInfo.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.BaseFolder = folder;
            _settings.BluetoothEnabled = BtYes.IsChecked == true;
            _settings.WifiEnabled = true;
            _settings.StartWithWindows = AutostartBox.IsChecked == true;
            DialogResult = true;
        }
    }
}
