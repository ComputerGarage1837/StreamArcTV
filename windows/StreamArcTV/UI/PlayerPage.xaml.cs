using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using StreamArcTV.Data;
using StreamArcTV.Player;
using StreamArcTV.Transfer;
using StreamArcTV.Util;

namespace StreamArcTV.UI;

/// <summary>
/// The player: live channels (with the LIVE / behind-live badge, skip 10 s, storage timeshift,
/// automatic reconnects), movies and episodes (resume, watched marks, auto-play next), and
/// downloaded files. Playback runs on LibVLC.
/// </summary>
public partial class PlayerPage : AppPage
{
    private const string TAG = "Player";
    private const int MAX_RETRIES = 4;
    private const long BEHIND_THRESHOLD_MS = 2_000;
    private const long SEEK_STEP_MS = 10_000;
    private const int NEXT_COUNTDOWN_S = 10;
    private const int MAX_AUTO_PLAYS = 3;

    private readonly Prefs _prefs = Prefs.Instance;
    private LibVLC? _libVlc;
    private bool _libVlcSoftware;
    private MediaPlayer? _player;
    private Media? _media;
    private TimeshiftServer? _timeshift;
    /// Byte offset into the stored stream where the current media item starts (storage buffer only).
    private long _itemFromBytes;
    private string _url;
    private string _title;
    private readonly bool _isLive;
    /// Progress key for movies, episodes and downloads; null for live TV.
    private string? _watchKey;
    private bool _resumeAsked;
    private long _lastProgressSave;

    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _retryTimer = new();
    private readonly DispatcherTimer _nextTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _retries;
    private bool _pendingRetry;

    /// Total time spent paused since the stream was (re)opened; approximates how far behind live a stream is.
    private long _pausedTotalMs;
    private long _pausedSince;
    private bool _behindNow;

    // Diagnostics shown under the spinner while buffering.
    private long _bytesLoaded;
    private int _loadsStarted;
    private string? _lastLoadError;
    private long _bufferingSince;
    private string? _videoInfo;
    private string? _decoderName;
    private bool _firstFrame;
    private int _nudges;
    private float _cachePct;
    private bool _buffering;
    private bool _userPaused;
    private bool _ended;
    /// Set when the hardware decoder produced nothing: the player is rebuilt preferring software decoders.
    private bool _preferSoftware;
    /// Episodes started automatically in a row; after a few we ask "Still watching?".
    private int _autoPlays;
    private bool _nextOffered;
    private int _nextLeft;
    private Action? _nextAction;
    private long _startPositionMs;
    private long _lengthMs;
    private long _timeMs;
    private bool _seekDragging;
    private bool _controlsVisible = true;
    private bool _dead;

    public PlayerPage(string url, string title, bool live, string? watchKey = null)
    {
        InitializeComponent();
        _url = url; _title = title; _isLive = live;
        _watchKey = live ? null : watchKey;
        AppLog.I(TAG, $"open live={live} title='{title}' url={AppLog.SafeUrl(url)}");
        TxtTitle.Text = title;

        TxtLiveStatus.Click += (_, _) => GoLive();
        BtnSeekBack.Click += (_, _) => SeekBy(-SEEK_STEP_MS);
        BtnSeekFwd.Click += (_, _) => SeekBy(SEEK_STEP_MS);
        BtnRetry.Click += (_, _) => { _retries = 0; _nudges = 0; ReleasePlayer(); InitPlayer(); };
        BtnNextCancel.Click += (_, _) => HideNext();
        BtnPlay.Click += (_, _) => TogglePause();
        BtnCc.Click += (_, _) => PickSubtitles();
        BtnFull.Click += (_, _) => App.Window?.SetFullScreen(!App.Window.IsFullScreen);
        BtnClose.Click += (_, _) => Finish();
        Seek.PreviewMouseDown += (_, _) => _seekDragging = true;
        Seek.PreviewMouseUp += (_, _) => { _seekDragging = false; SeekTo((long)(Seek.Value / 1000.0 * Math.Max(1, _lengthMs))); };
        Seek.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Left) { SeekBy(-SEEK_STEP_MS); e.Handled = true; }
            else if (e.Key == Key.Right) { SeekBy(SEEK_STEP_MS); e.Handled = true; }
        };

        Overlay.MouseMove += (_, _) => ShowControls();
        Overlay.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) App.Window?.SetFullScreen(!App.Window.IsFullScreen); else if (!_controlsVisible) ShowControls(); else if (e.OriginalSource == Overlay) TogglePause(); };
        Overlay.PreviewKeyDown += OnOverlayKey;
        _ticker.Tick += (_, _) => UpdateStatus();
        _controlsTimer.Tick += (_, _) => { _controlsTimer.Stop(); HideControls(); };
        _nextTimer.Tick += (_, _) => NextTick();
        _retryTimer.Tick += (_, _) => { _retryTimer.Stop(); _pendingRetry = false; ReleasePlayer(); InitPlayer(); };
        SetControlsVisible(true);
    }

    public override IInputElement InitialFocus => BtnPlay;

    public override void OnResume()
    {
        if (_dead) return;
        TransferService.PlaybackActive = true;
        _nudges = 0;
        var key = _watchKey;
        var saved = key != null && !_resumeAsked ? WatchProgress.ResumePosition(key) : 0;
        if (saved > 0) AskResume(saved); else InitPlayer();
        _ticker.Start();
        ShowControls();
    }

    /// Asked before the stream is opened, so the player itself never waits on the answer.
    private void AskResume(long pos)
    {
        _resumeAsked = true;
        var r = Dialogs.Alert(string.IsNullOrWhiteSpace(_title) ? "Resume?" : _title, $"You stopped at {Clock(pos)} last time.", $"Resume from {Clock(pos)}", "Start over");
        _startPositionMs = r == DialogResultKind.Negative ? 0 : pos;
        InitPlayer();
    }

    public override void OnPause()
    {
        TransferService.PlaybackActive = false;
        SaveProgress(force: true);
        _ticker.Stop();
        HideNext();
        CancelRetry();
        ReleasePlayer();
        DeleteIfWatchedDownload();
    }

    public override void OnDestroy()
    {
        _dead = true;
        if (_libVlc != null) _libVlc.Log -= OnVlcLog;
        _libVlc = null;
    }

    public override bool OnKey(KeyEventArgs e)
    {
        // Keys arriving through the main window (focus outside the video overlay).
        return HandleKey(e);
    }

    private void OnOverlayKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.BrowserBack) { e.Handled = true; if (App.Window is { IsFullScreen: true } && e.Key == Key.Escape && !_controlsVisible) { App.Window.SetFullScreen(false); return; } Finish(); return; }
        if (e.Key == Key.F11) { App.Window?.SetFullScreen(!App.Window.IsFullScreen); e.Handled = true; return; }
        if (HandleKey(e)) e.Handled = true;
    }

    private bool HandleKey(KeyEventArgs e)
    {
        _autoPlays = 0;   // someone is definitely watching
        switch (e.Key)
        {
            case Key.MediaPlayPause: TogglePause(); return true;
            case Key.MediaStop: Finish(); return true;
            case Key.Space: if (Keyboard.FocusedElement is not System.Windows.Controls.Primitives.ButtonBase) { TogglePause(); return true; } return false;
            case Key.F: App.Window?.SetFullScreen(!App.Window.IsFullScreen); return true;
            case Key.Left: if (!_controlsVisible) { SeekBy(-SEEK_STEP_MS); return true; } break;
            case Key.Right: if (!_controlsVisible) { SeekBy(SEEK_STEP_MS); return true; } break;
            case Key.Up: case Key.Down: if (!_controlsVisible) { ShowControls(); return true; } break;
            case Key.Enter: if (!_controlsVisible) { ShowControls(); return true; } break;
        }
        if (_controlsVisible) ShowControls();   // restart the auto-hide timer
        return false;
    }

    // ---- Controls visibility -----------------------------------------------------

    private void ShowControls()
    {
        SetControlsVisible(true);
        _controlsTimer.Stop();
        _controlsTimer.Start();
    }

    private void HideControls()
    {
        if (NextBox.Visibility == Visibility.Visible || ErrorBox.Visibility == Visibility.Visible) return;
        SetControlsVisible(false);
        if (Keyboard.FocusedElement is FrameworkElement fe && !fe.IsVisible || Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Slider)
            Overlay.Focus();
    }

    private void SetControlsVisible(bool on)
    {
        _controlsVisible = on;
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        TitleBar.Visibility = v;
        Controller.Visibility = v;
        TxtLiveStatus.Visibility = _isLive ? v : Visibility.Collapsed;
        BtnSeekBack.Visibility = v;
        BtnSeekFwd.Visibility = v;
        Overlay.Cursor = on ? Cursors.Arrow : Cursors.None;
    }

    // ---- Auto-play next episode ----------------------------------------------------

    private async void OfferNext()
    {
        var key = _watchKey;
        if (key == null || _nextOffered || !_prefs.AutoPlayNext || !key.StartsWith("ep:")) return;
        var entry = WatchProgress.Get(key);
        var seriesId = entry?.SeriesId;
        if (entry == null || seriesId == null) return;
        var account = _prefs.Account(Service.VOD);
        if (account == null) return;
        _nextOffered = true;
        List<Episode> eps;
        try { eps = SeriesCache.Ordered(await SeriesCache.Episodes(Service.VOD, account, seriesId)); } catch { return; }
        var idx = eps.FindIndex(e => e.Season == entry.Season && e.Number == entry.Episode);
        var after = eps.Skip(idx + 1).ToList();
        var next = after.FirstOrDefault(e => !WatchProgress.IsWatched(WatchProgress.EpisodeKey(e.Id))) ?? after.FirstOrDefault();
        if (next == null || _player == null) return;
        ShowNext(next, entry.Title ?? "", entry.Image, seriesId, account);
    }

    private void ShowNext(Episode next, string seriesTitle, string? image, string seriesId, Account account)
    {
        TxtNextTitle.Text = $"Up next: S{next.Season} E{next.Number} · {next.Title}";
        NextBox.Visibility = Visibility.Visible;
        _nextAction = () => PlayNext(next, seriesTitle, image, seriesId, account);
        BtnNextPlay.Click -= NextPlayClick; BtnNextPlay.Click += NextPlayClick;
        ShowControls();
        BtnNextPlay.Focus();
        if (_autoPlays >= MAX_AUTO_PLAYS)
        {
            // Three in a row without a hand on the remote: wait for a press.
            TxtNextCount.Text = "Still watching?";
            return;
        }
        _nextLeft = NEXT_COUNTDOWN_S;
        TxtNextCount.Text = $"Playing in {_nextLeft} s";
        _nextTimer.Start();
    }

    private void NextPlayClick(object sender, RoutedEventArgs e) { var a = _nextAction; HideNext(); a?.Invoke(); }

    private void NextTick()
    {
        if (_nextLeft <= 0) { _nextTimer.Stop(); var a = _nextAction; HideNext(); a?.Invoke(); return; }
        _nextLeft--;
        TxtNextCount.Text = $"Playing in {_nextLeft} s";
    }

    private void HideNext()
    {
        _nextTimer.Stop();
        NextBox.Visibility = Visibility.Collapsed;
        _nextAction = null;
    }

    private void PlayNext(Episode next, string seriesTitle, string? image, string seriesId, Account account)
    {
        string nextUrl;
        try { nextUrl = XtreamApi.EpisodeUrl(Service.VOD, account, next); }
        catch (Exception e) { Dialogs.Toast(e.Message); return; }
        _autoPlays++;
        var key = WatchProgress.EpisodeKey(next.Id);
        WatchProgress.Describe(key, WatchProgress.KIND_EPISODE, seriesTitle, image, next.ContainerExtension, next.Id,
            subtitle: next.Title, seriesId: seriesId, season: next.Season, episode: next.Number);
        AppLog.I(TAG, $"auto-play next S{next.Season}E{next.Number} ({_autoPlays} in a row)");
        SaveProgress(force: true);
        _url = nextUrl;
        _title = $"{seriesTitle} · S{next.Season}E{next.Number} {next.Title}";
        TxtTitle.Text = _title;
        _watchKey = key;
        _nextOffered = false;
        _resumeAsked = true;
        _startPositionMs = WatchProgress.ResumePosition(key);
        _retries = 0; _nudges = 0;
        ReleasePlayer();
        InitPlayer();
    }

    /// Settings → "Delete downloads after watching": a finished download goes once the player closes.
    private void DeleteIfWatchedDownload()
    {
        var key = _watchKey;
        if (key == null || !key.StartsWith("dl:") || !_prefs.DeleteAfterWatched || !WatchProgress.IsWatched(key)) return;
        var store = TransferStore.Get();
        var job = store.Get(key["dl:".Length..]);
        if (job == null) return;
        Folders.Delete(job.FileUri);
        store.Remove(job.Id);
        WatchProgress.Remove(key);
        Dialogs.Toast($"Deleted {job.Title} (watched).");
    }

    // ---- Player set-up ---------------------------------------------------------

    private void InitPlayer()
    {
        if (_player != null || _dead) return;
        ErrorBox.Visibility = Visibility.Collapsed;
        BtnRetry.Content = "Retry";
        _pausedTotalMs = 0; _pausedSince = 0; _behindNow = false; _itemFromBytes = 0;
        _userPaused = false; _ended = false; _lengthMs = 0; _timeMs = 0;

        // Storage levels only make sense for live channels; movies and series get the biggest
        // in-memory buffer instead, since they can already be scrubbed freely.
        var chosen = BufferLevel.From(_prefs.BufferLevelKey);
        var level = chosen.OnDisk && !_isLive ? BufferLevel.MAX : chosen;
        AppLog.I(TAG, $"initPlayer level={level.Key} preferSoftware={_preferSoftware} start={_startPositionMs}ms");

        try
        {
            if (_libVlc == null || _libVlcSoftware != _preferSoftware)
            {
                if (_libVlc != null) _libVlc.Log -= OnVlcLog;
                _libVlc = PlayerCore.Shared(_preferSoftware);
                _libVlcSoftware = _preferSoftware;
                _libVlc.Log += OnVlcLog;
            }
            var p = new MediaPlayer(_libVlc) { EnableHardwareDecoding = !_preferSoftware };
            _player = p;
            _bytesLoaded = 0; _loadsStarted = 0; _lastLoadError = null; _bufferingSince = Ui.Elapsed;
            _videoInfo = null; _decoderName = null; _firstFrame = false; _cachePct = 0; _buffering = true;
            p.Buffering += (_, e) => Ui.Post(() => OnBuffering(e.Cache));
            p.Playing += (_, _) => Ui.Post(OnPlaying);
            p.Paused += (_, _) => Ui.Post(() => { AppLog.I(TAG, "state=PAUSED"); if (_pausedSince == 0) _pausedSince = Ui.Elapsed; UpdateStatus(); });
            p.EndReached += (_, _) => Ui.Post(OnEnded);
            p.EncounteredError += (_, _) => Ui.Post(HandleError);
            p.TimeChanged += (_, e) => { _timeMs = e.Time; };
            p.LengthChanged += (_, e) => { _lengthMs = e.Length; };
            p.Vout += (_, e) => Ui.Post(() => { if (e.Count > 0 && !_firstFrame) { _firstFrame = true; AppLog.I(TAG, "first frame rendered"); } });
            p.ESAdded += (_, e) => Ui.Post(() => OnTrackAdded(e.Type));
            p.Opening += (_, _) => Ui.Post(() => { _loadsStarted++; });
            Video.MediaPlayer = p;

            var playUrl = _url;
            if (_isLive && level.OnDisk)
            {
                // Storage timeshift works on the raw MPEG-TS stream; swap an HLS address for it.
                var tsUrl = Regex.Replace(_url, @"\.m3u8$", ".ts");
                _timeshift = new TimeshiftServer(tsUrl, level.DiskMinutes * 60_000L);
                playUrl = _timeshift.LocalUrl;
            }
            var media = File.Exists(playUrl) ? new Media(_libVlc, playUrl, FromType.FromPath) : new Media(_libVlc, new Uri(playUrl));
            // Playback starts as soon as ~1.5 s is ready; the level only changes how far ahead the prefetcher keeps downloading.
            media.AddOption(":network-caching=1500");
            media.AddOption(":live-caching=1500");
            media.AddOption(":file-caching=1000");
            media.AddOption($":prefetch-buffer-size={level.Bytes / 1024}");
            media.AddOption(":prefetch-read-size=1048576");
            media.AddOption(":http-user-agent=" + XtreamApi.USER_AGENT);
            media.AddOption(":http-reconnect");
            if (_isLive && !level.OnDisk) media.AddOption(":clock-jitter=0");
            if (_startPositionMs > 0) media.AddOption($":start-time={_startPositionMs / 1000.0:0.###}");
            _startPositionMs = 0;
            _media = media;
            BufferBox.Visibility = Visibility.Visible;
            TxtBuffer.Text = "Buffering…";
            p.Play(media);
            SetPlayIcon(true);
        }
        catch (Exception e)
        {
            AppLog.E(TAG, "initPlayer failed", e);
            ShowFailure($"Playback failed: {e.Message}");
        }
    }

    private void OnVlcLog(object? sender, LogEventArgs e)
    {
        if (e.Level == LogLevel.Debug) return;
        var msg = e.Message;
        if (e.Level == LogLevel.Error)
        {
            _lastLoadError = DescribeVlcError(msg);
            AppLog.E(TAG, $"vlc {e.Module}: {AppLog.SafeUrl(msg)}");
        }
        else if (e.Level == LogLevel.Warning && (msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) || msg.Contains("HTTP", StringComparison.Ordinal)))
        {
            AppLog.W(TAG, $"vlc {e.Module}: {AppLog.SafeUrl(msg)}");
        }
        var hw = Regex.Match(msg, @"Using (.+?) for hardware decoding", RegexOptions.IgnoreCase);
        if (hw.Success) _decoderName = hw.Groups[1].Value.Trim();
    }

    /// Human words for an HTTP failure reported by LibVLC, or null if it isn't one.
    private static string? DescribeVlcError(string msg)
    {
        var m = Regex.Match(msg, @"HTTP/\d\.\d\s+(\d{3})|status\s*(\d{3})|\b(4\d\d|5\d\d)\b");
        if (m.Success)
        {
            var code = int.Parse(m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value);
            return code switch
            {
                403 or 429 or 458 or 509 => $"The provider refused this stream (HTTP {code}): too many streams open on this account. Close other players or wait for a download to finish, then retry.",
                404 => $"The provider has no file for this title (HTTP {code}).",
                _ => $"Provider error (HTTP {code})."
            };
        }
        if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "The provider stopped sending data (timed out).";
        if (msg.Contains("resolve", StringComparison.OrdinalIgnoreCase) || msg.Contains("unknown host", StringComparison.OrdinalIgnoreCase)) return "Can't find the provider's server (no internet or wrong address).";
        if (msg.Contains("connection", StringComparison.OrdinalIgnoreCase) && msg.Contains("fail", StringComparison.OrdinalIgnoreCase)) return "Can't connect to the provider's server.";
        return null;
    }

    private void OnBuffering(float cache)
    {
        _cachePct = cache;
        if (cache < 100f)
        {
            if (!_buffering) { _buffering = true; _bufferingSince = Ui.Elapsed; }
            BufferBox.Visibility = Visibility.Visible;
        }
        else
        {
            _buffering = false;
            BufferBox.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPlaying()
    {
        AppLog.I(TAG, $"state=PLAYING pos={_timeMs} len={_lengthMs}");
        _retries = 0;
        _buffering = false;
        BufferBox.Visibility = Visibility.Collapsed;
        if (_pausedSince != 0) { _pausedTotalMs += Ui.Elapsed - _pausedSince; _pausedSince = 0; }
        SetPlayIcon(true);
        ApplySubtitlePreference();
        LogTracks();
        UpdateStatus();
    }

    private void OnEnded()
    {
        AppLog.I(TAG, "state=ENDED");
        _ended = true;
        BufferBox.Visibility = Visibility.Collapsed;
        SetPlayIcon(false);
        if (_isLive)
        {
            // A live stream that "ends" was cut by the provider: reconnect like a network error.
            HandleError();
            return;
        }
        _watchKey?.Let(WatchProgress.Ended);
        OfferNext();
    }

    private bool _tracksLogged;

    private void LogTracks()
    {
        if (_tracksLogged || _media == null) return;
        try
        {
            var tracks = _media.Tracks;
            if (tracks.Length == 0) return;
            _tracksLogged = true;
            foreach (var t in tracks)
            {
                if (t.TrackType == TrackType.Video)
                {
                    _videoInfo = $"{CodecName(t.Codec)} {t.Data.Video.Width}x{t.Data.Video.Height}";
                    AppLog.I(TAG, $"video format {_videoInfo} fps={t.Data.Video.FrameRateNum}/{t.Data.Video.FrameRateDen} bitrate={t.Bitrate}");
                }
                else if (t.TrackType == TrackType.Audio)
                    AppLog.I(TAG, $"audio format {CodecName(t.Codec)} ch={t.Data.Audio.Channels} rate={t.Data.Audio.Rate}");
            }
        }
        catch { }
    }

    private static string CodecName(uint fourcc)
    {
        var s = new string(new[] { (char)(fourcc & 0xFF), (char)((fourcc >> 8) & 0xFF), (char)((fourcc >> 16) & 0xFF), (char)((fourcc >> 24) & 0xFF) }).Trim();
        return s.ToLowerInvariant() switch
        {
            "h264" or "avc1" => "H.264",
            "hevc" or "hvc1" or "hev1" or "h265" => "HEVC (H.265)",
            "av01" => "AV1",
            "vp90" => "VP9",
            "eac3" or "ec-3" => "Dolby Digital Plus (E-AC3)",
            "a52" or "ac-3" => "Dolby Digital (AC3)",
            "mp4a" or "aac" => "AAC",
            "mpga" or "mp3" => "MP3",
            _ => s
        };
    }

    private void OnTrackAdded(TrackType type)
    {
        if (type == TrackType.Text) ApplySubtitlePreference();
    }

    /// Subtitles follow the saved preference; the CC button in the controls changes and remembers it.
    private void ApplySubtitlePreference()
    {
        var p = _player;
        if (p == null) return;
        try
        {
            if (!_prefs.Subtitles) { if (p.Spu != -1) p.SetSpu(-1); }
        }
        catch { }
    }

    private void PickSubtitles()
    {
        var p = _player;
        if (p == null) return;
        TrackDescription[] tracks;
        try { tracks = p.SpuDescription; } catch { tracks = Array.Empty<TrackDescription>(); }
        var real = tracks.Where(t => t.Id != -1).ToList();
        if (real.Count == 0) { Dialogs.Toast("This title has no subtitle tracks."); ShowControls(); return; }
        var labels = new List<string> { "Off" };
        labels.AddRange(real.Select(t => string.IsNullOrWhiteSpace(t.Name) ? $"Track {t.Id}" : t.Name));
        var current = p.Spu == -1 ? 0 : real.FindIndex(t => t.Id == p.Spu) + 1;
        var which = Dialogs.SingleChoice("Subtitles", labels, Math.Max(0, current));
        if (which < 0) { ShowControls(); return; }
        try
        {
            if (which == 0) { p.SetSpu(-1); _prefs.Subtitles = false; }
            else { p.SetSpu(real[which - 1].Id); _prefs.Subtitles = true; }
        }
        catch { }
        ShowControls();
    }

    // ---- Watchdog, progress, release -----------------------------------------------

    /// A player that has plenty buffered but never becomes ready is stuck in the decoder, not the
    /// network. Nudge it with a seek, then a fresh open, then give up with a clear message.
    private void Watchdog(MediaPlayer p)
    {
        if (!_buffering && p.IsPlaying) return;
        if (_userPaused || _ended || ErrorBox.Visibility == Visibility.Visible) return;
        var waited = Ui.Elapsed - _bufferingSince;
        if (_cachePct < 100f || waited < 12_000) return;
        AppLog.W(TAG, $"watchdog stage={_nudges} cache={_cachePct} waited={waited}ms firstFrame={_firstFrame} decoder={_decoderName}");
        switch (_nudges)
        {
            case 0:
                _nudges = 1; _bufferingSince = Ui.Elapsed;
                try { if (p.IsSeekable) p.Time = Math.Max(0, _timeMs); else p.SetPause(false); } catch { }
                break;
            case 1:
                _nudges = 2; _bufferingSince = Ui.Elapsed;
                var pos = _timeMs;
                ReleasePlayer();
                _startPositionMs = pos;
                InitPlayer();
                break;
            case 2:
                if (!_firstFrame && !_preferSoftware)
                {
                    // Data is there but no frame has ever been drawn: try the software decoder.
                    _nudges = 3;
                    _preferSoftware = true;
                    var pos2 = _timeMs;
                    ReleasePlayer();
                    _startPositionMs = pos2;
                    InitPlayer();
                    _bufferingSince = Ui.Elapsed;
                }
                else goto case 3;
                break;
            case 3:
                _nudges = 4;
                try { p.SetPause(true); } catch { }
                BufferBox.Visibility = Visibility.Collapsed;
                ShowFailure($"The video is downloaded but this device's decoder is not producing a picture ({_videoInfo ?? "?"} via {_decoderName ?? "?"}). Try again, or pick a different quality/format of this title if the provider offers one.");
                break;
        }
    }

    private void SaveProgress(bool force = false)
    {
        var key = _watchKey;
        var p = _player;
        if (key == null || p == null) return;
        var now = Ui.Elapsed;
        if (!force && now - _lastProgressSave < 5_000) return;
        _lastProgressSave = now;
        var duration = _lengthMs;
        var pos = _timeMs;
        if (duration <= 0 || pos <= 0) return;
        WatchProgress.Save(key, pos, duration);
    }

    private void ReleasePlayer()
    {
        var p = _player;
        if (p != null) AppLog.I(TAG, $"release pos={_timeMs}");
        _player = null;
        Video.MediaPlayer = null;
        if (p != null) PlayerCore.DisposePlayer(p);
        _media = null;
        _tracksLogged = false;
        _timeshift?.Close();
        _timeshift = null;
    }

    // ---- Transport ---------------------------------------------------------------

    private void TogglePause()
    {
        var p = _player;
        if (p == null) { if (ErrorBox.Visibility != Visibility.Visible) InitPlayer(); return; }
        if (_ended && !_isLive) { _startPositionMs = 0; ReleasePlayer(); InitPlayer(); return; }
        try
        {
            if (p.IsPlaying) { p.SetPause(true); _userPaused = true; if (_pausedSince == 0) _pausedSince = Ui.Elapsed; SetPlayIcon(false); }
            else { p.SetPause(false); _userPaused = false; SetPlayIcon(true); }
        }
        catch { }
        ShowControls();
        UpdateStatus();
    }

    private void SetPlayIcon(bool playing) => IconPlayPause.Data = Ui.Res<Geometry>(playing ? "IconPause" : "IconPlay");

    private void SeekTo(long ms)
    {
        var p = _player;
        if (p == null) return;
        try { if (p.IsSeekable) p.Time = Math.Clamp(ms, 0, Math.Max(0, _lengthMs)); } catch { }
        ShowControls();
    }

    private void SeekBy(long deltaMs)
    {
        var p = _player;
        if (p == null) return;
        ShowControls();
        var ts = _timeshift;
        if (ts != null)
        {
            var rate = ts.RateBytesPerMs();
            if (rate <= 0.0) return;
            var posBytes = _itemFromBytes + (long)(_timeMs * rate);
            var target = Math.Clamp(posBytes + (long)(deltaMs * rate), ts.BaseBytes(), Math.Max(ts.BaseBytes(), ts.WrittenBytes() - (long)(rate * 1500)));
            if (target == posBytes) return;
            var wasPlaying = !_userPaused;
            _itemFromBytes = target;
            OpenLocal(ts.UrlFrom(target), wasPlaying);
        }
        else if (p.IsSeekable && _lengthMs > 0)
        {
            var target = Math.Clamp(_timeMs + deltaMs, 0, _lengthMs);
            try { p.Time = target; } catch { }
        }
        else if (p.IsSeekable)
        {
            try { p.Time = Math.Max(0, _timeMs + deltaMs); } catch { }
        }
        else if (_isLive)
        {
            Dialogs.Toast("Skipping back and forward on an MPEG-TS channel needs a stored buffer: Settings → Playback → Playback buffer.");
        }
        UpdateStatus();
    }

    /// Re-opens the timeshift stream from another byte offset without rebuilding the player.
    private void OpenLocal(string url, bool play)
    {
        var p = _player; var lib = _libVlc;
        if (p == null || lib == null) return;
        try
        {
            var old = _media;
            var media = new Media(lib, new Uri(url));
            media.AddOption(":network-caching=1500");
            _media = media;
            p.Play(media);
            if (!play) p.SetPause(true);
            old?.Dispose();
            _timeMs = 0;
        }
        catch (Exception e) { AppLog.E(TAG, "reopen failed", e); }
    }

    private void GoLive()
    {
        var p = _player;
        if (p == null) return;
        _pausedTotalMs = 0; _pausedSince = 0;
        var ts = _timeshift;
        if (ts != null)
        {
            _itemFromBytes = Math.Max(ts.WrittenBytes() - 512L * 1024, ts.BaseBytes());
            _userPaused = false;
            OpenLocal(ts.UrlLive(), true);
            SetPlayIcon(true);
        }
        else
        {
            // Reopen the stream at the live point.
            _retries = 0;
            ReleasePlayer();
            InitPlayer();
        }
        ShowControls();
    }

    // ---- Errors & reconnects ---------------------------------------------------

    private void HandleError()
    {
        var why = _lastLoadError;
        AppLog.E(TAG, $"player error: {why ?? "unknown"}");
        var providerRefused = why != null && why.Contains("HTTP", StringComparison.Ordinal) && !why.Contains("timed out", StringComparison.Ordinal);
        if (_isLive && !providerRefused && _retries < MAX_RETRIES)
        {
            _retries++;
            var delay = 1000 * _retries;
            BufferBox.Visibility = Visibility.Visible;
            TxtBuffer.Text = $"Reconnecting… ({_retries} of {MAX_RETRIES})";
            _pendingRetry = true;
            _retryTimer.Interval = TimeSpan.FromMilliseconds(delay);
            _retryTimer.Stop();
            _retryTimer.Start();
            return;
        }
        BufferBox.Visibility = Visibility.Collapsed;
        ShowFailure(why ?? "Playback failed: the stream could not be opened.");
    }

    private void ShowFailure(string text)
    {
        TxtError.Text = text;
        ErrorBox.Visibility = Visibility.Visible;
        SetControlsVisible(true);
        _controlsTimer.Stop();
        BtnRetry.Focus();
    }

    private void CancelRetry()
    {
        _retryTimer.Stop();
        _pendingRetry = false;
    }

    // ---- Status line -----------------------------------------------------------

    private void UpdateStatus()
    {
        var p = _player;
        if (p == null) return;
        if (!_isLive) SaveProgress();
        Watchdog(p);
        try
        {
            if (_media != null)
            {
                var st = _media.Statistics;
                _bytesLoaded = st.ReadBytes;
            }
        }
        catch { }
        var isBuffering = (_buffering || (!p.IsPlaying && !_userPaused && !_ended)) && ErrorBox.Visibility != Visibility.Visible;
        if (isBuffering && !_pendingRetry)
        {
            BufferBox.Visibility = Visibility.Visible;
            var waited = (Ui.Elapsed - _bufferingSince) / 1000;
            var head = _cachePct > 0 && _cachePct < 100 ? $"Buffering… {(int)_cachePct}%" : "Buffering…";
            var detail = waited >= 3
                ? "\n" + $"{Format.Size(_bytesLoaded)} received · waiting {waited} s {(_loadsStarted == 0 ? "connecting…" : "")}".TrimEnd() +
                  (waited >= 6 ? "\n" + $"Video {_videoInfo ?? "?"} · decoder {_decoderName ?? "?"} · {(_firstFrame ? "picture OK" : "no picture yet")}" : "") +
                  (_lastLoadError != null ? "\n" + _lastLoadError : "")
                : "";
            TxtBuffer.Text = head + detail;
        }
        else if (!_pendingRetry) BufferBox.Visibility = Visibility.Collapsed;

        // Controller
        var len = _lengthMs;
        var time = _timeMs;
        if (!_seekDragging)
        {
            if (len > 0) { Seek.IsEnabled = true; Seek.Value = Math.Clamp(time * 1000.0 / len, 0, 1000); }
            else { Seek.IsEnabled = false; Seek.Value = 0; }
        }
        TxtTime.Text = len > 0 ? $"{Clock(time)} / {Clock(len)}" : (_isLive ? Clock(time) : "");

        if (!_isLive) return;
        var behindMs = BehindLiveMs(p);
        var paused = _userPaused;
        _behindNow = paused || behindMs >= BEHIND_THRESHOLD_MS;
        TxtLiveStatus.Content = paused ? $"❚❚ Paused · {Clock(behindMs)} behind live"
            : _behindNow ? $"{Clock(behindMs)} behind live · select to go live"
            : "● LIVE";
        Ui.SetSelected(TxtLiveStatus, _behindNow);
    }

    private long BehindLiveMs(MediaPlayer p)
    {
        var paused = _pausedSince != 0 ? Ui.Elapsed - _pausedSince : 0;
        var pausedEstimate = _pausedTotalMs + paused;
        var ts = _timeshift;
        if (ts != null)
        {
            if (ts.Jumped)
            {
                ts.Jumped = false;
                Dialogs.Toast("Paused longer than the stored buffer holds; skipping ahead to the oldest video kept.");
            }
            var rate = ts.RateBytesPerMs();
            if (rate <= 0.0) return pausedEstimate;
            var posBytes = _itemFromBytes + (long)(_timeMs * rate);
            return Math.Max(0, (long)((ts.WrittenBytes() - posBytes) / rate));
        }
        return pausedEstimate;
    }

    private static string Clock(long ms)
    {
        var s = Math.Max(0, ms) / 1000;
        return s >= 3600 ? $"{s / 3600}:{(s % 3600) / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
}
