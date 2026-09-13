using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Web;

namespace StreamArcTV.Data;

/// <summary>Minimal Xtream Codes API client.</summary>
public static class XtreamApi
{
    public class ApiException : Exception { public ApiException(string m) : base(m) { } }

    /// Thrown from the progress callback to stop a guide download (e.g. it is too large).
    public class GuideTooLarge : Exception
    {
        public long Bytes { get; }
        public GuideTooLarge(long bytes) : base("Guide too large") { Bytes = bytes; }
    }

    public const string USER_AGENT = "StreamArcTV";

    /// Shared client: short idle timeouts so no connection to the provider lingers in the pool
    /// (most panels count an idle keep-alive connection as one of the account's streams).
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.None,
    })
    { Timeout = TimeSpan.FromSeconds(40) };

    // ---- Public API ----------------------------------------------------

    public static async Task<UserInfo> Login(Service service, string username, string password)
    {
        var body = await Get(ApiUrl(service, username, password));
        LoginResponse? resp;
        try { resp = JsonSerializer.Deserialize<LoginResponse>(body, Json.Options); }
        catch (JsonException) { throw new ApiException("Unexpected response from server"); }
        var info = resp?.UserInfo ?? throw new ApiException("Unexpected response from server");
        if (!info.IsAuthenticated)
        {
            var msg = info.Message.Str();
            throw new ApiException(!string.IsNullOrWhiteSpace(msg) ? msg! : "Invalid username or password");
        }
        return info;
    }

    public static async Task<List<Category>> Categories(Service service, Account account, ContentKind? kindOverride = null)
    {
        var kind = kindOverride ?? service.Kind();
        var action = kind switch
        {
            ContentKind.LIVE => "get_live_categories",
            ContentKind.MOVIE => "get_vod_categories",
            _ => "get_series_categories"
        };
        var body = await Get(ApiUrl(service, account.Username, account.Password, action));
        return ParseList<Category>(body);
    }

    public static async Task<List<Stream>> Streams(Service service, Account account, string? categoryId, ContentKind? kindOverride = null)
    {
        var kind = kindOverride ?? service.Kind();
        var action = kind switch
        {
            ContentKind.LIVE => "get_live_streams",
            ContentKind.MOVIE => "get_vod_streams",
            _ => "get_series"
        };
        var extra = categoryId != null ? new Dictionary<string, string> { ["category_id"] = categoryId } : null;
        var body = await Get(ApiUrl(service, account.Username, account.Password, action, extra));
        return ParseList<Stream>(body);
    }

    /// Episodes of a series grouped by season (`get_series_info`).
    public static async Task<List<Episode>> SeriesInfo(Service service, Account account, string seriesId)
    {
        var body = await Get(ApiUrl(service, account.Username, account.Password, "get_series_info",
            new Dictionary<string, string> { ["series_id"] = seriesId }));
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); } catch { throw new ApiException("Unexpected response from server"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new();
            var episodes = root.Get("episodes");
            if (episodes == null) return new();
            var outList = new List<Episode>();

            void Add(string seasonKey, JsonElement arr)
            {
                if (arr.ValueKind != JsonValueKind.Array) return;
                foreach (var o in arr.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object) continue;
                    string? S(string n) => o.Get(n)?.Str();
                    var id = S("id");
                    if (id == null) continue;
                    var info = o.Get("info");
                    string? I(string n) => info?.ValueKind == JsonValueKind.Object ? info.Value.Get(n)?.Str() : null;
                    var title = S("title");
                    outList.Add(new Episode(
                        id,
                        !string.IsNullOrWhiteSpace(title) ? title! : $"Episode {S("episode_num") ?? ""}".Trim(),
                        int.TryParse(S("season"), out var s) ? s : (int.TryParse(seasonKey, out var sk) ? sk : 0),
                        int.TryParse(S("episode_num"), out var n) ? n : 0,
                        !string.IsNullOrWhiteSpace(S("container_extension")) ? S("container_extension")! : "mp4",
                        I("plot"),
                        I("duration")));
                }
            }

            if (episodes.Value.ValueKind == JsonValueKind.Object)
                foreach (var p in episodes.Value.EnumerateObject()) Add(p.Name, p.Value);
            else if (episodes.Value.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var v in episodes.Value.EnumerateArray()) Add((++i).ToString(), v);
            }
            return outList.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();
        }
    }

    public static string EpisodeUrl(Service service, Account account, Episode episode) =>
        $"{ServerUrl(service)}/series/{Seg(account.Username)}/{Seg(account.Password)}/{Seg(episode.Id + "." + episode.ContainerExtension)}";

    /// The whole programme guide in one request (`xmltv.php`), parsed as a stream so large files
    /// stay cheap. Returns programmes keyed by XMLTV channel id, limited to [fromEpoch, toEpoch).
    public static async Task<XmltvParser.Guide> FullGuide(Service service, Account account, long fromEpoch, long toEpoch,
        Action<long, long>? onProgress = null)
    {
        var url = $"{ServerUrl(service)}/xmltv.php?username={Q(account.Username)}&password={Q(account.Password)}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(USER_AGENT);
        // Ask for gzip ourselves so the byte count below is what really crosses the wire.
        req.Headers.AcceptEncoding.ParseAdd("gzip");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) throw new ApiException($"Guide download failed (HTTP {(int)resp.StatusCode})");
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var raw = await resp.Content.ReadAsStreamAsync(cts.Token);
            var counting = new CountingStream(raw, total, onProgress);
            var gzip = resp.Content.Headers.ContentEncoding.Any(e => e.Contains("gzip", StringComparison.OrdinalIgnoreCase));
            await using System.IO.Stream input = gzip ? new GZipStream(counting, CompressionMode.Decompress) : counting;
            var guide = await Task.Run(() => XmltvParser.Parse(input, fromEpoch, toEpoch), cts.Token);
            counting.Finish();
            return guide;
        }
        catch (GuideTooLarge) { throw; }
        catch (ApiException) { throw; }
        catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException)
        {
            throw new ApiException($"Can't download guide: {e.Message}");
        }
    }

    private sealed class CountingStream : System.IO.Stream
    {
        private readonly System.IO.Stream _inner;
        private readonly long _total;
        private readonly Action<long, long>? _onProgress;
        private long _count, _lastReport;
        private bool _finished;

        public CountingStream(System.IO.Stream inner, long total, Action<long, long>? onProgress)
        {
            _inner = inner; _total = total; _onProgress = onProgress;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0) Tick(n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = _inner.Read(buffer);
            if (n > 0) Tick(n);
            return n;
        }

        private void Tick(int n)
        {
            _count += n;
            if (_count - _lastReport >= 128 * 1024) { _lastReport = _count; _onProgress?.Invoke(_count, _total); }
        }

        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            _onProgress?.Invoke(_count, _total > 0 ? _total : _count);   // final, exact
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// Now/next programmes for one live channel (`get_short_epg`).
    public static async Task<List<EpgProgramme>> ShortEpg(Service service, Account account, string streamId, int limit = 48)
    {
        var body = await Get(ApiUrl(service, account.Username, account.Password, "get_short_epg",
            new Dictionary<string, string> { ["stream_id"] = streamId, ["limit"] = limit.ToString() }));
        return EpgParser.Parse(body);
    }

    /// Direct movie address from an id and container extension (for items remembered outside the catalogue).
    public static string MovieUrl(Service service, Account account, string streamId, string? ext) =>
        $"{ServerUrl(service)}/movie/{Seg(account.Username)}/{Seg(account.Password)}/{Seg(streamId + "." + (string.IsNullOrWhiteSpace(ext) ? "mp4" : ext))}";

    public static string StreamUrl(Service service, Account account, Stream stream, string liveFormat)
    {
        var baseUrl = ServerUrl(service);
        var id = stream.StreamId ?? throw new ApiException("Stream has no id");
        if (service.Kind() == ContentKind.LIVE)
            return $"{baseUrl}/live/{Seg(account.Username)}/{Seg(account.Password)}/{Seg(id + "." + liveFormat)}";
        var ext = !string.IsNullOrWhiteSpace(stream.ContainerExtension) ? stream.ContainerExtension! : "mp4";
        return $"{baseUrl}/movie/{Seg(account.Username)}/{Seg(account.Password)}/{Seg(id + "." + ext)}";
    }

    // ---- Internals -----------------------------------------------------

    private static string ServerUrl(Service service)
    {
        if (!service.IsConfigured()) throw new ApiException($"{service.Title()} isn't configured in this build");
        if (!Uri.TryCreate(service.BaseUrl(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new ApiException($"Bad server URL: {service.BaseUrl()}");
        return service.BaseUrl();
    }

    private static string Seg(string s) => Uri.EscapeDataString(s);
    private static string Q(string s) => HttpUtility.UrlEncode(s);

    private static List<T> ParseList<T>(string body)
    {
        var trimmed = body.Trim();
        // Some panels answer with an object (e.g. an error) instead of a list.
        if (!trimmed.StartsWith("[")) return new();
        try { return JsonSerializer.Deserialize<List<T>>(trimmed, Json.Options) ?? new(); }
        catch (JsonException) { throw new ApiException("Unexpected response from server"); }
    }

    private static string ApiUrl(Service service, string username, string password, string? action = null, Dictionary<string, string>? extra = null)
    {
        var sb = new System.Text.StringBuilder(ServerUrl(service));
        sb.Append("/player_api.php?username=").Append(Q(username)).Append("&password=").Append(Q(password));
        if (action != null) sb.Append("&action=").Append(Q(action));
        if (extra != null) foreach (var (k, v) in extra) sb.Append('&').Append(Q(k)).Append('=').Append(Q(v));
        return sb.ToString();
    }

    private static async Task<string> Get(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(USER_AGENT);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        try
        {
            using var resp = await Client.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ApiException("Invalid username or password");
                throw new ApiException($"Server error (HTTP {(int)resp.StatusCode})");
            }
            if (string.IsNullOrWhiteSpace(text)) throw new ApiException("Empty response from server");
            return text;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new ApiException($"Can't reach server: {(e is TaskCanceledException ? "timed out" : e.Message)}");
        }
    }
}
