using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamArcTV.Data;

public class LoginResponse
{
    [JsonPropertyName("user_info")] public UserInfo? UserInfo { get; set; }
    [JsonPropertyName("server_info")] public ServerInfo? ServerInfo { get; set; }
}

public class UserInfo
{
    [JsonPropertyName("username")] public JsonElement? Username { get; set; }
    [JsonPropertyName("password")] public JsonElement? Password { get; set; }
    [JsonPropertyName("message")] public JsonElement? Message { get; set; }
    [JsonPropertyName("auth")] public JsonElement? Auth { get; set; }
    [JsonPropertyName("status")] public JsonElement? Status { get; set; }
    [JsonPropertyName("exp_date")] public JsonElement? ExpDate { get; set; }
    [JsonPropertyName("is_trial")] public JsonElement? IsTrial { get; set; }
    [JsonPropertyName("active_cons")] public JsonElement? ActiveCons { get; set; }
    [JsonPropertyName("created_at")] public JsonElement? CreatedAt { get; set; }
    [JsonPropertyName("max_connections")] public JsonElement? MaxConnections { get; set; }

    public bool IsAuthenticated
    {
        get
        {
            if (Auth == null) return false;
            if (Auth.Value.ValueKind == JsonValueKind.True) return true;
            if (Auth.Value.ValueKind is JsonValueKind.False or JsonValueKind.Undefined or JsonValueKind.Null) return false;
            var s = Auth.Str();
            return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public long? ExpDateEpochSeconds => Json.PositiveLong(ExpDate.Str());

    public Account ToAccount(string username, string password) => new(
        username, password,
        Status.Str(),
        ExpDateEpochSeconds,
        MaxConnections.Str(),
        ActiveCons.Str(),
        Json.PositiveLong(CreatedAt.Str()),
        IsTrial.Str() == "1" || string.Equals(IsTrial.Str(), "true", StringComparison.OrdinalIgnoreCase));
}

public class ServerInfo
{
    [JsonPropertyName("url")] public JsonElement? Url { get; set; }
    [JsonPropertyName("port")] public JsonElement? Port { get; set; }
    [JsonPropertyName("https_port")] public JsonElement? HttpsPort { get; set; }
    [JsonPropertyName("server_protocol")] public JsonElement? ServerProtocol { get; set; }
}

public class Category
{
    [JsonPropertyName("category_id")] public JsonElement? IdRaw { get; set; }
    [JsonPropertyName("category_name")] public JsonElement? NameRaw { get; set; }

    [JsonIgnore] public string? Id { get => _id ?? IdRaw.Str(); set => _id = value; }
    [JsonIgnore] public string? Name { get => _name ?? NameRaw.Str(); set => _name = value; }
    private string? _id, _name;
    private bool _idSet;

    public Category() { }

    public Category(string? id, string? name)
    {
        _id = id; _idSet = true; _name = name;
        IdRaw = null; NameRaw = null;
    }

    /// Distinguishes the synthetic "All" category (null id) from a category whose id is missing.
    [JsonIgnore] public bool HasExplicitNullId => _idSet && _id == null;
}

public class Stream
{
    [JsonPropertyName("name")] public JsonElement? NameRaw { get; set; }
    [JsonPropertyName("stream_id")] public JsonElement? StreamIdRaw { get; set; }
    [JsonPropertyName("series_id")] public JsonElement? SeriesIdRaw { get; set; }
    [JsonPropertyName("stream_icon")] public JsonElement? IconRaw { get; set; }
    [JsonPropertyName("cover")] public JsonElement? CoverRaw { get; set; }
    [JsonPropertyName("plot")] public JsonElement? PlotRaw { get; set; }
    [JsonPropertyName("genre")] public JsonElement? GenreRaw { get; set; }
    [JsonPropertyName("category_id")] public JsonElement? CategoryIdRaw { get; set; }
    [JsonPropertyName("category_ids")] public JsonElement? CategoryIdsRaw { get; set; }
    [JsonPropertyName("container_extension")] public JsonElement? ContainerExtensionRaw { get; set; }
    [JsonPropertyName("epg_channel_id")] public JsonElement? EpgChannelIdRaw { get; set; }
    [JsonPropertyName("num")] public JsonElement? NumberRaw { get; set; }
    [JsonPropertyName("rating")] public JsonElement? RatingRaw { get; set; }
    [JsonPropertyName("added")] public JsonElement? AddedRaw { get; set; }

    [JsonIgnore] public string? Name => NameRaw.Str();
    [JsonIgnore] public string? StreamId => StreamIdRaw.Str();
    [JsonIgnore] public string? SeriesId => SeriesIdRaw.Str();
    [JsonIgnore] public string? Icon => IconRaw.Str();
    [JsonIgnore] public string? Cover => CoverRaw.Str();
    [JsonIgnore] public string? Plot => PlotRaw.Str();
    [JsonIgnore] public string? Genre => GenreRaw.Str();
    [JsonIgnore] public string? CategoryId => CategoryIdRaw.Str();
    [JsonIgnore] public string? ContainerExtension => ContainerExtensionRaw.Str();
    [JsonIgnore] public string? EpgChannelId => EpgChannelIdRaw.Str();
    [JsonIgnore] public string? Number => NumberRaw.Str();
    [JsonIgnore] public string? Rating => RatingRaw.Str();
    [JsonIgnore] public string? Added => AddedRaw.Str();

    [JsonIgnore]
    public List<string> CategoryIds
    {
        get
        {
            if (CategoryIdsRaw == null || CategoryIdsRaw.Value.ValueKind != JsonValueKind.Array) return new();
            return CategoryIdsRaw.Value.EnumerateArray().Select(e => e.Str()).Where(s => s != null).Select(s => s!).ToList();
        }
    }

    /// Genre names from the panel's free-text genre field ("Comedy, Drama" → [Comedy, Drama]).
    [JsonIgnore]
    public List<string> Genres =>
        (Genre ?? "").Split(new[] { ',', '/', '|', ';' }).Select(s => s.Trim())
            .Where(s => s.Length >= 2 && s.Length <= 30).Distinct().ToList();

    /// Every category this item belongs to (some panels send a list as well as the single id).
    [JsonIgnore]
    public List<string> AllCategoryIds
    {
        get
        {
            var l = new List<string>();
            if (CategoryId != null) l.Add(CategoryId);
            l.AddRange(CategoryIds);
            return l.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        }
    }

    /// Stream id for live/movies, series id for series.
    [JsonIgnore] public string? Id => StreamId ?? SeriesId;

    [JsonIgnore]
    public string? Image =>
        !string.IsNullOrWhiteSpace(Icon) ? Icon : (!string.IsNullOrWhiteSpace(Cover) ? Cover : null);
}

/// One episode of a series, as returned by `get_series_info`.
public record Episode(string Id, string Title, int Season, int Number, string ContainerExtension, string? Plot, string? Duration);

/// Locally stored, signed-in account for one service.
public record Account(
    string Username,
    string Password,
    string? Status,
    long? ExpDate,           // epoch seconds, null = unlimited/unknown
    string? MaxConnections,
    string? ActiveConnections,
    long? CreatedAt,
    bool IsTrial);

/// JSON helpers: Xtream panels send numbers as strings and strings as numbers interchangeably.
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string? Str(this JsonElement? e) => e == null ? null : e.Value.Str();

    public static string? Str(this JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    public static long? PositiveLong(string? s) =>
        long.TryParse(s?.Trim(), out var v) && v > 0 ? v : null;

    public static JsonElement? Get(this JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
}
