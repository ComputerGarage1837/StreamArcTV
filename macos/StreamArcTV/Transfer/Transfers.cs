using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using StreamArcTV.Util;

namespace StreamArcTV.Transfer;

public enum TransferType { DOWNLOAD, RECORDING }

public enum TransferState { SCHEDULED, QUEUED, RUNNING, DONE, FAILED, CANCELLED }

/// <summary>
/// One download or recording. Recordings have a start and end time; downloads run as soon as a
/// slot is free. <see cref="Folder"/> is a folder the user picked, or null for the app's own
/// Downloads / Recordings directory.
/// </summary>
public class TransferJob
{
    public string Id { get; set; } = "";
    public TransferType Type { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? Folder { get; set; }
    public long StartAt { get; set; }          // epoch ms; recordings start here, downloads use 0
    public long EndAt { get; set; }            // epoch ms; recordings stop here, downloads use 0
    public long CreatedAt { get; set; }
    public TransferState State { get; set; }
    public long Bytes { get; set; }
    public long Total { get; set; } = -1;
    public string? FileUri { get; set; }       // full path of the output file once created
    public string? Error { get; set; }

    [JsonIgnore]
    public bool IsActive => State is TransferState.QUEUED or TransferState.RUNNING or TransferState.SCHEDULED;

    public static TransferJob Create(TransferType type, string title, string subtitle, string url, string fileName, string? folder, long startAt, long endAt) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = type, Title = title, Subtitle = subtitle, Url = url, FileName = fileName, Folder = folder,
        StartAt = startAt, EndAt = endAt, CreatedAt = Data.Format.NowMs,
        State = startAt > 0 ? TransferState.SCHEDULED : TransferState.QUEUED
    };
}

/// Persistent list of jobs shared by the engine and the screens.
public class TransferStore
{
    private static readonly object Lock = new();
    private static TransferStore? _instance;
    private static readonly JsonSerializerOptions Opts = new() { Converters = { new JsonStringEnumConverter() } };

    public static TransferStore Get() => _instance ??= new TransferStore();

    public List<TransferJob> All()
    {
        lock (Lock)
        {
            var json = Data.Prefs.Blob("transfers");
            if (json == null) return new();
            try { return JsonSerializer.Deserialize<List<TransferJob>>(json, Opts) ?? new(); } catch { return new(); }
        }
    }

    public TransferJob? Get(string id) => All().FirstOrDefault(j => j.Id == id);

    public void Put(TransferJob job) { lock (Lock) Save(All().Where(j => j.Id != job.Id).Append(job).ToList()); }

    public void Update(string id, Action<TransferJob> change)
    {
        lock (Lock)
        {
            var list = All();
            var i = list.FindIndex(j => j.Id == id);
            if (i >= 0) { change(list[i]); Save(list); }
        }
    }

    public void Remove(string id) { lock (Lock) Save(All().Where(j => j.Id != id).ToList()); }

    private static void Save(List<TransferJob> list) => Data.Prefs.SetBlob("transfers", JsonSerializer.Serialize(list, Opts));
}

/// <summary>
/// Clear names for downloaded files: "Show - S01E02 - Episode title" for episodes (season and
/// episode always two digits, "S01E01", never "S1E1") and the plain title for movies.
/// </summary>
public static class Names
{
    private static readonly Regex OldEpisode = new(@"^(?<show>.+?)\s+S(?<s>\d{1,3})E(?<e>\d{1,4})\s*(?<title>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Episode(string show, int season, int number, string? title)
    {
        var code = $"S{Math.Max(0, season):00}E{Math.Max(0, number):00}";
        var t = (title ?? "").Trim();
        // Providers often repeat the show name or the episode code in the episode title.
        if (t.StartsWith(show, StringComparison.OrdinalIgnoreCase)) t = t[show.Length..].TrimStart(' ', '-', ':', '·');
        t = Regex.Replace(t, @"^S?\d{1,3}\s*[Ex]\s*\d{1,4}\s*[-:·]?\s*", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(t) || string.Equals(t, code, StringComparison.OrdinalIgnoreCase) ? $"{show} - {code}" : $"{show} - {code} - {t}";
    }

    public static string Movie(string name) => name.Trim();

    /// Rewrites a title from an earlier version ("Show S1E2 Title") into the clear form; null when it is not one.
    public static string? Upgrade(string oldTitle)
    {
        var m = OldEpisode.Match(oldTitle.Trim());
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups["s"].Value, out var s) || !int.TryParse(m.Groups["e"].Value, out var e)) return null;
        return Episode(m.Groups["show"].Value.Trim(), s, e, m.Groups["title"].Value);
    }
}

/// Folder handling for user-picked folders and the app's own directory.
public static class Folders
{
    public static string AppDir(TransferType type)
    {
        // ~/Movies/Stream Arc TV (the Movies folder is where macOS keeps video).
        var movies = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Movies");
        var baseDir = Directory.Exists(movies) ? Path.Combine(movies, "Stream Arc TV") : Path.Combine(AppPaths.Data, "Media");
        var dir = Path.Combine(baseDir, type == TransferType.RECORDING ? "Recordings" : "Downloads");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// Human-readable name of a folder choice.
    public static string Describe(string? folder, TransferType type)
    {
        if (folder == null) return "Movies / Stream Arc TV / " + (type == TransferType.RECORDING ? "Recordings" : "Downloads");
        try
        {
            var name = new DirectoryInfo(folder).Name;
            return string.IsNullOrWhiteSpace(name) ? folder : name;
        }
        catch { return "Chosen folder"; }
    }

    /// True when the picked folder is still accessible (drive present, writable).
    public static bool Usable(string? folder)
    {
        if (folder == null) return true;
        try
        {
            if (!Directory.Exists(folder)) return false;
            var probe = Path.Combine(folder, ".streamarc-probe-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public class Target : IDisposable
    {
        public string Path { get; }
        public System.IO.Stream Stream { get; }
        public Target(string path, System.IO.Stream stream) { Path = path; Stream = stream; }
        public void Dispose() { try { Stream.Dispose(); } catch { } }
    }

    /// Creates the output file (replacing a same-named one) and opens it for writing.
    public static Target Create(string? folder, TransferType type, string fileName)
    {
        var dir = folder ?? AppDir(type);
        if (!Directory.Exists(dir)) throw new InvalidOperationException("Folder unavailable");
        var f = Path.Combine(dir, fileName);
        if (File.Exists(f)) File.Delete(f);
        return new Target(f, new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.Read, 256 * 1024));
    }

    /// Reopens an existing output file for appending (used to resume an interrupted download).
    public static Target Append(string path) =>
        new(path, new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 256 * 1024));

    public static void Delete(string? fileUri)
    {
        if (fileUri == null) return;
        try { if (File.Exists(fileUri)) File.Delete(fileUri); } catch { }
    }

    public static bool Exists(string? fileUri) => fileUri != null && File.Exists(fileUri);

    private static readonly Regex Bad = new("[\\\\/:*?\"<>|\\x00-\\x1F]", RegexOptions.Compiled);   // stricter than macOS needs, so names also work on a shared drive
    private static readonly Regex Spaces = new("\\s+", RegexOptions.Compiled);

    public static string SafeName(string name)
    {
        var s = Spaces.Replace(Bad.Replace(name, " "), " ").Trim();
        if (s.Length > 120) s = s[..120];
        return string.IsNullOrWhiteSpace(s) ? "video" : s;
    }
}
