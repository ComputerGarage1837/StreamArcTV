using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamArcTV.Util;

/// <summary>
/// The few things that are specific to macOS: where the app bundle and the bundled LibVLC are,
/// user notifications, opening URLs, and launchd for recordings that start while the app is closed.
/// </summary>
public static class Mac
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// The .app bundle this process runs from, or null when started from a plain publish folder
    /// (dotnet run, or a developer copy).
    public static string? BundlePath
    {
        get
        {
            try
            {
                var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
                var macos = Path.GetDirectoryName(exe);                 // …/Contents/MacOS
                var contents = macos != null ? Path.GetDirectoryName(macos) : null;
                if (macos == null || contents == null) return null;
                if (Path.GetFileName(macos) != "MacOS" || Path.GetFileName(contents) != "Contents") return null;
                var app = Path.GetDirectoryName(contents);
                return app != null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app : null;
            }
            catch { return null; }
        }
    }

    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    // ---- LibVLC location ----------------------------------------------------------

    /// Folder that holds libvlc.dylib and libvlccore.dylib, and the plugins folder next to it.
    /// Looks in the bundle first (Contents/Frameworks/libvlc), then next to the executable, then
    /// the STREAMARC_LIBVLC variable, and finally an installed VLC.app (handy for development).
    public static (string Lib, string Plugins)? LibVlc()
    {
        var roots = new List<string>();
        var bundle = BundlePath;
        if (bundle != null) roots.Add(Path.Combine(bundle, "Contents", "Frameworks", "libvlc"));
        roots.Add(Path.Combine(AppContext.BaseDirectory, "libvlc"));
        var env = Environment.GetEnvironmentVariable("STREAMARC_LIBVLC");
        if (!string.IsNullOrWhiteSpace(env)) roots.Add(env);
        roots.Add("/Applications/VLC.app/Contents/MacOS");
        roots.Add(Path.Combine(Home, "Applications", "VLC.app", "Contents", "MacOS"));
        foreach (var root in roots)
        {
            foreach (var lib in new[] { Path.Combine(root, "lib"), root })
            {
                if (!File.Exists(Path.Combine(lib, "libvlc.dylib")) || !File.Exists(Path.Combine(lib, "libvlccore.dylib"))) continue;
                foreach (var plugins in new[] { Path.Combine(root, "plugins"), Path.Combine(lib, "plugins"), Path.Combine(lib, "vlc", "plugins") })
                    if (Directory.Exists(plugins)) return (lib, plugins);
                return (lib, Path.Combine(root, "plugins"));
            }
        }
        return null;
    }

    public static string LibVlcDescription => LibVlc() is { } l ? $"libvlc at {l.Lib}" : "libvlc not found";

    // ---- Notifications and URLs -----------------------------------------------------

    /// Posts a Notification Center banner (the counterpart of the tray balloon).
    public static void Notify(string title, string text)
    {
        try
        {
            var script = $"display notification \"{Esc(text)}\" with title \"{Esc(title)}\"";
            Run("/usr/bin/osascript", new[] { "-e", script }, wait: false);
        }
        catch (Exception e) { AppLog.W("Mac", "notification failed: " + e.Message); }
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static void OpenUrl(string url)
    {
        try { Run("/usr/bin/open", new[] { url }, wait: false); } catch { }
    }

    public static void RevealInFinder(string path)
    {
        try { Run("/usr/bin/open", new[] { "-R", path }, wait: false); } catch { }
    }

    /// Runs a command line tool; returns (exit code, stdout, stderr). Exit code -1 when it could not start.
    public static (int Code, string Out, string Err) Run(string file, IEnumerable<string> args, bool wait = true, int timeoutMs = 20_000)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = wait, RedirectStandardError = wait, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return (-1, "", "could not start " + file);
        if (!wait) return (0, "", "");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
        return (p.HasExited ? p.ExitCode : -1, stdout.Result, stderr.Result);
    }

    // ---- launchd (recordings that start while the app is closed) ------------------------

    private static string AgentsDir => Path.Combine(Home, "Library", "LaunchAgents");

    public static string LaunchdLabel(string id) => $"com.computergarage.streamarctv.recording.{id}";

    private static string PlistPath(string id) => Path.Combine(AgentsDir, LaunchdLabel(id) + ".plist");

    private static string Uid => Run("/usr/bin/id", new[] { "-u" }).Out.Trim();

    /// Registers a launch agent that starts the app in the background at the given local time.
    public static void ScheduleLaunch(string id, DateTime at)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("no executable path");
        Directory.CreateDirectory(AgentsDir);
        var plist = new StringBuilder();
        plist.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        plist.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        plist.Append("<plist version=\"1.0\"><dict>\n");
        plist.Append($"  <key>Label</key><string>{X(LaunchdLabel(id))}</string>\n");
        plist.Append("  <key>ProgramArguments</key><array>\n");
        plist.Append($"    <string>{X(exe)}</string><string>--tray</string>\n");
        plist.Append("  </array>\n");
        plist.Append("  <key>StartCalendarInterval</key><dict>\n");
        plist.Append($"    <key>Month</key><integer>{at.Month}</integer><key>Day</key><integer>{at.Day}</integer>\n");
        plist.Append($"    <key>Hour</key><integer>{at.Hour}</integer><key>Minute</key><integer>{at.Minute}</integer>\n");
        plist.Append("  </dict>\n");
        plist.Append("  <key>RunAtLoad</key><false/>\n");
        plist.Append("</dict></plist>\n");
        var path = PlistPath(id);
        File.WriteAllText(path, plist.ToString());
        Run("/bin/launchctl", new[] { "bootout", $"gui/{Uid}/{LaunchdLabel(id)}" });
        var r = Run("/bin/launchctl", new[] { "bootstrap", $"gui/{Uid}", path });
        if (r.Code != 0)
        {
            // Older macOS: the legacy verbs.
            var r2 = Run("/bin/launchctl", new[] { "load", "-w", path });
            if (r2.Code != 0) throw new InvalidOperationException((r.Err + " " + r2.Err).Trim());
        }
    }

    public static void UnscheduleLaunch(string id)
    {
        try
        {
            var path = PlistPath(id);
            Run("/bin/launchctl", new[] { "bootout", $"gui/{Uid}/{LaunchdLabel(id)}" });
            if (File.Exists(path)) { Run("/bin/launchctl", new[] { "unload", path }); File.Delete(path); }
        }
        catch { }
    }

    private static string X(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
