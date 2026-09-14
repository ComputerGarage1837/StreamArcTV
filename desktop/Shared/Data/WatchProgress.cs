using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamArcTV.Data;

/// <summary>
/// Where you left off in movies, episodes and downloaded files, plus a watched flag.
/// Keyed by a stable id per item so the same title resumes from any screen.
/// </summary>
public static class WatchProgress
{
    public class Entry
    {
        [JsonPropertyName("positionMs")] public long PositionMs { get; set; }
        [JsonPropertyName("durationMs")] public long DurationMs { get; set; }
        [JsonPropertyName("watched")] public bool Watched { get; set; }
        [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
        // What the item is, so Continue Watching / Next Episodes can show and play it without a catalogue lookup.
        [JsonPropertyName("kind")] public string? Kind { get; set; }       // "movie", "ep" or "dl"
        [JsonPropertyName("title")] public string? Title { get; set; }      // movie title, series title, or download title
        [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }   // episode title
        [JsonPropertyName("image")] public string? Image { get; set; }
        [JsonPropertyName("ext")] public string? Ext { get; set; }
        [JsonPropertyName("itemId")] public string? ItemId { get; set; }     // stream id, episode id or download job id
        [JsonPropertyName("seriesId")] public string? SeriesId { get; set; }
        [JsonPropertyName("season")] public int Season { get; set; }
        [JsonPropertyName("episode")] public int Episode { get; set; }

        [JsonIgnore] public long RemainingMs => Math.Max(0, DurationMs - PositionMs);
    }

    public const string KIND_MOVIE = "movie";
    public const string KIND_EPISODE = "ep";
    public const string KIND_DOWNLOAD = "dl";

    private const int MAX_ENTRIES = 2000;
    /// Within this of the end (or past 93%) counts as watched.
    private const long END_SLACK_MS = 90_000L;
    private const long RESUME_MIN_MS = 10_000L;

    private static readonly object Lock = new();
    private static Dictionary<string, Entry>? _cache;

    public static string MovieKey(Service s, string id) => $"movie:{s}:{id}";
    public static string EpisodeKey(string id) => $"ep:{id}";
    public static string DownloadKey(string jobId) => $"dl:{jobId}";

    public static Entry? Get(string key) { lock (Lock) return Map().TryGetValue(key, out var e) ? e : null; }

    /// Position to offer on resume, or 0 when there is nothing worth resuming.
    public static long ResumePosition(string key)
    {
        var e = Get(key);
        if (e == null) return 0;
        return !e.Watched && e.PositionMs >= RESUME_MIN_MS ? e.PositionMs : 0;
    }

    /// 0..1 progress for a partly watched item, 1 when watched, null when never played.
    public static float? Fraction(string key)
    {
        var e = Get(key);
        if (e == null) return null;
        if (e.Watched) return 1f;
        if (e.DurationMs <= 0 || e.PositionMs < RESUME_MIN_MS) return null;
        return Math.Clamp(e.PositionMs / (float)e.DurationMs, 0f, 1f);
    }

    public static bool IsWatched(string key) => Get(key)?.Watched == true;

    /// Records what an item is (called when it is opened) without touching its position.
    public static void Describe(string key, string kind, string title, string? image, string? ext, string itemId,
        string? subtitle = null, string? seriesId = null, int season = 0, int episode = 0)
    {
        lock (Lock)
        {
            var m = Map();
            if (!m.TryGetValue(key, out var e)) e = new Entry();
            e.Kind = kind; e.Title = title; e.Subtitle = subtitle; e.Image = image; e.Ext = ext; e.ItemId = itemId;
            e.SeriesId = seriesId; e.Season = season; e.Episode = episode;
            if (e.UpdatedAt == 0) e.UpdatedAt = Format.NowMs;
            m[key] = e;
            Persist(m);
        }
    }

    public static void Remove(string key) { lock (Lock) { var m = Map(); if (m.Remove(key)) Persist(m); } }

    /// Partly watched items, most recent first.
    public static List<(string Key, Entry Entry)> ContinueWatching(int limit = 20)
    {
        lock (Lock)
        {
            return Map().Where(kv => kv.Value.Kind != null && !kv.Value.Watched && kv.Value.PositionMs >= RESUME_MIN_MS && kv.Value.DurationMs > 0)
                .OrderByDescending(kv => kv.Value.UpdatedAt).Take(limit).Select(kv => (kv.Key, kv.Value)).ToList();
        }
    }

    /// Series that have been started: the most recently touched episode entry per series, newest first.
    public static List<Entry> StartedSeries(int limit = 12)
    {
        lock (Lock)
        {
            return Map().Values.Where(e => e.Kind == KIND_EPISODE && e.SeriesId != null && (e.Watched || e.PositionMs >= RESUME_MIN_MS))
                .GroupBy(e => e.SeriesId!).Select(g => g.MaxBy(e => e.UpdatedAt)!)
                .OrderByDescending(e => e.UpdatedAt).Take(limit).ToList();
        }
    }

    /// Records the current position; near the end it flips to watched and clears the position.
    public static void Save(string key, long positionMs, long durationMs)
    {
        lock (Lock)
        {
            var m = Map();
            if (!m.TryGetValue(key, out var e)) e = new Entry();
            var nearEnd = durationMs > 0 && (positionMs >= durationMs * 0.93 || durationMs - positionMs <= END_SLACK_MS);
            if (nearEnd) { e.Watched = true; e.PositionMs = 0; } else { e.Watched = false; e.PositionMs = positionMs; }
            e.DurationMs = durationMs;
            e.UpdatedAt = Format.NowMs;
            m[key] = e;
            Persist(m);
        }
    }

    public static void SetWatched(string key, bool watched)
    {
        lock (Lock)
        {
            var m = Map();
            if (!m.TryGetValue(key, out var e)) e = new Entry();
            e.Watched = watched;
            e.PositionMs = 0;
            e.UpdatedAt = Format.NowMs;
            m[key] = e;
            Persist(m);
        }
    }

    public static void Ended(string key) => SetWatched(key, true);

    private static Dictionary<string, Entry> Map()
    {
        if (_cache != null) return _cache;
        var json = Prefs.Blob("watch");
        Dictionary<string, Entry> m;
        try { m = json == null ? new() : JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? new(); }
        catch { m = new(); }
        return _cache = m;
    }

    /// Bumped on every change, so screens can tell whether their watched marks are stale.
    public static int Version { get; private set; }

    private static void Persist(Dictionary<string, Entry> m)
    {
        Version++;
        if (m.Count > MAX_ENTRIES)
        {
            foreach (var k in m.OrderBy(kv => kv.Value.UpdatedAt).Take(m.Count - MAX_ENTRIES).Select(kv => kv.Key).ToList()) m.Remove(k);
        }
        Prefs.SetBlob("watch", JsonSerializer.Serialize(m));
    }
}
