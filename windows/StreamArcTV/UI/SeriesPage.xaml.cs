using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamArcTV.Data;
using StreamArcTV.Transfer;

namespace StreamArcTV.UI;

/// Seasons and episodes of one series; choosing an episode plays it.
public partial class SeriesPage : AppPage
{
    private readonly Service _service;
    private readonly string _seriesId;
    private readonly string _title;
    private readonly string? _cover;
    private List<Episode> _episodes = new();
    private readonly List<(FocusCard Row, Episode Ep, TextBlock Title, ProgressBar Progress)> _rows = new();

    public SeriesPage(Service service, string seriesId, string title, string? cover, string? plot)
    {
        InitializeComponent();
        _service = service; _seriesId = seriesId; _title = title; _cover = cover;
        TxtTitle.Text = title;
        TxtPlot.Text = plot ?? "";
        ImageLoader.Load(ImgCover, string.IsNullOrWhiteSpace(cover) ? null : cover, 400);
        BtnBack.Click += (_, _) => Finish();
        BtnDownloadAll.Click += (_, _) => DownloadAll();
        Load();
    }

    public override IInputElement? InitialFocus => _rows.Count > 0 ? _rows[0].Row : BtnBack;

    private async void Load()
    {
        var account = Prefs.Instance.Account(_service);
        if (account == null) { Finish(); return; }
        Progress.Visibility = Visibility.Visible;
        try
        {
            var episodes = await XtreamApi.SeriesInfo(_service, account, _seriesId);
            Progress.Visibility = Visibility.Collapsed;
            if (episodes.Count == 0) { TxtError.Text = "No episodes found."; TxtError.Visibility = Visibility.Visible; }
            else
            {
                _episodes = episodes;
                Render();
                BtnDownloadAll.Visibility = Visibility.Visible;
                if (IsResumed) _ = Dispatcher.BeginInvoke(() => _rows.FirstOrDefault().Row?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        }
        catch (Exception e)
        {
            Progress.Visibility = Visibility.Collapsed;
            TxtError.Text = string.IsNullOrWhiteSpace(e.Message) ? "Couldn't load content." : e.Message;
            TxtError.Visibility = Visibility.Visible;
        }
    }

    public override void OnResume() => RefreshRows();

    private void Render()
    {
        List.Children.Clear();
        _rows.Clear();
        for (var i = 0; i < _episodes.Count; i++)
        {
            var ep = _episodes[i];
            var firstOfSeason = i == 0 || _episodes[i - 1].Season != ep.Season;
            if (firstOfSeason)
            {
                var head = new Grid { Margin = new Thickness(6, i == 0 ? 0 : 10, 0, 6) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.Children.Add(new TextBlock { Text = $"SEASON {ep.Season}", Style = Ui.Res<Style>("SectionHeader"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
                var season = ep.Season;
                var dl = new Button { Content = "Download…", Style = Ui.Res<Style>("SmallButton") };
                dl.Click += (_, _) => DownloadSeason(season);
                Grid.SetColumn(dl, 1);
                head.Children.Add(dl);
                List.Children.Add(head);
            }
            var stack = new StackPanel();
            var title = new TextBlock { FontSize = 15, Foreground = Ui.Brush("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
            var info = string.Join("  ·  ", new[] { ep.Duration, ep.Plot }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var infoText = new TextBlock { Text = info, FontSize = 12, Foreground = Ui.Brush("TextMutedBrush"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36, Margin = new Thickness(0, 2, 0, 0), Visibility = string.IsNullOrWhiteSpace(info) ? Visibility.Collapsed : Visibility.Visible };
            var progress = new ProgressBar { Style = Ui.Res<Style>("ThinProgress"), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            stack.Children.Add(title); stack.Children.Add(infoText); stack.Children.Add(progress);
            var row = new FocusCard { Content = stack, Style = Ui.Res<Style>("StreamItem"), Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(14, 10, 14, 10) };
            row.Click += () => Play(ep);
            row.LongClick += () => AskDownload(ep);
            _rows.Add((row, ep, title, progress));
            List.Children.Add(row);
        }
        RefreshRows();
    }

    private void RefreshRows()
    {
        foreach (var (_, ep, title, progress) in _rows)
        {
            var frac = WatchProgress.Fraction(WatchProgress.EpisodeKey(ep.Id));
            title.Text = (frac >= 1f ? "✓ " : "") + $"E{ep.Number} · {ep.Title}";
            progress.Visibility = frac != null ? Visibility.Visible : Visibility.Collapsed;
            if (frac != null) progress.Value = frac.Value * 1000;
        }
    }

    private DownloadItem? Item(Episode ep)
    {
        var account = Prefs.Instance.Account(_service);
        if (account == null) return null;
        string url;
        try { url = XtreamApi.EpisodeUrl(_service, account, ep); } catch { return null; }
        return new DownloadItem(Names.Episode(_title, ep.Season, ep.Number, ep.Title), _title, url, ep.ContainerExtension);
    }

    private void AskDownload(Episode ep)
    {
        var watched = WatchProgress.IsWatched(WatchProgress.EpisodeKey(ep.Id));
        switch (Dialogs.Items(ep.Title, new[] { "Play", "Download…", $"Download Season {ep.Season}", watched ? "Mark as unwatched" : "Mark as watched" }))
        {
            case 0: Play(ep); break;
            case 1: if (Item(ep) is { } it) TransferDialogs.Download(new[] { it }); break;
            case 2: DownloadSeason(ep.Season); break;
            case 3:
                var k = WatchProgress.EpisodeKey(ep.Id);
                WatchProgress.SetWatched(k, !WatchProgress.IsWatched(k));
                RefreshRows();
                break;
        }
    }

    private void DownloadSeason(int season)
    {
        var eps = _episodes.Where(e => e.Season == season).ToList();
        if (eps.Count == 0) return;
        var r = Dialogs.Alert($"Download Season {season}", $"Download all {eps.Count} episodes of Season {season}?", "Download", "Cancel");
        if (r != DialogResultKind.Positive) return;
        TransferDialogs.Download(eps.Select(Item).Where(i => i != null).Select(i => i!).ToList());
    }

    /// Episodes not yet watched and not already downloaded (or downloading), in viewing order.
    private List<Episode> UnwatchedNotDownloaded()
    {
        var jobs = TransferStore.Get().All().Where(j => j.State != TransferState.FAILED && j.State != TransferState.CANCELLED).Select(j => j.Url).ToHashSet();
        return SeriesCache.Ordered(_episodes).Where(ep => !WatchProgress.IsWatched(WatchProgress.EpisodeKey(ep.Id)) && !(Item(ep)?.Url is { } u && jobs.Contains(u))).ToList();
    }

    private void DownloadAll()
    {
        if (_episodes.Count == 0) return;
        var pending = UnwatchedNotDownloaded();
        var options = new[]
        {
            "Next 3 unwatched episodes", "Next 5 unwatched episodes", "Next 10 unwatched episodes",
            $"All unwatched episodes ({pending.Count})", $"Entire series ({_episodes.Count} episodes)"
        };
        var which = Dialogs.Items(_title, options);
        if (which < 0) return;
        var chosen = which switch { 0 => pending.Take(3).ToList(), 1 => pending.Take(5).ToList(), 2 => pending.Take(10).ToList(), 3 => pending, _ => _episodes };
        if (chosen.Count == 0) { Dialogs.Toast("Nothing left to download."); return; }
        TransferDialogs.Download(chosen.Select(Item).Where(i => i != null).Select(i => i!).ToList());
    }

    private void Play(Episode ep)
    {
        var account = Prefs.Instance.Account(_service);
        if (account == null) return;
        string url;
        try { url = XtreamApi.EpisodeUrl(_service, account, ep); }
        catch (Exception e) { TxtError.Text = e.Message; TxtError.Visibility = Visibility.Visible; return; }
        var title = $"{_title} · S{ep.Season}E{ep.Number} {ep.Title}";
        WatchProgress.Describe(WatchProgress.EpisodeKey(ep.Id), WatchProgress.KIND_EPISODE, _title, _cover, ep.ContainerExtension, ep.Id,
            subtitle: ep.Title, seriesId: _seriesId, season: ep.Season, episode: ep.Number);
        Nav.Push(new PlayerPage(url, title, false, WatchProgress.EpisodeKey(ep.Id)));
    }
}
