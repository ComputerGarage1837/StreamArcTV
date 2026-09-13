using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using StreamArcTV.Data;
using StreamArcTV.Util;

namespace StreamArcTV.Player;

/// <summary>
/// Storage-backed timeshift for a live MPEG-TS stream.
///
/// Keeps downloading the channel into chunk files in the app cache no matter what the player
/// does, and serves those bytes back to the player over a loopback HTTP connection at whatever
/// pace it reads. Pausing the player therefore never stops the download, and resuming carries on
/// from the same byte. Chunks older than the window (or the oldest ones when storage runs low)
/// are deleted; a reader that has fallen off the back of the window jumps forward to the oldest
/// data that is left.
/// </summary>
public sealed class TimeshiftServer
{
    private const string TAG = "Timeshift";
    private const string DIR = "timeshift";
    private const long CHUNK_BYTES = 8L * 1024 * 1024;
    private const long MIN_FREE_BYTES = 1L * 1024 * 1024 * 1024;   // keep at least 1 GB free
    private const long MAX_TOTAL_BYTES = 8L * 1024 * 1024 * 1024;  // hard ceiling per channel
    private const long GIVE_UP_MS = 90_000L;
    private const long LIVE_HEADROOM = 512L * 1024;

    private sealed class Chunk
    {
        public long Start;
        public string File = "";
        public long CreatedAt;
    }

    private readonly string _upstream;
    private readonly long _windowMs;
    private readonly string _dir;
    private readonly TcpListener _listener;
    private readonly object _lock = new();
    private readonly List<Chunk> _chunks = new();          // oldest first
    private long _written;                                  // absolute end of the data
    private long _base;                                     // absolute start of the oldest chunk
    private long _firstByteAt;
    private volatile bool _closed;
    private volatile bool _failed;
    private CancellationTokenSource _cts = new();

    /// Plays from the start of what has been stored (the point the channel was opened).
    public string LocalUrl { get; }

    /// Set when a reader had to skip forward because the data it wanted was already dropped.
    public volatile bool Jumped;

    public long ReaderPos { get; private set; }

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(2),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public TimeshiftServer(string upstream, long windowMs)
    {
        _upstream = upstream;
        _windowMs = windowMs;
        _dir = Path.Combine(AppPaths.Cache, DIR);
        Directory.CreateDirectory(_dir);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(4);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LocalUrl = $"http://127.0.0.1:{port}/live.ts";
        new Thread(DownloadLoop) { IsBackground = true, Name = "timeshift-download" }.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "timeshift-accept" }.Start();
    }

    /// Plays from an absolute byte offset into the stored stream.
    public string UrlFrom(long offset) => $"{LocalUrl}?from={offset}";

    /// Plays from just behind the newest data.
    public string UrlLive() => $"{LocalUrl}?from=live";

    // ---- Download side ----------------------------------------------------------

    private void DownloadLoop()
    {
        var attempt = 0;
        long lastFailure = 0;
        FileStream? raf = null;
        long chunkLen = 0;
        var buf = new byte[64 * 1024];
        while (!_closed)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, _upstream);
                req.Headers.UserAgent.ParseAdd(XtreamApi.USER_AGENT);
                req.Headers.ConnectionClose = true;
                using var resp = Client.Send(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                if (!resp.IsSuccessStatusCode) throw new IOException($"HTTP {(int)resp.StatusCode}");
                using var input = resp.Content.ReadAsStream(_cts.Token);
                while (!_closed)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    readCts.CancelAfter(20_000);
                    int n;
                    try { n = input.ReadAsync(buf, readCts.Token).AsTask().GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { throw new IOException("Read timed out"); }
                    if (n <= 0) throw new IOException("Stream ended");
                    if (raf == null || chunkLen >= CHUNK_BYTES)
                    {
                        raf?.Dispose();
                        var chunk = new Chunk { Start = _written, File = Path.Combine(_dir, $"c{_written}.ts"), CreatedAt = Environment.TickCount64 };
                        raf = new FileStream(chunk.File, FileMode.Create, FileAccess.Write, FileShare.Read);
                        chunkLen = 0;
                        lock (_lock) _chunks.Add(chunk);
                        Trim();
                    }
                    raf.Write(buf, 0, n);
                    raf.Flush();
                    chunkLen += n;
                    lock (_lock)
                    {
                        if (_firstByteAt == 0) _firstByteAt = Environment.TickCount64;
                        _written += n;
                        Monitor.PulseAll(_lock);
                    }
                    attempt = 0;
                }
            }
            catch (Exception e)
            {
                if (_closed) break;
                var now = Environment.TickCount64;
                if (attempt == 0) lastFailure = now;
                attempt++;
                AppLog.W(TAG, $"upstream dropped (attempt {attempt}): {e.Message}");
                if (now - lastFailure > GIVE_UP_MS)
                {
                    _failed = true;
                    lock (_lock) Monitor.PulseAll(_lock);
                    break;
                }
                try { Thread.Sleep((int)Math.Min(1000L * attempt, 5000L)); } catch { break; }
            }
        }
        try { raf?.Dispose(); } catch { }
    }

    /// Drops chunks that are entirely older than the window, or the oldest ones when storage is short.
    private void Trim()
    {
        var now = Environment.TickCount64;
        lock (_lock)
        {
            while (_chunks.Count > 1)
            {
                var oldest = _chunks[0];
                var next = _chunks[1];
                var stale = next.CreatedAt < now - _windowMs;
                var total = _written - _base;
                var low = FreeBytes() < MIN_FREE_BYTES || total > MAX_TOTAL_BYTES;
                if (!stale && !low) break;
                _chunks.RemoveAt(0);
                try { File.Delete(oldest.File); } catch { }
                _base = next.Start;
            }
        }
    }

    private long FreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(_dir)!).AvailableFreeSpace; } catch { return long.MaxValue; }
    }

    // ---- Serving side -----------------------------------------------------------

    private void AcceptLoop()
    {
        while (!_closed)
        {
            TcpClient s;
            try { s = _listener.AcceptTcpClient(); } catch { break; }
            new Thread(() => Serve(s)) { IsBackground = true, Name = "timeshift-serve" }.Start();
        }
    }

    private void Serve(TcpClient sock)
    {
        using (sock)
        {
            FileStream? raf = null;
            try
            {
                sock.NoDelay = true;
                var ns = sock.GetStream();
                var reader = new StreamReader(ns, Encoding.Latin1, false, 1024, true);
                long start = 0;
                var ranged = false;
                var first = true;
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line == null) return;
                    if (line.Length == 0) break;
                    if (first)
                    {
                        first = false;
                        var m = Regex.Match(line, @"[?&]from=(\d+|live)");
                        if (m.Success) start = m.Groups[1].Value == "live" ? LiveOffset() : long.Parse(m.Groups[1].Value);
                        continue;
                    }
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        var m = Regex.Match(line, @"bytes=(\d+)-");
                        if (m.Success && long.TryParse(m.Groups[1].Value, out var off)) { start += off; ranged = true; }
                    }
                }
                var status = ranged ? "206 Partial Content" : "200 OK";
                var head = Encoding.Latin1.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: video/mp2t\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                ns.Write(head, 0, head.Length);

                var pos = start;
                Chunk? current = null;
                var buf = new byte[64 * 1024];
                while (!_closed)
                {
                    Chunk chunk;
                    long avail;
                    lock (_lock)
                    {
                        while (!_closed && !_failed && pos >= _written) Monitor.Wait(_lock, 250);
                        if (_closed || (_failed && pos >= _written)) return;
                        if (pos < _base) { pos = _base; Jumped = true; }
                        chunk = _chunks.Last(c => c.Start <= pos);
                        var idx = _chunks.IndexOf(chunk);
                        var chunkEnd = idx + 1 < _chunks.Count ? _chunks[idx + 1].Start : _written;
                        avail = Math.Min(chunkEnd, _written) - pos;
                    }
                    if (avail <= 0) continue;
                    if (!ReferenceEquals(current, chunk))
                    {
                        raf?.Dispose();
                        raf = new FileStream(chunk.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        current = chunk;
                    }
                    raf!.Seek(pos - chunk.Start, SeekOrigin.Begin);
                    var n = raf.Read(buf, 0, (int)Math.Min(buf.Length, avail));
                    if (n <= 0) continue;
                    ns.Write(buf, 0, n);
                    pos += n;
                    ReaderPos = pos;
                }
            }
            catch
            {
                // Player closed the connection or storage vanished; the player will reconnect if it wants to.
            }
            finally { raf?.Dispose(); }
        }
    }

    // ---- Status -----------------------------------------------------------------

    /// Absolute end of the stored data.
    public long WrittenBytes() { lock (_lock) return _written; }

    /// Absolute start of the oldest stored data.
    public long BaseBytes() { lock (_lock) return _base; }

    /// Average stream rate seen so far, in bytes per millisecond (0 until data arrives).
    public double RateBytesPerMs()
    {
        lock (_lock)
        {
            if (_written == 0 || _firstByteAt == 0) return 0.0;
            var elapsed = Math.Max(1, Environment.TickCount64 - _firstByteAt);
            return _written / (double)elapsed;
        }
    }

    /// Byte offset a little behind the newest data, so playback from there has something to read.
    private long LiveOffset() { lock (_lock) return Math.Max(_written - LIVE_HEADROOM, _base); }

    public void Close()
    {
        _closed = true;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        lock (_lock) Monitor.PulseAll(_lock);
        new Thread(() => Cleanup(_dir)) { IsBackground = true, Name = "timeshift-clean" }.Start();
    }

    /// Removes leftovers from a previous run (crash, task kill).
    public static void Cleanup() => Cleanup(Path.Combine(AppPaths.Cache, DIR));

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch { } } } catch { }
    }

    public static long FreeSpaceBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(AppPaths.Cache)!).AvailableFreeSpace; } catch { return 0; }
    }
}
