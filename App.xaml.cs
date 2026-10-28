using System;
using System.Threading;
using System.Windows;

namespace LyricPop;

public partial class App : Application
{
    private Mutex? _mutex;
    private MainWindow? _win;
    private System.Windows.Forms.NotifyIcon? _tray;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Hanya satu instance
        _mutex = new Mutex(true, "LyricPop.arlnxs.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        _win = new MainWindow();
        BuildTray();
        _ = _win.StartAsync();
    }

    private void BuildTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Tampilkan widget", null, (_, _) => _win?.ShowAgain());

        var auto = new System.Windows.Forms.ToolStripMenuItem("Mulai bersama Windows")
        {
            CheckOnClick = true,
            Checked = Autostart.IsEnabled()
        };
        auto.CheckedChanged += (_, _) => Autostart.Set(auto.Checked);
        menu.Items.Add(auto);

        menu.Items.Add("Tentang", null, (_, _) => MainWindow.ShowAbout());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Keluar", null, (_, _) => Shutdown());

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "Lyric Pop - dibuat oleh arlnxs",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => _win?.ShowAgain();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _win?.Cleanup();
        try { _mutex?.ReleaseMutex(); } catch (Exception) { /* abaikan */ }
    }
}
