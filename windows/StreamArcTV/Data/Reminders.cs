using System.Windows;
using StreamArcTV.Transfer;
using StreamArcTV.UI;
using StreamArcTV.Util;

namespace StreamArcTV.Data;

/// <summary>
/// Reminders while the app is running (it stays in the tray when the window is closed): a heads-up a
/// week (and a day) before a subscription ends, an alert when a show in My List gets a new episode,
/// and, if switched on, downloads of those episodes overnight. The counterpart of the Android
/// background check.
/// </summary>
public static class Reminders
{
    private const string TAG = "Reminders";
    private const int MAX_SHOWS = 40;
    private static System.Threading.Timer? _timer;
    private static int _running;

    /// Checks shortly after start-up, then every six hours.
    public static void Start()
    {
        _timer ??= new System.Threading.Timer(_ => { _ = RunAsync(first: _first); _first = false; }, null, TimeSpan.FromSeconds(90), TimeSpan.FromHours(6));
    }

    private static bool _first = true;

    public static async Task RunAsync(bool first)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var prefs = Prefs.Instance;
            if (prefs.RemindersOn)
            {
                try { await CheckExpiry(); } catch (Exception e) { AppLog.W(TAG, "expiry check failed: " + e.Message); }
            }
            if (prefs.NewEpisodeAlerts || prefs.AutoDownloadNew)
            {
                try { await CheckNewEpisodes(); } catch (Exception e) { AppLog.W(TAG, "episode check failed: " + e.Message); }
            }
            try { ProcessPendingDownloads(anyHour: first); } catch (Exception e) { AppLog.W(TAG, "auto-download failed: " + e.Message); }
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    // ---- Subscription expiry -------------------------------------------------------

    /// Reminds a week before, a day before, and once when a subscription has ended.
    private static async Task CheckExpiry()
    {
        var prefs = Prefs.Instance;
        var now = Format.NowSec;
        foreach (var service in Enum.GetValues<Service>())
        {
            var account = prefs.Account(service);
            if (account == null) continue;
            try
            {
                var info = await XtreamApi.Login(service, account.Username, account.Password);
                account = info.ToAccount(account.Username, account.Password);
                prefs.SaveAccount(service, account);
            }
            catch { /* keep the saved expiry */ }
            if (account.ExpDate is not { } exp) continue;
            var left = exp - now;
            var stage = left <= 0 ? 3 : left <= 86_400 ? 2 : left <= 7 * 86_400 ? 1 : 0;
            if (stage == 0) continue;
            var last = prefs.ExpiryNotified(service)?.Split(':');
            var lastExp = last is { Length: > 0 } && long.TryParse(last[0], out var le) ? le : (long?)null;
            var lastStage = last is { Length: > 1 } && int.TryParse(last[1], out var ls) ? ls : 0;
            if (lastExp == exp && lastStage >= stage) continue;
            prefs.SetExpiryNotified(service, $"{exp}:{stage}");
            var date = Format.Expiry(exp);
            var text = stage switch
            {
                3 => $"Your {service.Title()} subscription ended on {date}.",
                2 => $"Your {service.Title()} subscription ends tomorrow ({date}). Renew it to keep watching.",
                _ => $"Your {service.Title()} subscription ends on {date} (in {Math.Max(1, left / 86_400)} days). Renew it to keep watching."
            };
            AppLog.I(TAG, $"expiry reminder {service}: {text}");
            Application.Current?.Dispatcher.Invoke(() => Tray.ShowBalloon("Subscription ending", text, System.Windows.Forms.ToolTipIcon.Warning));
        }
    }

    // ---- New episodes -------------------------------------------------------------

    private static int Code(Episode e) => e.Season * 10_000 + e.Number;

    /// Alerts for My List shows with an episode newer than the last one seen, and queues them for download.
    private static async Task CheckNewEpisodes()
    {
        var prefs = Prefs.Instance;
        var service = Service.VOD;
        var account = prefs.Account(service);
        if (account == null) return;
        var ids = prefs.Favorites(service, ContentKind.SERIES).Take(MAX_SHOWS).ToList();
        if (ids.Count == 0) return;
        Dictionary<string, Data.Stream> names;
        try { names = (await CatalogCache.Get(service, account, ContentKind.SERIES)).Where(s => s.Id != null).GroupBy(s => s.Id!).ToDictionary(g => g.Key, g => g.First()); }
        catch { names = new(); }
        var known = prefs.KnownEpisodes();
        var firstRun = known.Count == 0;
        foreach (var id in ids)
        {
            List<Episode> eps;
            try { eps = await XtreamApi.SeriesInfo(service, account, id); } catch { continue; }
            await Task.Delay(400);   // be gentle with the provider
            if (eps.Count == 0) continue;
            var latest = eps.Max(Code);
            var hadBefore = known.TryGetValue(id, out var before);
            known[id] = latest;
            if (!hadBefore || latest <= before) continue;   // first sighting: just remember it
            var fresh = eps.Where(e => Code(e) > before).OrderBy(Code).ToList();
            var show = names.TryGetValue(id, out var st) && !string.IsNullOrWhiteSpace(st.Name) ? st.Name! : "A show in My List";
            var newest = fresh[^1];
            var text = fresh.Count == 1 ? $"{show}: S{newest.Season:00}E{newest.Number:00} {newest.Title} is ready to watch." : $"{show} has {fresh.Count} new episodes.";
            AppLog.I(TAG, $"new episodes for '{show}': {fresh.Count}");
            if (prefs.NewEpisodeAlerts)
                Application.Current?.Dispatcher.Invoke(() => Tray.ShowBalloon("New episode", text));
            if (prefs.AutoDownloadNew)
            {
                var add = fresh.Select(e => $"{id}\t{e.Id}\t{e.Season}\t{e.Number}\t{e.ContainerExtension}\t{show.Replace('\t', ' ')}\t{e.Title.Replace('\t', ' ')}");
                prefs.PendingEpisodes = prefs.PendingEpisodes.Concat(add).ToHashSet();
            }
        }
        prefs.SetKnownEpisodes(known);
        if (firstRun) AppLog.I(TAG, $"remembered the current episodes of {known.Count} My List shows");
    }

    // ---- Overnight downloads ----------------------------------------------------------

    /// Queues the downloads of new episodes: overnight (1 am to 7 am), or when the app has just started.
    private static void ProcessPendingDownloads(bool anyHour)
    {
        var prefs = Prefs.Instance;
        if (!prefs.AutoDownloadNew) return;
        var pending = prefs.PendingEpisodes;
        if (pending.Count == 0) return;
        if (!anyHour && DateTime.Now.Hour is < 1 or > 7) return;
        var account = prefs.Account(Service.VOD);
        if (account == null) return;
        var folder = prefs.DownloadFolder != null && Folders.Usable(prefs.DownloadFolder) ? prefs.DownloadFolder : null;
        var existing = TransferStore.Get().All().Select(j => j.Url).ToHashSet();
        var left = pending.ToHashSet();
        foreach (var line in pending.Take(12))
        {
            left.Remove(line);
            var p = line.Split('\t');
            if (p.Length < 7) continue;
            var ep = new Episode(p[1], p[6], int.TryParse(p[2], out var s) ? s : 0, int.TryParse(p[3], out var n) ? n : 0, p[4], null, null);
            string url;
            try { url = XtreamApi.EpisodeUrl(Service.VOD, account, ep); } catch { continue; }
            if (existing.Contains(url)) continue;
            var title = $"{p[5]} S{ep.Season:00}E{ep.Number:00} {ep.Title}";
            var job = TransferJob.Create(TransferType.DOWNLOAD, title, p[5], url,
                Folders.SafeName(title) + "." + (string.IsNullOrWhiteSpace(ep.ContainerExtension) ? "mp4" : ep.ContainerExtension), folder, 0, 0);
            TransferService.Enqueue(job);
            AppLog.I(TAG, "queued auto-download: " + title);
        }
        prefs.PendingEpisodes = left;
    }
}
