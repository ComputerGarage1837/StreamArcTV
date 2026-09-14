using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace StreamArcTV.UI;

public enum DialogResultKind { Positive, Negative, Neutral, Cancel }

/// <summary>
/// Dark, keyboard-friendly dialogs in the style of the Android app's AlertDialogs: message boxes,
/// item lists ("hold OK" menus), single and multiple choice lists, custom content, progress, and
/// short toasts at the bottom of the window. Dialogs block like Android's showAndWait: each one
/// runs a nested dispatcher frame until it closes, so callers read the result straight after.
/// </summary>
public static class Dialogs
{
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
            var b = new Button { Content = items[i], Classes = { "listrow" } };
            b.Click += (_, _) => { idx = n; d.Result = DialogResultKind.Positive; d.Close(); };
            panel.Children.Add(b);
        }
        d.SetContent(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460 });
        d.SetButtons(null, negative, null);
        d.FocusFirst = panel.Children.Count > 0 ? panel.Children[0] : null;
        d.Run();
        return idx;
    }

    /// Radio-style single choice; returns the chosen index or -1. Choosing an item closes the dialog.
    public static int SingleChoice(string title, IList<string> items, int current, string? negative = "Cancel", string? neutral = null, Action? onNeutral = null)
    {
        var d = new DialogWindow(title, true);
        var idx = -1;
        var panel = new StackPanel();
        for (var i = 0; i < items.Count; i++)
        {
            var n = i;
            var rb = new ChoiceRow(items[i], i == current, radio: true);
            rb.Changed += _ => { idx = n; d.Result = DialogResultKind.Positive; d.Close(); };
            panel.Children.Add(rb);
        }
        d.SetContent(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460 });
        d.SetButtons(null, negative, neutral);
        d.FocusFirst = current >= 0 && current < panel.Children.Count ? panel.Children[current] : (panel.Children.Count > 0 ? panel.Children[0] : null);
        var r = d.Run();
        if (r == DialogResultKind.Neutral) onNeutral?.Invoke();
        return idx;
    }

    /// Check-box list; returns the final states, or null when cancelled.
    public static bool[]? MultiChoice(string title, IList<string> items, bool[] state, string positive = "OK", string negative = "Cancel")
    {
        var d = new DialogWindow(title, true);
        var boxes = new List<ChoiceRow>();
        var panel = new StackPanel();
        for (var i = 0; i < items.Count; i++)
        {
            var cb = new ChoiceRow(items[i], state[i], radio: false);
            boxes.Add(cb);
            panel.Children.Add(cb);
        }
        d.SetContent(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460 });
        d.SetButtons(positive, negative, null);
        d.FocusFirst = panel.Children.Count > 0 ? panel.Children[0] : null;
        if (d.Run() != DialogResultKind.Positive) return null;
        return boxes.Select(b => b.IsChecked).ToArray();
    }

    /// Dialog with custom content.
    public static DialogResultKind Custom(string title, Control content, string? positive, string? negative = "Cancel", string? neutral = null, IInputElement? focus = null, bool cancelable = true, Action<DialogWindow>? setup = null)
    {
        var d = new DialogWindow(title, cancelable);
        d.SetContent(content);
        d.SetButtons(positive, negative, neutral);
        d.FocusFirst = focus;
        setup?.Invoke(d);
        return d.Run();
    }

    public class ProgressHandle
    {
        internal DialogWindow? Window;
        internal ThinProgress? Bar;
        internal TextBlock? Text;
        public void Report(double? fraction, string text)
        {
            Ui.Post(() =>
            {
                if (Bar == null || Text == null) return;
                Bar.IsIndeterminate = fraction == null;
                if (fraction != null) Bar.Value = Math.Clamp(fraction.Value, 0, 1) * 1000;
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
        // Fixed width and a fixed-height text line, so a size that follows the changing
        // "12.3 MB of 105 MB" text never makes the box pulse.
        var panel = new StackPanel { Margin = new Thickness(4, 8, 4, 0), Width = 420 };
        var bar = new ThinProgress { IsIndeterminate = true, Maximum = 1000, Height = 8 };
        var txt = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 0), Foreground = Ui.Brush("TextMutedBrush"), FontSize = 14, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Height = 20 };
        panel.Children.Add(bar); panel.Children.Add(txt);
        d.SetContent(panel);
        d.SetButtons(null, onCancel != null ? "Cancel" : null, null);
        d.AllowClose = false;
        d.Closed += (_, _) => { if (d.Result == DialogResultKind.Negative) onCancel?.Invoke(); };
        h.Window = d; h.Bar = bar; h.Text = txt;
        d.ShowNonBlocking();
        return h;
    }
}

/// The dark dialog window used by <see cref="Dialogs"/>.
public class DialogWindow : Window
{
    private readonly TextBlock _title;
    private readonly ContentControl _body;
    private readonly StackPanel _buttons;
    private Button? _positive, _negative, _neutral;
    public DialogResultKind Result { get; set; } = DialogResultKind.Cancel;
    public IInputElement? FocusFirst { get; set; }
    public bool AllowClose { get; set; } = true;
    private readonly bool _cancelable;

    public DialogWindow(string title, bool cancelable)
    {
        _cancelable = cancelable;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Brush("SurfaceBrush");
        Foreground = Ui.Brush("TextBrush");
        MaxWidth = 560; MinWidth = 340;
        Title = title;
        XYFocus.SetNavigationModes(this, XYFocusNavigationModes.Keyboard);

        _title = new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = Ui.Brush("TextBrush") };
        _body = new ContentControl { Margin = new Thickness(0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(_title); stack.Children.Add(_body); stack.Children.Add(_buttons);
        Content = new Border
        {
            Background = Ui.Brush("SurfaceBrush"), BorderBrush = Ui.Brush("OutlineBrush"), BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 18, 22, 16),
            Child = stack
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _cancelable && AllowClose) { Result = DialogResultKind.Cancel; Close(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        PointerPressed += (_, e) => { if (e.Source is Border or TextBlock && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { try { BeginMoveDrag(e); } catch { } } };
        Closing += (_, e) => { if (!AllowClose && Result == DialogResultKind.Cancel) e.Cancel = true; };
        Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var target = FocusFirst ?? (IInputElement?)_positive ?? _negative ?? _neutral ?? Nav.FirstFocusable(this);
                target?.Focus();
            }, DispatcherPriority.Input);
        };
    }

    public void SetMessage(string message)
    {
        _body.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14.5, Foreground = Ui.Brush("TextMutedBrush"), LineHeight = 21 }
        };
    }

    public void SetContent(Control content) => _body.Content = content;

    public void SetButtons(string? positive, string? negative, string? neutral)
    {
        _buttons.Children.Clear();
        if (neutral != null)
        {
            _neutral = Make(neutral, DialogResultKind.Neutral, false);
            _buttons.Children.Add(_neutral);
        }
        if (negative != null) { _negative = Make(negative, DialogResultKind.Negative, false); _buttons.Children.Add(_negative); }
        if (positive != null) { _positive = Make(positive, DialogResultKind.Positive, true); _buttons.Children.Add(_positive); }
        _buttons.IsVisible = _buttons.Children.Count > 0;
    }

    private Button Make(string text, DialogResultKind kind, bool primary)
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), MinWidth = 96 };
        b.Classes.Add(primary ? "dialogprimary" : "dialog");
        b.Click += (_, _) => { Result = kind; AllowClose = true; Close(); };
        return b;
    }

    /// Shows the dialog and waits (in a nested dispatcher frame) until it closes.
    public DialogResultKind Run()
    {
        var frame = new DispatcherFrame();
        Closed += (_, _) => frame.Continue = false;
        var owner = Ui.Owner;
        try
        {
            if (owner != null) _ = ShowDialog(owner); else Show();
        }
        catch (Exception e)
        {
            Util.AppLog.E("Dialogs", "could not open a dialog", e);
            return Result;
        }
        Dispatcher.UIThread.PushFrame(frame);
        return Result;
    }

    /// Shows the dialog without waiting (progress boxes).
    public void ShowNonBlocking()
    {
        var owner = Ui.Owner;
        try { if (owner != null) Show(owner); else Show(); }
        catch (Exception e) { Util.AppLog.E("Dialogs", "could not open a dialog", e); }
    }
}
