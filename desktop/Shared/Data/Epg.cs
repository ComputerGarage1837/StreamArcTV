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
        public Guide(Dictionary<string, List<EpgProgramme>> byId, Dictionary<string, string> idByName) { ById = byId; IdByName = idByName; }

        /// Case-insensitive id lookup through a lazily built index (no scans per guide row).
        public List<EpgProgramme>? ByIdIgnoreCase(string id)
        {
            if (ById.TryGetValue(id, out var l)) return l;
            _lowerIds ??= ById.Keys.GroupBy(k => k.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            return _lowerIds.TryGetValue(id.ToLowerInvariant(), out var real) && ById.TryGetValue(real, out var pl) ? pl : null;
        }
    }

    public static Guide Parse(System.IO.Stream input, long from, long to)
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
        foreach (var list in outMap.Values) list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new Guide(outMap, names);
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
            var guide = await XtreamApi.FullGuide(s, account, from, to, onProgress).ConfigureAwait(false);
            XmltvParser.Guide? current;
            lock (Guides) current = Guides.TryGetValue(s, out var c) ? c.Guide : null;
            if (guide.ById.Count == 0) throw new InvalidOperationException("Empty guide");
            if (current != null && !AtLeastAsComplete(guide, current))
            {
                // The panel is probably regenerating its EPG; keep the good copy and try later.
                lock (Guides) GuideFailedAt[s] = Format.NowMs;
                return current.ById.Count > 0;
            }
            var at = Format.NowMs;
            lock (Guides) { Guides[s] = (at, guide); GuideFailedAt.Remove(s); }
            _ = Task.Run(() => WriteDisk(s, at, guide));
            return true;
        }
        catch (XtreamApi.GuideTooLarge)
        {
            // Caller decided the file is too big; don't retry the full download for a while.
            lock (Guides) GuideFailedAt[s] = Format.NowMs + 6 * 60 * 60 * 1000L;
            return GuideLoaded(s);
        }
        catch (Exception)
        {
            lock (Guides) GuideFailedAt[s] = Format.NowMs;
            return GuideLoaded(s);
        }
    }

    private static bool AtLeastAsComplete(XmltvParser.Guide fresh, XmltvParser.Guide old)
    {
        var oldChannels = old.ById.Count;
        var oldProgrammes = old.ById.Values.Sum(l => l.Count);
        var newChannels = fresh.ById.Count;
        var newProgrammes = fresh.ById.Values.Sum(l => l.Count);
        return newChannels >= oldChannels * 0.7 && newProgrammes >= oldProgrammes * 0.5;
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

    /// Programmes for the channel, fetching (at most 6 at a time) when not cached.
    public static async Task<List<EpgProgramme>> Get(Service s, Account account, string streamId)
    {
        var p = Peek(s, streamId);
        if (p != null) return p;
        await Gate.WaitAsync();
        try
        {
            p = Peek(s, streamId);
            if (p != null) return p;
            List<EpgProgramme> list;
            try { list = await XtreamApi.ShortEpg(s, account, streamId); } catch { list = new(); }
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
