using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using StreamArcTV.Data;
using StreamArcTV.Player;
using StreamArcTV.Transfer;
using StreamArcTV.Update;
using StreamArcTV.Util;

namespace StreamArcTV.UI;

public partial class SettingsPage : AppPage
{
    private readonly Prefs _prefs = Prefs.Instance;
    private List<Category>? _liveCategories;

    public SettingsPage()
    {
        InitializeComponent();
        BtnBack.Click += (_, _) => Finish();

        SwitchAutoUpdate.IsChecked = _prefs.AutoCheckUpdates;
        SwitchAutoUpdate.Click += (_, _) => _prefs.AutoCheckUpdates = SwitchAutoUpdate.IsChecked == true;

        RowLiveFormat.Click += (_, _) => PickLiveFormat();
        RenderLiveFormat();
        RowBuffer.Click += (_, _) => PickBuffer();
        RenderBuffer();
        SwitchAutoPlayNext.IsChecked = _prefs.AutoPlayNext;
        SwitchAutoPlayNext.Click += (_, _) => _prefs.AutoPlayNext = SwitchAutoPlayNext.IsChecked == true;
        SwitchWifiOnly.IsChecked = _prefs.DownloadsWifiOnly;
        SwitchWifiOnly.Click += (_, _) => _prefs.DownloadsWifiOnly = SwitchWifiOnly.IsChecked == true;
        SwitchDeleteWatched.IsChecked = _prefs.DeleteAfterWatched;
        SwitchDeleteWatched.Click += (_, _) => _prefs.DeleteAfterWatched = SwitchDeleteWatched.IsChecked == true;
        RowSubtitles.Click += (_, _) => PickSubtitles();
        RenderSubtitles();
        RowLayout.Click += (_, _) => PickLayout();
        RenderLayout();
        RowDiagnostics.Click += (_, _) => RunDiagnostics();
        RowLogs.Click += (_, _) => ExportLogs();
        RowLiveCategories.Click += (_, _) => PickLiveCategories();
        RowDefaultCategory.Click += (_, _) => PickDefaultCategory();
        RowGuideMode.Click += (_, _) => PickGuideMode();
        RenderGuideMode();
        RenderLiveCategories();
        RowDownloadFolder.Click += (_, _) => TransferDialogs.PickFolder(TransferType.DOWNLOAD, _ => RenderFolders());
        RowRecordingFolder.Click += (_, _) => TransferDialogs.PickFolder(TransferType.RECORDING, _ => RenderFolders());
        RenderFolders();

        BtnCheckUpdates.Click += (_, _) => UpdateChecker.Check(manual: true);
        BtnClearSkipped.Click += (_, _) =>
        {
            _prefs.SkippedVersion = null;
            Dialogs.Toast("Skipped version cleared.");
            RenderSkipped();
        };

        BtnLogoutLive.Click += (_, _) => Logout(Service.LIVE);
        BtnLogoutVod.Click += (_, _) => Logout(Service.VOD);

        TxtVersion.Text = $"v{BuildInfo.VersionName}";
        TxtRepo.Text = $"github.com/{BuildInfo.GitHubRepo}";
        RowGithub.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo($"https://github.com/{BuildInfo.GitHubRepo}/releases") { UseShellExecute = true }); } catch { }
        };
    }

    public override IInputElement InitialFocus => SwitchAutoUpdate;

    public override void OnResume()
    {
        RenderSkipped();
        RenderAccounts();
    }

    private void RenderLiveFormat() => TxtLiveFormatValue.Text = _prefs.LiveFormat == "ts" ? "MPEG-TS (.ts)" : "HLS (.m3u8)";

    private void PickLiveFormat()
    {
        var which = Dialogs.SingleChoice("Live stream format", new[] { "HLS (.m3u8)", "MPEG-TS (.ts)" }, _prefs.LiveFormat == "ts" ? 1 : 0, negative: null);
        if (which < 0) return;
        _prefs.LiveFormat = which == 1 ? "ts" : "m3u8";
        RenderLiveFormat();
    }

    private void RenderSubtitles() => TxtSubtitlesValue.Text = _prefs.Subtitles ? "On when available" : "Off";

    private void PickSubtitles()
    {
        var which = Dialogs.SingleChoice("Subtitles", new[] { "Off", "On when available" }, _prefs.Subtitles ? 1 : 0, negative: null);
        if (which < 0) return;
        _prefs.Subtitles = which == 1;
        RenderSubtitles();
    }

    private const string BufferHelp =
        "The buffer is how much of the stream the app downloads ahead of what you are watching, for live channels and for movies and series alike. " +
        "A bigger buffer rides out slow or patchy connections and lets you pause for longer without losing your place, but the channel takes a little longer to start and uses more memory.\n\n" +
        "When you pause, the app keeps downloading until the buffer is full, and pressing play carries on exactly where you stopped. Once you have paused longer than the buffer allows, playback jumps back to live.";

    private void RenderBuffer()
    {
        var level = BufferLevel.From(_prefs.BufferLevelKey);
        TxtBufferValue.Text = level.OnDisk ? $"{level.ShortLabel} · {Format.Size(TimeshiftServer.FreeSpaceBytes())} free" : level.ShortLabel;
    }

    private void PickBuffer()
    {
        var current = Array.IndexOf(BufferLevel.Entries, BufferLevel.From(_prefs.BufferLevelKey));
        var which = Dialogs.SingleChoice("Playback buffer (live TV & VOD)", BufferLevel.Entries.Select(e => e.Label).ToList(), current,
            negative: "Cancel", neutral: "About buffer sizes",
            onNeutral: () => Dialogs.Alert("About buffer sizes", BufferHelp));
        if (which < 0) return;
        var level = BufferLevel.Entries[which];
        if (!level.OnDisk)
        {
            _prefs.BufferLevelKey = level.Key;
            RenderBuffer();
            return;
        }
        var free = Format.Size(TimeshiftServer.FreeSpaceBytes());
        var notice = "Buffers longer than 10 minutes are kept in the app's cache on disk instead of memory.\n\n" +
                     "Expect roughly 1 GB per hour for SD channels, 2–4 GB for HD and 7–11 GB for 4K. Check the free space below before choosing this.\n\n" +
                     "The cache is deleted when you leave the channel, the app always keeps at least 1 GB free (dropping the oldest video first), and this option plays the MPEG-TS version of the channel.\n\n" +
                     $"Free space now: {free}";
        if (Dialogs.Alert("Uses disk storage", notice, "OK", "Cancel") == DialogResultKind.Positive)
        {
            _prefs.BufferLevelKey = level.Key;
            RenderBuffer();
        }
    }

    private void ExportLogs()
    {
        var r = Dialogs.Alert("Export logs",
            "The app keeps a log of playback, downloads and errors (no passwords). Save it as a text file, or copy it to the clipboard.",
            "Save…", "Clear", "Copy");
        switch (r)
        {
            case DialogResultKind.Positive: if (App.Window != null) AppLog.Share(App.Window); break;
            case DialogResultKind.Neutral: AppLog.Copy(); break;
            case DialogResultKind.Negative: AppLog.Clear(); Dialogs.Toast("Log cleared."); break;
        }
    }

    // ---- Diagnostics -------------------------------------------------------

    /// Compares the panel's category lists with the categories actually attached to items.
    private async void RunDiagnostics()
    {
        var account = _prefs.Account(Service.VOD);
        if (account == null) { Dialogs.Toast("Sign in to Video on Demand first."); return; }
        var progress = Dialogs.Progress("Category diagnostics", "Checking the provider's categories…", null);
        var report = new StringBuilder();
        foreach (var kind in new[] { ContentKind.MOVIE, ContentKind.SERIES })
        {
            try
            {
                var cats = await XtreamApi.Categories(Service.VOD, account, kind);
                var items = await CatalogCache.Get(Service.VOD, account, kind);
                var listed = cats.Where(c => c.Id != null).Select(c => c.Id!).ToHashSet();
                var used = items.SelectMany(i => i.AllCategoryIds).ToHashSet();
                var missing = used.Except(listed).ToList();
                var emptyCats = listed.Except(used).ToList();
                report.Append(kind == ContentKind.MOVIE ? "MOVIES\n" : "\nSERIES\n");
                report.Append($"Categories listed by panel: {cats.Count}\n");
                report.Append($"Items in catalogue: {items.Count}\n");
                report.Append($"Category ids used by items: {used.Count}\n");
                report.Append($"Used but not listed: {missing.Count}{(missing.Count == 0 ? "" : "  (" + string.Join(", ", missing.Take(12)) + ")")}\n");
                report.Append($"Listed but empty: {emptyCats.Count}\n");
                var withGenre = items.Count(i => i.Genres.Count > 0);
                var genreNames = items.SelectMany(i => i.Genres).GroupBy(g => g).Where(g => g.Count() >= 3).Select(g => g.Key).OrderBy(g => g).ToList();
                report.Append($"Items with a genre: {withGenre} → {genreNames.Count} genre groups\n");
                report.Append("Names: " + string.Join(", ", cats.Take(40).Select(c => c.Name ?? "?")) + (cats.Count > 40 ? " …" : "") + "\n");
            }
            catch (Exception e)
            {
                report.Append($"{kind}: {e.Message}\n");
            }
        }
        progress.Close();
        Dialogs.Alert("Category diagnostics", report.ToString());
    }

    // ---- Live TV categories ---------------------------------------------

    private void RenderGuideMode() => TxtGuideModeValue.Text = _prefs.GuideMode switch
    {
        "full" => "Whole guide download",
        "channel" => "Per channel (lighter)",
        _ => "Automatic"
    };

    private void PickGuideMode()
    {
        var values = new[] { "auto", "full", "channel" };
        var which = Dialogs.SingleChoice("TV guide source", new[] { "Automatic", "Whole guide download", "Per channel (lighter)" }, Math.Max(0, Array.IndexOf(values, _prefs.GuideMode)));
        if (which < 0) return;
        _prefs.GuideMode = values[which];
        RenderGuideMode();
    }

    private void RenderLiveCategories()
    {
        var hidden = _prefs.HiddenLiveCategories.Count;
        TxtLiveCategoriesValue.Text = hidden == 0 ? "All" : $"{hidden} hidden";
        var d = _prefs.DefaultLiveCategory;
        TxtDefaultCategoryValue.Text = d switch
        {
            null => "General Streams (automatic)",
            Prefs.CATEGORY_FAVORITES => "Favorites",
            Prefs.CATEGORY_ALL => "All",
            _ => _liveCategories?.FirstOrDefault(c => c.Id == d)?.Name ?? "Chosen category"
        };
    }

    /// Loads the provider's live categories once (needs a Live TV sign-in).
    private async void WithLiveCategories(Action<List<Category>> then)
    {
        if (_liveCategories != null) { then(_liveCategories); return; }
        var account = _prefs.Account(Service.LIVE);
        if (account == null) { Dialogs.Toast("Sign in to Live TV first."); return; }
        try
        {
            var cats = (await XtreamApi.Categories(Service.LIVE, account, ContentKind.LIVE)).Where(c => c.Id != null).ToList();
            _liveCategories = cats;
            RenderLiveCategories();
            then(cats);
        }
        catch (Exception e)
        {
            Dialogs.Toast(string.IsNullOrWhiteSpace(e.Message) ? "Couldn't load content." : e.Message);
        }
    }

    private void PickLiveCategories() => WithLiveCategories(cats =>
    {
        var hidden = _prefs.HiddenLiveCategories;
        var names = cats.Select(c => c.Name ?? "—").ToList();
        var state = cats.Select(c => !hidden.Contains(c.Id!)).ToArray();
        var result = Dialogs.MultiChoice("Categories shown", names, state);
        if (result == null) return;
        var newHidden = new HashSet<string>();
        for (var i = 0; i < cats.Count; i++) if (!result[i]) newHidden.Add(cats[i].Id!);
        _prefs.HiddenLiveCategories = newHidden;
        if (_prefs.DefaultLiveCategory != null && newHidden.Contains(_prefs.DefaultLiveCategory)) _prefs.DefaultLiveCategory = null;
        RenderLiveCategories();
    });

    private void PickDefaultCategory() => WithLiveCategories(cats =>
    {
        var visible = cats.Where(c => !_prefs.HiddenLiveCategories.Contains(c.Id!)).ToList();
        var labels = new List<string> { "General Streams (automatic)", "★ Favorites", "All" };
        labels.AddRange(visible.Select(c => c.Name ?? "—"));
        var values = new List<string?> { null, Prefs.CATEGORY_FAVORITES, Prefs.CATEGORY_ALL };
        values.AddRange(visible.Select(c => c.Id));
        var current = Math.Max(0, values.IndexOf(_prefs.DefaultLiveCategory));
        var which = Dialogs.SingleChoice("Default category", labels, current);
        if (which < 0) return;
        _prefs.DefaultLiveCategory = values[which];
        RenderLiveCategories();
    });

    private void RenderFolders()
    {
        TxtDownloadFolderValue.Text = Folders.Describe(Folders.Usable(_prefs.DownloadFolder) ? _prefs.DownloadFolder : null, TransferType.DOWNLOAD);
        TxtRecordingFolderValue.Text = Folders.Describe(Folders.Usable(_prefs.RecordingFolder) ? _prefs.RecordingFolder : null, TransferType.RECORDING);
    }

    private void RenderLayout() => TxtLayoutValue.Text = _prefs.LayoutMode == "phone" ? "Phone" : "TV or tablet";

    private void PickLayout()
    {
        var which = Dialogs.SingleChoice("Display layout", new[] { "Phone", "TV or tablet" }, _prefs.LayoutMode == "phone" ? 0 : 1, negative: null);
        if (which < 0) return;
        _prefs.LayoutMode = which == 0 ? "phone" : "tv";
        RenderLayout();
    }

    private void RenderSkipped()
    {
        var v = _prefs.SkippedVersion;
        TxtSkipped.Text = v == null ? "No skipped version" : $"Currently skipping v{v}";
        BtnClearSkipped.Visibility = v == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderAccounts()
    {
        var live = _prefs.Account(Service.LIVE);
        var vod = _prefs.Account(Service.VOD);
        BtnLogoutLive.IsEnabled = live != null;
        BtnLogoutLive.Content = live != null ? $"Log out of {Service.LIVE.Title()} ({live.Username})" : $"Not signed in to {Service.LIVE.Title()}";
        BtnLogoutVod.IsEnabled = vod != null;
        BtnLogoutVod.Content = vod != null ? $"Log out of {Service.VOD.Title()} ({vod.Username})" : $"Not signed in to {Service.VOD.Title()}";
    }

    private void Logout(Service service)
    {
        var r = Dialogs.Alert("Log out", $"Log out of {service.Title()}? You'll need to sign in again to watch.", "Log out", "Cancel");
        if (r != DialogResultKind.Positive) return;
        _prefs.ClearAccount(service);
        RenderAccounts();
    }
}
