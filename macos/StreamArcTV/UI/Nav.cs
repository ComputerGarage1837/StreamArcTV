using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace StreamArcTV.UI;

/// <summary>
/// A screen of the app (the counterpart of an Android activity). Pages are pushed onto
/// <see cref="Nav"/>; the lifecycle mirrors onResume / onPause / onDestroy.
/// </summary>
public abstract class AppPage : UserControl
{
    protected AppPage()
    {
        Focusable = false;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        Background = Ui.Brush("BgBrush");
    }

    /// True while this page is the visible top of the stack.
    public bool IsResumed { get; internal set; }
    internal bool Finished { get; set; }

    /// Called when the page becomes the visible screen (first time and when a page above it closes).
    public virtual void OnResume() { }
    /// Called when the page stops being the visible screen.
    public virtual void OnPause() { }
    /// Called once when the page is removed for good.
    public virtual void OnDestroy() { }
    /// Return true to consume the back press.
    public virtual bool OnBack() => false;
    /// Return true to consume a key (called before the window's default handling).
    public virtual bool OnKey(KeyEventArgs e) => false;
    /// The element that should get focus when the page opens or comes back.
    public virtual IInputElement? InitialFocus => null;
    /// A panel drawn above the page's video (if any) where toasts should go; null for the window's own.
    public virtual Panel? ToastHost => null;

    /// Closes this page (the counterpart of finish()).
    public void Finish() => Nav.Finish(this);
}

/// The page stack inside the main window.
public static class Nav
{
    private static Panel? _host;
    private static readonly List<AppPage> Stack = new();

    public static void Attach(Panel host) => _host = host;
    public static int Depth => Stack.Count;
    public static AppPage? Current => Stack.Count > 0 ? Stack[^1] : null;

    public static void Push(AppPage page)
    {
        if (_host == null) return;
        var prev = Current;
        if (prev != null)
        {
            prev.IsResumed = false;
            prev.OnPause();
            prev.IsVisible = false;
        }
        Stack.Add(page);
        _host.Children.Add(page);
        page.IsVisible = true;
        page.IsResumed = true;
        page.OnResume();
        FocusInto(page);
    }

    /// Replaces the current page with a new one (start the next activity, finish this one).
    public static void Replace(AppPage page)
    {
        var top = Current;
        Push(page);
        if (top != null) Remove(top);
    }

    public static void Finish(AppPage page)
    {
        if (page.Finished) return;
        if (ReferenceEquals(page, Current)) Pop();
        else Remove(page);
    }

    public static void Back()
    {
        var top = Current;
        if (top == null) return;
        if (top.OnBack()) return;
        if (Stack.Count <= 1)
        {
            // Home is the root: back leaves the app, like Android.
            App.Window?.Close();
            return;
        }
        Pop();
    }

    public static void Pop()
    {
        if (_host == null || Stack.Count == 0) return;
        var top = Stack[^1];
        Stack.RemoveAt(Stack.Count - 1);
        Destroy(top);
        var now = Current;
        if (now != null)
        {
            now.IsVisible = true;
            now.IsResumed = true;
            now.OnResume();
            FocusInto(now);
        }
    }

    /// Closes every page above the home screen (the counterpart of FLAG_ACTIVITY_CLEAR_TOP to Main).
    public static void PopToRoot()
    {
        while (Stack.Count > 1) { var top = Stack[^1]; Stack.RemoveAt(Stack.Count - 1); Destroy(top); }
        var now = Current;
        if (now != null) { now.IsVisible = true; now.IsResumed = true; now.OnResume(); FocusInto(now); }
    }

    public static void PopAll()
    {
        while (Stack.Count > 0) { var top = Stack[^1]; Stack.RemoveAt(Stack.Count - 1); Destroy(top); }
    }

    private static void Remove(AppPage page)
    {
        if (!Stack.Remove(page)) return;
        Destroy(page);
    }

    private static void Destroy(AppPage page)
    {
        if (page.IsResumed) { page.IsResumed = false; page.OnPause(); }
        page.Finished = true;
        page.OnDestroy();
        _host?.Children.Remove(page);
    }

    public static void FocusInto(AppPage page)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(Current, page)) return;
            var target = page.InitialFocus;
            if (target != null && target is Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) { target.Focus(); return; }
            FirstFocusable(page)?.Focus();
        }, DispatcherPriority.Input);
    }

    /// The first control inside root that can take keyboard focus (in visual order).
    public static InputElement? FirstFocusable(Control root) =>
        root.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(x => x.Focusable && x.IsTabStop && x.IsEffectivelyEnabled && x.IsEffectivelyVisible);
}
