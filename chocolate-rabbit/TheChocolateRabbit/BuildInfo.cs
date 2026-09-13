using System.Reflection;

namespace TheChocolateRabbit;

/// <summary>
/// Build-time configuration: the version, the GitHub repository that hosts the update feed, and
/// the address newsletter sign-ups are sent to (all set in chocolate-rabbit/Directory.Build.props).
/// </summary>
public static class BuildInfo
{
    /// Must never change: the update feed and the installed base use this id.
    public const string ApplicationId = "ca.thechocolaterabbit.app.windows";

    public const string AppName = "The Chocolate Rabbit";

    public static readonly string VersionName = ReadVersion();
    public static readonly int VersionCode = VersionCodeFrom(VersionName);
    public static readonly string GitHubRepo = Meta("GitHubRepo", "ComputerGarage1837/StreamArcTV");
    public static readonly string NewsletterEmail = Meta("NewsletterEmail", "info@thechocolaterabbit.ca");

    /// major*10000 + minor*100 + patch, the same formula as the Stream Arc TV builds.
    public static int VersionCodeFrom(string v)
    {
        var parts = v.Split('.').Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        int At(int i) => i < parts.Length ? parts[i] : 0;
        return At(0) * 10000 + At(1) * 100 + At(2);
    }

    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return (plus > 0 ? info[..plus] : info).TrimStart('v');
        }
        return asm.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private static string Meta(string key, string fallback)
    {
        var v = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(v) ? fallback : v!;
    }
}
