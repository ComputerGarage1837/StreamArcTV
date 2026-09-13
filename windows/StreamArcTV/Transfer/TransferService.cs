using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using StreamArcTV.Data;
using StreamArcTV.Util;

namespace StreamArcTV.Transfer;

/// <summary>
/// Background engine that performs downloads and live recordings: streams the URL into the
/// chosen folder, reports progress to <see cref="TransferStore"/>, stops a recording when its end
/// time passes, and schedules future recordings (in-process while the app runs, and as a Windows
/// scheduled task that starts the app in the background when it is closed).
/// </summary>
public static class TransferService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Running = new();
    // Xtream panels normally allow one connection per account, so downloads run strictly one
    // after another; a whole season is a queue, not a burst.
    private static readonly SemaphoreSlim DownloadSlots = new(1);
    private static readonly TransferStore Store = TransferStore.Get();
    private static System.Threading.Timer? _scheduler;

    private const int MAX_ATTEMPTS = 6;
    private static readonly long[] RETRY_DELAYS_MS = { 5_000, 15_000, 30_000, 60_000, 120_000 };

    /// True while the player is open; downloads hold off so the stream gets the connection.
    public static volatile bool PlaybackActive;

    /// Live progress by job id; only present while the transfer is actually moving bytes.
    public static readonly ConcurrentDictionary<string, Progress> Live = new();

    /// Up-to-the-chunk progress of a running transfer, read directly by the lists.
    public class Progress
    {
        public long Bytes;
        public long Total;
        public double BytesPerSec;
        public Progress(long bytes, long total) { Bytes = bytes; Total = total; }
    }

    /// Fired (on a worker thread) when a transfer finishes or fails, for the tray notification.
    public static event Action<TransferJob, bool, string?>? Finished;

    public static bool AnyActive => Running.Count > 0 || Store.All().Any(j => j.State is TransferState.QUEUED or TransferState.RUNNING);

    /// Adds a job and makes sure it is started (or scheduled).
    public static void Enqueue(TransferJob job)
    {
        Store.Put(job);
        if (job.State == TransferState.SCHEDULED && job.StartAt > Format.NowMs + 60_000) ScheduleTask(job);
        Kick();
    }

    /// Starts anything that is due: queued downloads and recordings whose time has come.
    public static void Kick()
    {
        _scheduler ??= new System.Threading.Timer(_ => Kick(), null, 15_000, 15_000);
        var now = Format.NowMs;
        foreach (var job in Store.All())
        {
            if (Running.ContainsKey(job.Id)) continue;
            switch (job.State)
            {
                case TransferState.QUEUED: Start(job); break;
                case TransferState.SCHEDULED when job.StartAt <= now + 15_000: Start(job); break;
                case TransferState.RUNNING: Start(job); break;   // the process died mid-transfer: restart it
            }
        }
    }

    /// Re-arms scheduled recordings on start-up: missed ones fail, due ones start, later ones are scheduled.
    /// Renames the files of downloads completed by earlier versions ("Show S1E2 Title.mp4") to the
    /// clear form ("Show - S01E02 - Title.mp4") and updates their entries. Runs once, in the background.
    public static void RenameCompleted()
    {
        if (Prefs.Instance.DownloadsRenamed) return;
        Task.Run(() =>
        {
            var renamed = 0;
            foreach (var job in Store.All())
            {
                if (job.Type != TransferType.DOWNLOAD || job.State != TransferState.DONE) continue;
                var title = Names.Upgrade(job.Title);
                if (title == null || title == job.Title) continue;
                var ext = Path.GetExtension(job.FileName);
                var fileName = Folders.SafeName(title) + ext;
                var newPath = job.FileUri;
                try
                {
                    if (job.FileUri != null && File.Exists(job.FileUri))
                    {
                        newPath = Path.Combine(Path.GetDirectoryName(job.FileUri)!, fileName);
                        if (!string.Equals(newPath, job.FileUri, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(newPath)) File.Delete(newPath);
                            File.Move(job.FileUri, newPath);
                        }
                    }
                }
                catch (Exception e) { AppLog.W("Transfer", $"rename '{job.FileName}' failed: {e.Message}"); continue; }
                Store.Update(job.Id, j => { j.Title = title; j.FileName = fileName; j.FileUri = newPath; });
                renamed++;
            }
            if (renamed > 0) AppLog.I("Transfer", $"renamed {renamed} completed download(s) to the clear form");
            Prefs.Instance.DownloadsRenamed = true;
        });
    }

    public static void RescheduleAll()
    {
        var now = Format.NowMs;
        foreach (var job in Store.All())
        {
            if (job.State != TransferState.SCHEDULED) continue;
            if (job.EndAt <= now) Store.Update(job.Id, j => { j.State = TransferState.FAILED; j.Error = "Missed"; });
            else if (job.StartAt > now + 60_000) ScheduleTask(job);
        }
        Kick();
    }

    private static void Start(TransferJob job)
    {
        var cts = new CancellationTokenSource();
        if (!Running.TryAdd(job.Id, cts)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (job.Type == TransferType.DOWNLOAD)
                {
                    await DownloadSlots.WaitAsync(cts.Token);
                    try { await Run(job, cts.Token); } finally { DownloadSlots.Release(); }
                }
                else await Run(job, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { AppLog.E("Transfer", $"'{job.Title}' crashed", e); }
            finally { Running.TryRemove(job.Id, out _); }
        });
    }

    public static void Cancel(string id)
    {
        var job = Store.Get(id);
        if (Running.TryRemove(id, out var cts)) cts.Cancel();
        if (job != null)
        {
            Folders.Delete(job.FileUri);
            Store.Update(id, j => j.State = TransferState.CANCELLED);
        }
        UnscheduleTask(id);
    }

    private static async Task Run(TransferJob job, CancellationToken ct)
    {
        var id = job.Id;
        // A recording scheduled for later waits here (the scheduler normally wakes us anyway).
        if (job.Type == TransferType.RECORDING)
        {
            var wait = job.StartAt - Format.NowMs;
            if (wait > 0) await Task.Delay((int)Math.Min(wait, int.MaxValue), ct);
        }
        AppLog.I("Transfer", $"start {job.Type} '{job.Title}' {AppLog.SafeUrl(job.Url)}");
        Store.Update(id, j => { j.State = TransferState.RUNNING; j.Error = null; });
        Folders.Target? target = null;
        long done = 0;
        var attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested && Running.ContainsKey(id))
            {
                // Playback has priority: most providers allow one stream per account, so a
                // download would stop the video from loading. Wait here until the player closes.
                if (job.Type == TransferType.DOWNLOAD && HoldReason() != null)
                {
                    AppLog.I("Transfer", $"'{job.Title}' held ({HoldReason()}) at {done} bytes");
                    Store.Update(id, j => j.Error = HoldReason() == "wifi" ? "Waiting for Wi-Fi (mobile data is off for downloads)" : "Paused while you watch – resumes when the player closes");
                    while (HoldReason() != null && !ct.IsCancellationRequested && Running.ContainsKey(id)) await Task.Delay(1000, ct);
                    Store.Update(id, j => j.Error = null);
                    continue;
                }
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, job.Url);
                    req.Headers.UserAgent.ParseAdd(XtreamApi.USER_AGENT);
                    req.Headers.ConnectionClose = true;
                    var resuming = job.Type == TransferType.DOWNLOAD && done > 0 && target != null;
                    if (resuming) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(done, null);
                    using var pauseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    using var resp = await HttpRedirects.SendAsync(Client, req, HttpCompletionOption.ResponseHeadersRead, pauseCts.Token);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // Capture exactly what the provider answered so the reason is in the log and the list.
                        string detail = "";
                        try
                        {
                            var body = await resp.Content.ReadAsStringAsync(ct);
                            detail = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", " ").Trim();
                            if (detail.Length > 160) detail = detail[..160] + "…";
                        }
                        catch { }
                        AppLog.W("Transfer", $"'{job.Title}' HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} for {AppLog.SafeUrl(job.Url)} · {detail}");
                        throw new HttpException((int)resp.StatusCode, resp.ReasonPhrase, detail);
                    }
                    var code = (int)resp.StatusCode;
                    Folders.Target? outT = null;
                    if (resuming && code == 206)
                    {
                        // Carry on where the last attempt stopped.
                        try { target?.Dispose(); } catch { }
                        try { outT = Folders.Append(target!.Path); } catch { outT = null; }
                    }
                    if (outT == null)
                    {
                        try { target?.Dispose(); } catch { }
                        outT = Folders.Create(job.Folder, job.Type, job.FileName);
                        done = 0;
                        if (resuming && code == 206) throw new InvalidOperationException("Can't resume file");
                    }
                    target = outT;
                    var path = outT.Path;
                    var len = resp.Content.Headers.ContentLength ?? -1;
                    var total = job.Type != TransferType.DOWNLOAD ? -1L : (code == 206 && len >= 0 ? len + done : len);
                    Store.Update(id, j => { j.FileUri = path; j.Total = total; j.Error = null; });
                    await using var input = await resp.Content.ReadAsStreamAsync(pauseCts.Token);
                    var buf = new byte[256 * 1024];
                    var prog = new Progress(done, total);
                    Live[id] = prog;
                    var lastFlush = Format.NowMs;
                    var lastFlushBytes = done;
                    var paused = false;
                    while (!ct.IsCancellationRequested && Running.ContainsKey(id))
                    {
                        if (job.Type == TransferType.RECORDING && Format.NowMs >= job.EndAt) break;
                        if (job.Type == TransferType.DOWNLOAD && HoldReason() != null)
                        {
                            // Close the socket for real, so the provider frees this account's stream slot for the player.
                            paused = true;
                            pauseCts.Cancel();
                            break;
                        }
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(pauseCts.Token);
                        readCts.CancelAfter(60_000);
                        int n;
                        try { n = await input.ReadAsync(buf, readCts.Token); }
                        catch (OperationCanceledException) when (!pauseCts.IsCancellationRequested) { throw new TimeoutException("The provider stopped sending data (timed out)."); }
                        if (n <= 0) break;
                        outT.Stream.Write(buf, 0, n);
                        done += n;
                        prog.Bytes = done;
                        var now = Format.NowMs;
                        if (now - lastFlush > 1000)
                        {
                            prog.BytesPerSec = (done - lastFlushBytes) * 1000.0 / (now - lastFlush);
                            lastFlush = now;
                            lastFlushBytes = done;
                            var d = done;
                            Store.Update(id, j => j.Bytes = d);
                        }
                    }
                    outT.Stream.Flush();
                    var dd = done;
                    Store.Update(id, j => j.Bytes = dd);
                    if (paused) continue;   // back to the top of the loop, which waits and then resumes with a Range request
                    if (job.Type == TransferType.DOWNLOAD && total > 0 && done < total && Running.ContainsKey(id) && !ct.IsCancellationRequested)
                        throw new InvalidOperationException("Connection closed early");
                    AppLog.I("Transfer", $"'{job.Title}' done, {done} bytes");
                    if (!Running.ContainsKey(id) || ct.IsCancellationRequested) return;   // cancelled
                    Store.Update(id, j => { j.State = TransferState.DONE; j.Error = null; });
                    NotifyDone(job, true, null);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    if (HoldReason() != null && job.Type == TransferType.DOWNLOAD) continue;   // the cancel above surfaces as an exception
                    if (!Running.ContainsKey(id) || ct.IsCancellationRequested) return;
                    attempt++;
                    if (attempt >= MAX_ATTEMPTS) throw;
                    var wait = RETRY_DELAYS_MS[Math.Min(attempt - 1, RETRY_DELAYS_MS.Length - 1)];
                    var why = Describe(e);
                    AppLog.W("Transfer", $"'{job.Title}' attempt {attempt} failed at {done} bytes: {why} ({e.GetType().Name}: {e.Message}); retry in {wait / 1000}s");
                    Store.Update(id, j => j.Error = $"Retrying ({attempt} of {MAX_ATTEMPTS}) in {wait / 1000} s – {why}");
                    await Task.Delay((int)wait, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!Running.ContainsKey(id)) return;
            var why = Describe(e);
            AppLog.E("Transfer", $"'{job.Title}' gave up: {why}", e);
            if (job.Type == TransferType.DOWNLOAD)
            {
                // A download that gave up leaves nothing behind: drop the partial file and the
                // list entry; the notification carries the reason.
                try { target?.Dispose(); } catch { }
                Folders.Delete(target?.Path);
                Store.Remove(id);
            }
            else
            {
                Store.Update(id, j => { j.State = TransferState.FAILED; j.Error = why; });
            }
            NotifyDone(job, false, why);
        }
        finally
        {
            Live.TryRemove(id, out _);
            try { target?.Dispose(); } catch { }
        }
    }

    /// Streaming client with no overall timeout (reads are watched individually above).
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,   // followed by hand, see HttpRedirects
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(2),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    /// Why a download must wait right now: "playback", "wifi", or null to go ahead.
    private static string? HoldReason()
    {
        if (PlaybackActive) return "playback";
        if (Prefs.Instance.DownloadsWifiOnly && OnMobileData()) return "wifi";
        return null;
    }

    /// True when the only way out is a mobile-broadband (WWAN) or dial-up adapter.
    public static bool OnMobileData()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Where(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                .ToList();
            if (up.Count == 0) return false;
            return up.All(n => n.NetworkInterfaceType is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 or NetworkInterfaceType.Ppp);
        }
        catch { return false; }
    }

    private class HttpException : Exception
    {
        public int Code { get; }
        public string Reason { get; }
        public string Detail { get; }
        public HttpException(int code, string? reason, string detail) : base($"HTTP {code} {reason}".Trim()) { Code = code; Reason = reason ?? ""; Detail = detail; }
        public string Label => $"HTTP {Code}{(Reason.Length > 0 ? " " + Reason : "")}{(Detail.Length > 0 ? ": " + Detail : "")}";
    }

    private static string Describe(Exception e) => e switch
    {
        HttpException { Code: 401 or 403 or 429 or 458 or 503 or 509 } h =>
            $"Provider refused the connection ({h.Label}), usually because too many streams are open on this account. It is retried automatically; pause other playback if it keeps failing.",
        HttpException { Code: 404 } h => $"The provider has no file for this title ({h.Label}).",
        HttpException h => $"Provider error ({h.Label}).",
        _ => string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message
    };

    private static void NotifyDone(TransferJob job, bool ok, string? error)
    {
        try { Finished?.Invoke(job, ok, error); } catch { }
    }

    // ---- Windows scheduled tasks (recordings start even when the app is closed) ----

    private static string TaskName(string id) => $"StreamArcTV Recording {id}";

    /// Registers a scheduled task that starts the app in the background just before the recording.
    public static void ScheduleTask(TransferJob job)
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe)) return;
            var at = DateTimeOffset.FromUnixTimeMilliseconds(job.StartAt - 5_000).LocalDateTime;
            var script =
                $"$a = New-ScheduledTaskAction -Execute '{Ps(exe)}' -Argument '--tray'; " +
                $"$t = New-ScheduledTaskTrigger -Once -At ([datetime]'{at:yyyy-MM-ddTHH:mm:ss}'); " +
                $"$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable; " +
                $"Register-ScheduledTask -TaskName '{Ps(TaskName(job.Id))}' -Action $a -Trigger $t -Settings $s -Force | Out-Null";
            RunPowerShell(script);
        }
        catch (Exception e) { AppLog.W("Transfer", $"couldn't register the scheduled task: {e.Message}"); }
    }

    public static void UnscheduleTask(string id)
    {
        try { RunPowerShell($"Unregister-ScheduledTask -TaskName '{Ps(TaskName(id))}' -Confirm:$false -ErrorAction SilentlyContinue"); }
        catch { }
    }

    private static string Ps(string s) => s.Replace("'", "''");

    private static void RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script.Replace("\"", "\\\"")}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
        };
        using var p = Process.Start(psi);
        if (p == null) return;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(20_000);
        if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(err)) AppLog.W("Transfer", "powershell: " + err.Trim());
    }
}
