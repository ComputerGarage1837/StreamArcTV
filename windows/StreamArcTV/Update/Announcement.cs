using System.Text.Json;
using StreamArcTV.Data;
using StreamArcTV.Util;

namespace StreamArcTV.Update;

/// <summary>
/// The service notice shown on the home screen (see ANNOUNCEMENT_FORMAT.md). Read from
/// `release/announcement.json` on the repository's main branch, so it can be posted and removed
/// without an app update. The last notice seen is kept so it still shows while offline.
/// </summary>
public static class Announcement
{
    public record Notice(bool Active, string Level, string Title, string Message, string Link, DateTimeOffset? Until, IReadOnlyList<string> Platforms)
    {
        /// True when this notice should be on screen right now on Windows.
        public bool Visible =>
            Active && !string.IsNullOrWhiteSpace(Message)
            && (Platforms.Count == 0 || Platforms.Any(p => string.Equals(p, "windows", StringComparison.OrdinalIgnoreCase)))
            && (Until == null || Until > DateTimeOffset.Now);
    }

    private const int SUPPORTED_SCHEMA = 1;
    public const string FILE = "release/announcement.json";
    private static string Repo => BuildInfo.GitHubRepo;
    private static string ApiUrl => $"https://api.github.com/repos/{Repo}/contents/{FILE}?ref=main";
    private static string RawUrl => $"https://raw.githubusercontent.com/{Repo}/main/{FILE}";

    /// The notice last fetched (or remembered from a previous run); null when none was ever seen.
    public static Notice? Cached()
    {
        var json = Prefs.Blob("announcement");
        if (json == null) return null;
        try { using var d = JsonDocument.Parse(json); return Parse(d.RootElement); } catch { return null; }
    }

    /// Fetches the current notice; returns the cached one when the network is unavailable.
    public static async Task<Notice?> Fetch()
    {
        return await Task.Run(() =>
        {
            JsonDocument? doc = null;
            try { doc = UpdateChecker.GetJson(ApiUrl, "application/vnd.github.raw"); } catch { doc = null; }
            if (doc == null) { try { doc = UpdateChecker.GetJson(RawUrl, "application/json"); } catch { doc = null; } }
            if (doc == null) return Cached();
            using (doc)
            {
                var notice = Parse(doc.RootElement);
                if (notice == null) return Cached();
                try { Prefs.SetBlob("announcement", doc.RootElement.GetRawText()); } catch { }
                return notice;
            }
        }).ConfigureAwait(false);
    }

    private static Notice? Parse(JsonElement e)
    {
        try
        {
            if ((e.Get("schemaVersion")?.GetInt32() ?? 0) != SUPPORTED_SCHEMA) return null;
            var active = e.Get("active") is { ValueKind: JsonValueKind.True };
            var level = (e.Get("level")?.Str() ?? "info").Trim().ToLowerInvariant();
            if (level is not ("info" or "warning" or "outage")) level = "info";
            var title = (e.Get("title")?.Str() ?? "").Trim();
            var message = (e.Get("message")?.Str() ?? "").Replace("\\n", "\n").Trim();
            var link = (e.Get("link")?.Str() ?? "").Trim();
            DateTimeOffset? until = null;
            var u = e.Get("until")?.Str();
            if (!string.IsNullOrWhiteSpace(u) && DateTimeOffset.TryParse(u, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)) until = parsed;
            var platforms = new List<string>();
            if (e.Get("platforms") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var p in arr.EnumerateArray()) { var s = p.Str(); if (!string.IsNullOrWhiteSpace(s)) platforms.Add(s.Trim()); }
            return new Notice(active, level, title, message, link, until, platforms);
        }
        catch (Exception ex) { AppLog.W("Notice", "unreadable announcement: " + ex.Message); return null; }
    }
}
