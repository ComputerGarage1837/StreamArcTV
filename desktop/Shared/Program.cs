using Avalonia;
using Avalonia.Controls;

namespace StreamArcTV;

public static class Program
{
    /// <summary>
    /// Entry point. "--selftest" loads the bundled LibVLC and exits (used by the release workflow
    /// to prove the bundle plays), "--tray" starts in the background for a scheduled recording.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

/// Headless check that the app can find and start its LibVLC: prints the version and exits 0.
public static class SelfTest
{
    public static int Run()
    {
        try
        {
            Util.AppPaths.Ensure();
            var lib = Player.PlayerCore.Shared();
            Console.WriteLine($"Stream Arc TV {BuildInfo.VersionName} · LibVLC {lib.Version} · {Util.Platform.LibVlcDescription}");
            using var probe = new LibVLCSharp.Shared.MediaPlayer(lib);
            Console.WriteLine("media player created OK");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("SELFTEST FAILED: " + e);
            return 1;
        }
    }
}
