using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using StreamArcTV.Data;

namespace StreamArcTV.UI;

/// One row of the poster grid (the grid is virtualized by rows so a 10,000-title catalogue stays light).
public class PosterRow
{
    public List<Data.Stream> Items { get; }
    public int Columns { get; }
    public HashSet<string> Favorites { get; }
    public Func<Data.Stream, string?>? WatchKey { get; }
    public Action<Data.Stream> OnClick { get; }
    public Action<Data.Stream> OnLongClick { get; }

    public PosterRow(List<Data.Stream> items, int columns, HashSet<string> favorites, Func<Data.Stream, string?>? watchKey, Action<Data.Stream> onClick, Action<Data.Stream> onLongClick)
    {
        Items = items; Columns = columns; Favorites = favorites; WatchKey = watchKey; OnClick = onClick; OnLongClick = onLongClick;
    }
}

public class PosterRowView : ContentControl
{
    public PosterRowView()
    {
        Focusable = false;
        IsTabStop = false;
        DataContextChanged += (_, _) => Build();
    }

    private void Build()
    {
        if (DataContext is not PosterRow row) { Content = null; return; }
        var grid = new UniformGrid { Rows = 1, Columns = row.Columns };
        foreach (var s in row.Items) grid.Children.Add(Card(row, s));
        Content = grid;
        Dispatcher.UIThread.Post(LazyImages.Sweep, DispatcherPriority.Background);
    }

    /// Poster box at 2:3 from the column width so the whole poster shows without stretching.
    private static FocusCard Card(PosterRow row, Data.Stream s)
    {
        var stack = new StackPanel();
        var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(0) };
        var poster = new Border { Background = Ui.Brush("PosterBgBrush"), CornerRadius = new CornerRadius(6), ClipToBounds = true };
        var posterGrid = new Panel();
        posterGrid.Children.Add(img);
        var progress = new ThinProgress { VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false };
        posterGrid.Children.Add(progress);
        poster.Child = posterGrid;
        // Height follows the width at 2:3.
        poster.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) poster.Height = e.NewSize.Width * 3 / 2; };
        stack.Children.Add(poster);
        var key = row.WatchKey?.Invoke(s);
        var frac = key != null ? WatchProgress.Fraction(key) : null;
        var title = new TextBlock
        {
            Text = (s.Id != null && row.Favorites.Contains(s.Id) ? "★ " : "") + (frac != null && frac >= 1f ? "✓ " : "") + (s.Name ?? "—"),
            FontSize = 13, Foreground = Ui.Brush("TextBrush"), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(0, 6, 0, 0)
        };
        stack.Children.Add(title);
        if (frac != null) { progress.IsVisible = true; progress.Value = frac.Value * 1000; }
        LazyImages.Register(img, s.Image, 360);
        var card = new FocusCard { Content = stack, Classes = { "stream" } };
        card.Click += () => row.OnClick(s);
        card.LongClick += () => row.OnLongClick(s);
        return card;
    }
}
