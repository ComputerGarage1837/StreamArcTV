using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StreamArcTV.Update;
using StreamArcTV.Util;

namespace StreamArcTV.UI;

/// <summary>
/// The service-notice banner above the Live TV / Video on Demand cards: coloured by level
/// (info blue, warning amber, outage red), with an optional bold title and an optional link
/// opened when the banner is selected. Collapsed when there is nothing to show.
/// </summary>
public class NoticeBanner : FocusCard
{
    private readonly Border _box;
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
    private readonly TextBlock _link = new() { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Opacity = 0.85 };
    private Announcement.Notice? _notice;

    public NoticeBanner()
    {
        Visibility = Visibility.Collapsed;
        IsTabStop = false;
        Focusable = false;
        Margin = new Thickness(0, 0, 0, 14);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_message);
        stack.Children.Add(_link);
        _box = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), Padding = new Thickness(18, 12, 18, 12),
            Child = stack
        };
        Content = _box;
        Click += () => { if (!string.IsNullOrWhiteSpace(_notice?.Link)) Open(_notice!.Link); };
    }

    public void Show(Announcement.Notice? notice)
    {
        _notice = notice;
        if (notice == null || !notice.Visible) { Visibility = Visibility.Collapsed; return; }
        var (bg, border, fg) = notice.Level switch
        {
            "outage" => ("#E6B91C1C", "#F87171", "#FEE2E2"),
            "warning" => ("#E6B45309", "#FBBF24", "#FEF3C7"),
            _ => ("#E61D4ED8", "#60A5FA", "#DBEAFE"),
        };
        _box.Background = Brush(bg);
        _box.BorderBrush = Brush(border);
        _title.Foreground = _message.Foreground = _link.Foreground = Brush(fg);
        _title.Text = notice.Title;
        _title.Visibility = string.IsNullOrWhiteSpace(notice.Title) ? Visibility.Collapsed : Visibility.Visible;
        _title.Margin = new Thickness(0, 0, 0, 4);
        _message.Text = notice.Message;
        var hasLink = !string.IsNullOrWhiteSpace(notice.Link);
        _link.Text = hasLink ? "Press to open: " + notice.Link : "";
        _link.Visibility = hasLink ? Visibility.Visible : Visibility.Collapsed;
        Focusable = IsTabStop = hasLink;
        Cursor = hasLink ? System.Windows.Input.Cursors.Hand : null;
        Visibility = Visibility.Visible;
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { AppLog.W("Notice", "open link failed: " + e.Message); }
    }
}
