using System.Drawing;
using System.IO;
using System.Windows;
using StreamArcTV.Transfer;

namespace StreamArcTV.UI;

/// <summary>
/// Notification-area icon: keeps downloads and recordings running when the window is closed,
/// and shows "finished / failed" notices (the counterpart of the Android foreground service and
/// its notifications).
/// </summary>
public static class Tray
{
    private static System.Windows.Forms.NotifyIcon? _icon;

    public static void Init()
    {
        try
        {
            _icon = new System.Windows.Forms.NotifyIcon { Text = "Stream Arc TV", Visible = true };
            var iconUri = new Uri("pack://application:,,,/Assets/app.ico");
            using var s = Application.GetResourceStream(iconUri)?.Stream;
            if (s != null) _icon.Icon = new Icon(s);
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open Stream Arc TV", null, (_, _) => Open());
            menu.Items.Add("Downloads", null, (_, _) => { Open(); Nav.Push(new TransfersPage(TransferType.DOWNLOAD)); });
            menu.Items.Add("Recordings", null, (_, _) => { Open(); Nav.Push(new TransfersPage(TransferType.RECORDING)); });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => Quit());
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += (_, _) => Open();
            TransferService.Finished += (job, ok, error) =>
            {
                var text = ok ? $"{job.Title} finished." : $"{job.Title} failed: {error}";
                ShowBalloon("Stream Arc TV", text, ok ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
            };
        }
        catch { _icon = null; }
    }

    public static void ShowBalloon(string title, string text, System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info)
    {
        try { _icon?.ShowBalloonTip(5000, title, text, icon); } catch { }
    }

    public static void Open()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var w = App.Window;
            if (w == null) return;
            w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        });
    }

    public static void Quit()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            App.Window?.ForceClose();
            Application.Current.Shutdown();
        });
    }

    public static void Dispose()
    {
        try { if (_icon != null) { _icon.Visible = false; _icon.Dispose(); } } catch { }
        _icon = null;
    }
}
