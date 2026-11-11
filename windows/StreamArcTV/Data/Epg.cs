using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

using StreamArcTV.Util;

namespace StreamArcTV.Data;

/// One programme in the electronic programme guide. Times are epoch seconds.
public record EpgProgramme(string Title, string Description, long Start, long End)
{
    public bool IsOnNow(long? now = null)
    {
        var n = now ?? Format.NowSec;
        return n >= Start && n < End;
    }

    /// 0..1000 progress through the programme, or 0 if it hasn't started.
    public int Progress(long? now = null)
    {
        var n = now ?? Format.NowSec;
        if (End <= Start || n < Start) return 0;
        if (n >= End) return 1000;
        return (int)((n - Start) * 1000 / (End - Start));
    }
}

/// Parses the `get_short_epg` response of an Xtream Codes panel.
public static class EpgParser
{
    public static List<EpgProgramme> Parse(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); } catch { return new(); }
        using (doc)
        {
            var root = doc.RootElement;
            JsonElement listings;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("epg_listings", out var l) && l.ValueKind == JsonValueKind.Array) listings = l;
            else if (root.ValueKind == JsonValueKind.Array) listings = root;
            else return new();

            var outList = new List<EpgProgramme>();
            foreach (var o in listings.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                string? S(string n) => o.Get(n)?.Str();
                var start = long.TryParse(S("start_timestamp"), out var st) ? st : ParseSql(S("start"));
                if (start == null) continue;
                var end = long.TryParse(S("stop_timestamp"), out var en) ? en : (ParseSql(S("end")) ?? ParseSql(S("stop")));
                if (end == null) continue;
                var title = Decode(S("title"));
                if (string.IsNullOrWhiteSpace(title)) title = "Untitled programme";
                outList.Add(new EpgProgramme(title, Decode(S("description")), start.Value, end.Value));
            }
            return outList.OrderBy(p => p.Start).ToList();
        }
    }

    private static string Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim())).Trim(); }
        catch { return value.Trim(); }
    }

    private static long? ParseSql(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? new DateTimeOffset(d).ToUnixTimeSeconds() : null;
    }
}

/// Streaming parser for XMLTV (`xmltv.php`).
public static class XmltvParser
{
    /// Result of a parse: programmes by channel id, plus display-name → id for loose matching.
    public class Guide
    {
        public Dictionary<string, List<EpgProgramme>> ById { get; }
        public Dictionary<string, string> IdByName { get; }
        private Dictionary<string, string>? _lowerIds;
        /// True when the download broke off part-way and only what had arrived was kept.
        public bool Partial { get; }
        public Guide(Dictionary<string, List<EpgProgramme>> byId, Dictionary<string, string> idByName, bool partial = false) { ById = byId; IdByName = idByName; Partial = partial; }

        /// Case-insensitive id lookup through a lazily built index (no scans per guide row).
        public List<EpgProgramme>? ByIdIgnoreCase(string id)
        {
            if (ById.TryGetValue(id, out var l)) return l;
            _lowerIds ??= ById.Keys.GroupBy(k => k.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            return _lowerIds.TryGetValue(id.ToLowerInvariant(), out var real) && ById.TryGetValue(real, out var pl) ? pl : null;
        }
    }

    /// With keepPartial, a download that breaks off part-way (the connection was cut) returns what had been read so far,
    /// flagged Partial, instead of throwing it all away.
    public static Guide Parse(System.IO.Stream input, long from, long to, bool keepPartial = false)
    {
        var outMap = new Dictionary<string, List<EpgProgramme>>();
        var names = new Dictionary<string, string>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = false, CheckCharacters = false };
        using var r = XmlReader.Create(input, settings);
        string? channelId = null;
        string? channel = null;
        long start = 0, end = 0;
        var title = ""; var desc = "";
        var inProgramme = false;
        StringBuilder? text = null;
        var partial = false;
        try
        {
        while (r.Read())
        {
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                    switch (r.LocalName)
                    {
                        case "channel": channelId = r.GetAttribute("id"); break;
                        case "display-name": if (channelId != null) text = new StringBuilder(); break;
                        case "programme":
                            inProgramme = true;
                            channel = r.GetAttribute("channel");
                            start = Time(r.GetAttribute("start"));
                            end = Time(r.GetAttribute("stop"));
                            title = ""; desc = "";
                            if (r.IsEmptyElement) { inProgramme = false; }
                            break;
                        case "title":
                        case "desc":
                            if (inProgramme && !r.IsEmptyElement) text = new StringBuilder();
                            break;
                    }
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    text?.Append(r.Value);
                    break;
                case XmlNodeType.EndElement:
                    switch (r.LocalName)
                    {
                        case "display-name":
                            if (channelId != null && text != null)
                            {
                                var key = Normalize(text.ToString());
                                if (key.Length > 0 && !names.ContainsKey(key)) names[key] = channelId;
                                text = null;
                            }
                            break;
                        case "channel": channelId = null; break;
                        case "title":
                            if (inProgramme && text != null) { if (string.IsNullOrWhiteSpace(title)) title = text.ToString().Trim(); text = null; }
                            break;
                        case "desc":
                            if (inProgramme && text != null) { if (string.IsNullOrWhiteSpace(desc)) desc = text.ToString().Trim(); text = null; }
                            break;
                        case "programme":
                            inProgramme = false;
                            if (channel != null && end > from && start < to && end > start)
                            {
                                if (!outMap.TryGetValue(channel, out var list)) outMap[channel] = list = new List<EpgProgramme>();
                                list.Add(new EpgProgramme(string.IsNullOrWhiteSpace(title) ? "Untitled programme" : title, desc, start, end));
                            }
                            break;
                    }
                    break;
            }
        }
        }
        catch (Exception e) when (keepPartial && outMap.Count > 0 && e is not XtreamApi.GuideTooLarge)
        {
            AppLog.W("Epg", $"guide download broke off ({e.GetType().Name}: {e.Message}); keeping the {outMap.Count} channels read so far");
            partial = true;
        }
        foreach (var list in outMap.Values) list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new Guide(outMap, names, partial);
    }

    private static readonly Regex Quality = new(@"\b(uhd|fhd|hd|sd|4k)\b", RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new("[^a-z0-9]", RegexOptions.Compiled);

    /// Loose key for name matching: lowercase, no punctuation/spaces, no HD/FHD/4K suffixes.
    public static string Normalize(string name) => NonAlnum.Replace(Quality.Replace(name.ToLowerInvariant(), ""), "");

    private static long Time(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var v = value.Trim();
        try
        {
            if (v.Length > 14)
            {
                // "yyyyMMddHHmmss +0000"
                if (DateTimeOffset.TryParseExact(v, "yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d.ToUnixTimeSeconds();
                var m = Regex.Match(v, @"^(\d{14})\s*([+-])(\d{2})(\d{2})$");
                if (m.Success)
                {
                    var b = DateTime.ParseExact(m.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    var off = new TimeSpan(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), 0);
                    if (m.Groups[2].Value == "-") off = -off;
                    return new DateTimeOffset(b, TimeSpan.Zero).ToUnixTimeSeconds() - (long)off.TotalSeconds;
                }
                return 0;
            }
            var u = DateTime.ParseExact(v[..Math.Min(14, v.Length)].PadRight(14, '0'), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return new DateTimeOffset(u, TimeSpan.Zero).ToUnixTimeSeconds();
        }
        catch { return 0; }
    }
}

/// <summary>
/// Small in-memory cache of per-channel EPG so guide rows can be filled lazily without hammering
/// the panel, plus the whole-guide handling: the last good guide is kept on disk, refreshed in the
/// background, and only replaced by a new download that is at least as complete.
/// </summary>
public static class EpgCache
{
    private const long TTL_MS = 6 * 60 * 60 * 1000L;   // per-channel data is refreshed after this
    private const long REFRESH_MS = 3 * 60 * 60 * 1000L;   // refresh quietly after this age
    private const long RETRY_MS = 5 * 60 * 1000L;          // back-off after a failed download

    private static readonly Dictionary<string, (long At, List<EpgProgramme> List)> Cache = new();
    private static readonly SemaphoreSlim Gate = new(6);
    private static readonly Dictionary<Service, (long At, XmltvParser.Guide Guide)> Guides = new();
    private static readonly Dictionary<Service, long> GuideFailedAt = new();
    private static readonly SemaphoreSlim GuideMutex = new(1);
    private static readonly HashSet<Service> Refreshing = new();

    /// True while a guide (fresh or older) is in memory.
    public static bool GuideLoaded(Service s) { lock (Guides) return Guides.ContainsKey(s); }

    /// Age of the in-memory guide in ms, or -1.
    public static long GuideAge(Service s) { lock (Guides) return Guides.TryGetValue(s, out var g) ? Format.NowMs - g.At : -1; }

    /// Programmes for a channel from the full guide: by its XMLTV id first, then by a loose
    /// match on the channel name. Null when the guide has nothing for it.
    public static List<EpgProgramme>? GuideFor(Service s, string? epgChannelId, string? channelName = null)
    {
        XmltvParser.Guide g;
        lock (Guides) { if (!Guides.TryGetValue(s, out var e)) return null; g = e.Guide; }
        var id = epgChannelId?.Trim();
        if (!string.IsNullOrEmpty(id))
        {
            var l = g.ByIdIgnoreCase(id);
            if (l != null) return l;
        }
        if (channelName == null) return null;
        var name = XmltvParser.Normalize(channelName);
        if (name.Length == 0) return null;
        return g.IdByName.TryGetValue(name, out var byName) && g.ById.TryGetValue(byName, out var pl) ? pl : null;
    }

    /// How many channels the loaded guide covers (0 when not loaded).
    public static int GuideChannelCount(Service s) { lock (Guides) return Guides.TryGetValue(s, out var g) ? g.Guide.ById.Count : 0; }

    /// Makes a guide available: from memory, else from the disk copy (instantly), else by
    /// downloading. A guide older than REFRESH_MS is refreshed in the background while the old
    /// one stays in use. Returns true when a guide is available afterwards. Never throws.
    public static async Task<bool> LoadGuide(Service s, Account account, Action<long, long>? onProgress = null)
    {
        lock (Guides)
        {
            if (Guides.TryGetValue(s, out var mem))
            {
                if (Format.NowMs - mem.At > REFRESH_MS) RefreshInBackground(s, account);
                return true;
            }
        }
        await GuideMutex.WaitAsync();
        try
        {
            lock (Guides) if (Guides.ContainsKey(s)) return true;
            var disk = await Task.Run(() => ReadDisk(s)).ConfigureAwait(false);
            if (disk != null)
            {
                lock (Guides) Guides[s] = disk.Value;
                AppLog.I("Epg", $"{s} guide from disk: {disk.Value.Guide.ById.Count} channels, {(Format.NowMs - disk.Value.At) / 60_000} min old");
                if (Format.NowMs - disk.Value.At > REFRESH_MS) RefreshInBackground(s, account);
                return true;
            }
            long? failed;
            lock (Guides) failed = GuideFailedAt.TryGetValue(s, out var f) ? f : null;
            if (failed != null && Format.NowMs - failed.Value < RETRY_MS) return false;
            return await Download(s, account, onProgress);
        }
        finally { GuideMutex.Release(); }
    }

    /// Downloads and installs a guide when it passes the completeness check against the current one.
    private static async Task<bool> Download(Service s, Account account, Action<long, long>? onProgress)
    {
        var now = Format.NowSec;
        var from = (now / 1800) * 1800 - 2 * 3600;
        var to = from + 50 * 3600;      // two days so a disk copy still covers the grid later
        try
        {
            var started = Format.NowMs;
            AppLog.I("Epg", $"{s} guide: downloading whole listing (xmltv)");
            XmltvParser.Guide guide;
            try { guide = await XtreamApi.FullGuide(s, account, from, to, onProgress).ConfigureAwait(false); }
            catch (Exception e) when (e is not XtreamApi.GuideTooLarge)
            {
                // One quiet retry: a cut-off download usually works the second time.
                AppLog.W("Epg", $"{s} guide download failed ({e.Message}); trying once more");
                await Task.Delay(2000).ConfigureAwait(false);
                guide = await XtreamApi.FullGuide(s, account, from, to, onProgress).ConfigureAwait(false);
            }
            XmltvParser.Guide? current;
            lock (Guides) current = Guides.TryGetValue(s, out var c) ? c.Guide : null;
            AppLog.I("Epg", $"{s} guide downloaded: {guide.ById.Count} channels, {guide.ById.Values.Sum(l => l.Count)} programmes in {(Format.NowMs - started) / 1000}s");
            if (guide.ById.Count == 0) throw new InvalidOperationException("Empty guide");
            if (guide.Partial) AppLog.W("Epg", $"{s} guide: only part of the listing arrived");
            if (current != null && !AtLeastAsComplete(guide, current))
            {
                // The panel is probably regenerating its EPG; keep the good copy and try later.
                AppLog.W("Epg", $"{s} guide: new listing is less complete than the kept one ({current.ById.Count} channels); keeping the old copy");
                lock (Guides) GuideFailedAt[s] = Format.NowMs;
                return current.ById.Count > 0;
            }
            var at = Format.NowMs;
            // A partial guide is kept (better than an old one) but counts as already due for a refresh, so the
            // rest is fetched again soon.
            if (guide.Partial) at -= REFRESH_MS - RETRY_MS;
            lock (Guides) { Guides[s] = (at, guide); if (guide.Partial) GuideFailedAt[s] = Format.NowMs; else GuideFailedAt.Remove(s); }
            _ = Task.Run(() => WriteDisk(s, at, guide));
            return true;
        }
        catch (XtreamApi.GuideTooLarge)
        {
            // Caller decided the file is too big; don't retry the full download for a while.
            AppLog.W("Epg", $"{s} guide: too large; using per-channel lookups");
            lock (Guides) GuideFailedAt[s] = Format.NowMs + 6 * 60 * 60 * 1000L;
            return GuideLoaded(s);
        }
        catch (Exception e)
        {
            AppLog.E("Epg", $"{s} guide download failed", e);
            lock (Guides) GuideFailedAt[s] = Format.NowMs;
            return GuideLoaded(s);
        }
    }

    /// Compared on what is still to come: a new listing only has to cover most of what the kept one covers from now
    /// on. An old copy whose programmes have mostly ended therefore never blocks a fresh (even partial) one.
    private static bool AtLeastAsComplete(XmltvParser.Guide fresh, XmltvParser.Guide old)
    {
        var now = Format.NowSec;
        int Channels(XmltvParser.Guide g) => g.ById.Values.Count(l => l.Any(p => p.End > now));
        int Programmes(XmltvParser.Guide g) => g.ById.Values.Sum(l => l.Count(p => p.End > now));
        return Channels(fresh) >= Channels(old) * 0.7 && Programmes(fresh) >= Programmes(old) * 0.5;
    }

    private static void RefreshInBackground(Service s, Account account)
    {
        lock (Refreshing) { if (!Refreshing.Add(s)) return; }
        long? failed;
        lock (Guides) failed = GuideFailedAt.TryGetValue(s, out var f) ? f : null;
        if (failed != null && Format.NowMs - failed.Value < RETRY_MS) { lock (Refreshing) Refreshing.Remove(s); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                await GuideMutex.WaitAsync();
                try { await Download(s, account, null); } finally { GuideMutex.Release(); }
            }
            finally { lock (Refreshing) Refreshing.Remove(s); }
        });
    }

    // ---- Disk copy -----------------------------------------------------

    private class DiskGuide
    {
        public long SavedAt { get; set; }
        public Dictionary<string, List<EpgProgramme>> ById { get; set; } = new();
        public Dictionary<string, string> IdByName { get; set; } = new();
    }

    private static string GuideFile(Service s) => Path.Combine(AppPaths.Cache, $"guide_{s}.json");

    private static (long At, XmltvParser.Guide Guide)? ReadDisk(Service s)
    {
        var f = GuideFile(s);
        if (!File.Exists(f)) return null;
        try
        {
            using var fs = File.OpenRead(f);
            var d = JsonSerializer.Deserialize<DiskGuide>(fs);
            if (d == null || d.ById.Count == 0) return null;
            // Drop a copy whose programmes have all ended; it can't fill the grid anyway.
            var nowSec = Format.NowSec;
            if (!d.ById.Values.Any(list => list.Any(p => p.End > nowSec))) { File.Delete(f); return null; }
            return (d.SavedAt, new XmltvParser.Guide(d.ById, d.IdByName));
        }
        catch { try { File.Delete(f); } catch { } return null; }
    }

    private static void WriteDisk(Service s, long savedAt, XmltvParser.Guide guide)
    {
        try
        {
            AppPaths.Ensure();
            var f = GuideFile(s);
            var tmp = f + ".tmp";
            using (var fs = File.Create(tmp))
                JsonSerializer.Serialize(fs, new DiskGuide { SavedAt = savedAt, ById = guide.ById, IdByName = guide.IdByName });
            File.Move(tmp, f, true);
        }
        catch { }
    }

    // ---- Per-channel short EPG (fallback for channels the guide doesn't cover) ----

    private static string Key(Service s, string streamId) => $"{s}/{streamId}";

    /// Cached programmes if fresh, else null (no network).
    public static List<EpgProgramme>? Peek(Service s, string streamId)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(Key(s, streamId), out var e)) return null;
            return Format.NowMs - e.At < TTL_MS ? e.List : null;
        }
    }

    // Some panels have no per-channel listings at all and answer every request with an empty list.
    // Asking for each of thousands of channels anyway is pure load on the panel (and can get the
    // account rate-limited or blocked), so after a run of empty answers the lookups are paused.
    private const int EMPTY_STREAK_LIMIT = 40;
    private const long SHORT_EPG_PAUSE_MS = 6 * 60 * 60 * 1000L;
    private static readonly Dictionary<Service, int> EmptyStreak = new();
    private static readonly Dictionary<Service, int> RequestCount = new();

    /// True while per-channel lookups are paused for this service (the panel had nothing for any channel).
    public static bool ShortEpgPaused(Service s) => Prefs.Instance.ShortEpgOffUntil(s) > Format.NowMs;

    /// Programmes for the channel, fetching (at most 6 at a time) when not cached.
    public static async Task<List<EpgProgramme>> Get(Service s, Account account, string streamId)
    {
        var p = Peek(s, streamId);
        if (p != null) return p;
        if (ShortEpgPaused(s)) return new();
        await Gate.WaitAsync();
        try
        {
            p = Peek(s, streamId);
            if (p != null) return p;
            if (ShortEpgPaused(s)) return new();
            List<EpgProgramme> list;
            try { list = await XtreamApi.ShortEpg(s, account, streamId); } catch { list = new(); }
            lock (EmptyStreak)
            {
                var n = (RequestCount.TryGetValue(s, out var rc) ? rc : 0) + 1;
                RequestCount[s] = n;
                if (n % 500 == 0) AppLog.W("Epg", $"{s}: {n} per-channel programme requests this session");
                if (list.Count == 0)
                {
                    var streak = (EmptyStreak.TryGetValue(s, out var es) ? es : 0) + 1;
                    EmptyStreak[s] = streak;
                    if (streak >= EMPTY_STREAK_LIMIT)
                    {
                        Prefs.Instance.SetShortEpgOffUntil(s, Format.NowMs + SHORT_EPG_PAUSE_MS);
                        EmptyStreak[s] = 0;
                        AppLog.W("Epg", $"{s}: the panel returned no programme data for {EMPTY_STREAK_LIMIT} channels in a row; per-channel lookups paused for {SHORT_EPG_PAUSE_MS / 3_600_000} h");
                    }
                }
                else EmptyStreak[s] = 0;
            }
            // A panel hiccup that returns nothing must not wipe programmes we already had.
            List<EpgProgramme>? previous;
            lock (Cache) previous = Cache.TryGetValue(Key(s, streamId), out var e) ? e.List : null;
            var kept = list.Count == 0 && previous is { Count: > 0 } ? previous : list;
            lock (Cache) Cache[Key(s, streamId)] = (Format.NowMs, kept);
            return kept;
        }
        finally { Gate.Release(); }
    }

    public static void Clear() { lock (Cache) Cache.Clear(); }
}
