using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheChocolateRabbit.Util;

namespace TheChocolateRabbit.Data;

/// <summary>
/// User settings, kept in a JSON file under %LocalAppData%\TheChocolateRabbit.
/// </summary>
public sealed class Prefs
{
    private static readonly object Lock = new();
    private static JsonObject? _root;
    private static string File => Path.Combine(AppPaths.Data, "prefs.json");

    public static Prefs Instance { get; } = new();

    private static JsonObject Root
    {
        get
        {
            lock (Lock)
            {
                if (_root != null) return _root;
                try
                {
                    if (System.IO.File.Exists(File))
                        _root = JsonNode.Parse(System.IO.File.ReadAllText(File)) as JsonObject;
                }
                catch { }
                return _root ??= new JsonObject();
            }
        }
    }

    private static bool _dirty;
    private static bool _flushPending;

    /// Marks the file for writing; the write happens a moment later on a worker thread.
    private static void Save()
    {
        lock (Lock)
        {
            _dirty = true;
            if (_flushPending) return;
            _flushPending = true;
        }
        Task.Run(async () => { await Task.Delay(300).ConfigureAwait(false); Flush(); });
    }

    /// Writes pending changes now (called on exit and before a crash is reported).
    public static void Flush()
    {
        string json;
        lock (Lock)
        {
            _flushPending = false;
            if (!_dirty) return;
            _dirty = false;
            json = Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        try
        {
            AppPaths.Ensure();
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Move(tmp, File, true);
        }
        catch { }
    }

    private static string? GetString(string key, string? def = null)
    {
        lock (Lock) { return Root[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : def; }
    }

    private static void PutString(string key, string? value)
    {
        lock (Lock) { if (value == null) Root.Remove(key); else Root[key] = value; }
        Save();
    }

    private static bool GetBool(string key, bool def)
    {
        lock (Lock) { return Root[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def; }
    }

    private static void PutBool(string key, bool value) { lock (Lock) { Root[key] = value; } Save(); }

    /// Version name the user chose to skip in the update prompt.
    public string? SkippedVersion
    {
        get => GetString("skipped_version");
        set => PutString("skipped_version", value);
    }

    /// Check the release feed once per launch.
    public bool AutoCheckUpdates
    {
        get => GetBool("auto_check_updates", true);
        set => PutBool("auto_check_updates", value);
    }

    /// Set when the previous run ended in a crash; cleared once reported.
    public bool Crashed
    {
        get => GetBool("crashed", false);
        set => PutBool("crashed", value);
    }

    /// "left,top,width,height,max|normal" of the main window.
    public string? WindowPlacement
    {
        get => GetString("window_placement");
        set => PutString("window_placement", value);
    }
}
