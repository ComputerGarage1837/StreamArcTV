using LibVLCSharp.Shared;
using StreamArcTV.Data;
using StreamArcTV.Util;

namespace StreamArcTV.Player;

/// LibVLC set-up shared by the player, the guide preview and multi-view.
public static class PlayerCore
{
    private static bool _initialized;
    private static readonly object Lock = new();
    private static LibVLC? _hardware, _software;

    /// The app-wide LibVLC instance (one for hardware decoding, one for software). Creating a
    /// LibVLC takes a good fraction of a second, so it is done once and never on the window's
    /// thread when <see cref="Warm"/> has run first.
    public static LibVLC Shared(bool preferSoftware = false)
    {
        lock (Lock)
        {
            if (preferSoftware) return _software ??= CreateLibVlc(true);
            return _hardware ??= CreateLibVlc(false);
        }
    }

    /// Creates the hardware-decoding instance in the background so the first playback opens at once.
    public static void Warm() => Task.Run(() =>
    {
        try { Shared(false); } catch (Exception e) { AppLog.W("Player", "LibVLC warm-up failed: " + e.Message); }
    });

    private static LibVLC CreateLibVlc(bool preferSoftware)
    {
        lock (Lock)
        {
            if (!_initialized) { Core.Initialize(); _initialized = true; }
        }
        var opts = new List<string>
        {
            "--no-video-title-show",
            "--no-osd",
            "--http-user-agent=" + XtreamApi.USER_AGENT,
            "--http-reconnect",
            "--adaptive-logic=highest",
            "--quiet",
        };
        if (preferSoftware) opts.Add("--avcodec-hw=none");
        var lib = new LibVLC(opts.ToArray());
        return lib;
    }

    /// Stops and disposes a player off the UI thread (LibVLC stop can block while a network read finishes).
    public static void DisposePlayer(MediaPlayer p)
    {
        try
        {
            var media = p.Media;
            Task.Run(() =>
            {
                try { p.Stop(); } catch { }
                try { media?.Dispose(); } catch { }
                try { p.Dispose(); } catch { }
            });
        }
        catch (Exception e) { AppLog.W("Player", "dispose: " + e.Message); }
    }
}
