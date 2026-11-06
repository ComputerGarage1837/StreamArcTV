using System.Windows;
using System.Windows.Controls;
using StreamArcTV.Data;

namespace StreamArcTV.UI;

/// One movie: poster, facts, plot, cast, trailer, and the Play / Resume / Start over buttons.
public partial class MoviePage : AppPage
{
    private readonly Service _service;
    private readonly Prefs _prefs = Prefs.Instance;
    private readonly Data.Stream _stream;
    private readonly string _id;
    private Details? _details;

    public MoviePage(Service service, Data.Stream stream)
    {
        InitializeComponent();
        _service = service; _stream = stream;
        _id = stream.StreamId ?? stream.Id ?? "";
        TxtTitle.Text = stream.Name ?? "";
        BindFacts(stream.Genre, stream.Rating, null, null);
        TxtPlot.Text = stream.Plot ?? "";
        ImageLoader.Load(ImgPoster, string.IsNullOrWhiteSpace(stream.Image) ? null : stream.Image, 400);

        BtnBack.Click += (_, _) => Finish();
        BtnPlay.Click += (_, _) => Play(resume: true);
        BtnStartOver.Click += (_, _) => Play(resume: false);
        BtnTrailer.Click += (_, _) => Trailer.Open(_details?.TrailerUrl);
        BtnList.Click += (_, _) => { _prefs.ToggleFavorite(_service, ContentKind.MOVIE, _id); RenderButtons(); };
        BtnWatched.Click += (_, _) =>
        {
            var key = WatchProgress.MovieKey(_service, _id);
            WatchProgress.Describe(key, WatchProgress.KIND_MOVIE, _stream.Name ?? "", _stream.Image, _stream.ContainerExtension, _id);
            WatchProgress.SetWatched(key, !WatchProgress.IsWatched(key));
            RenderButtons();
        };
        RenderButtons();
        LoadDetails();
        LoadExtras();
    }

    /// Actor photos and similar titles (from TMDB), matched to what this provider actually has.
    private async void LoadExtras()
    {
        try
        {
            var info = await Tmdb.Lookup(_stream.Name ?? "", null, series: false);
            if (Finished) return;
            Extras.ShowCast(info.Cast);
            var account = _prefs.Account(_service);
            if (account == null) return;
            var catalogue = await CatalogCache.Get(_service, account, ContentKind.MOVIE);
            if (Finished) return;
            var similar = Tmdb.SimilarIn(info, catalogue, _id, _details?.Genre ?? _stream.Genre);
            Extras.ShowSimilar(similar, s => Nav.Push(new MoviePage(_service, s)));
        }
        catch { }
    }

    public override IInputElement? InitialFocus => BtnPlay;

    public override void OnResume() => RenderButtons();

    /// The provider's own cast, rating and trailer, when it has them.
    private async void LoadDetails()
    {
        var account = _prefs.Account(_service);
        if (account == null || _id.Length == 0) return;
        Details? d;
        try { d = await XtreamApi.VodInfo(_service, account, _id); } catch { return; }
        if (d == null || Finished) return;
        _details = d;
        if (!string.IsNullOrWhiteSpace(d.Plot)) TxtPlot.Text = d.Plot;
        BindFacts(d.Genre ?? _stream.Genre, d.Rating ?? _stream.Rating, d.Year, d.Duration);
        if (!string.IsNullOrWhiteSpace(d.Cast)) { TxtCast.Text = "Cast: " + d.Cast; TxtCast.Visibility = Visibility.Visible; }
        if (!string.IsNullOrWhiteSpace(d.Director)) { TxtDirector.Text = "Director: " + d.Director; TxtDirector.Visibility = Visibility.Visible; }
        if (string.IsNullOrWhiteSpace(_stream.Image) && !string.IsNullOrWhiteSpace(d.Poster)) ImageLoader.Load(ImgPoster, d.Poster, 400);
        RenderButtons();
    }

    private void BindFacts(string? genre, string? rating, string? year, string? duration)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(year)) parts.Add(year!);
        if (double.TryParse(rating, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0) parts.Add("★ " + r.ToString("0.0"));
        if (!string.IsNullOrWhiteSpace(duration)) parts.Add(duration!);
        if (!string.IsNullOrWhiteSpace(genre)) parts.Add(genre!);
        TxtFacts.Text = string.Join("  ·  ", parts);
        TxtFacts.Visibility = parts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Clock(long ms)
    {
        var s = ms / 1000;
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    private void RenderButtons()
    {
        var key = WatchProgress.MovieKey(_service, _id);
        var pos = WatchProgress.ResumePosition(key);
        BtnPlay.Content = pos > 0 ? $"Resume from {Clock(pos)}" : "Play";
        BtnStartOver.Visibility = pos > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnTrailer.Visibility = _details?.TrailerUrl != null ? Visibility.Visible : Visibility.Collapsed;
        BtnList.Content = _prefs.IsFavorite(_service, ContentKind.MOVIE, _id) ? "Remove from favorites" : "Add to favorites";
        BtnWatched.Content = WatchProgress.IsWatched(key) ? "Mark as unwatched" : "Mark as watched";
    }

    private void Play(bool resume)
    {
        var account = _prefs.Account(_service);
        if (account == null) return;
        string url;
        try { url = XtreamApi.StreamUrl(_service, account, _stream, _prefs.LiveFormat); }
        catch (Exception e) { Dialogs.Toast(e.Message); return; }
        var key = WatchProgress.MovieKey(_service, _id);
        if (!resume) WatchProgress.Remove(key);
        WatchProgress.Describe(key, WatchProgress.KIND_MOVIE, _stream.Name ?? "", _stream.Image, _stream.ContainerExtension, _id);
        Nav.Push(new PlayerPage(url, _stream.Name ?? "", false, key));
    }
}
