using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PocketDrop.Core;
using PocketDrop.UI;
using Xunit;

namespace PocketDrop.Tests
{
    /// <summary>
    /// Renders the windows to PNG for a visual check. Runs only when POCKETDROP_SCREENSHOTS points
    /// to an output folder.
    /// </summary>
    public sealed class ScreenshotTests
    {
        [Fact]
        public void Render_windows()
        {
            var outDir = Environment.GetEnvironmentVariable("POCKETDROP_SCREENSHOTS");
            if (string.IsNullOrEmpty(outDir)) return;
            Directory.CreateDirectory(outDir);

            Exception? error = null;
            var thread = new Thread(() =>
            {
                try { RenderAll(outDir); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) throw error;
        }

        private static void RenderAll(string outDir)
        {
            var lang = Environment.GetEnvironmentVariable("POCKETDROP_LANG") ?? "en";
            Loc.Init(lang);
            App.ResourcesOnly = true; // never start the real receiver from a test
            var app = new App();
            app.InitializeComponent();

            var offer = new TransferOffer
            {
                Device = "Pixel 8 Pro",
                Transport = "Wi-Fi",
                Folder = new FolderChoice { Mode = "new", Name = "Holiday 2026" },
            };
            for (var i = 1; i <= 7; i++)
                offer.Files.Add(new OfferedFile { Name = $"IMG_20261125_14{i:00}12.jpg", Size = 3_400_000 + i * 120_000 });

            Save(new ConfirmWindow(offer, "Holiday 2026"), Path.Combine(outDir, $"confirm-{lang}.png"));

            var session = new ReceiveSession(offer, "Holiday 2026", Path.Combine(Path.GetTempPath(), "pd-shot"));
            Save(new ProgressOverlay(session), Path.Combine(outDir, $"progress-{lang}.png"));

            var settings = new Settings { BaseFolder = @"D:\PocketDrop", DeviceName = "DESKTOP-ABC", BluetoothEnabled = true };
            var host = new ReceiverHost(settings, new FakeUi());
            Save(new MainWindow(host), Path.Combine(outDir, $"main-{lang}.png"));
            Save(new SetupWindow(settings), Path.Combine(outDir, $"setup-{lang}.png"));

            // The whole window, with "from anywhere" connected.
            var remoteSettings = new Settings { BaseFolder = @"D:\PocketDrop", DeviceName = "DESKTOP-ABC", RemoteEnabled = true };
            var remoteHost = new ReceiverHost(remoteSettings, new FakeUi());
            var tunnel = new PocketDrop.Transport.CloudflareTunnel(AppInfo.RemotePort);
            tunnel.ShowForPreview("https://quiet-river-sample-words.trycloudflare.com", "3f9c2a7b51d04e6a8c1b2d3e4f5a6b7c");
            remoteHost.SetTunnelForPreview(tunnel);
            Save(new MainWindow(remoteHost) { Height = 2100 }, Path.Combine(outDir, $"main-full-{lang}.png"));
        }

        private static void Save(Window w, string path)
        {
            w.ShowActivated = false;
            w.Left = -5000;
            w.Top = 0;
            w.Show();
            w.UpdateLayout();
            Dispatch();
            var content = (FrameworkElement)w.Content;
            var width = (int)Math.Ceiling(content.ActualWidth);
            var height = (int)Math.Ceiling(content.ActualHeight);
            var bmp = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var ctx = visual.RenderOpen())
            {
                ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x80, 0x88, 0x99)), null, new Rect(0, 0, width, height));
                ctx.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
            }
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
            w.Hide();
        }

        private static void Dispatch()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }
}
