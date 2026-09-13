namespace StreamArcTV.Data;

public static class Format
{
    public static DateTime Local(long epochSeconds) => DateTimeOffset.FromUnixTimeSeconds(epochSeconds).LocalDateTime;

    public static string Time(long epochSeconds) => Local(epochSeconds).ToString("h:mm tt");

    public static string TimeRange(long start, long end) => $"{Time(start)} – {Time(end)}";

    public static string Expiry(long? epochSeconds) =>
        epochSeconds == null ? "Unlimited" : Local(epochSeconds.Value).ToString("MMM d, yyyy");

    public static string Date(long? epochSeconds) =>
        epochSeconds == null ? "—" : Local(epochSeconds.Value).ToString("MMM d, yyyy");

    public static bool IsExpired(long? epochSeconds) =>
        epochSeconds != null && epochSeconds.Value * 1000L < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B"
    };

    public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static long NowSec => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
