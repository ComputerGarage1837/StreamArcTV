using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamArcTV.Util;

/// <summary>
/// App-wide diagnostic log: appended to a file under the app's data folder and exportable from
/// Settings. Never log credentials: use <see cref="SafeUrl"/> for stream addresses.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 2L * 1024 * 1024;
    private static readonly object Lock = new();
    private static string? _file;

    public static string Dir => Path.Combine(AppPaths.Data, "logs");
    private static string OldFile => Path.Combine(Dir, "streamarc.1.log");

    public static void Init()
    {
        Directory.CreateDirectory(Dir);
        _file = Path.Combine(Dir, "streamarc.log");
        I("App", $"Stream Arc TV {BuildInfo.VersionName} · Windows {Environment.OSVersion.Version} · " +
                 $"{(Environment.Is64BitOperatingSystem ? "x64" : "x86")} os · {(Environment.Is64BitProcess ? "x64" : "x86")} process · .NET {Environment.Version}");
    }

    public static void I(string tag, string msg) => Write("I", tag, msg);
    public static void W(string tag, string msg) => Write("W", tag, msg);
    public static void E(string tag, string msg, Exception? t = null) => Write("E", tag, t != null ? msg + "\n" + t : msg);

    /// Written on the crashing thread, synchronously, before the process dies.
    public static void Crash(string tag, string msg, Exception t)
    {
        var line = $"{DateTime.Now:MM-dd HH:mm:ss.fff} E/{tag}: {msg}\n{t}\n";
        Debug.WriteLine(line);
        var f = _file;
        if (f == null) return;
        lock (Lock) { try { File.AppendAllText(f, line); } catch { } }
    }

    private static void Write(string level, string tag, string msg)
    {
        Debug.WriteLine($"{level}/{tag}: {msg}");
        var line = $"{DateTime.Now:MM-dd HH:mm:ss.fff} {level}/{tag}: {msg}\n";
        var f = _file;
        if (f == null) return;
        Task.Run(() =>
        {
            lock (Lock)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        try { File.Delete(OldFile); } catch { }
                        File.Move(f, OldFile);
                    }
                    File.AppendAllText(f, line);
                }
                catch { }
            }
        });
    }

    /// Strips the user name and password segments out of an Xtream stream address.
    public static string SafeUrl(string url) =>
        Regex.Replace(
            Regex.Replace(url, "/(live|movie|series|timeshift)/[^/]+/[^/]+/", "/$1/***/***/"),
            "(username|password)=[^&]*", "$1=***");

    public static string Text()
    {
        lock (Lock)
        {
            var f = _file;
            if (f == null) return "";
            var sb = new StringBuilder();
            try { if (File.Exists(OldFile)) sb.Append(File.ReadAllText(OldFile)); } catch { }
            try { if (File.Exists(f)) sb.Append(File.ReadAllText(f)); } catch { }
            return sb.ToString();
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            try { if (_file != null) File.Delete(_file); } catch { }
            try { File.Delete(OldFile); } catch { }
        }
    }

    /// Writes the log to a text file the user chooses (the Windows counterpart of "Share…").
    public static void Share(System.Windows.Window owner)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export log",
            FileName = $"streamarc-log-{BuildInfo.VersionName}.txt",
            Filter = "Text file (*.txt)|*.txt",
            DefaultExt = ".txt"
        };
        if (dlg.ShowDialog(owner) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, Text());
            UI.Dialogs.Toast($"Log saved to {dlg.FileName}");
        }
        catch (Exception e)
        {
            UI.Dialogs.Toast($"Couldn't save the log: {e.Message}. Path: {_file}");
        }
    }

    public static void Copy()
    {
        try
        {
            var t = Text();
            if (t.Length > 200_000) t = t[^200_000..];
            System.Windows.Clipboard.SetText(t);
            UI.Dialogs.Toast("Log copied to the clipboard");
        }
        catch (Exception e)
        {
            UI.Dialogs.Toast($"Couldn't copy the log: {e.Message}");
        }
    }
}

/// Where the app keeps its files (the Windows counterpart of the Android app's private storage).
public static class AppPaths
{
    public static string Data { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamArcTV");

    /// Caches (catalogues, guides, posters, timeshift chunks) that can be deleted at any time.
    public static string Cache { get; } = Path.Combine(Data, "cache");

    public static void Ensure()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Cache);
    }
}
