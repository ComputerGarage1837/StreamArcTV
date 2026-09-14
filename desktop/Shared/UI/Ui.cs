using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace StreamArcTV.UI;

/// Small helpers shared by every screen (resources, the UI thread, layout questions).
public static class Ui
{
    public static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;
    public static T Res<T>(string key) => (T)Application.Current!.FindResource(key)!;
    public static Geometry Icon(string key) => Res<Geometry>(key);

    /// Runs on the UI thread.
    public static void Post(Action a) => Dispatcher.UIThread.Post(a);

    /// Milliseconds since an arbitrary point, like SystemClock.elapsedRealtime().
    public static long Elapsed => Environment.TickCount64;

    public static bool IsEnterLike(Key k) => k is Key.Enter or Key.Space;

    /// The "hold OK" key on a keyboard: the context-menu (Apps) key.
    public static bool IsMenuKey(Key k) => k is Key.Apps;

    /// The "selected" state used by the styles (like Android's state_selected) on plain buttons.
    public static void SetSelected(StyledElement e, bool v) => e.Classes.Set("selected", v);

    public static Window? Owner => App.Window is { IsVisible: true } m ? m : null;

    public static IInputElement? Focused => App.Window?.FocusManager?.GetFocusedElement();

    public static double WindowWidth => App.Window?.ClientSize.Width is > 0 and var w ? w : 1280;
    public static double WindowHeight => App.Window?.ClientSize.Height is > 0 and var h ? h : 720;

    /// True when the host window is narrow like a phone held upright.
    public static bool Compact(Control e)
    {
        var w = e.Bounds.Width > 0 ? e.Bounds.Width : WindowWidth;
        var h = e.Bounds.Height > 0 ? e.Bounds.Height : WindowHeight;
        var phone = Data.Prefs.Instance.LayoutMode == "phone";
        return w < 640 || (phone && h > w);
    }

    public static bool Landscape(Control e)
    {
        var w = e.Bounds.Width > 0 ? e.Bounds.Width : WindowWidth;
        var h = e.Bounds.Height > 0 ? e.Bounds.Height : WindowHeight;
        return w >= h;
    }

    private static readonly Dictionary<string, Bitmap> Assets = new();

    /// A bundled image (Assets/name.png), decoded once.
    public static Bitmap Asset(string name)
    {
        lock (Assets)
        {
            if (!Assets.TryGetValue(name, out var b))
            {
                using var s = AssetLoader.Open(new Uri($"avares://StreamArcTV/Assets/{name}"));
                Assets[name] = b = new Bitmap(s);
            }
            return b;
        }
    }

    public static Image AssetImage(string name, double w, double h) => new() { Source = Asset(name), Width = w, Height = h, Stretch = Stretch.Uniform };

    public static Path IconPath(string key, IBrush fill, double size) => new() { Data = Icon(key), Fill = fill, Width = size, Height = size, Stretch = Stretch.Uniform };

    public static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    public static readonly Cursor NoCursor = new(StandardCursorType.None);
    public static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    public static ImmutableSolidColorBrush Color(uint argb) => new(argb);
}

/// <summary>
/// A focusable card that reports a click (Enter, Space, left click) and a long click (holding
/// Enter, right click or Control-click, the context-menu key): the desktop counterpart of
/// "press OK / hold OK".
/// </summary>
public class FocusCard : ContentControl
{
    public static readonly StyledProperty<bool> SelectedProperty =
        AvaloniaProperty.Register<FocusCard, bool>(nameof(Selected));

    public bool Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    public event Action? Click;
    public event Action? LongClick;

    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private bool _holdFired;
    private bool _keyDown;
    private bool _pressed;

    public FocusCard()
    {
        Focusable = true;
        IsTabStop = true;
        _hold.Tick += (_, _) => { _hold.Stop(); if (_keyDown) { _holdFired = true; LongClick?.Invoke(); } };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedProperty) PseudoClasses.Set(":selected", Selected);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        Focus();
        if (props.IsRightButtonPressed)
        {
            e.Handled = true;
            (LongClick ?? Click)?.Invoke();
            return;
        }
        if (props.IsLeftButtonPressed) _pressed = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed || e.InitialPressMouseButton != MouseButton.Left) return;
        _pressed = false;
        if (IsPointerOver) { e.Handled = true; Click?.Invoke(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Ui.IsEnterLike(e.Key))
        {
            e.Handled = true;
            if (_keyDown) return;   // key repeat
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

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _hold.Stop(); _keyDown = false; _pressed = false;
    }
}

/// A settings row with a switch on the right (the SwitchCompat look). Enter, Space or a click toggles it.
public class SwitchRow : FocusCard
{
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 15.5, TextWrapping = TextWrapping.Wrap };
    private readonly Border _track = new() { CornerRadius = new CornerRadius(11), Background = Ui.Color(0xFF3B4B63), Width = 44, Height = 22 };
    private readonly Ellipse _thumb = new() { Width = 16, Height = 16, Fill = Ui.Color(0xFFCBD5E1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0) };
    private bool _checked;

    public event Action<bool>? Toggled;

    public SwitchRow() : this("", false) { }

    public SwitchRow(string text, bool isChecked)
    {
        Classes.Add("switch");
        _label.Text = text;
        var knob = new Panel { Width = 44, Height = 22, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        knob.Children.Add(_track);
        knob.Children.Add(_thumb);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(_label);
        Grid.SetColumn(knob, 1);
        grid.Children.Add(knob);
        Content = grid;
        IsChecked = isChecked;
        Click += () => { IsChecked = !IsChecked; Toggled?.Invoke(IsChecked); };
    }

    public bool IsChecked
    {
        get => _checked;
        set
        {
            _checked = value;
            _track.Background = value ? Ui.Brush("AccentBrush") : Ui.Color(0xFF3B4B63);
            _thumb.Fill = value ? Ui.Brush("BgBrush") : Ui.Color(0xFFCBD5E1);
            _thumb.HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _thumb.Margin = value ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
        }
    }

    public string Text { get => _label.Text ?? ""; set => _label.Text = value; }
}

/// A radio or check-box row for dialogs.
public class ChoiceRow : FocusCard
{
    private readonly bool _radio;
    private readonly Border _box;
    private readonly Control _mark;
    private bool _checked;

    public event Action<bool>? Changed;

    public ChoiceRow(string text, bool isChecked, bool radio)
    {
        _radio = radio;
        Classes.Add("choice");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        _box = new Border
        {
            Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = Ui.Brush("TextMutedBrush"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(radio ? 10 : 4)
        };
        _mark = radio
            ? new Ellipse { Width = 10, Height = 10, Fill = Ui.Brush("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            : new Path { Data = Geometry.Parse("M3,8 L6.5,11.5 L13,4.5"), Stroke = Ui.Brush("BgBrush"), StrokeThickness = 2.5, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _box.Child = _mark;
        grid.Children.Add(_box);
        var label = new TextBlock { Text = text, FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        Content = grid;
        IsChecked = isChecked;
        Click += () => { if (_radio && _checked) { Changed?.Invoke(true); return; } IsChecked = !_checked; Changed?.Invoke(_checked); };
    }

    public bool IsChecked
    {
        get => _checked;
        set
        {
            _checked = value;
            _mark.IsVisible = value;
            if (!_radio)
            {
                _box.Background = value ? Ui.Brush("AccentBrush") : Brushes.Transparent;
                _box.BorderBrush = value ? Ui.Brush("AccentBrush") : Ui.Brush("TextMutedBrush");
            }
        }
    }
}

/// Thin progress bar (progress_epg): a determinate 0..Maximum bar or a moving glow when indeterminate.
public class ThinProgress : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ThinProgress, double>(nameof(Value));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<ThinProgress, double>(nameof(Maximum), 1000);
    public static readonly StyledProperty<bool> IsIndeterminateProperty = AvaloniaProperty.Register<ThinProgress, bool>(nameof(IsIndeterminate));
    public static readonly StyledProperty<IBrush> ForegroundProperty = AvaloniaProperty.Register<ThinProgress, IBrush>(nameof(Foreground), new ImmutableSolidColorBrush(0xFF22D3EE));
    public static readonly StyledProperty<IBrush> BackgroundProperty = AvaloniaProperty.Register<ThinProgress, IBrush>(nameof(Background), new ImmutableSolidColorBrush(0xFF1E3A5F));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public IBrush Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _phase;

    static ThinProgress()
    {
        AffectsRender<ThinProgress>(ValueProperty, MaximumProperty, IsIndeterminateProperty, ForegroundProperty, BackgroundProperty);
    }

    public ThinProgress()
    {
        Height = 4;
        Focusable = false;
        IsHitTestVisible = false;
        _timer.Tick += (_, _) => { _phase = (_phase + 0.03) % 1.2; if (IsEffectivelyVisible) InvalidateVisual(); };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsIndeterminateProperty) UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); UpdateTimer(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }

    private void UpdateTimer()
    {
        if (IsIndeterminate && this.GetVisualRoot() != null) _timer.Start(); else _timer.Stop();
    }

    public override void Render(DrawingContext c)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var r = Math.Min(2, h / 2);
        c.DrawRectangle(Background, null, new Rect(0, 0, w, h), r, r);
        if (IsIndeterminate)
        {
            var glow = Math.Min(60, w / 3);
            var x = _phase * (w + glow) - glow;
            using (c.PushClip(new Rect(0, 0, w, h)))
                c.DrawRectangle(Foreground, null, new Rect(x, 0, glow, h), r, r);
        }
        else
        {
            var frac = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
            if (frac > 0) c.DrawRectangle(Foreground, null, new Rect(0, 0, w * frac, h), r, r);
        }
    }
}

/// Circular indeterminate spinner.
public class Spinner : Control
{
    private static readonly IPen Ring = new ImmutablePen(new ImmutableSolidColorBrush(0xFF334155), 4);
    private static readonly IPen Arc = new ImmutablePen(new ImmutableSolidColorBrush(0xFF22D3EE), 4, null, PenLineCap.Round);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _angle;

    public Spinner()
    {
        Width = 40; Height = 40;
        Focusable = false;
        IsHitTestVisible = false;
        _timer.Tick += (_, _) => { _angle = (_angle + 0.12) % (2 * Math.PI); if (IsEffectivelyVisible) InvalidateVisual(); };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }

    public override void Render(DrawingContext c)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height);
        if (s <= 8) return;
        var r = s / 2 - 3;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        c.DrawEllipse(null, Ring, center, r, r);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(center.X, center.Y - r), false);
            g.ArcTo(new Point(center.X + r, center.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        var m = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(_angle) * Matrix.CreateTranslation(center.X, center.Y);
        using (c.PushTransform(m)) c.DrawGeometry(null, Arc, geo);
    }
}

internal static class Ext
{
    public static void Let<T>(this T v, Action<T> a) where T : class => a(v);
}
