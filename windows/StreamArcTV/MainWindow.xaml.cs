using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StreamArcTV.Data;
using StreamArcTV.Transfer;
using StreamArcTV.UI;

namespace StreamArcTV;

/// <summary>
/// The single window that hosts every screen (each Android activity is a page pushed onto
/// <see cref="Nav"/>). Escape, Backspace, the browser-back key and the mouse's back button go
/// back; F11 toggles full screen.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2800) };
    private bool _forceClose;
    private WindowState _stateBeforeFull = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        Nav.Attach(Host);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBox.Visibility = Visibility.Collapsed; };
        RestorePlacement();
        Loaded += (_, _) => { if (Nav.Depth == 0) Nav.Push(new HomePage()); };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { Nav.Back(); e.Handled = true; return; }
            // The second click of a double-click must not act on the page that just opened.
            if (Nav.SinceNavigationMs < Nav.CLICK_GUARD_MS) e.Handled = true;
        };
        PreviewMouseUp += (_, e) => { if (Nav.SinceNavigationMs < Nav.CLICK_GUARD_MS) e.Handled = true; };
    }

    private bool _mini;
    private Rect _boundsBeforeMini;
    private WindowState _stateBeforeMini = WindowState.Normal;
    private double _minWidthBefore, _minHeightBefore;

    /// True while the window is the small always-on-top mini player.
    public bool IsMini => _mini;

    /// A small window pinned above everything else in the bottom-right corner, so a channel can keep
    /// playing while you work. Turning it off puts the window back where it was.
    public void SetMini(bool on)
    {
        if (on == _mini) return;
        if (on)
        {
            if (IsFullScreen) SetFullScreen(false);
            _stateBeforeMini = WindowState;
            _boundsBeforeMini = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _minWidthBefore = MinWidth; _minHeightBefore = MinHeight;
            WindowState = WindowState.Normal;
            MinWidth = 320; MinHeight = 200;
            Width = 480; Height = 300;
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 16;
            Top = wa.Bottom - Height - 16;
            Topmost = true;
            _mini = true;
        }
        else
        {
            _mini = false;
            Topmost = false;
            MinWidth = _minWidthBefore; MinHeight = _minHeightBefore;
            Left = _boundsBeforeMini.Left; Top = _boundsBeforeMini.Top;
            Width = _boundsBeforeMini.Width; Height = _boundsBeforeMini.Height;
            WindowState = _stateBeforeMini == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
    }

    public bool IsFullScreen => WindowStyle == WindowStyle.None && WindowState == WindowState.Maximized;

    public void SetFullScreen(bool on)
    {
        if (on == IsFullScreen) return;
        if (on)
        {
            _stateBeforeFull = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;   // force a re-layout so the taskbar is covered
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFull == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Nav.Current?.OnKey(e) == true) { e.Handled = true; return; }
        if (e.Key == Key.F11) { SetFullScreen(!IsFullScreen); e.Handled = true; return; }
        var inText = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox;
        if (e.Key == Key.Escape || e.Key == Key.BrowserBack || (e.Key == Key.Back && !inText))
        {
            if (Nav.Depth <= 1 && IsFullScreen && e.Key == Key.Escape) { SetFullScreen(false); e.Handled = true; return; }
            Nav.Back();
            e.Handled = true;
        }
    }

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void ForceClose() { _forceClose = true; Close(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        if (!_forceClose && TransferService.AnyActive)
        {
            // Keep downloads and recordings going in the background, like the Android service.
            e.Cancel = true;
            Hide();
            Tray.ShowBalloon("Stream Arc TV", "Still downloading or recording in the background. Double-click the tray icon to open the app; Quit from its menu to stop.");
            return;
        }
        if (!_forceClose)
        {
            e.Cancel = true;
            Nav.PopAll();
            _forceClose = true;
            Application.Current.Shutdown();
            return;
        }
        Nav.PopAll();
        base.OnClosing(e);
    }

    private void RestorePlacement()
    {
        try
        {
            var p = Prefs.Instance.WindowPlacement?.Split(',');
            if (p == null || p.Length < 5) return;
            var l = double.Parse(p[0]); var t = double.Parse(p[1]); var w = double.Parse(p[2]); var h = double.Parse(p[3]);
            if (w < MinWidth || h < MinHeight) return;
            var screen = SystemParameters.VirtualScreenWidth;
            if (l < -w + 100 || l > screen - 100) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l; Top = t; Width = w; Height = h;
            if (p[4] == "max") WindowState = WindowState.Maximized;
        }
        catch { }
    }

    private void SavePlacement()
    {
        try
        {
            var b = _mini ? _boundsBeforeMini : RestoreBounds;
            var maximized = _mini ? _stateBeforeMini == WindowState.Maximized : WindowState == WindowState.Maximized;
            Prefs.Instance.WindowPlacement = $"{b.Left},{b.Top},{b.Width},{b.Height},{(maximized ? "max" : "normal")}";
        }
        catch { }
    }
}
