using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace StreamArcTV.UI;

/// <summary>
/// Loads poster images only once they are (nearly) on screen, like a recycler view does, so a
/// home with hundreds of cards doesn't fetch every poster at once.
/// </summary>
public static class LazyImages
{
    private static readonly List<(Image Img, string Url, int Width)> Pending = new();
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private static bool _started;

    public static void Register(Image img, string? url, int decodeWidth = 480)
    {
        img.Tag = url;
        img.Source = ImageLoader.Placeholder;
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (Pending) Pending.Add((img, url, decodeWidth));
        if (!_started) { _started = true; Timer.Tick += (_, _) => Sweep(); Timer.Start(); }
        Timer.Start();
    }

    /// Loads everything currently in view; call after a scroll or layout change to react at once.
    public static void Sweep()
    {
        var window = App.Window;
        if (window == null) return;
        var bounds = new Rect(-300, -300, window.ActualWidth + 600, window.ActualHeight + 600);
        List<(Image, string, int)> snapshot;
        lock (Pending) snapshot = Pending.ToList();
        if (snapshot.Count == 0) { Timer.Stop(); return; }
        var loaded = new List<(Image, string, int)>();
        foreach (var item in snapshot)
        {
            var (img, url, w) = item;
            if (!Equals(img.Tag, url)) { loaded.Add(item); continue; }   // re-used for another poster
            if (!img.IsLoaded || !img.IsVisible) continue;
            try
            {
                var p = img.TransformToAncestor(window).Transform(new Point(0, 0));
                var r = new Rect(p, new Size(Math.Max(1, img.ActualWidth), Math.Max(1, img.ActualHeight)));
                if (!bounds.IntersectsWith(r)) continue;
            }
            catch { continue; }
            ImageLoader.Load(img, url, w);
            loaded.Add(item);
        }
        if (loaded.Count > 0) lock (Pending) foreach (var l in loaded) Pending.Remove(l);
    }

    /// Forgets images that belong to a page that is gone.
    public static void Forget(DependencyObject root)
    {
        lock (Pending) Pending.RemoveAll(p => IsUnder(p.Img, root));
    }

    private static bool IsUnder(DependencyObject child, DependencyObject root)
    {
        var d = child;
        while (d != null) { if (ReferenceEquals(d, root)) return true; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d); }
        return false;
    }
}
