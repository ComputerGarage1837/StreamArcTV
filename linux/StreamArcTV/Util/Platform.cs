using System.Diagnostics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using StreamArcTV.UI;
using StreamArcTV.Update;

namespace StreamArcTV.Util;

/// <summary>
/// The Linux side of the desktop app: XDG folders, the system LibVLC, desktop notifications,
/// xdg-open, systemd user timers for recordings that start while the app is closed, and
/// installing updates (a portable folder is swapped in place; a .deb is handed to the package
/// installer). The macOS edition has its own copy of this class with the same members.
/// </summary>
public static class Platform
{
    /// Must never change: the update feed and the installed base use this id.
    public const string ApplicationId = "com.computergarage.streamarctv.linux";
    public const string OsName = "Linux";
    public const string FeedFile = "release/update-linux.json";
    public const string TagPrefix = "linux-v";
    public const string UpdatePackageKey = "tar";
    public const string InstallerPackageKey = "deb";
    public const string TrayName = "tray icon";
    public const string VideosLabel = "Videos";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Xdg(string variable, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(v) && Path.IsPathRooted(v) ? v : Path.Combine(Home, fallback);
    }

    /// Settings, accounts, transfer list, logs: $XDG_DATA_HOME/StreamArcTV (~/.local/share/StreamArcTV).
    public static string DataDir { get; } = Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "StreamArcTV");
    /// Caches: $XDG_CACHE_HOME/StreamArcTV (~/.cache/StreamArcTV).
    public static string CacheDir { get; } = Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), "StreamArcTV");

    private static string? _videos;

    /// The user's Videos folder (xdg-user-dir VIDEOS, else ~/Videos).
    public static string? VideosDir
    {
        get
        {
            if (_videos != null) return _videos;
            try
            {
                var r = Run("xdg-user-dir", new[] { "VIDEOS" }, timeoutMs: 5_000);
                var dir = r.Out.Trim();
                if (r.Code == 0 && dir.Length > 0 && dir != Home) return _videos = dir;
            }
            catch { }
            return _videos = Path.Combine(Home, "Videos");
        }
    }

    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// The folder the app runs from (a portable folder, or /opt/streamarctv from the .deb).
    public static string InstallDir => AppContext.BaseDirectory.TrimEnd('/');

    public static string InstallDescription => $"install folder {InstallDir}";

    /// A .deb install lives in /opt, which the user cannot write: the updater then fetches the
    /// new .deb and opens it with the package installer instead of swapping files itself.
    public static bool WantsInstallerPackage => !Installer.Writable(InstallDir);

    private static bool _libVlcMissing;

    /// Shown once on the home screen: VLC has to be installed from the distribution.
    public static (string Title, string Message)? StartupNotice => _libVlcMissing
        ? ("VLC is needed", "Stream Arc TV plays video through VLC's library, which is not installed on this computer.\n\nInstall VLC with your package manager, for example:\n  Debian / Ubuntu / Mint:  sudo apt install vlc\n  Fedora:  sudo dnf install vlc\n  Arch:  sudo pacman -S vlc\n\nThen start Stream Arc TV again.")
        : null;

    public static void Startup()
    {
        _libVlcMissing = FindLibVlc() == null;
        if (_libVlcMissing) AppLog.W("Player", "libvlc.so.5 not found; VLC must be installed");
    }

    // ---- LibVLC ----------------------------------------------------------------------

    private static IntPtr _libvlcHandle;
    private static string? _libvlcName;

    /// The system libvlc: the versioned runtime library (from the libvlc5 package) first, so the
    /// -dev package with its unversioned symlink is not required.
    private static string? FindLibVlc()
    {
        foreach (var name in new[] { "libvlc.so.5", "libvlc.so", "libvlc" })
        {
            if (NativeLibrary.TryLoad(name, out var h) && h != IntPtr.Zero) { _libvlcHandle = h; return _libvlcName = name; }
        }
        return null;
    }

    public static string LibVlcDescription => _libvlcName != null ? $"system libvlc ({_libvlcName})" : "libvlc not found (install VLC)";

    public static void InitializeLibVlc()
    {
        if (_libvlcHandle == IntPtr.Zero && FindLibVlc() == null)
            throw new InvalidOperationException("VLC is not installed. Install it with your package manager (for example: sudo apt install vlc) and start the app again.");
        NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, _, _) =>
            name is "libvlc" or "libvlc.so" && _libvlcHandle != IntPtr.Zero ? _libvlcHandle : IntPtr.Zero);
        try { Core.Initialize(); }
        catch (Exception e) { AppLog.W("Player", "Core.Initialize reported: " + e.Message + " (using the pre-loaded library)"); }
    }

    // ---- Notifications and URLs -----------------------------------------------------

    /// Posts a desktop notification through notify-send (the counterpart of the tray balloon).
    public static void Notify(string title, string text)
    {
        try { Run("notify-send", new[] { "-a", "Stream Arc TV", title, text }, wait: false); }
        catch (Exception e) { AppLog.W("Linux", "notification failed: " + e.Message); }
    }

    public static void OpenUrl(string url)
    {
        try { Run("xdg-open", new[] { url }, wait: false); } catch { }
    }

    /// Runs a command line tool; returns (exit code, stdout, stderr). Exit code -1 when it could not start.
    public static (int Code, string Out, string Err) Run(string file, IEnumerable<string> args, bool wait = true, int timeoutMs = 20_000)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = wait, RedirectStandardError = wait, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception e) { return (-1, "", e.Message); }
        if (p == null) return (-1, "", "could not start " + file);
        using (p)
        {
            if (!wait) return (0, "", "");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
            return (p.HasExited ? p.ExitCode : -1, stdout.Result, stderr.Result);
        }
    }

    // ---- systemd user timers (recordings that start while the app is closed) -------------

    private static string Unit(string id) => $"streamarctv-recording-{id}";

    /// Registers a one-shot systemd user timer that starts the app in the background at the given local time.
    public static void ScheduleLaunch(string id, DateTime at)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("no executable path");
        UnscheduleLaunch(id);
        var r = Run("systemd-run", new[]
        {
            "--user", "--collect", "--quiet", $"--unit={Unit(id)}", $"--on-calendar={at:yyyy-MM-dd HH:mm:ss}",
            $"--description=Stream Arc TV recording {id}", exe, "--tray"
        });
        if (r.Code != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Err) ? "systemd-run is not available; the recording only starts while the app is open" : r.Err.Trim());
    }

    public static void UnscheduleLaunch(string id)
    {
        try
        {
            Run("systemctl", new[] { "--user", "stop", Unit(id) + ".timer" });
            Run("systemctl", new[] { "--user", "reset-failed", Unit(id) + ".service" });
        }
        catch { }
    }

    // ---- Installing an update ------------------------------------------------------------

    /// Why an in-app update cannot be installed right now, or null when it can.
    public static (string Title, string Message)? CheckSelfUpdate() => null;

    /// <summary>
    /// A .deb is opened with the desktop's package installer. A portable folder is updated in
    /// place: the archive is unpacked into the data folder, each old file is moved aside (a running
    /// program's files may be renamed on Linux), the new files take their place, and a helper
    /// relaunches the app once this process has exited. Leftover "*.old" files go on the next start.
    /// </summary>
    public static async void Install(string package)
    {
        if (package.EndsWith(".deb", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.I("Update", $"handing {Path.GetFileName(package)} to the package installer");
            var r = Run("xdg-open", new[] { package }, wait: false);
            if (r.Code != 0)
            {
                Dialogs.Alert("Update downloaded", $"The package was saved as {package}.\n\nInstall it with:\n  sudo apt install {package}\n\nthen start Stream Arc TV again.");
                return;
            }
            Dialogs.Alert("Update downloaded", "The new package has been opened in your package installer. Finish the installation there, then quit and reopen Stream Arc TV.\n\nIf nothing opened, install it by hand:\n  sudo apt install " + package);
            return;
        }

        var installDir = InstallDir;
        var updatesDir = Path.GetDirectoryName(package)!;
        var staged = Path.Combine(updatesDir, "staged");
        var progress = Dialogs.Progress("Installing update", "Unpacking…", null);
        string? failure = null;
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(staged)) Directory.Delete(staged, true);
                Directory.CreateDirectory(staged);
                var r = Run("tar", new[] { "-xzf", package, "-C", staged }, timeoutMs: 600_000);
                if (r.Code != 0) throw new InvalidOperationException("could not unpack the package: " + r.Err.Trim());
                if (!Installer.Writable(installDir)) throw new InvalidOperationException($"the folder {installDir} is not writable by your account");
            });
            progress.Report(null, "Installing…");
            await Task.Run(() =>
            {
                // The archive holds one folder with the app inside it.
                var root = Directory.GetFiles(staged, "StreamArcTV", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                           ?? throw new InvalidOperationException("the package holds no StreamArcTV program");
                Swap(root!, installDir);
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
            Dialogs.Alert("Update failed", $"The update could not be installed: {failure}\n\nApp folder: {installDir}\n\nYou can download the archive from the releases page and unpack it over that folder by hand (quit the app first).");
            return;
        }
        try { Directory.Delete(staged, true); } catch { }
        try { File.Delete(package); } catch { }
        AppLog.I("Update", $"installed {Path.GetFileName(package)} into {installDir}; restarting");
        var exe = Environment.ProcessPath ?? Path.Combine(installDir, "StreamArcTV");
        Installer.RelaunchAfterExit($"cd {Installer.Sh(installDir)} && exec {Installer.Sh(exe)}");
    }

    /// Moves every staged file into place, renaming the existing (possibly running) file aside first.
    private static void Swap(string staged, string installDir)
    {
        foreach (var src in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(staged, src);
            var dest = Path.Combine(installDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest))
            {
                var old = dest + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch { old = dest + "." + Guid.NewGuid().ToString("N")[..6] + ".old"; }
                File.Move(dest, old);
            }
            File.Move(src, dest);
        }
    }

    /// Called at start-up: removes files left behind by the previous update.
    public static void CleanLeftovers()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(InstallDir, "*.old", SearchOption.AllDirectories)) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }
}
