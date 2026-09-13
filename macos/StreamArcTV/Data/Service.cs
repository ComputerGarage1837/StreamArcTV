namespace StreamArcTV.Data;

/// <summary>
/// The two content services the app connects to. Each has its own Xtream Codes server
/// (supplied at build time) and its own sign-in.
/// </summary>
public enum Service { LIVE, VOD }

public enum ContentKind { LIVE, MOVIE, SERIES }

public static class ServiceInfo
{
    public static string Title(this Service s) => s == Service.LIVE ? "Live TV" : "Video on Demand";

    public static string BaseUrl(this Service s) =>
        (s == Service.LIVE ? BuildInfo.LiveUrl : BuildInfo.VodUrl).Trim().TrimEnd('/');

    public static ContentKind Kind(this Service s) => s == Service.LIVE ? ContentKind.LIVE : ContentKind.MOVIE;

    /// False when this build was made without a server address for the service.
    public static bool IsConfigured(this Service s) => !string.IsNullOrWhiteSpace(s.BaseUrl());

    public static readonly Service[] All = { Service.LIVE, Service.VOD };
}
