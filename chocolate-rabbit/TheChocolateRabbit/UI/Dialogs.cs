using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TheChocolateRabbit.UI;

public enum DialogResultKind { Positive, Negative, Neutral, Cancel }

/// <summary>
/// Keyboard-friendly dialogs in the app's chocolate-and-gold look (the same dialog set as Stream
/// Arc TV for Windows): message boxes, item lists, custom content, progress, and short toasts at
/// the bottom of the window.
/// </summary>
public static class Dialogs
{
    private static Window? Owner => App.Window is { IsVisible: true } m ? m : null;

    public static void Toast(string text)
    {
        Ui.Post(() => App.Window?.ShowToast(text));
    }

    public static DialogResultKind Alert(string title, string? message, string positive = "OK", string? negative = null, string? neutral = null, bool cancelable = true)
    {
        var d = new DialogWindow(title, cancelable);
        if (!string.IsNullOrEmpty(message)) d.SetMessage(message);
        d.SetButtons(positive, negative, neutral);
        return d.Run();
    }

    /// A list of choices; returns the index or -1.
    public static int Items(string title, IList<string> items, string? negative = "Cancel")
    {
        var d = new DialogWindow(title, true);
        var idx = -1;
        var panel = new StackPanel();
        for (var i = 0; i < items.Count; i++)
        {
            var n = i;
            var b = new Button { Content = items[i], Style = Ui.Res<Style>("ListRowButton") };
            b.Click += (_, _) => { idx = n; d.Result = DialogResultKind.Positive; d.Close(); };
            panel.Children.Add(b);
        }
        d.SetContent(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460 });
        d.SetButtons(null, negative, null);
        d.FocusFirst = panel.Children.Count > 0 ? panel.Children[0] : null;
        d.Run();
        return idx;
    }

    /// Dialog with custom content.
    public static DialogResultKind Custom(string title, FrameworkElement content, string? positive, string? negative = "Cancel", string? neutral = null, IInputElement? focus = null, bool cancelable = true)
    {
        var d = new DialogWindow(title, cancelable);
        d.SetContent(content);
        d.SetButtons(positive, negative, neutral);
        d.FocusFirst = focus;
        return d.Run();
    }

    public class ProgressHandle
    {
        internal DialogWindow? Window;
        internal ProgressBar? Bar;
        internal TextBlock? Text;

        /// <param name="fraction">0..1, or null for indeterminate.</param>
        public void Report(double? fraction, string text)
        {
            Ui.Post(() =>
            {
                if (Bar == null || Text == null) return;
                if (fraction == null) Bar.IsIndeterminate = true;
                else { Bar.IsIndeterminate = false; Bar.Value = Math.Clamp(fraction.Value, 0, 1) * 1000; }
                Text.Text = text;
            });
        }

        public void Close() => Ui.Post(() => { if (Window != null) { Window.Result = DialogResultKind.Cancel; Window.AllowClose = true; Window.Close(); } });
    }

    /// Non-blocking progress dialog (returns immediately; use Report/Close).
    public static ProgressHandle Progress(string title, string text, Action? onCancel)
    {
        var h = new ProgressHandle();
        var d = new DialogWindow(title, false);
        // Fixed width and a fixed-height text line so the box never pulses as the text changes.
        var panel = new StackPanel { Margin = new Thickness(4, 8, 4, 0), Width = 420 };
        var bar = new ProgressBar { IsIndeterminate = true, Maximum = 1000, Height = 8, Style = Ui.Res<Style>("ThinProgress") };
        var txt = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 0), Foreground = Ui.Brush("TextMutedBrush"), FontSize = 14, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Height = 20 };
        panel.Children.Add(bar); panel.Children.Add(txt);
        d.SetContent(panel);
        d.SetButtons(null, onCancel != null ? "Cancel" : null, null);
        d.AllowClose = false;
        d.Loaded += (_, _) => { d.SizeToContent = SizeToContent.Manual; d.Width = d.ActualWidth; d.Height = d.ActualHeight; };
        d.Closed += (_, _) => { if (d.Result == DialogResultKind.Negative) onCancel?.Invoke(); };
        h.Window = d; h.Bar = bar; h.Text = txt;
        d.Owner = Owner;
        d.Show();
        return h;
    }
}

/// The dialog window used by <see cref="Dialogs"/>.
public class DialogWindow : Window
{
    private readonly TextBlock _title;
    private readonly ContentPresenter _body;
    private readonly StackPanel _buttons;
    private Button? _positive, _negative, _neutral;
    public DialogResultKind Result { get; set; } = DialogResultKind.Cancel;
    public IInputElement? FocusFirst { get; set; }
    public bool AllowClose { get; set; } = true;
    private readonly bool _cancelable;

    public DialogWindow(string title, bool cancelable)
    {
        _cancelable = cancelable;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // An opaque window: a transparent (layered) window with a shadow effect is drawn in
        // software and makes every message box open with a visible stall.
        Background = Ui.Brush("SurfaceBrush");
        FontFamily = Ui.Res<FontFamily>("BodyFont");
        Foreground = Ui.Brush("TextBrush");
        MaxWidth = 560; MinWidth = 340;
        MaxHeight = SystemParameters.PrimaryScreenHeight * 0.9;

        _title = new TextBlock { Text = title, FontSize = 21, FontFamily = Ui.Res<FontFamily>("SerifFont"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Foreground = Ui.Brush("GoldBrush") };
        _body = new ContentPresenter { Margin = new Thickness(0, 0, 0, 8) };
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(_title); stack.Children.Add(_body); stack.Children.Add(_buttons);
        Content = new Border
        {
            Background = Ui.Brush("SurfaceBrush"), BorderBrush = Ui.Brush("OutlineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0), Padding = new Thickness(24, 20, 24, 18),
            Child = stack
        };
        Owner = App.Window is { IsVisible: true } m ? m : null;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _cancelable && AllowClose) { Result = DialogResultKind.Cancel; Close(); e.Handled = true; }
        };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border or TextBlock) try { DragMove(); } catch { } };
    }

    public void SetMessage(string message)
    {
        _body.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = Ui.Brush("TextMutedBrush"), LineHeight = 23 }
        };
    }

    public void SetContent(FrameworkElement content) => _body.Content = content;

    public void SetButtons(string? positive, string? negative, string? neutral)
    {
        _buttons.Children.Clear();
        if (neutral != null)
        {
            _neutral = Make(neutral, DialogResultKind.Neutral, false);
            _neutral.HorizontalAlignment = HorizontalAlignment.Left;
            _buttons.Children.Add(_neutral);
        }
        if (negative != null) { _negative = Make(negative, DialogResultKind.Negative, false); _buttons.Children.Add(_negative); }
        if (positive != null) { _positive = Make(positive, DialogResultKind.Positive, true); _buttons.Children.Add(_positive); }
        _buttons.Visibility = _buttons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button Make(string text, DialogResultKind kind, bool primary)
    {
        var b = new Button { Content = text, Style = Ui.Res<Style>(primary ? "DialogPrimaryButton" : "DialogButton"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 96 };
        b.Click += (_, _) => { Result = kind; AllowClose = true; Close(); };
        return b;
    }

    public DialogResultKind Run()
    {
        Loaded += (_, _) =>
        {
            var target = FocusFirst ?? (IInputElement?)_positive ?? _negative ?? _neutral;
            if (target != null) Keyboard.Focus(target);
        };
        try { ShowDialog(); } catch { }
        return Result;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose && Result == DialogResultKind.Cancel) { e.Cancel = true; return; }
        base.OnClosing(e);
    }
}
