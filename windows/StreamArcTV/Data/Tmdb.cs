using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamArcTV.Data;

/// The Movie Database: actor photos and "similar titles" for a movie or show. The provider only sends
/// actor names, so the title is looked up on TMDB and the photos come from there. Every failure just
/// means "nothing to show".
public static class Tmdb
{
    private const string Key = "ecd110afb0b691daebc36389f6c8a0cf";
    private const string Api = "https://api.themoviedb.org/3/";
    private const string Img = "https://image.tmdb.org/t/p/w185";

    public record Cast(string Name, string? Character, string? Photo);
    public record Info(List<Cast> Cast, List<string> SimilarTitles);

    private static readonly Dictionary<string, Info> Cache = new();
    private static readonly Info None = new(new(), new());

    /// Drops the clutter providers put in names: "EN - Title (2019) 4K [Multi-Sub]".
    public static string CleanTitle(string raw)
    {
        var t = Regex.Replace(raw, @"[\[(].*?[\])]", " ");
        t = Regex.Replace(t, @"^\s*[A-Za-z]{2,3}\s*[-:|]\s+", "");
        t = Regex.Replace(t, @"\b(4K|UHD|FHD|HD|SD|HDR|1080p|720p|2160p|MULTI|SUBS?)\b", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(t, @"\s+", " ").Trim(' ', '-', '|', ':');
    }

    public static string? YearOf(string raw, string? year)
    {
        if (year is { Length: >= 4 } && year[..4].All(char.IsDigit)) return year[..4];
        var m = Regex.Match(raw, @"\((\d{4})\)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static async Task<JsonElement?> GetAsync(string url)
    {
        using var resp = await XtreamApi.Client.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string? Str(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        string? text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public static async Task<Info> Lookup(string rawTitle, string? year, bool series)
    {
        var title = CleanTitle(rawTitle);
        if (title.Length < 2) return None;
        var y = YearOf(rawTitle, year);
        var ck = $"{(series ? "tv" : "movie")}|{title.ToLowerInvariant()}|{y}";
        lock (Cache) if (Cache.TryGetValue(ck, out var hit)) return hit;
        Info? info;
        try { info = await Fetch(title, y, series); } catch { info = null; }
        if (info == null) return None;   // failures are not cached
        lock (Cache) Cache[ck] = info;
        return info;
    }

    private static async Task<Info?> Fetch(string title, string? year, bool series)
    {
        var kind = series ? "tv" : "movie";
        var q = $"{Api}search/{kind}?api_key={Key}&query={Uri.EscapeDataString(title)}";
        var yq = year != null ? $"&{(series ? "first_air_date_year" : "year")}={year}" : "";
        var res = await GetAsync(q + yq);
        JsonElement? first = null;
        if (res is { } r1 && r1.TryGetProperty("results", out var a1) && a1.GetArrayLength() > 0) first = a1[0];
        if (first == null && year != null && await GetAsync(q) is { } r2 && r2.TryGetProperty("results", out var a2) && a2.GetArrayLength() > 0) first = a2[0];
        if (first is not { } f || Str(f, "id") is not { } id) return null;
        if (await GetAsync($"{Api}{kind}/{id}?api_key={Key}&append_to_response=credits,recommendations,similar") is not { } d) return null;
        var cast = new List<Cast>();
        if (d.TryGetProperty("credits", out var cr) && cr.TryGetProperty("cast", out var ca))
            foreach (var c in ca.EnumerateArray().Take(20))
                if (Str(c, "name") is { } n) cast.Add(new Cast(n, Str(c, "character"), Str(c, "profile_path") is { } p ? Img + p : null));
        var titles = new List<string>();
        foreach (var section in new[] { "recommendations", "similar" })
            if (d.TryGetProperty(section, out var sec) && sec.TryGetProperty("results", out var rs))
                foreach (var e in rs.EnumerateArray())
                {
                    if (Str(e, series ? "name" : "title") is { } t) titles.Add(t);
                    if (Str(e, series ? "original_name" : "original_title") is { } ot) titles.Add(ot);
                }
        return new Info(cast, titles);
    }

    private static string Norm(string s) => Regex.Replace(CleanTitle(s).ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();

    /// Titles from the catalogue that TMDB lists as similar, so everything shown can be played here; if there are
    /// few, the rest is filled with the catalogue's titles of the same genre.
    public static List<Stream> SimilarIn(Info info, IEnumerable<Stream> catalogue, string? selfId, string? genre, int max = 20)
    {
        var rank = new Dictionary<string, int>();
        var i = 0;
        foreach (var t in info.SimilarTitles) { var n = Norm(t); if (n.Length > 0) rank.TryAdd(n, i); i++; }
        var list = catalogue.ToList();
        var matched = list.Where(s => s.Id != selfId && !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => (s, ok: rank.TryGetValue(Norm(s.Name!), out var r), r))
            .Where(x => x.ok).OrderBy(x => x.r).Select(x => x.s)
            .GroupBy(s => s.Id).Select(g => g.First()).Take(max).ToList();
        if (matched.Count < 8 && !string.IsNullOrWhiteSpace(genre))
        {
            var g = genre.Split(',', '/', '|', ';').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 2).ToHashSet();
            var have = matched.Select(s => s.Id).ToHashSet();
            matched.AddRange(list.Where(s => s.Id != selfId && !have.Contains(s.Id) && s.Genres.Any(x => g.Contains(x.ToLowerInvariant())))
                .OrderByDescending(s => double.TryParse(s.Rating, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rt) ? rt : 0)
                .Take(max - matched.Count));
        }
        return matched;
    }
}
