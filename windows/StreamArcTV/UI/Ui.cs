using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StreamArcTV.UI;

/// Attached properties used by the styles (a "selected" state like Android's state_selected).
public static class Ui
{
    public static readonly DependencyProperty SelectedProperty =
        DependencyProperty.RegisterAttached("Selected", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetSelected(DependencyObject d) => (bool)d.GetValue(SelectedProperty);
    public static void SetSelected(DependencyObject d, bool v) => d.SetValue(SelectedProperty, v);

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static Color Color(string key) => (Color)Application.Current.Resources[key];

    public static T Res<T>(string key) => (T)Application.Current.Resources[key];

    /// Runs on the UI thread.
    public static void Post(Action a) => Application.Current?.Dispatcher.BeginInvoke(a, DispatcherPriority.Normal);

    /// Milliseconds since an arbitrary point, like SystemClock.elapsedRealtime().
    public static long Elapsed => Environment.TickCount64;

    public static bool IsEnterLike(Key k) => k is Key.Enter or Key.Space;

    /// The "hold OK" key on a keyboard: the context-menu (Apps) key or a plain M.
    public static bool IsMenuKey(Key k) => k is Key.Apps;

    /// True when the host window is narrow like a phone held upright.
    public static bool Compact(FrameworkElement e)
    {
        var w = e.ActualWidth > 0 ? e.ActualWidth : (App.Window?.ActualWidth ?? 1280);
        var h = e.ActualHeight > 0 ? e.ActualHeight : (App.Window?.ActualHeight ?? 720);
        var phone = Data.Prefs.Instance.LayoutMode == "phone";
        return w < 640 || (phone && h > w);
    }

    public static bool Landscape(FrameworkElement e)
    {
        var w = e.ActualWidth > 0 ? e.ActualWidth : (App.Window?.ActualWidth ?? 1280);
        var h = e.ActualHeight > 0 ? e.ActualHeight : (App.Window?.ActualHeight ?? 720);
        return w >= h;
    }
}

/// <summary>
/// A focusable card that reports a click (Enter, Space, left click) and a long click (holding
/// Enter, right click, the context-menu key): the Windows counterpart of "press OK / hold OK".
/// </summary>
public class FocusCard : ContentControl
{
    public static readonly DependencyProperty SelectedProperty =
        DependencyProperty.Register(nameof(Selected), typeof(bool), typeof(FocusCard), new FrameworkPropertyMetadata(false));

    public bool Selected { get => (bool)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    public event Action? Click;
    public event Action? LongClick;

    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private bool _holdFired;
    private bool _keyDown;

    public FocusCard()
    {
        Focusable = true;
        IsTabStop = true;
        _hold.Tick += (_, _) => { _hold.Stop(); if (_keyDown) { _holdFired = true; LongClick?.Invoke(); } };
        FocusVisualStyle = null;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        Focus();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseOver) { e.Handled = true; Click?.Invoke(); }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Focus();
        e.Handled = true;
        (LongClick ?? Click)?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Ui.IsEnterLike(e.Key))
        {
            e.Handled = true;
            if (e.IsRepeat) return;
            _keyDown = true; _holdFired = false;
            if (LongClick != null) _hold.Start();
            return;
        }
        if (Ui.IsMenuKey(e.Key) && LongClick != null) { e.Handled = true; LongClick.Invoke(); return; }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (Ui.IsEnterLike(e.Key))
        {
            e.Handled = true;
            _hold.Stop();
            if (_keyDown && !_holdFired) Click?.Invoke();
            _keyDown = false; _holdFired = false;
            return;
        }
        base.OnKeyUp(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _hold.Stop(); _keyDown = false;
    }
}

/// A plain button whose Enter/Space/click all raise Click (WPF Button already does), kept for symmetry.
public static class Buttons
{
    public static void OnClick(this Button b, Action a) => b.Click += (_, _) => a();
}
