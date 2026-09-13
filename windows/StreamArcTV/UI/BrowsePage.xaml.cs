using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using StreamArcTV.Data;
using StreamArcTV.Player;
using StreamArcTV.Transfer;
using StreamArcTV.Util;

namespace StreamArcTV.UI;

/// <summary>
/// Category + content browser. For Live TV this is the TV guide: the programme grid with a
/// details panel above it and a small live preview. For movies and series it is the poster grid.
/// Holding Enter (or right-clicking) on any item opens a context menu with Play and Add/Remove
/// favorites.
/// </summary>
public partial class BrowsePage : AppPage, EpgGridView.IListener
{
    private const string FAV_ID = Prefs.CATEGORY_FAVORITES;
    public const string RECENT_ID = "__recent__";
    public const string GENRE_PREFIX = "__genre__:";
    /// Above this, the whole-guide file is not worth downloading; channels are fetched individually.
    private const long FULL_GUIDE_LIMIT = 60L * 1024 * 1024;

    private readonly Service _service;
    private readonly Prefs _prefs = Prefs.Instance;
    private readonly Account _account = null!;
    private ContentKind _kind;
    private bool IsLive => _kind == ContentKind.LIVE;

    private List<Data.Stream> _allStreams = new();
    private readonly Dictionary<string, List<Data.Stream>> _streamCache = new();
    private int _loadSerial;
    private string? _selectedCategoryId;
    private bool _favoritesMode;
    private bool _guideReady;
    private CancellationTokenSource? _prefetchCts;
    private List<Category> _categories = new();
    private string? _selectedId;
    private List<Category> _providerCategories = new();
    private bool _augmented;
    private string? _pendingCategory;
    private readonly bool _openSearch;
    private bool _compact;
    private bool _dead;

    /// Small live preview in the guide panel.
    private LibVLC? _libVlc;
    private MediaPlayer? _preview;
    private Data.Stream? _previewStream;

    public BrowsePage(Service service) : this(service, null, null, false) { }

    /// Opens movies or series on a given group (a category id, RECENT_ID or a GENRE_PREFIX genre), or on the search box.
    public BrowsePage(Service service, ContentKind? kind, string? category = null, bool search = false)
    {
        InitializeComponent();
        _service = service;
        var acct = _prefs.Account(service);
        if (acct == null)
        {
            _dead = true;
            Loaded += (_, _) => { Nav.Replace(new LoginPage(service)); };
            return;
        }
        _account = acct;
        _kind = kind ?? service.Kind();
        _pendingCategory = category;
        _openSearch = search;

        TxtTitle.Text = IsLive ? "TV Guide" : service.Title();
        BtnBack.Click += (_, _) => Finish();
        BtnProfile.Click += (_, _) => Nav.Push(new ProfilePage(service));
        BtnMulti.Visibility = IsLive ? Visibility.Visible : Visibility.Collapsed;
        BtnMulti.Click += (_, _) => Nav.Push(new MultiViewPage(service));

        if (service.Kind() != ContentKind.LIVE)
        {
            BtnTabMovies.Click += (_, _) => SwitchKind(ContentKind.MOVIE);
            BtnTabSeries.Click += (_, _) => SwitchKind(ContentKind.SERIES);
            BtnTabDownloads.Click += (_, _) => Nav.Push(new TransfersPage(TransferType.DOWNLOAD));
            RenderTabs();
        }

        if (IsLive)
        {
            PanelEpg.Visibility = Visibility.Visible;
            TxtPanelChannel.Text = "TV Guide";
            TxtPanelNow.Text = "";
            TxtPanelDesc.Text = "Hold Enter for options";
            TxtPanelUpcoming.Text = "";
            ImgPanelLogo.Source = ImageLoader.Placeholder;
            ListStreams.Visibility = Visibility.Collapsed;
            EpgGrid.Visibility = Visibility.Visible;
            EpgGrid.GuideLookup = ch => EpgCache.GuideFor(service, ch.EpgChannelId, ch.Name);
            EpgGrid.Listener = this;
            _ = LoadGuide();
        }
        else
        {
            PanelEpg.Visibility = Visibility.Collapsed;
            EpgGrid.Visibility = Visibility.Collapsed;
        }

        InputSearch.TextChanged += (_, _) => ApplyFilter();
        SizeChanged += (_, _) => { ApplyCategoryLayout(); if (!IsLive) RebuildGrid(); };
        Loaded += (_, _) =>
        {
            ApplyCategoryLayout();
            if (_openSearch) Dispatcher.BeginInvoke(() => InputSearch.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
        LoadCategories();
    }

    public override IInputElement? InitialFocus => _openSearch ? InputSearch : (IsLive ? EpgGrid : null);

    public override void OnResume()
    {
        if (_dead) return;
        // If the user signed out from the profile screen, leave.
        if (!_prefs.IsSignedIn(_service)) { Finish(); return; }
        // Watched marks may have changed in the player; rebuilding thousands of rows for nothing is a visible stall.
        if (!IsLive && _watchVersion != WatchProgress.Version) RebuildGrid(force: true);
        if (IsLive && _previewStream != null) StartPreview(_previewStream);
    }

    public override void OnPause() => ReleasePreview();

    public override void OnDestroy()
    {
        _prefetchCts?.Cancel();
        ReleasePreview();
        _libVlc = null;
        LazyImages.Forget(this);
    }

    // ---- Live preview -------------------------------------------------------

    private void StartPreview(Data.Stream stream)
    {
        string url;
        try { url = XtreamApi.StreamUrl(_service, _account, stream, _prefs.LiveFormat); } catch { return; }
        _previewStream = stream;
        try
        {
            _libVlc ??= PlayerCore.Shared();
            if (_preview == null)
            {
                _preview = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
                _preview.EncounteredError += (_, _) => Dialogs.Toast("Preview failed");
                PreviewPlayer.MediaPlayer = _preview;
            }
            PreviewPlayer.Visibility = Visibility.Visible;
            ImgPanelLogo.Visibility = Visibility.Collapsed;
            PreviewHint.Visibility = Visibility.Visible;
            using var media = new Media(_libVlc, new Uri(url));
            media.AddOption(":network-caching=3000");
            media.AddOption(":http-user-agent=" + XtreamApi.USER_AGENT);
            _preview.Play(media);
        }
        catch (Exception e)
        {
            AppLog.E("Browse", "preview failed", e);
        }
    }

    private void ReleasePreview()
    {
        var p = _preview;
        _preview = null;
        PreviewPlayer.MediaPlayer = null;
        if (p != null) PlayerCore.DisposePlayer(p);
        PreviewPlayer.Visibility = Visibility.Collapsed;
        PreviewHint.Visibility = Visibility.Collapsed;
        ImgPanelLogo.Visibility = Visibility.Visible;
    }

    // ---- Layout ------------------------------------------------------------

    /// Narrow windows get a horizontal chip row so the content gets the full width.
    private void ApplyCategoryLayout()
    {
        var compact = Ui.Compact(this);
        if (compact == _compact && CategoryList.Children.Count + ChipsRow.Children.Count > 0) return;
        _compact = compact;
        PlaceTabs(compact);
        // Keep the guide panel short when the window is small so the grid gets the height.
        TxtPanelDesc.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TxtPanelUpcoming.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PanelEpg.Padding = new Thickness(12, compact ? 6 : 10, 16, compact ? 6 : 10);
        if (compact)
        {
            CategoryScroll.Visibility = Visibility.Collapsed;
            CategoryCol.Width = new GridLength(0);
            ChipsScroll.Visibility = Visibility.Visible;
        }
        else
        {
            ChipsScroll.Visibility = Visibility.Collapsed;
            CategoryScroll.Visibility = Visibility.Visible;
            // Live TV keeps the column slim; long names ellipsize and the guide gets the width.
            CategoryCol.Width = new GridLength(IsLive ? 140 : 220);
        }
        RenderCategories();
    }

    /// Tabs live in the top bar on wide screens and on their own full-width row on narrow ones.
    private void PlaceTabs(bool compact)
    {
        var showTabs = _service.Kind() != ContentKind.LIVE;
        var buttons = new[] { BtnTabMovies, BtnTabSeries, BtnTabDownloads };
        foreach (var b in buttons)
        {
            (b.Parent as Panel)?.Children.Remove(b);
            b.Margin = new Thickness(compact ? 3 : (ReferenceEquals(b, BtnTabMovies) ? 0 : 6), 0, compact ? 3 : 0, 0);
            if (compact) TabsRow.Children.Add(b); else Tabs.Children.Add(b);
        }
        Tabs.Visibility = showTabs && !compact ? Visibility.Visible : Visibility.Collapsed;
        TabsRow.Visibility = showTabs && compact ? Visibility.Visible : Visibility.Collapsed;
        // The chip row already shows the category; the top-bar label is dropped when narrow.
        TxtCategory.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderTabs()
    {
        Ui.SetSelected(BtnTabMovies, _kind == ContentKind.MOVIE);
        Ui.SetSelected(BtnTabSeries, _kind == ContentKind.SERIES);
    }

    private void SwitchKind(ContentKind newKind)
    {
        if (newKind == _kind) return;
        _kind = newKind;
        _augmented = false;
        RenderTabs();
        _loadSerial++;
        _streamCache.Clear();
        _allStreams = new();
        InputSearch.Text = "";
        ListStreams.ItemsSource = null;
        LoadCategories();
    }

    // ---- Categories ----------------------------------------------------------

    private readonly List<(FocusCard Card, Category Cat)> _categoryCards = new();

    private void RenderCategories()
    {
        CategoryList.Children.Clear();
        ChipsRow.Children.Clear();
        _categoryCards.Clear();
        foreach (var c in _categories)
        {
            var cat = c;
            var card = new FocusCard
            {
                Style = Ui.Res<Style>(_compact ? "ChipItem" : "CategoryItem"),
                Selected = cat.Id == _selectedId,
                Content = new TextBlock
                {
                    Text = cat.Name ?? "—", FontSize = _compact ? 13 : 14, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = _compact ? FontWeights.SemiBold : FontWeights.Normal, MaxWidth = _compact ? 200 : double.PositiveInfinity
                }
            };
            card.Click += () => SelectCategory(cat);
            _categoryCards.Add((card, cat));
            if (_compact) ChipsRow.Children.Add(card); else CategoryList.Children.Add(card);
        }
        // Chips inherit the card foreground from the style trigger.
        foreach (var chip in ChipsRow.Children.OfType<FocusCard>())
            ((TextBlock)chip.Content).SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = chip });
    }

    private void SetSelectedCategory(string? id)
    {
        _selectedId = id;
        foreach (var (card, cat) in _categoryCards) card.Selected = cat.Id == id;
    }

    private async void LoadCategories()
    {
        SetLoading(true);
        try
        {
            var fetched = await XtreamApi.Categories(_service, _account, _kind);
            var cats = IsLive ? fetched.Where(c => c.Id == null || !_prefs.HiddenLiveCategories.Contains(c.Id)).ToList() : fetched;
            _providerCategories = cats;
            // With the catalogue already on disk the genre / extra groups are known now;
            // otherwise they are added as soon as it arrives (see AugmentCategories).
            var cached = IsLive ? null : CatalogCache.Peek(_service, _kind);
            List<Category> all;
            if (IsLive)
                all = new List<Category> { new(FAV_ID, "★ Favorites"), new(null, "All") }.Concat(cats).ToList();
            else if (cached != null) { _augmented = true; all = await Task.Run(() => BuildVodCategories(cats, cached)); }
            else
                all = new List<Category> { new(FAV_ID, "★ Favorites"), new(RECENT_ID, "Recently added"), new(null, "All") }.Concat(cats).ToList();
            _categories = all;
            RenderCategories();
            var wanted = _pendingCategory;
            _pendingCategory = null;
            if (IsLive) SelectCategory(DefaultLiveCategory(all));
            else if (wanted != null)
            {
                // Opened from the VOD home on a particular group (a genre row's "See all", say).
                SelectCategory(all.FirstOrDefault(c => c.Id == wanted)
                    ?? (wanted.StartsWith(GENRE_PREFIX) ? new Category(wanted, wanted[GENRE_PREFIX.Length..]) : all[1]));
            }
            else SelectCategory(all[1]);   // Movies and series open on Recently added.
        }
        catch (Exception e)
        {
            ShowError(string.IsNullOrWhiteSpace(e.Message) ? "Couldn't load content." : e.Message);
        }
    }

    /// The category the guide opens on: the user's setting, else "General …" if the provider has one, else All.
    private Category DefaultLiveCategory(List<Category> all)
    {
        var wanted = _prefs.DefaultLiveCategory;
        switch (wanted)
        {
            case Prefs.CATEGORY_FAVORITES: return all[0];
            case Prefs.CATEGORY_ALL: return all[1];
            case null: break;
            default:
                var hit = all.FirstOrDefault(c => c.Id == wanted);
                if (hit != null) return hit;
                break;
        }
        var provider = all.Where(c => c.Id != null && c.Id != FAV_ID).ToList();
        return provider.FirstOrDefault(c => string.Equals(c.Name?.Trim(), "General Streams", StringComparison.OrdinalIgnoreCase))
               ?? provider.FirstOrDefault(c => c.Name?.Contains("general streams", StringComparison.OrdinalIgnoreCase) == true)
               ?? provider.FirstOrDefault(c => c.Name?.Contains("general", StringComparison.OrdinalIgnoreCase) == true)
               ?? all[1];
    }

    /// Favorites / Recently added / All, then genre groups from the catalogue, then the provider's list.
    private static List<Category> BuildVodCategories(List<Category> provider, List<Data.Stream> catalogue)
    {
        var cats = provider;
        var known = cats.Where(c => c.Id != null).Select(c => c.Id!).ToHashSet();
        var extra = catalogue.SelectMany(s => s.AllCategoryIds).Where(id => !known.Contains(id)).Distinct().ToList();
        if (extra.Count > 0) cats = cats.Concat(extra.Select(id => new Category(id, $"Category {id}"))).ToList();
        var providerNames = cats.Where(c => c.Name != null).Select(c => c.Name!.Trim().ToLowerInvariant()).ToHashSet();
        var counts = new Dictionary<string, int>();
        foreach (var item in catalogue) foreach (var g in item.Genres) counts[g] = counts.GetValueOrDefault(g) + 1;
        var genres = counts.Where(kv => kv.Value >= 3 && !providerNames.Contains(kv.Key.ToLowerInvariant()))
            .Select(kv => kv.Key).OrderBy(g => g.ToLowerInvariant())
            .Select(g => new Category(GENRE_PREFIX + g, g)).ToList();
        return new List<Category> { new(FAV_ID, "★ Favorites"), new(RECENT_ID, "Recently added"), new(null, "All") }
            .Concat(genres).Concat(cats).ToList();
    }

    /// Called once the catalogue is available: adds genre / extra groups without losing the selection.
    private async void AugmentCategories(List<Data.Stream> catalogue)
    {
        if (IsLive || _augmented) return;
        _augmented = true;
        var provider = _providerCategories;
        var built = await Task.Run(() => BuildVodCategories(provider, catalogue));
        if (Finished) return;
        _categories = built;
        RenderCategories();
        SetSelectedCategory(_selectedId);
    }

    private async void SelectCategory(Category cat)
    {
        _favoritesMode = cat.Id == FAV_ID;
        var recentMode = cat.Id == RECENT_ID;
        _selectedCategoryId = _favoritesMode || recentMode ? null : cat.Id;
        SetSelectedCategory(cat.Id);
        TxtCategory.Text = cat.Name ?? "";
        var serial = ++_loadSerial;
        SetLoading(true);
        try
        {
            var key = _selectedCategoryId;
            List<Data.Stream> list;
            if (IsLive)
            {
                var cacheKey = key ?? "";
                if (!_streamCache.TryGetValue(cacheKey, out list!))
                {
                    list = await XtreamApi.Streams(_service, _account, key, _kind);
                    _streamCache[cacheKey] = list;
                }
            }
            else
            {
                // Movies/series: the whole catalogue is fetched once (kept on disk) and filtered here.
                var all = await CatalogCache.Get(_service, _account, _kind);
                if (serial != _loadSerial) return;
                AugmentCategories(all);
                list = await Task.Run(() =>
                {
                    if (recentMode) return CatalogCache.RecentlyAdded(all);
                    if (key == null) return all;
                    if (key.StartsWith(GENRE_PREFIX))
                    {
                        var g = key[GENRE_PREFIX.Length..];
                        return all.Where(item => item.Genres.Any(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase))).ToList();
                    }
                    return all.Where(item => item.AllCategoryIds.Contains(key)).ToList();
                });
            }
            if (serial != _loadSerial) return;
            _allStreams = list;
            ApplyFilter();
            SetLoading(false);
        }
        catch (Exception e)
        {
            if (serial != _loadSerial) return;
            ShowError(string.IsNullOrWhiteSpace(e.Message) ? "Couldn't load content." : e.Message);
        }
    }

    private void ApplyFilter()
    {
        if (_dead) return;
        var q = InputSearch.Text.Trim();
        var favs = _prefs.Favorites(_service, _kind);
        var list = _favoritesMode ? _allStreams.Where(s => s.Id != null && favs.Contains(s.Id)).ToList() : _allStreams;
        if (IsLive)
        {
            var hidden = _prefs.HiddenLiveCategories;
            if (hidden.Count > 0) list = list.Where(s => s.CategoryId == null || !hidden.Contains(s.CategoryId)).ToList();
        }
        if (q.Length > 0) list = list.Where(s => s.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) == true).ToList();

        if (IsLive)
        {
            EpgGrid.Favorites = favs;
            EpgGrid.SetChannels(list);
            if (list.Count > 0 && !InputSearch.IsKeyboardFocused && Keyboard.FocusedElement is not FrameworkElement { IsVisible: true }) EpgGrid.Focus();
            if (_guideReady) PrefetchEpg();
        }
        else
        {
            _filtered = list;
            RebuildGrid(force: true);
        }

        TxtEmpty.Text = _favoritesMode && favs.Count == 0 ? "No favorites yet. Hold Enter on an item to add it." : "Nothing here yet.";
        TxtEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<Data.Stream> _filtered = new();

    private int PosterColumns()
    {
        var w = Math.Max(300, ActualWidth - (_compact ? 0 : (IsLive ? 140 : 220)) - 40);
        return Math.Max(2, Math.Min(10, (int)(w / 150)));
    }

    private int _lastCols;

    private int _watchVersion = -1;

    private void RebuildGrid(bool force = false)
    {
        if (IsLive) return;
        var cols = PosterColumns();
        if (!force && cols == _lastCols && ListStreams.ItemsSource != null) return;
        _lastCols = cols;
        _watchVersion = WatchProgress.Version;
        var favs = _prefs.Favorites(_service, _kind);
        Func<Data.Stream, string?>? watchKey = _kind == ContentKind.MOVIE ? s => s.StreamId != null ? WatchProgress.MovieKey(_service, s.StreamId) : null : null;
        var rows = new List<PosterRow>();
        for (var i = 0; i < _filtered.Count; i += cols)
            rows.Add(new PosterRow(_filtered.GetRange(i, Math.Min(cols, _filtered.Count - i)), cols, favs, watchKey, Play, ShowItemMenu));
        ListStreams.ItemsSource = rows;
    }

    // ---- Actions -------------------------------------------------------

    private void Play(Data.Stream stream)
    {
        if (_kind == ContentKind.SERIES)
        {
            var id = stream.SeriesId ?? stream.StreamId;
            if (id == null) return;
            Nav.Push(new SeriesPage(_service, id, stream.Name ?? "", stream.Image, stream.Plot));
            return;
        }
        string url;
        try { url = XtreamApi.StreamUrl(_service, _account, stream, _prefs.LiveFormat); }
        catch (Exception e) { ShowError(string.IsNullOrWhiteSpace(e.Message) ? "Couldn't load content." : e.Message); return; }
        var key = _kind == ContentKind.MOVIE && stream.StreamId != null ? WatchProgress.MovieKey(_service, stream.StreamId) : null;
        if (key != null) WatchProgress.Describe(key, WatchProgress.KIND_MOVIE, stream.Name ?? "", stream.Image, stream.ContainerExtension, stream.StreamId!);
        Nav.Push(new PlayerPage(url, stream.Name ?? "", IsLive, key));
    }

    /// Context menu opened by holding Enter / right-clicking an item.
    private void ShowItemMenu(Data.Stream stream)
    {
        var id = stream.Id;
        if (id == null) return;
        var isFav = _prefs.IsFavorite(_service, _kind, id);
        var favLabel = isFav ? "Remove from favorites" : "Add to favorites";
        var first = _kind == ContentKind.SERIES ? "Open" : "Play";
        var third = _kind switch { ContentKind.LIVE => "Record…", ContentKind.MOVIE => "Download…", _ => "Download entire series…" };
        var watchKey = _kind == ContentKind.MOVIE && stream.StreamId != null ? WatchProgress.MovieKey(_service, stream.StreamId) : null;
        var options = new List<string> { first, favLabel, third };
        if (watchKey != null) options.Add(WatchProgress.IsWatched(watchKey) ? "Mark as unwatched" : "Mark as watched");
        var which = Dialogs.Items(stream.Name ?? "", options);
        switch (which)
        {
            case 0: Play(stream); break;
            case 1:
                var nowFav = _prefs.ToggleFavorite(_service, _kind, id);
                Dialogs.Toast(nowFav ? $"{stream.Name} added to favorites" : $"{stream.Name} removed from favorites");
                ApplyFilter();
                break;
            case 2:
                switch (_kind)
                {
                    case ContentKind.LIVE: RecordChannel(stream); break;
                    case ContentKind.MOVIE: DownloadMovie(stream); break;
                    default: DownloadSeries(stream); break;
                }
                break;
            case 3:
                WatchProgress.SetWatched(watchKey!, !WatchProgress.IsWatched(watchKey!));
                RebuildGrid(force: true);
                break;
        }
    }

    private void DownloadMovie(Data.Stream stream)
    {
        string url;
        try { url = XtreamApi.StreamUrl(_service, _account, stream, _prefs.LiveFormat); }
        catch (Exception e) { Dialogs.Toast(e.Message); return; }
        var ext = string.IsNullOrWhiteSpace(stream.ContainerExtension) ? "mp4" : stream.ContainerExtension!;
        TransferDialogs.Download(new[] { new DownloadItem(stream.Name ?? "Movie", "Movies", url, ext) });
    }

    private async void DownloadSeries(Data.Stream stream)
    {
        var seriesId = stream.SeriesId ?? stream.StreamId;
        if (seriesId == null) return;
        var name = stream.Name ?? "Series";
        List<Episode> episodes;
        try { episodes = await XtreamApi.SeriesInfo(_service, _account, seriesId); }
        catch (Exception e) { Dialogs.Toast(string.IsNullOrWhiteSpace(e.Message) ? "Couldn't load content." : e.Message); return; }
        if (episodes.Count == 0) { Dialogs.Toast("No episodes found."); return; }
        var r = Dialogs.Alert(name, $"Download all {episodes.Count} episodes?", "Download all", "Cancel");
        if (r != DialogResultKind.Positive) return;
        var items = episodes.Select(ep => new DownloadItem($"{name} S{ep.Season}E{ep.Number} {ep.Title}", name, XtreamApi.EpisodeUrl(_service, _account, ep), ep.ContainerExtension)).ToList();
        TransferDialogs.Download(items);
    }

    private void RecordChannel(Data.Stream stream)
    {
        // Recordings always use the MPEG-TS stream so the file plays back directly.
        string url;
        try { url = XtreamApi.StreamUrl(_service, _account, stream, "ts"); }
        catch (Exception e) { Dialogs.Toast(e.Message); return; }
        TransferDialogs.Record(stream.Name ?? "Channel", url);
    }

    // ---- Guide ---------------------------------------------------------------

    private async Task LoadGuide()
    {
        var mode = _prefs.GuideMode;
        var remembered = _prefs.GuideSize(_service);
        var usePerChannel = mode == "channel" || (mode == "auto" && remembered > FULL_GUIDE_LIMIT);
        long lastBytes = 0;
        var ok = false;
        if (!usePerChannel)
        {
            ShowGuideProgress("Downloading guide…", null);
            ok = await EpgCache.LoadGuide(_service, _account, (bytes, total) =>
            {
                lastBytes = bytes;
                if (mode == "auto" && bytes > FULL_GUIDE_LIMIT)
                {
                    // Far too big to pull on every refresh: remember that and switch to
                    // per-channel lookups, which only fetch the channels you actually list.
                    _prefs.SetGuideSize(_service, bytes);
                    throw new XtreamApi.GuideTooLarge(bytes);
                }
                var exact = total > 0;
                var estimate = exact ? total : remembered > 0 ? remembered : 25L * 1024 * 1024;
                var shown = Math.Max(estimate, bytes + 1);
                var pct = (int)Math.Clamp(bytes * 100 / shown, 0, 99);
                Ui.Post(() => ShowGuideProgress($"Downloading guide: {Format.Size(bytes)} / {(exact ? "" : "~")}{Format.Size(shown)} ({pct}%)", (int)(bytes * 1000 / shown)));
            });
        }
        if (_dead || Finished) return;
        if (ok && lastBytes > 0) _prefs.SetGuideSize(_service, lastBytes);
        if (ok) EpgGrid.GuideLoaded();
        _guideReady = true;
        PrefetchEpg();
    }

    public void OnFocusChanged(Data.Stream channel, EpgProgramme? programme) =>
        ShowChannelDetails(channel, channel.StreamId != null ? EpgCache.Peek(_service, channel.StreamId) : null, programme);

    public void OnChannelClick(Data.Stream channel)
    {
        if (_previewStream?.StreamId != null && _previewStream.StreamId == channel.StreamId) Play(channel);
        else StartPreview(channel);
    }

    public void OnChannelLongClick(Data.Stream channel) => ShowItemMenu(channel);

    public async void OnNeedEpg(Data.Stream channel)
    {
        var id = channel.StreamId;
        if (id == null) return;
        // Prefer the whole-guide download when it is in use; fall back to the per-channel call for channels it doesn't cover.
        if (_prefs.GuideMode != "channel" && !(_prefs.GuideMode == "auto" && _prefs.GuideSize(_service) > FULL_GUIDE_LIMIT))
            await EpgCache.LoadGuide(_service, _account);
        var fromGuide = EpgCache.GuideFor(_service, channel.EpgChannelId, channel.Name);
        var list = fromGuide ?? await EpgCache.Get(_service, _account, id);
        if (!Finished) EpgGrid.SetEpg(id, list);
    }

    /// Fill the guide for every channel in the current list ahead of scrolling: from the
    /// downloaded listing when it covers the channel, otherwise one short-EPG call per channel.
    private async void PrefetchEpg()
    {
        _prefetchCts?.Cancel();
        var cts = _prefetchCts = new CancellationTokenSource();
        var channels = _allStreams;
        var total = channels.Count;
        var done = 0;
        long lastShown = 0;
        ShowGuideProgress($"Loading guide: 0 / {total} channels", 0);
        // Several channels at a time (EpgCache limits real network calls to 6 at once).
        foreach (var chunk in channels.Chunk(12))
        {
            if (cts.IsCancellationRequested) return;
            var results = await Task.WhenAll(chunk.Select(async ch =>
            {
                var id = ch.StreamId;
                if (id == null) return ((string, List<EpgProgramme>)?)null;
                if (EpgCache.Peek(_service, id) != null) return null;
                var fromGuide = EpgCache.GuideFor(_service, ch.EpgChannelId, ch.Name);
                return (id, fromGuide ?? await EpgCache.Get(_service, _account, id));
            }));
            if (cts.IsCancellationRequested || Finished) return;
            foreach (var pair in results)
            {
                if (pair != null) EpgGrid.SetEpg(pair.Value.Item1, pair.Value.Item2);
                done++;
            }
            var now = Format.NowMs;
            if (now - lastShown > 150 || done == total)
            {
                lastShown = now;
                ShowGuideProgress($"Loading guide: {done} / {total} channels", total > 0 ? done * 1000 / total : 0);
            }
        }
        HideGuideProgress();
    }

    /// Thin bar under the guide panel: indeterminate while the listing downloads, then per-channel progress.
    private void ShowGuideProgress(string text, int? progress)
    {
        if (!IsLive) return;
        GuideLoading.Visibility = Visibility.Visible;
        TxtGuideLoading.Text = text;
        if (progress == null) ProgressGuide.IsIndeterminate = true;
        else { ProgressGuide.IsIndeterminate = false; ProgressGuide.Value = Math.Clamp(progress.Value, 0, 1000); }
    }

    private void HideGuideProgress() => GuideLoading.Visibility = Visibility.Collapsed;

    private void ShowChannelDetails(Data.Stream stream, List<EpgProgramme>? programmes, EpgProgramme? focused = null)
    {
        if (!IsLive) return;
        TxtPanelChannel.Text = stream.Name ?? "";
        ImageLoader.Load(ImgPanelLogo, stream.Icon, 288);
        var now = Format.NowSec;
        var list = programmes ?? new();
        var current = focused ?? list.FirstOrDefault(p => p.IsOnNow(now)) ?? list.FirstOrDefault(p => p.End > now);
        if (current == null)
        {
            TxtPanelNow.Text = programmes == null ? "Loading guide…" : "No programme information";
            TxtPanelDesc.Text = "Hold Enter for options";
            TxtPanelUpcoming.Text = "";
            return;
        }
        TxtPanelNow.Text = $"{Format.TimeRange(current.Start, current.End)}   {current.Title}";
        TxtPanelDesc.Text = string.IsNullOrWhiteSpace(current.Description) ? "Hold Enter for options" : current.Description;
        var up = string.Join("   ·   ", list.Where(p => p.Start >= current.End).Take(2).Select(p => $"{Format.Time(p.Start)}  {p.Title}"));
        TxtPanelUpcoming.Text = string.IsNullOrWhiteSpace(up) ? "" : $"Next: {up}";
    }

    private void SetLoading(bool loading)
    {
        Progress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        TxtError.Visibility = Visibility.Collapsed;
        if (loading) TxtEmpty.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string msg)
    {
        Progress.Visibility = Visibility.Collapsed;
        TxtError.Text = msg;
        TxtError.Visibility = Visibility.Visible;
    }
}
