using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using StreamArcTV.Util;

namespace StreamArcTV.Data;

/// <summary>
/// User settings and accounts, kept in a JSON file under %LocalAppData%\StreamArcTV (the Windows
/// counterpart of the Android SharedPreferences). Same keys as the Android app.
/// </summary>
public sealed class Prefs
{
    public const string CATEGORY_FAVORITES = "__favorites__";
    public const string CATEGORY_ALL = "__all__";

    private static readonly object Lock = new();
    private static JsonObject? _root;
    private static string File => Path.Combine(AppPaths.Data, "prefs.json");

    public static Prefs Instance { get; } = new();

    private static JsonObject Root
    {
        get
        {
            lock (Lock)
            {
                if (_root != null) return _root;
                try
                {
                    if (System.IO.File.Exists(File))
                        _root = JsonNode.Parse(System.IO.File.ReadAllText(File)) as JsonObject;
                }
                catch { }
                return _root ??= new JsonObject();
            }
        }
    }

    private static bool _dirty;
    private static bool _flushPending;

    /// Marks the file for writing; the write itself happens a moment later on a worker thread so
    /// that a favorite toggle or a progress tick never stalls the window.
    private static void Save()
    {
        lock (Lock)
        {
            _dirty = true;
            if (_flushPending) return;
            _flushPending = true;
        }
        Task.Run(async () => { await Task.Delay(300).ConfigureAwait(false); Flush(); });
    }

    /// Writes pending changes now (called on exit and before a crash is reported).
    public static void Flush()
    {
        string json;
        lock (Lock)
        {
            _flushPending = false;
            if (!_dirty) return;
            _dirty = false;
            json = Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        try
        {
            AppPaths.Ensure();
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Move(tmp, File, true);
        }
        catch { }
    }

    // ---- Typed accessors -------------------------------------------------

    private static string? GetString(string key, string? def = null)
    {
        lock (Lock) { return Root[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : def; }
    }

    private static void PutString(string key, string? value)
    {
        lock (Lock) { if (value == null) Root.Remove(key); else Root[key] = value; }
        Save();
    }

    private static bool GetBool(string key, bool def)
    {
        lock (Lock) { return Root[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def; }
    }

    private static void PutBool(string key, bool value) { lock (Lock) { Root[key] = value; } Save(); }

    private static long GetLong(string key, long def)
    {
        lock (Lock) { return Root[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : def; }
    }

    private static void PutLong(string key, long value) { lock (Lock) { Root[key] = value; } Save(); }

    private static int GetInt(string key, int def) => (int)GetLong(key, def);
    private static void PutInt(string key, int value) => PutLong(key, value);

    private static HashSet<string> GetSet(string key)
    {
        lock (Lock)
        {
            return Root[key] is JsonArray a
                ? a.Select(n => n?.GetValue<string>()).Where(s => s != null).Select(s => s!).ToHashSet()
                : new HashSet<string>();
        }
    }

    private static void PutSet(string key, IEnumerable<string> value)
    {
        lock (Lock) { Root[key] = new JsonArray(value.Select(s => (JsonNode)s).ToArray()); }
        Save();
    }

    private static void Remove(params string[] keys) { lock (Lock) { foreach (var k in keys) Root.Remove(k); } Save(); }

    private static string K(Service s, string key) => $"{s.ToString().ToLowerInvariant()}_{key}";

    // ---- Accounts ------------------------------------------------------

    public Account? Account(Service service)
    {
        var user = GetString(K(service, "user"));
        var pass = GetString(K(service, "pass"));
        if (user == null || pass == null) return null;
        var exp = GetLong(K(service, "exp"), -1);
        var created = GetLong(K(service, "created"), -1);
        return new Account(user, pass, GetString(K(service, "status")), exp > 0 ? exp : null,
            GetString(K(service, "maxc")), GetString(K(service, "activec")), created > 0 ? created : null,
            GetBool(K(service, "trial"), false));
    }

    public bool IsSignedIn(Service service) => Account(service) != null;

    public void SaveAccount(Service service, Account a)
    {
        lock (Lock)
        {
            Root[K(service, "user")] = a.Username;
            Root[K(service, "pass")] = a.Password;
            Root[K(service, "status")] = a.Status;
            Root[K(service, "exp")] = a.ExpDate ?? -1L;
            Root[K(service, "maxc")] = a.MaxConnections;
            Root[K(service, "activec")] = a.ActiveConnections;
            Root[K(service, "created")] = a.CreatedAt ?? -1L;
            Root[K(service, "trial")] = a.IsTrial;
        }
        Save();
    }

    public void ClearAccount(Service service) =>
        Remove(new[] { "user", "pass", "status", "exp", "maxc", "activec", "created", "trial" }.Select(k => K(service, k)).ToArray());

    // ---- Live TV categories --------------------------------------------

    /// Category ids the user switched off; their channels are left out everywhere.
    public HashSet<string> HiddenLiveCategories
    {
        get => GetSet("live_hidden_cats");
        set => PutSet("live_hidden_cats", value);
    }

    /// The category the guide opens on: a provider category id, CATEGORY_FAVORITES, CATEGORY_ALL, or null for automatic.
    public string? DefaultLiveCategory
    {
        get => GetString("live_default_cat");
        set => PutString("live_default_cat", value);
    }

    /// "auto" (full download unless it is huge), "full", or "channel" (per-channel lookups only).
    public string GuideMode
    {
        get => GetString("guide_mode", "auto") ?? "auto";
        set => PutString("guide_mode", value);
    }

    public long GuideSize(Service s) => GetLong(K(s, "guide_size"), 0);
    public void SetGuideSize(Service s, long bytes) => PutLong(K(s, "guide_size"), bytes);

    // ---- Favorites -----------------------------------------------------

    private static string FavKey(Service s, ContentKind kind) => K(s, $"favs_{kind.ToString().ToLowerInvariant()}");

    public HashSet<string> Favorites(Service s, ContentKind kind) => GetSet(FavKey(s, kind));

    public bool IsFavorite(Service s, ContentKind kind, string? id) => id != null && Favorites(s, kind).Contains(id);

    /// Adds or removes the item; returns true when it is now a favorite.
    public bool ToggleFavorite(Service s, ContentKind kind, string id)
    {
        var set = Favorites(s, kind);
        bool nowFav;
        if (set.Contains(id)) { set.Remove(id); nowFav = false; } else { set.Add(id); nowFav = true; }
        PutSet(FavKey(s, kind), set);
        return nowFav;
    }

    // ---- Updates -------------------------------------------------------

    public string? SkippedVersion { get => GetString("skipped_version"); set => PutString("skipped_version", value); }
    public bool AutoCheckUpdates { get => GetBool("auto_check_updates", true); set => PutBool("auto_check_updates", value); }

    // ---- Display -------------------------------------------------------

    /// "phone", "tv", or null when the user hasn't been asked yet.
    public string? LayoutMode { get => GetString("layout_mode"); set => PutString("layout_mode", value); }

    // ---- Folders (paths; null = inside the app) ----------------------------

    public string? DownloadFolder { get => GetString("download_folder"); set => PutString("download_folder", value); }
    public string? RecordingFolder { get => GetString("recording_folder"); set => PutString("recording_folder", value); }

    // ---- Playback ------------------------------------------------------

    /// "m3u8" (HLS) or "ts" (MPEG-TS) for live streams.
    public string LiveFormat { get => GetString("live_format", "m3u8") ?? "m3u8"; set => PutString("live_format", value); }

    /// Whether subtitle tracks are shown by default (the player's CC button changes and remembers this).
    public bool Subtitles { get => GetBool("subtitles", false); set => PutBool("subtitles", value); }

    /// Start the next episode automatically when one ends.
    public bool AutoPlayNext { get => GetBool("auto_play_next", true); set => PutBool("auto_play_next", value); }

    /// Downloads wait for Wi-Fi / Ethernet instead of using mobile data.
    public bool DownloadsWifiOnly { get => GetBool("downloads_wifi_only", false); set => PutBool("downloads_wifi_only", value); }

    /// Delete a downloaded file once it has been watched to the end.
    public bool DeleteAfterWatched { get => GetBool("delete_after_watched", false); set => PutBool("delete_after_watched", value); }

    /// Set synchronously by the crash handler; the home screen offers the log on the next start.
    public bool Crashed { get => GetBool("crashed", false); set => PutBool("crashed", value); }

    /// Set once the files of earlier downloads have been renamed to the clear "Show - S01E01 - Title" form.
    public bool DownloadsRenamed { get => GetBool("downloads_renamed_v2", false); set => PutBool("downloads_renamed_v2", value); }

    /// Multi-view: number of tiles (2 or 4) and the channel id in each slot ("" = empty).
    public int MultiviewTiles { get => GetInt("multiview_tiles", 4); set => PutInt("multiview_tiles", value); }

    public List<string> MultiviewSlots
    {
        get
        {
            var l = (GetString("multiview_slots", "") ?? "").Split('|');
            return Enumerable.Range(0, 4).Select(i => i < l.Length ? l[i] : "").ToList();
        }
        set => PutString("multiview_slots", string.Join("|", value.Take(4)));
    }

    /// One of BufferLevel.Key; how much live video the player keeps buffered.
    public string BufferLevelKey { get => GetString("buffer_level", BufferLevel.MAX.Key) ?? BufferLevel.MAX.Key; set => PutString("buffer_level", value); }

    /// Main window placement, so the app opens where it was left.
    public string? WindowPlacement { get => GetString("window_placement"); set => PutString("window_placement", value); }

    /// Generic JSON blob storage used by WatchProgress and TransferStore (kept in separate files on Android).
    public static string? Blob(string name) => GetString("blob_" + name);
    public static void SetBlob(string name, string? json) => PutString("blob_" + name, json);
}
