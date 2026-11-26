using System;
using System.Threading;
using System.Windows;
using PocketDrop.Core;
using PocketDrop.UI;
using Xunit;

namespace PocketDrop.Tests
{
    public sealed class WindowBehaviorTests
    {
        [Fact]
        public void Minimizing_main_window_hides_it_from_taskbar_but_keeps_it_alive()
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    App.ResourcesOnly = true;
                    if (Application.Current == null) new App().InitializeComponent();

                    var host = new ReceiverHost(new Settings { BaseFolder = System.IO.Path.GetTempPath() }, new FakeUi());
                    var window = new MainWindow(host) { ShowActivated = false, Left = -5000 };
                    var hidden = 0;
                    window.Hiding += () => hidden++;
                    window.Show();
                    Assert.True(window.IsVisible);

                    window.WindowState = WindowState.Minimized;
                    Assert.False(window.IsVisible);   // gone from the taskbar
                    Assert.Equal(1, hidden);          // tray tip shown once

                    // What App.ShowMain does when the tray icon or the shortcut is used.
                    window.WindowState = WindowState.Normal;
                    window.Show();
                    Assert.True(window.IsVisible);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) throw error;
        }
    }
}
