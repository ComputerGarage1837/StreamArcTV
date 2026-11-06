using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StreamArcTV.Data;

namespace StreamArcTV.UI;

/// "Cast" (round photos with names) and "More like this" (poster cards) rows shown below a movie or show.
public sealed class ExtrasPanel : StackPanel
{
    public ExtrasPanel() { Visibility = Visibility.Collapsed; Margin = new Thickness(0, 8, 0, 0); }

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("TextBrush"), Margin = new Thickness(4, 14, 0, 6),
    };

    private static ScrollViewer Row(StackPanel inner) => new()
    {
        Content = inner, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        Padding = new Thickness(0, 0, 0, 8),
    };

    public void ShowCast(List<Tmdb.Cast> cast)
    {
        if (cast.Count == 0) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in cast)
        {
            var col = new StackPanel { Width = 96, Margin = new Thickness(0, 0, 10, 0) };
            FrameworkElement photo;
            if (c.Photo != null)
            {
                var img = new Image { Width = 72, Height = 72, Stretch = Stretch.UniformToFill };
                ImageLoader.Load(img, c.Photo, 160);
                photo = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(36), ClipToBounds = true, Background = Ui.Brush("InputBgBrush"), Child = img };
            }
            else photo = UserDialogs.Avatar(c.Name, "#475569", 72);
            photo.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(photo);
            col.Children.Add(new TextBlock { Text = c.Name, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Brush("TextBrush"), Margin = new Thickness(0, 4, 0, 0) });
            if (!string.IsNullOrWhiteSpace(c.Character))
                col.Children.Add(new TextBlock { Text = c.Character, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Brush("TextMutedBrush") });
            row.Children.Add(col);
        }
        Children.Add(Header("Cast"));
        Children.Add(Row(row));
        Visibility = Visibility.Visible;
    }

    public void ShowSimilar(List<Data.Stream> list, Action<Data.Stream> open)
    {
        if (list.Count == 0) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var s in list)
        {
            var stream = s;
            var img = new Image { Stretch = Stretch.UniformToFill };
            ImageLoader.Load(img, string.IsNullOrWhiteSpace(s.Image) ? null : s.Image, 240);
            var poster = new Border { Width = 120, Height = 174, Background = Ui.Brush("InputBgBrush"), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = img };
            var stack = new StackPanel { Width = 120 };
            stack.Children.Add(poster);
            stack.Children.Add(new TextBlock { Text = s.Name ?? "", FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 50, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Brush("TextBrush"), Margin = new Thickness(0, 4, 0, 0) });
            var btn = new Button { Content = stack, Padding = new Thickness(4), Margin = new Thickness(0, 0, 8, 0), Style = Ui.Res<Style>("SmallButton"), HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top };
            btn.Click += (_, _) => open(stream);
            row.Children.Add(btn);
        }
        Children.Add(Header("More like this"));
        Children.Add(Row(row));
        Visibility = Visibility.Visible;
    }
}
