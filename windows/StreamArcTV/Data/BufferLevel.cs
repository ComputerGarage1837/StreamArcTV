namespace StreamArcTV.Data;

/// <summary>
/// How much of a stream the player keeps ahead. Bigger buffers ride out network hiccups and let
/// you pause for longer, at the cost of more RAM. Playback starts as soon as one second is ready
/// (<see cref="PlaybackMs"/>); the level only changes how far ahead it keeps downloading. Levels
/// with <see cref="DiskMinutes"/> &gt; 0 keep the stream on disk instead, and only use a modest
/// in-memory buffer in front of it.
/// </summary>
public sealed class BufferLevel
{
    public string Key { get; }
    public int MinBufferMs { get; }
    public int MaxBufferMs { get; }
    public int PlaybackMs { get; }
    public int RebufferMs { get; }
    public int Bytes { get; }
    public int DiskMinutes { get; }
    public string Label { get; }

    private BufferLevel(string key, int min, int max, int playback, int rebuffer, int bytes, string label, int diskMinutes = 0)
    {
        Key = key; MinBufferMs = min; MaxBufferMs = max; PlaybackMs = playback; RebufferMs = rebuffer; Bytes = bytes; Label = label; DiskMinutes = diskMinutes;
    }

    public bool OnDisk => DiskMinutes > 0;

    /// Short name shown in Settings (the part before the parenthesis).
    public string ShortLabel => Label.Split(" (")[0];

    public static readonly BufferLevel SMALL = new("small", 8_000, 30_000, 1_000, 1_000, 24 << 20, "Small (30 s ahead, fastest start)");
    public static readonly BufferLevel NORMAL = new("normal", 20_000, 60_000, 1_000, 1_000, 64 << 20, "Normal (1 min ahead)");
    public static readonly BufferLevel LARGE = new("large", 45_000, 120_000, 1_000, 1_000, 128 << 20, "Large (2 min ahead, pause up to 2 min)");
    public static readonly BufferLevel XLARGE = new("xlarge", 90_000, 300_000, 1_000, 1_000, 256 << 20, "Very large (5 min ahead, pause up to 5 min)");
    public static readonly BufferLevel MAX = new("max", 180_000, 600_000, 1_000, 1_000, 512 << 20, "Huge (10 min ahead, pause up to 10 min) – default");
    public static readonly BufferLevel DISK20 = new("disk20", 20_000, 60_000, 1_000, 1_000, 64 << 20, "20 min (written to disk)", 20);
    public static readonly BufferLevel DISK30 = new("disk30", 20_000, 60_000, 1_000, 1_000, 64 << 20, "30 min (written to disk)", 30);
    public static readonly BufferLevel DISK60 = new("disk60", 20_000, 60_000, 1_000, 1_000, 64 << 20, "1 hour (written to disk)", 60);

    public static readonly BufferLevel[] Entries = { SMALL, NORMAL, LARGE, XLARGE, MAX, DISK20, DISK30, DISK60 };

    /// Huge is the default.
    public static BufferLevel From(string? key) => Entries.FirstOrDefault(e => e.Key == key) ?? MAX;
}
