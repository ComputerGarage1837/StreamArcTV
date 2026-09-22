using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;
using StreamArcTV.UI;
using StreamArcTV.Update;

namespace StreamArcTV.Util;

/// <summary>
/// The macOS side of the desktop app: where the app bundle and the bundled LibVLC are, user
/// notifications, opening URLs, launchd for recordings that start while the app is closed, and
/// replacing the app bundle when an update is installed. The Linux edition has its own copy of
/// this class with the same members.
/// </summary>
public static class Platform
{
    /// Must never change: the update feed and the installed base use this id.
    public const string ApplicationId = "com.computergarage.streamarctv.macos";
    public const string OsName = "macOS";
    public const string FeedFile = "release/update-macos.json";
    public const string TagPrefix = "macos-v";
    public const string UpdatePackageKey = "zip";
    public const string InstallerPackageKey = "dmg";
    public const string TrayName = "menu-bar icon";
    public const string VideosLabel = "Movies";
    /// The in-app updater always takes the zip (the disk image is for installing by hand).
    public const bool WantsInstallerPackage = false;

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Library => Path.Combine(Home, "Library");

    /// Settings, accounts, transfer list, logs: ~/Library/Application Support/StreamArcTV.
    public static string DataDir { get; } = Path.Combine(Library, "Application Support", "StreamArcTV");
    /// Caches: ~/Library/Caches/StreamArcTV.
    public static string CacheDir { get; } = Path.Combine(Library, "Caches", "StreamArcTV");
    /// ~/Movies.
    public static string? VideosDir => Path.Combine(Home, "Movies");

    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public static string InstallDescription => $"bundle {BundlePath ?? "(none)"}";

    /// A message shown once on the home screen after start-up (nothing on macOS).
    public static (string Title, string Message)? StartupNotice => null;

    public static void Startup() { }

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

    // ---- LibVLC ----------------------------------------------------------------------

    private static IntPtr _libvlcHandle;
    private static (string Lib, string Plugins)? _found;

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

    public static string LibVlcDescription => (_found ?? LibVlc()) is { } l ? $"libvlc at {l.Lib}, plugins {l.Plugins}" : "libvlc not found";

    /// <summary>
    /// Finds the LibVLC shipped inside the app bundle (Contents/Frameworks/libvlc, copied from
    /// VLC.app by the release workflow), points VLC at its plugins folder, loads the two libraries
    /// by their full paths and makes LibVLCSharp's P/Invokes resolve to that copy.
    /// </summary>
    public static void InitializeLibVlc()
    {
        var found = _found = LibVlc();
        if (found is { } loc)
        {
            // .NET's Environment.SetEnvironmentVariable only updates the managed copy on Unix; VLC
            // reads the variable with getenv(), so it has to be set natively. Without it, libvlccore
            // on macOS looks for plugins in lib/vlc/plugins next to itself and finds no modules
            // ("unknown option" for every module option, and libvlc_new fails).
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", loc.Plugins);
            try { setenv("VLC_PLUGIN_PATH", loc.Plugins, 1); }
            catch (Exception e) { AppLog.W("Player", "setenv failed: " + e.Message); }
            var core = Path.Combine(loc.Lib, "libvlccore.dylib");
            var lib = Path.Combine(loc.Lib, "libvlc.dylib");
            NativeLibrary.Load(core);
            _libvlcHandle = NativeLibrary.Load(lib);
            NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, _, _) =>
                name is "libvlc" or "libvlc.dylib" && _libvlcHandle != IntPtr.Zero ? _libvlcHandle : IntPtr.Zero);
            try { Core.Initialize(loc.Lib); }
            catch (Exception e) { AppLog.W("Player", "Core.Initialize(path) reported: " + e.Message + " (using the pre-loaded libraries)"); }
        }
        else
        {
            AppLog.W("Player", "no bundled libvlc found; trying the default search");
            Core.Initialize();
        }
    }

    [DllImport("libSystem.dylib", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

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

    // ---- Installing an update ------------------------------------------------------------

    /// Why an in-app update cannot be installed right now, or null when it can.
    public static (string Title, string Message)? CheckSelfUpdate()
    {
        var bundle = BundlePath;
        if (bundle == null)
            return ("Not an app bundle", "This copy of Stream Arc TV is not running from an app bundle, so it cannot replace itself. Download the disk image from the releases page instead.");
        if (bundle.StartsWith("/Volumes/", StringComparison.Ordinal))
            return ("Move the app first", "Stream Arc TV is running from the disk image. Drag it into your Applications folder, open it from there, and update again.");
        return null;
    }

    /// <summary>
    /// Unpacks the zip (with ditto, which keeps the bundle's permissions and symbolic links), moves
    /// the running bundle aside, puts the new one in its place, and hands over to a helper that
    /// relaunches the app after this process exits. Files of a running program may be renamed on
    /// macOS, so no elevation and no waiting are needed as long as the bundle's folder is writable.
    /// </summary>
    public static async void Install(string zipFile)
    {
        var bundle = BundlePath!;
        var parent = Path.GetDirectoryName(bundle)!;
        var updatesDir = Path.GetDirectoryName(zipFile)!;
        var staged = Path.Combine(updatesDir, "staged");
        var progress = Dialogs.Progress("Installing update", "Unpacking…", null);
        string? failure = null;
        string? newApp = null;
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(staged)) Directory.Delete(staged, true);
                Directory.CreateDirectory(staged);
                var r = Run("/usr/bin/ditto", new[] { "-x", "-k", zipFile, staged }, timeoutMs: 600_000);
                if (r.Code != 0) throw new InvalidOperationException("could not unpack the package: " + r.Err.Trim());
                newApp = Directory.GetDirectories(staged, "*.app").FirstOrDefault()
                         ?? throw new InvalidOperationException("the package holds no application bundle");
                if (!Installer.Writable(parent)) throw new InvalidOperationException($"the folder {parent} is not writable by your account. Move the app to a folder you own (your home folder's Applications folder, say) and update again, or replace it by hand from the releases page.");
            });
            progress.Report(null, "Installing…");
            await Task.Run(() =>
            {
                var old = OldPath(bundle);
                if (Directory.Exists(old)) Directory.Delete(old, true);
                Directory.Move(bundle, old);
                try { Directory.Move(newApp!, bundle); }
                catch
                {
                    // Different volume: copy instead, then put the old one back on failure.
                    var r = Run("/usr/bin/ditto", new[] { newApp!, bundle }, timeoutMs: 600_000);
                    if (r.Code != 0) { try { Directory.Move(old, bundle); } catch { } throw new InvalidOperationException("could not copy the new app into place: " + r.Err.Trim()); }
                }
                Run("/usr/bin/xattr", new[] { "-dr", "com.apple.quarantine", bundle });
            });
        }
        catch (Exception e)
        {
            failure = e.Message;
            AppLog.E("Update", "install failed", e);
        }
        progress.Close();
        if (failure != null)
        {
            Dialogs.Alert("Update failed", $"The update could not be installed: {failure}\n\nApp: {bundle}\n\nYou can download the disk image from the releases page and replace the app by hand (quit it first).");
            return;
        }
        try { Directory.Delete(staged, true); } catch { }
        try { File.Delete(zipFile); } catch { }
        AppLog.I("Update", $"installed {Path.GetFileName(zipFile)} as {bundle}; restarting");
        Installer.RelaunchAfterExit($"rm -rf {Installer.Sh(OldPath(bundle))}; /usr/bin/open {Installer.Sh(bundle)}");
    }

    /// Hidden sibling folder the previous bundle is parked in until the relaunch helper removes it.
    private static string OldPath(string bundle) => Path.Combine(Path.GetDirectoryName(bundle)!, "." + Path.GetFileName(bundle) + ".old");

    /// Called at start-up: removes a bundle left behind by the previous update.
    public static void CleanLeftovers()
    {
        try
        {
            var bundle = BundlePath;
            if (bundle == null) return;
            var old = OldPath(bundle);
            if (Directory.Exists(old)) Directory.Delete(old, true);
        }
        catch { }
    }
}
