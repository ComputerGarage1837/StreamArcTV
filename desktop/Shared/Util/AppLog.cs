using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Platform.Storage;

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
        I("App", $"Stream Arc TV {BuildInfo.VersionName} · {Platform.OsName} {Environment.OSVersion.Version} · " +
                 $"{RuntimeInformation.OSArchitecture} os · {RuntimeInformation.ProcessArchitecture} process · .NET {Environment.Version} · " +
                 $"{Platform.InstallDescription}");
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

    /// Writes the log to a text file the user chooses (the desktop counterpart of "Share…").
    public static async void Share(Avalonia.Controls.Window owner)
    {
        try
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export log",
                SuggestedFileName = $"streamarc-log-{BuildInfo.VersionName}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = new[] { new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" }, AppleUniformTypeIdentifiers = new[] { "public.plain-text" } } }
            });
            var path = file?.TryGetLocalPath();
            if (path == null) return;
            File.WriteAllText(path, Text());
            UI.Dialogs.Toast($"Log saved to {path}");
        }
        catch (Exception e)
        {
            UI.Dialogs.Toast($"Couldn't save the log: {e.Message}. Path: {_file}");
        }
    }

    public static async void Copy()
    {
        try
        {
            var t = Text();
            if (t.Length > 200_000) t = t[^200_000..];
            var clipboard = App.Window?.Clipboard;
            if (clipboard == null) throw new InvalidOperationException("no clipboard");
            await clipboard.SetTextAsync(t);
            UI.Dialogs.Toast("Log copied to the clipboard");
        }
        catch (Exception e)
        {
            UI.Dialogs.Toast($"Couldn't copy the log: {e.Message}");
        }
    }
}

/// Where the app keeps its files (the desktop counterpart of the Android app's private storage); see Platform.
public static class AppPaths
{
    /// Settings, accounts, transfer list, logs.
    public static string Data { get; } = Platform.DataDir;

    /// Caches (catalogues, guides, posters, timeshift chunks) that can be deleted at any time.
    public static string Cache { get; } = Platform.CacheDir;

    public static void Ensure()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Cache);
    }
}
