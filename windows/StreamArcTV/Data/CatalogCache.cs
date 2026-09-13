using System.IO;
using System.Text.Json;

using StreamArcTV.Util;

namespace StreamArcTV.Data;

/// <summary>
/// Whole-catalogue cache for movies and series. One network request per content kind; the
/// result is kept in memory and on disk, so the next launch shows the catalogue instantly while
/// a fresh copy is fetched in the background when the saved one is older than 30 minutes.
/// </summary>
public static class CatalogCache
{
    private const long TTL_MS = 30 * 60 * 1000L;
    private static readonly Dictionary<string, (long At, List<Stream> Items)> Lists = new();
    private static readonly SemaphoreSlim Mutex = new(1);
    private static readonly HashSet<string> Refreshing = new();

    private static string Key(Service s, ContentKind kind) => $"{s}_{kind}";
    private static string File(string k) => Path.Combine(AppPaths.Cache, $"catalog_{k}.json");

    public static List<Stream>? Peek(Service s, ContentKind kind)
    {
        lock (Lists) return Lists.TryGetValue(Key(s, kind), out var e) ? e.Items : null;
    }

    public static async Task<List<Stream>> Get(Service s, Account account, ContentKind kind)
    {
        var k = Key(s, kind);
        (long At, List<Stream> Items)? mem;
        lock (Lists) mem = Lists.TryGetValue(k, out var e) ? e : null;
        if (mem != null)
        {
            if (Format.NowMs - mem.Value.At > TTL_MS) RefreshInBackground(s, account, kind);
            return mem.Value.Items;
        }
        await Mutex.WaitAsync();
        try
        {
            lock (Lists) if (Lists.TryGetValue(k, out var e2)) return e2.Items;
            // Disk copy first: instant, then refreshed behind the scenes if stale.
            var disk = await Task.Run(() => ReadDisk(k));
            if (disk != null)
            {
                lock (Lists) Lists[k] = disk.Value;
                if (Format.NowMs - disk.Value.At > TTL_MS) RefreshInBackground(s, account, kind);
                return disk.Value.Items;
            }
            var all = await XtreamApi.Streams(s, account, null, kind);
            var now = Format.NowMs;
            lock (Lists) Lists[k] = (now, all);
            await Task.Run(() => WriteDisk(k, now, all));
            return all;
        }
        finally { Mutex.Release(); }
    }

    private static void RefreshInBackground(Service s, Account account, ContentKind kind)
    {
        var k = Key(s, kind);
        lock (Refreshing) { if (!Refreshing.Add(k)) return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var all = await XtreamApi.Streams(s, account, null, kind);
                var now = Format.NowMs;
                lock (Lists) Lists[k] = (now, all);
                WriteDisk(k, now, all);
            }
            catch { }
            finally { lock (Refreshing) Refreshing.Remove(k); }
        });
    }

    private class DiskEntry
    {
        public long SavedAt { get; set; }
        public List<Stream> Items { get; set; } = new();
    }

    private static (long At, List<Stream> Items)? ReadDisk(string k)
    {
        var f = File(k);
        if (!System.IO.File.Exists(f)) return null;
        try
        {
            using var fs = System.IO.File.OpenRead(f);
            var e = JsonSerializer.Deserialize<DiskEntry>(fs, Json.Options);
            return e == null ? null : (e.SavedAt, e.Items);
        }
        catch { try { System.IO.File.Delete(f); } catch { } return null; }
    }

    private static void WriteDisk(string k, long savedAt, List<Stream> items)
    {
        try
        {
            AppPaths.Ensure();
            var f = File(k);
            var tmp = f + ".tmp";
            using (var fs = System.IO.File.Create(tmp)) JsonSerializer.Serialize(fs, new DiskEntry { SavedAt = savedAt, Items = items });
            System.IO.File.Move(tmp, f, true);
        }
        catch { }
    }

    public static void Clear()
    {
        lock (Lists) Lists.Clear();
        try { foreach (var f in Directory.GetFiles(AppPaths.Cache, "catalog_*")) System.IO.File.Delete(f); } catch { }
    }

    /// Newest first by the panel's "added" timestamp; items without one go last.
    public static List<Stream> RecentlyAdded(IEnumerable<Stream> all, int limit = 300) =>
        all.OrderByDescending(s => long.TryParse(s.Added?.Trim(), out var a) ? a : 0L).Take(limit).ToList();
}

/// Episode lists per series, kept for half an hour so the VOD home and the player don't refetch them.
public static class SeriesCache
{
    private const long TTL_MS = 30 * 60 * 1000L;
    private static readonly Dictionary<string, (long At, List<Episode> Eps)> Cache = new();

    public static async Task<List<Episode>> Episodes(Service s, Account account, string seriesId)
    {
        var now = Format.NowMs;
        lock (Cache) if (Cache.TryGetValue(seriesId, out var e) && now - e.At < TTL_MS) return e.Eps;
        var eps = await XtreamApi.SeriesInfo(s, account, seriesId);
        lock (Cache) Cache[seriesId] = (now, eps);
        return eps;
    }

    /// Episodes in viewing order: by season, then episode number.
    public static List<Episode> Ordered(IEnumerable<Episode> eps) => eps.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();
}
