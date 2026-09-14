using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using StreamArcTV.Transfer;
using StreamArcTV.Util;

namespace StreamArcTV.UI;

/// <summary>
/// Tray (macOS menu bar / Linux status area) icon: keeps downloads and recordings running when the
/// window is closed, and posts "finished / failed" notices (the counterpart of the Android foreground
/// service and its notifications).
/// </summary>
public static class Tray
{
    private static TrayIcon? _icon;

    public static void Init()
    {
        try
        {
            var menu = new NativeMenu();
            menu.Add(Item("Open Stream Arc TV", Open));
            menu.Add(Item("Downloads", () => { Open(); Nav.Push(new TransfersPage(TransferType.DOWNLOAD)); }));
            menu.Add(Item("Recordings", () => { Open(); Nav.Push(new TransfersPage(TransferType.RECORDING)); }));
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(Item("Quit", Quit));
            _icon = new TrayIcon { ToolTipText = "Stream Arc TV", Menu = menu, IsVisible = true };
            using (var s = AssetLoader.Open(new Uri("avares://StreamArcTV/Assets/ic_logo_mark.png")))
            using (var full = new Bitmap(s))
            {
                // A status-bar sized copy (the asset is 512 px).
                _icon.Icon = new WindowIcon(full.CreateScaledBitmap(new PixelSize(44, 44), BitmapInterpolationMode.HighQuality));
            }
            _icon.Clicked += (_, _) => Open();
            if (Application.Current != null) TrayIcon.SetIcons(Application.Current, new TrayIcons { _icon });
            TransferService.Finished += (job, ok, error) =>
            {
                var text = ok ? $"{job.Title} finished." : $"{job.Title} failed: {error}";
                Platform.Notify("Stream Arc TV", text);
            };
        }
        catch (Exception e) { AppLog.W("Tray", "tray icon failed: " + e.Message); _icon = null; }
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    public static void Open()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var w = App.Window;
            if (w == null) return;
            w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            if (Nav.Depth == 0) Nav.Push(new HomePage());
        });
    }

    public static void Quit()
    {
        Dispatcher.UIThread.Post(() =>
        {
            App.Window?.ForceClose();
            App.Quit();
        });
    }

    public static void Dispose()
    {
        try { if (_icon != null) { _icon.IsVisible = false; _icon.Dispose(); } } catch { }
        _icon = null;
    }
}
