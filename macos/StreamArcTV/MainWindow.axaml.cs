using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StreamArcTV.Data;
using StreamArcTV.Transfer;
using StreamArcTV.UI;
using StreamArcTV.Util;

namespace StreamArcTV;

/// <summary>
/// The single window that hosts every screen (each Android activity is a page pushed onto
/// <see cref="Nav"/>). Escape, Backspace (Delete on a Mac keyboard), the browser-back key and the
/// mouse's back button go back; F11 (or Ctrl+Cmd+F) toggles full screen.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2800) };
    private bool _forceClose;
    private WindowState _stateBeforeFull = WindowState.Normal;
    private Border? _toastInPage;

    public MainWindow()
    {
        InitializeComponent();
        Nav.Attach(Host);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBox.IsVisible = false; if (_toastInPage != null) _toastInPage.IsVisible = false; };
        RestorePlacement();
        Opened += (_, _) => { if (Nav.Depth == 0) Nav.Push(new HomePage()); };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed) { Nav.Back(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        Closing += OnClosingWindow;
    }

    public bool IsFullScreen => WindowState == WindowState.FullScreen;

    public void SetFullScreen(bool on)
    {
        if (on == IsFullScreen) return;
        if (on)
        {
            _stateBeforeFull = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = _stateBeforeFull == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Nav.Current?.OnKey(e) == true) { e.Handled = true; return; }
        var cmdCtrlF = e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.F11 || cmdCtrlF) { SetFullScreen(!IsFullScreen); e.Handled = true; return; }
        var inText = FocusManager?.GetFocusedElement() is TextBox;
        if (e.Key == Key.Escape || e.Key == Key.BrowserBack || (e.Key == Key.Back && !inText))
        {
            if (Nav.Depth <= 1 && IsFullScreen && e.Key == Key.Escape) { SetFullScreen(false); e.Handled = true; return; }
            Nav.Back();
            e.Handled = true;
        }
    }

    /// Shows a short notice at the bottom; inside the player it goes over the video, which sits
    /// above everything else in the window.
    public void ShowToast(string text)
    {
        ToastBox.IsVisible = false;
        if (_toastInPage != null) { _toastInPage.IsVisible = false; _toastInPage = null; }
        var host = Nav.Current?.ToastHost;
        if (host != null)
        {
            var box = host.Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag, "toast"));
            if (box == null)
            {
                box = new Border
                {
                    Tag = "toast", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 96), Background = Ui.Brush("ToastBrush"), BorderBrush = Ui.Brush("OutlineBrush"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 10), MaxWidth = 640, IsHitTestVisible = false, ZIndex = 100,
                    Child = new TextBlock { Foreground = Ui.Brush("TextBrush"), FontSize = 14, TextWrapping = Avalonia.Media.TextWrapping.Wrap, TextAlignment = Avalonia.Media.TextAlignment.Center }
                };
                host.Children.Add(box);
            }
            ((TextBlock)box.Child!).Text = text;
            box.IsVisible = true;
            _toastInPage = box;
        }
        else
        {
            ToastText.Text = text;
            ToastBox.IsVisible = true;
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void MarkForceClose() => _forceClose = true;

    public void ForceClose() { _forceClose = true; Close(); }

    private void OnClosingWindow(object? sender, WindowClosingEventArgs e)
    {
        SavePlacement();
        if (!_forceClose && TransferService.AnyActive)
        {
            // Keep downloads and recordings going in the background, like the Android service.
            e.Cancel = true;
            Hide();
            Mac.Notify("Stream Arc TV", "Still downloading or recording in the background. Use the menu-bar icon to open the app; Quit from its menu to stop.");
            return;
        }
        if (!_forceClose)
        {
            e.Cancel = true;
            Nav.PopAll();
            _forceClose = true;
            App.Quit();
            return;
        }
        Nav.PopAll();
    }

    private void RestorePlacement()
    {
        try
        {
            var p = Prefs.Instance.WindowPlacement?.Split(',');
            if (p == null || p.Length < 5) return;
            var l = int.Parse(p[0]); var t = int.Parse(p[1]); var w = double.Parse(p[2]); var h = double.Parse(p[3]);
            if (w < MinWidth || h < MinHeight) return;
            var probe = new PixelPoint(l + 60, t + 20);
            if (!Screens.All.Any(s => s.Bounds.Contains(probe))) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(l, t);
            Width = w; Height = h;
            if (p[4] == "max") WindowState = WindowState.Maximized;
        }
        catch { }
    }

    private void SavePlacement()
    {
        try
        {
            if (WindowState == WindowState.FullScreen || WindowState == WindowState.Minimized) return;
            var prev = Prefs.Instance.WindowPlacement?.Split(',');
            if (WindowState == WindowState.Maximized)
            {
                // Keep the last normal placement and just remember that it was maximized.
                if (prev is { Length: >= 5 }) Prefs.Instance.WindowPlacement = string.Join(",", prev[..4]) + ",max";
                return;
            }
            Prefs.Instance.WindowPlacement = $"{Position.X},{Position.Y},{ClientSize.Width},{ClientSize.Height},normal";
        }
        catch { }
    }
}
