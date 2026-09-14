using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using StreamArcTV.Data;
using StreamArcTV.Transfer;
using StreamArcTV.Update;

namespace StreamArcTV.UI;

/// <summary>
/// Video on Demand home: a featured hero, Continue watching, Next episodes, My List, what's new,
/// and genre rows, in the style of a streaming service. Categories and search stay one click away
/// (the Categories / Search entries open the full grid browser).
/// </summary>
public partial class VodHomePage : AppPage
{
    private const int HERO_INTERVAL_MS = 8_000;

    private readonly Prefs _prefs = Prefs.Instance;
    private readonly Service _service;
    private readonly Account _account = null!;
    private readonly bool _dead;

    private List<Data.Stream> _movies = new();
    private List<Data.Stream> _series = new();
    private List<Card> _nextEpisodes = new();
    private List<Data.Stream> _featured = new();
    private int _heroIndex;
    private bool _loaded;
    private bool _land = true;
    private readonly DispatcherTimer _heroTicker = new() { Interval = TimeSpan.FromMilliseconds(HERO_INTERVAL_MS) };
    private Control? _heroView;
    private readonly List<(FocusCard Card, string Key)> _navItems = new();

    public class Card
    {
        public string Title = "";
        public string? Subtitle;
        public string? Image;
        public float? Progress;
        public string? Badge;
        public Action OnClick = () => { };
        public Action? OnLongClick;
    }

    public VodHomePage(Service service)
    {
        InitializeComponent();
        _service = service;
        var acct = _prefs.Account(service);
        if (acct == null) { _dead = true; Loaded += (_, _) => Nav.Replace(new LoginPage(service)); return; }
        _account = acct;

        Header.SetCompact(true);
        Header.SettingsButton.Click += (_, _) => Nav.Push(new SettingsPage());
        Header.UpdateButton.Click += (_, _) => UpdateChecker.Check(manual: true);

        BuildNav();
        _heroTicker.Tick += (_, _) =>
        {
            if (_featured.Count > 1)
            {
                _heroIndex = (_heroIndex + 1) % _featured.Count;
                BindHero();
            }
        };
        SizeChanged += (_, _) => ApplyNavLayout();
        RowsScroll.ScrollChanged += (_, _) => LazyImages.Sweep();
        Loaded += (_, _) => ApplyNavLayout();
        Render();
        Load();
    }

    public override IInputElement? InitialFocus => _navItems.FirstOrDefault().Card;

    public override void OnResume()
    {
        if (_dead) return;
        if (!_prefs.IsSignedIn(_service)) { Finish(); return; }
        if (_loaded) { Render(); LoadNextEpisodes(); }
        _heroTicker.Stop();
        _heroTicker.Start();
    }

    public override void OnPause() => _heroTicker.Stop();

    public override void OnDestroy() => LazyImages.Forget(this);

    private void BuildNav()
    {
        void Add(string key, string? png, string? icon, string label, bool selected, Action click)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            if (png != null) stack.Children.Add(Ui.AssetImage(png, 22, 22));
            else stack.Children.Add(Ui.IconPath(icon!, Ui.Brush("SoftTextBrush"), 22));
            var normal = selected ? Ui.Brush("PurpleBrush") : Ui.Brush("SoftTextBrush");
            var text = new TextBlock { Text = label, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Foreground = normal };
            stack.Children.Add(text);
            var card = new FocusCard { Content = stack, Classes = { "vodnav" }, Selected = selected };
            card.Click += click;
            card.GotFocus += (_, _) => text.Foreground = Ui.Brush("BgBrush");
            card.LostFocus += (_, _) => text.Foreground = normal;
            _navItems.Add((card, key));
        }
        Add("home", "img_nav_home.png", null, "Home", true, () => RowsScroll.ScrollToHome());
        Add("search", "img_nav_search.png", null, "Search", false, () => Nav.Push(new BrowsePage(_service, ContentKind.MOVIE, search: true)));
        Add("categories", null, "IconVod", "Categories", false, () => Nav.Push(new BrowsePage(_service, ContentKind.MOVIE)));
        Add("downloads", "img_nav_downloads.png", null, "Downloads", false, () => Nav.Push(new TransfersPage(TransferType.DOWNLOAD)));
        Add("profile", "img_nav_profile.png", null, "Profile", false, () => Nav.Push(new ProfilePage(_service)));
    }

    /// Side rail in landscape, a row of tabs under the header when the window is taller than wide.
    private void ApplyNavLayout()
    {
        var land = Ui.Landscape(this);
        if (land == _land && NavRail.Children.Count + NavTabs.Children.Count > 0) return;
        _land = land;
        NavRail.Children.Clear();
        NavTabs.Children.Clear();
        foreach (var (card, _) in _navItems)
        {
            card.Margin = land ? new Thickness(0, 0, 0, 4) : new Thickness(2, 0, 2, 4);
            if (land) NavRail.Children.Add(card); else NavTabs.Children.Add(card);
        }
        NavRail.IsVisible = land;
        NavTabs.IsVisible = !land;
        Body.ColumnDefinitions[0].Width = new GridLength(land ? 96 : 0);
        Grid.SetColumn(ContentBox, land ? 1 : 0);
        Grid.SetColumnSpan(ContentBox, land ? 1 : 2);
        BindHero();
    }

    // ---- Loading ----------------------------------------------------------------

    private async void Load()
    {
        Progress.IsVisible = _movies.Count == 0;
        try
        {
            _movies = await CatalogCache.Get(_service, _account, ContentKind.MOVIE);
            _series = await CatalogCache.Get(_service, _account, ContentKind.SERIES);
            if (Finished) return;
            _loaded = true;
            Progress.IsVisible = false;
            _featured = CatalogCache.RecentlyAdded(_movies, 40).Where(s => !string.IsNullOrWhiteSpace(s.Image)).Take(4)
                .Concat(CatalogCache.RecentlyAdded(_series, 40).Where(s => !string.IsNullOrWhiteSpace(s.Image)).Take(2)).ToList();
            Render();
            LoadNextEpisodes();
        }
        catch (Exception e)
        {
            Progress.IsVisible = false;
            if (_movies.Count == 0)
            {
                TxtError.Text = string.IsNullOrWhiteSpace(e.Message) ? "Couldn't load content." : e.Message;
                TxtError.IsVisible = true;
            }
        }
    }

    /// Series you've started: fetch their episode lists (cached) and pick the next unwatched one.
    private async void LoadNextEpisodes()
    {
        var started = WatchProgress.StartedSeries();
        if (started.Count == 0) { _nextEpisodes = new(); return; }
        var cards = await Task.WhenAll(started.Select(async entry =>
        {
            var sid = entry.SeriesId;
            if (sid == null) return null;
            List<Episode> eps;
            try { eps = await SeriesCache.Episodes(_service, _account, sid); } catch { return null; }
            var sorted = SeriesCache.Ordered(eps);
            var idx = sorted.FindIndex(e => e.Season == entry.Season && e.Number == entry.Episode);
            // Still mid-way through this one: it belongs in Continue watching instead.
            if (idx >= 0 && !entry.Watched) return null;
            var next = sorted.Skip(idx + 1).FirstOrDefault(e => !WatchProgress.IsWatched(WatchProgress.EpisodeKey(e.Id)));
            if (next == null) return null;
            var seriesTitle = entry.Title ?? "";
            return new Card
            {
                Title = seriesTitle,
                Subtitle = $"S{next.Season} E{next.Number}  ·  {next.Title}",
                Image = entry.Image,
                Badge = "Next episode",
                OnClick = () => PlayEpisode(sid, seriesTitle, entry.Image, next),
                OnLongClick = () => ShowSeriesMenu(sid, seriesTitle, entry.Image),
            };
        }));
        if (Finished) return;
        _nextEpisodes = cards.Where(c => c != null).Select(c => c!).ToList();
        Render();
    }

    // ---- Rows -------------------------------------------------------------------

    private record RowPlan(string Title, List<Card> Cards, Action? SeeAll);
    private int _renderSerial;

    /// Plans the rows on a worker thread (sorting and filtering thousands of titles), then adds
    /// them to the screen one at a time so the window never freezes while the home fills in.
    private async void Render()
    {
        if (_dead) return;
        var serial = ++_renderSerial;
        var movies = _movies; var series = _series; var next = _nextEpisodes;
        var favMovies = _prefs.Favorites(_service, ContentKind.MOVIE);
        var favSeries = _prefs.Favorites(_service, ContentKind.SERIES);
        var plan = await Task.Run(() =>
        {
            var rows = new List<RowPlan>();
            var cont = WatchProgress.ContinueWatching().Select(kv => ContinueCard(kv.Key, kv.Entry)).ToList();
            if (cont.Count > 0) rows.Add(new RowPlan("Continue watching", cont, null));
            if (next.Count > 0) rows.Add(new RowPlan("Next episodes", next, null));

            var movieById = movies.Where(m => m.Id != null).GroupBy(m => m.Id!).ToDictionary(g => g.Key, g => g.First());
            var seriesById = series.Where(s => s.Id != null).GroupBy(s => s.Id!).ToDictionary(g => g.Key, g => g.First());
            var myList = favMovies.Where(movieById.ContainsKey).Select(id => MovieCard(movieById[id]))
                .Concat(favSeries.Where(seriesById.ContainsKey).Select(id => SeriesCard(seriesById[id]))).ToList();
            if (myList.Count > 0) rows.Add(new RowPlan("My List", myList, null));

            if (movies.Count > 0) rows.Add(new RowPlan("New movies", CatalogCache.RecentlyAdded(movies, 30).Select(MovieCard).ToList(),
                () => Nav.Push(new BrowsePage(_service, ContentKind.MOVIE, BrowsePage.RECENT_ID))));
            if (series.Count > 0) rows.Add(new RowPlan("New series", CatalogCache.RecentlyAdded(series, 30).Select(SeriesCard).ToList(),
                () => Nav.Push(new BrowsePage(_service, ContentKind.SERIES, BrowsePage.RECENT_ID))));

            foreach (var g in TopGenres(movies, 8))
            {
                var genre = g;
                var items = CatalogCache.RecentlyAdded(movies.Where(m => m.Genres.Any(x => string.Equals(x, genre, StringComparison.OrdinalIgnoreCase))), 30);
                rows.Add(new RowPlan($"{genre} movies", items.Select(MovieCard).ToList(), () => Nav.Push(new BrowsePage(_service, ContentKind.MOVIE, BrowsePage.GENRE_PREFIX + genre))));
            }
            foreach (var g in TopGenres(series, 5))
            {
                var genre = g;
                var items = CatalogCache.RecentlyAdded(series.Where(s => s.Genres.Any(x => string.Equals(x, genre, StringComparison.OrdinalIgnoreCase))), 30);
                rows.Add(new RowPlan($"{genre} series", items.Select(SeriesCard).ToList(), () => Nav.Push(new BrowsePage(_service, ContentKind.SERIES, BrowsePage.GENRE_PREFIX + genre))));
            }
            return rows;
        });
        if (serial != _renderSerial || Finished || _dead) return;

        Rows.Children.Clear();
        _heroView = null;
        if (_featured.Count > 0) { _heroView = BuildHero(); Rows.Children.Add(_heroView); BindHero(); }
        // One row per pass through the dispatcher: the first rows appear at once and input stays responsive.
        var queue = new Queue<RowPlan>(plan);
        void AddNext()
        {
            if (serial != _renderSerial || Finished || queue.Count == 0) { LazyImages.Sweep(); return; }
            var r = queue.Dequeue();
            AddRow(r.Title, r.Cards, r.SeeAll);
            Dispatcher.UIThread.Post(AddNext, DispatcherPriority.Background);
        }
        AddNext();
    }

    private static List<string> TopGenres(List<Data.Stream> items, int n)
    {
        var counts = new Dictionary<string, int>();
        foreach (var s in items) foreach (var g in s.Genres) counts[g] = counts.GetValueOrDefault(g) + 1;
        return counts.Where(kv => kv.Value >= 5).OrderByDescending(kv => kv.Value).Take(n).Select(kv => kv.Key).ToList();
    }

    private Card MovieCard(Data.Stream s)
    {
        var key = s.StreamId != null ? WatchProgress.MovieKey(_service, s.StreamId) : null;
        var frac = key != null ? WatchProgress.Fraction(key) : null;
        return new Card
        {
            Title = s.Name ?? "", Subtitle = s.Genres.FirstOrDefault(), Image = s.Image,
            Progress = frac is < 1f ? frac : null, Badge = frac >= 1f ? "✓" : null,
            OnClick = () => PlayMovie(s),
            OnLongClick = () => ShowMovieMenu(s),
        };
    }

    private Card SeriesCard(Data.Stream s)
    {
        var id = s.Id ?? "";
        return new Card
        {
            Title = s.Name ?? "", Subtitle = s.Genres.FirstOrDefault(), Image = s.Image,
            OnClick = () => { if (id.Length > 0) OpenSeries(id, s.Name ?? "", s.Image, s.Plot); },
            OnLongClick = () => { if (id.Length > 0) ShowSeriesMenu(id, s.Name ?? "", s.Image, s.Plot); },
        };
    }

    private Card ContinueCard(string key, WatchProgress.Entry e)
    {
        var left = $"{Duration(e.RemainingMs)} left";
        var sub = e.Kind == WatchProgress.KIND_EPISODE ? $"S{e.Season} E{e.Episode}  ·  {left}" : left;
        return new Card
        {
            Title = e.Title ?? "", Subtitle = sub, Image = e.Image,
            Progress = Math.Clamp(e.PositionMs / (float)e.DurationMs, 0f, 1f),
            OnClick = () => Resume(key, e),
            OnLongClick = () => ShowContinueMenu(key, e),
        };
    }

    // ---- Views --------------------------------------------------------------------

    private Control BuildHero()
    {
        var root = new Border { Background = Ui.Brush("SurfaceBrush"), CornerRadius = new CornerRadius(18), Margin = new Thickness(10, 4, 10, 6), ClipToBounds = true, Height = 230 };
        var grid = new Panel();
        var backdrop = new Image { Stretch = Stretch.UniformToFill, Opacity = 0.7 };
        grid.Children.Add(backdrop);
        grid.Children.Add(new Border { Background = Ui.Brush("VodHeroScrimHBrush"), CornerRadius = new CornerRadius(18) });
        grid.Children.Add(new Border { Background = Ui.Brush("VodHeroScrimVBrush"), CornerRadius = new CornerRadius(18) });
        var poster = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 18, 24, 18), Width = 150 };
        grid.Children.Add(poster);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(18, 8, 190, 12) };
        text.Children.Add(new TextBlock { Text = "F E A T U R E D", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("AccentBrush") });
        var title = new TextBlock { FontSize = 26, FontWeight = FontWeight.Black, Foreground = Ui.Brush("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(0, 4, 0, 0) };
        var meta = new TextBlock { FontSize = 13, Foreground = Ui.Brush("TextMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        var plot = new TextBlock { FontSize = 12, Foreground = Ui.Color(0xFFD0D8E4), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(0, 6, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var play = new Button { Classes = { "primary" }, MinHeight = 38, Padding = new Thickness(20, 4, 20, 4), FontSize = 15 };
        var list = new Button { Classes = { "small" }, MinHeight = 38, Padding = new Thickness(16, 4, 16, 4), FontSize = 14, Margin = new Thickness(8, 0, 0, 0) };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(play); buttons.Children.Add(list); buttons.Children.Add(dots);
        text.Children.Add(title); text.Children.Add(meta); text.Children.Add(plot); text.Children.Add(buttons);
        grid.Children.Add(text);
        root.Child = grid;
        root.Tag = new HeroParts(backdrop, poster, title, meta, plot, play, list, dots, text);
        play.Click += HeroPlay;
        list.Click += HeroList;
        return root;
    }

    private record HeroParts(Image Backdrop, Image Poster, TextBlock Title, TextBlock Meta, TextBlock Plot, Button Play, Button List, StackPanel Dots, StackPanel Text);

    private void BindHero()
    {
        if (_heroView is not Border root || root.Tag is not HeroParts h) return;
        var s = _featured.ElementAtOrDefault(_heroIndex);
        if (s == null) return;
        // Never let the banner take more than ~40% of the rows area: the first row of posters
        // must be visible underneath without scrolling.
        var cap = RowsScroll.Bounds.Height * 0.40;
        var wanted = _land ? 250.0 : 230.0;
        root.Height = cap > 0 ? Math.Min(wanted, cap) : wanted;
        var wide = Bounds.Width >= 700;
        h.Poster.IsVisible = wide;
        h.Text.Margin = new Thickness(18, 8, wide ? 190 : 22, 12);
        h.Title.FontSize = wide ? 26 : 22;
        var isSeries = IsSeries(s);
        var kind = isSeries ? ContentKind.SERIES : ContentKind.MOVIE;
        ImageLoader.Load(h.Backdrop, s.Image, 960, placeholder: false);
        if (wide) ImageLoader.Load(h.Poster, s.Image, 400, placeholder: false);
        h.Title.Text = s.Name ?? "";
        var meta = new List<string> { isSeries ? "Series" : "Movie" };
        var genres = string.Join(", ", s.Genres.Take(2));
        if (!string.IsNullOrWhiteSpace(genres)) meta.Add(genres);
        var rating = s.Rating?.Trim();
        if (!string.IsNullOrWhiteSpace(rating) && rating != "0") meta.Add($"★ {rating}");
        h.Meta.Text = string.Join("   ·   ", meta);
        h.Plot.Text = s.Plot?.Trim() ?? "";
        h.Plot.IsVisible = !string.IsNullOrWhiteSpace(h.Plot.Text);
        h.Play.Content = isSeries ? "Open" : "▶  Play";
        var fav = s.Id != null && _prefs.IsFavorite(_service, kind, s.Id);
        h.List.Content = fav ? "✓ In My List" : "+ My List";
        h.Dots.Children.Clear();
        for (var i = 0; i < _featured.Count; i++)
        {
            h.Dots.Children.Add(new Border
            {
                Width = i == _heroIndex ? 18 : 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0),
                Background = Ui.Brush("AccentBrush"), Opacity = i == _heroIndex ? 1 : 0.4
            });
        }
    }

    private bool IsSeries(Data.Stream s) => (s.SeriesId != null && s.StreamId == null) || _series.Any(x => ReferenceEquals(x, s));

    private void HeroPlay(object? sender, RoutedEventArgs e)
    {
        var s = _featured.ElementAtOrDefault(_heroIndex);
        if (s == null) return;
        if (IsSeries(s)) { if (s.Id != null) OpenSeries(s.Id, s.Name ?? "", s.Image, s.Plot); }
        else PlayMovie(s);
    }

    private void HeroList(object? sender, RoutedEventArgs e)
    {
        var s = _featured.ElementAtOrDefault(_heroIndex);
        if (s?.Id == null) return;
        _prefs.ToggleFavorite(_service, IsSeries(s) ? ContentKind.SERIES : ContentKind.MOVIE, s.Id);
        Render();
    }

    private void AddRow(string title, List<Card> cards, Action? seeAll)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        var head = new Grid { Margin = new Thickness(14, 0, 10, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
        if (seeAll != null)
        {
            var b = new FocusCard { Classes = { "nav" }, Padding = new Thickness(10, 4, 10, 4), Content = new TextBlock { Text = "See all ›", FontSize = 13, Foreground = Ui.Brush("AccentBrush") } };
            b.Click += seeAll;
            Grid.SetColumn(b, 1);
            head.Children.Add(b);
        }
        panel.Children.Add(head);
        // Vertical wheel turns over the row chain up to the page scroller (IsScrollChainingEnabled).
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(8, 0, 8, 0) };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        var cardW = _land ? 124.0 : 104.0;
        var cardH = _land ? 180.0 : 150.0;
        foreach (var c in cards) strip.Children.Add(CardView(c, cardW, cardH));
        scroll.Content = strip;
        scroll.ScrollChanged += (_, _) => LazyImages.Sweep();
        panel.Children.Add(scroll);
        Rows.Children.Add(panel);
    }

    private static FocusCard CardView(Card c, double w, double h)
    {
        var stack = new StackPanel { Width = w };
        var posterGrid = new Panel { Height = h, Background = Ui.Brush("PosterBgBrush"), ClipToBounds = true };
        var img = new Image { Stretch = Stretch.UniformToFill };
        posterGrid.Children.Add(img);
        if (c.Badge != null)
        {
            posterGrid.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(5), CornerRadius = new CornerRadius(14),
                Background = c.Badge == "✓" ? Ui.Color(0xCC7F1D1D) : Ui.Color(0xCC92400E),
                BorderBrush = c.Badge == "✓" ? Ui.Color(0xFFF87171) : Ui.Color(0xFFF59E0B), BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 2, 7, 2),
                Child = new TextBlock { Text = c.Badge, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Ui.Brush("TextBrush") }
            });
        }
        if (c.Progress != null)
            posterGrid.Children.Add(new ThinProgress { VerticalAlignment = VerticalAlignment.Bottom, Value = c.Progress.Value * 1000 });
        stack.Children.Add(posterGrid);
        stack.Children.Add(new TextBlock { Text = c.Title, FontSize = 12, Foreground = Ui.Brush("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) });
        // An empty subtitle keeps its line so every card in the row is the same height.
        stack.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(c.Subtitle) ? " " : c.Subtitle, FontSize = 10.5, Foreground = Ui.Brush("TextMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        LazyImages.Register(img, c.Image, 320);
        var card = new FocusCard { Content = stack, Classes = { "stream" }, Padding = new Thickness(5) };
        card.Click += c.OnClick;
        if (c.OnLongClick != null) card.LongClick += c.OnLongClick;
        return card;
    }

    // ---- Actions ----------------------------------------------------------------

    private void PlayMovie(Data.Stream s)
    {
        var id = s.StreamId;
        if (id == null) return;
        string url;
        try { url = XtreamApi.StreamUrl(_service, _account, s, _prefs.LiveFormat); }
        catch (Exception e) { Dialogs.Toast(e.Message); return; }
        var key = WatchProgress.MovieKey(_service, id);
        WatchProgress.Describe(key, WatchProgress.KIND_MOVIE, s.Name ?? "", s.Image, s.ContainerExtension, id);
        Nav.Push(new PlayerPage(url, s.Name ?? "", false, key));
    }

    private void PlayEpisode(string seriesId, string seriesTitle, string? image, Episode ep)
    {
        string url;
        try { url = XtreamApi.EpisodeUrl(_service, _account, ep); }
        catch (Exception e) { Dialogs.Toast(e.Message); return; }
        var key = WatchProgress.EpisodeKey(ep.Id);
        WatchProgress.Describe(key, WatchProgress.KIND_EPISODE, seriesTitle, image, ep.ContainerExtension, ep.Id,
            subtitle: ep.Title, seriesId: seriesId, season: ep.Season, episode: ep.Number);
        Nav.Push(new PlayerPage(url, $"{seriesTitle} · S{ep.Season}E{ep.Number} {ep.Title}", false, key));
    }

    private void OpenSeries(string id, string title, string? image, string? plot) => Nav.Push(new SeriesPage(_service, id, title, image, plot));

    private void Resume(string key, WatchProgress.Entry e)
    {
        var id = e.ItemId;
        if (id == null) return;
        switch (e.Kind)
        {
            case WatchProgress.KIND_MOVIE:
            {
                string url;
                try { url = XtreamApi.MovieUrl(_service, _account, id, e.Ext); } catch (Exception ex) { Dialogs.Toast(ex.Message); return; }
                Nav.Push(new PlayerPage(url, e.Title ?? "", false, key));
                break;
            }
            case WatchProgress.KIND_EPISODE:
            {
                var ep = new Episode(id, e.Subtitle ?? "", e.Season, e.Episode, e.Ext ?? "mp4", null, null);
                string url;
                try { url = XtreamApi.EpisodeUrl(_service, _account, ep); } catch (Exception ex) { Dialogs.Toast(ex.Message); return; }
                Nav.Push(new PlayerPage(url, $"{e.Title} · S{e.Season}E{e.Episode} {e.Subtitle ?? ""}", false, key));
                break;
            }
            case WatchProgress.KIND_DOWNLOAD:
            {
                var job = TransferStore.Get().Get(id);
                var path = job?.FileUri;
                if (path == null || !Folders.Exists(path)) { Dialogs.Toast("The file is no longer on this computer."); return; }
                Nav.Push(new PlayerPage(path, e.Title ?? "", false, key));
                break;
            }
        }
    }

    private void ShowContinueMenu(string key, WatchProgress.Entry e)
    {
        var opts = new List<string> { "Play", "Mark as watched", "Remove from Continue watching" };
        if (e.Kind == WatchProgress.KIND_EPISODE && e.SeriesId != null) opts.Add("Open series");
        switch (Dialogs.Items(e.Title ?? "", opts))
        {
            case 0: Resume(key, e); break;
            case 1: WatchProgress.SetWatched(key, true); Render(); LoadNextEpisodes(); break;
            case 2: WatchProgress.Remove(key); Render(); break;
            case 3: OpenSeries(e.SeriesId!, e.Title ?? "", e.Image, null); break;
        }
    }

    private void ShowMovieMenu(Data.Stream s)
    {
        var id = s.Id;
        if (id == null) return;
        var key = s.StreamId != null ? WatchProgress.MovieKey(_service, s.StreamId) : null;
        var fav = _prefs.IsFavorite(_service, ContentKind.MOVIE, id);
        var opts = new List<string> { "Play", fav ? "Remove from favorites" : "Add to favorites", "Download…" };
        if (key != null) opts.Add(WatchProgress.IsWatched(key) ? "Mark as unwatched" : "Mark as watched");
        switch (Dialogs.Items(s.Name ?? "", opts))
        {
            case 0: PlayMovie(s); break;
            case 1: _prefs.ToggleFavorite(_service, ContentKind.MOVIE, id); Render(); break;
            case 2:
            {
                string url;
                try { url = XtreamApi.StreamUrl(_service, _account, s, _prefs.LiveFormat); } catch { return; }
                TransferDialogs.Download(new[] { new DownloadItem(Names.Movie(s.Name ?? "Movie"), "Movies", url, s.ContainerExtension ?? "mp4") });
                break;
            }
            case 3: WatchProgress.SetWatched(key!, !WatchProgress.IsWatched(key!)); Render(); break;
        }
    }

    private void ShowSeriesMenu(string id, string title, string? image, string? plot = null)
    {
        var fav = _prefs.IsFavorite(_service, ContentKind.SERIES, id);
        switch (Dialogs.Items(title, new[] { "Open", fav ? "Remove from favorites" : "Add to favorites" }))
        {
            case 0: OpenSeries(id, title, image, plot); break;
            case 1: _prefs.ToggleFavorite(_service, ContentKind.SERIES, id); Render(); break;
        }
    }

    private static string Duration(long ms)
    {
        var m = (int)(ms / 60_000);
        return m >= 60 ? $"{m / 60}h {m % 60}m" : $"{Math.Max(1, m)}m";
    }
}
