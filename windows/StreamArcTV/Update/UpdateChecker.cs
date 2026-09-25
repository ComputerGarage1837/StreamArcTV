using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamArcTV.Data;
using StreamArcTV.UI;

namespace StreamArcTV.Update;

/// <summary>
/// Checks the repository's `release/update-windows.json` feed (see UPDATE_FORMAT.md) for a newer
/// build and offers to download and install it, with a "skip this version" option.
///
/// The feed is the source of truth: it names the zip asset attached to the matching GitHub
/// release (`windows-v&lt;versionName&gt;`) and carries its SHA-256 and size so the download can be
/// verified before it is installed. The release notes are fetched from the GitHub release itself,
/// best-effort.
/// </summary>
public static class UpdateChecker
{
    /// One downloadable package on the release: the setup program or the portable zip.
    public record Package(string Url, string AssetName, string? Sha256, long SizeBytes);

    public record Release(string VersionName, int VersionCode, string Notes, string ZipUrl, string AssetName, string? Sha256, long SizeBytes, string HtmlUrl, Package? Setup = null)
    {
        public Package Zip => new(ZipUrl, AssetName, Sha256, SizeBytes);
    }

    private const int SUPPORTED_SCHEMA = 1;
    public const string FEED_FILE = "release/update-windows.json";

    private static string Repo => BuildInfo.GitHubRepo;

    // The Contents API is not behind GitHub's 5-minute raw-file CDN cache, so a check right after
    // a release sees the new feed. raw.githubusercontent.com is the fallback if the API is
    // unavailable or rate-limited.
    private static string FeedApiUrl => $"https://api.github.com/repos/{Repo}/contents/{FEED_FILE}?ref=main";
    private static string FeedRawUrl => $"https://raw.githubusercontent.com/{Repo}/main/{FEED_FILE}";

    public static string TagFor(string versionName) => $"windows-v{versionName}";

    /// <param name="manual">true when the user pressed the Update button: always reports the outcome and ignores the "skipped" version.</param>
    public static async void Check(bool manual)
    {
        var prefs = Prefs.Instance;
        if (manual) Dialogs.Toast("Checking for updates…");
        Release release;
        try { release = await Task.Run(FetchLatest); }
        catch (Exception e)
        {
            if (manual) Dialogs.Toast($"Couldn't check for updates: {e.Message}");
            return;
        }
        if (release.VersionCode <= BuildInfo.VersionCode)
        {
            if (manual) Dialogs.Toast($"You're on the latest version (v{BuildInfo.VersionName})");
            return;
        }
        if (!manual && prefs.SkippedVersion == release.VersionName) return;
        ShowDialog(release, prefs);
    }

    /// Reads and validates the update feed. Throws with a user-readable message on failure.
    public static Release FetchLatest()
    {
        JsonDocument? feedDoc = null;
        try { feedDoc = GetJson(FeedApiUrl, "application/vnd.github.raw"); } catch { feedDoc = null; }
        feedDoc ??= GetJson(FeedRawUrl, "application/json") ?? throw new InvalidOperationException("Update feed not found");
        using (feedDoc)
        {
            var feed = feedDoc.RootElement;
            var schema = feed.Get("schemaVersion")?.GetInt32() ?? 0;
            if (schema != SUPPORTED_SCHEMA) throw new InvalidOperationException($"Unsupported update feed (schema {schema})");
            var pkg = feed.Get("packageName")?.Str();
            if (pkg != BuildInfo.ApplicationId) throw new InvalidOperationException("Update feed is for a different app");
            var versionName = feed.Get("versionName")?.Str()?.Trim();
            if (string.IsNullOrEmpty(versionName)) throw new InvalidOperationException("Update feed has no version");
            var versionCode = feed.Get("versionCode")?.GetInt32() ?? throw new InvalidOperationException("Update feed has no version code");
            if (versionCode <= 0) return new Release(versionName, 0, "", "", "", null, 0, $"https://github.com/{Repo}/releases");   // nothing published yet
            var tag = TagFor(versionName);
            var zip = ReadPackage(feed.Get("zip"), tag) ?? throw new InvalidOperationException("Update feed has no package entry");
            var setup = ReadPackage(feed.Get("setup"), tag);   // older feeds have no installer
            var htmlUrl = $"https://github.com/{Repo}/releases/tag/{tag}";

            // Release notes come from the GitHub release body when available.
            var notes = "";
            try
            {
                using var rel = GetJson($"https://api.github.com/repos/{Repo}/releases/tags/{tag}", "application/vnd.github+json");
                notes = rel?.RootElement.Get("body")?.Str() ?? "";
            }
            catch { }

            return new Release(versionName, versionCode, notes, zip.Url, zip.AssetName, zip.Sha256, zip.SizeBytes, htmlUrl, setup);
        }
    }

    private static Package? ReadPackage(JsonElement? entry, string tag)
    {
        if (entry == null) return null;
        var assetName = entry.Value.Get("assetName")?.Str()?.Trim();
        if (string.IsNullOrEmpty(assetName)) return null;
        var sha = entry.Value.Get("sha256")?.Str()?.Trim().ToLowerInvariant();
        if (sha != null && !(sha.Length == 64 && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))) sha = null;
        var sizeBytes = entry.Value.Get("sizeBytes")?.GetInt64() ?? 0;
        var url = $"https://github.com/{Repo}/releases/download/{tag}/{Uri.EscapeDataString(assetName)}";
        return new Package(url, assetName, sha, sizeBytes);
    }

    /// GET a JSON document; returns null on 404.
    internal static JsonDocument? GetJson(string url, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd(accept);
        req.Headers.UserAgent.ParseAdd(XtreamApi.USER_AGENT);
        req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var resp = XtreamApi.Client.Send(req);
        if ((int)resp.StatusCode == 404) return null;
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub returned HTTP {(int)resp.StatusCode}");
        return JsonDocument.Parse(resp.Content.ReadAsStream());
    }

    private static void ShowDialog(Release release, Prefs prefs)
    {
        var notes = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes provided." : release.Notes;
        var size = release.SizeBytes > 0 ? $" ({Format.Size(release.SizeBytes)})" : "";
        var message = $"Version {release.VersionName}{size} is available (you have v{BuildInfo.VersionName}).\n\n" +
                      "What's new:\n\n" + PlainTextFromMarkdown(notes);
        var r = Dialogs.Alert($"Update available — v{release.VersionName}", message, "Update now", "Skip this version", "Later");
        if (r == DialogResultKind.Positive) Installer.Download(release);
        else if (r == DialogResultKind.Negative)
        {
            prefs.SkippedVersion = release.VersionName;
            Dialogs.Toast($"v{release.VersionName} skipped. You'll be asked again for the next version.");
        }
    }

    public static string FormatSize(long bytes) => Format.Size(bytes);

    private static string PlainTextFromMarkdown(string md) =>
        string.Join("\n", md.Split('\n').Select(line =>
        {
            var l = line.TrimEnd();
            l = Regex.Replace(l, @"^#{1,6}\s*", "");
            l = Regex.Replace(l, @"^\s*[-*]\s+", "• ");
            l = l.Replace("**", "").Replace("__", "").Replace("`", "");
            return l;
        })).Trim();
}
