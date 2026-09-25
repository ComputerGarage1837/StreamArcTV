using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using StreamArcTV.Data;
using StreamArcTV.UI;
using StreamArcTV.Util;

namespace StreamArcTV.Update;

/// <summary>
/// Downloads a release package with a progress dialog, verifies the SHA-256 from the update feed,
/// then installs it. An installed copy (set up by Stream-Arc-TV-Setup-*.exe) is updated by running
/// the new setup program silently; a portable copy (unpacked from the zip) has its files swapped
/// in place.
/// </summary>
public static class Installer
{
    private static bool _active;

    /// True when this copy was put here by the setup program (its uninstaller sits next to the exe).
    public static bool IsInstalled
    {
        get
        {
            try
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
                return Directory.EnumerateFiles(dir, "unins*.exe").Any();
            }
            catch { return false; }
        }
    }

    public static async void Download(UpdateChecker.Release release)
    {
        if (_active) { Dialogs.Toast("An update is already downloading."); return; }
        _active = true;
        var package = IsInstalled && release.Setup != null ? release.Setup : release.Zip;
        var dir = Path.Combine(AppPaths.Data, "updates");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, package.AssetName);
        try { if (File.Exists(file)) File.Delete(file); } catch { }

        var cts = new CancellationTokenSource();
        var progress = Dialogs.Progress($"Downloading v{release.VersionName}", "Starting download…", () => cts.Cancel());
        try
        {
            var ok = await Task.Run(async () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, package.Url);
                req.Headers.UserAgent.ParseAdd(XtreamApi.USER_AGENT);
                using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if ((int)resp.StatusCode == 404) throw new InvalidOperationException("package not found on the release");
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"server error (HTTP {(int)resp.StatusCode})");
                var total = resp.Content.Headers.ContentLength ?? package.SizeBytes;
                await using var input = await resp.Content.ReadAsStreamAsync(cts.Token);
                await using var output = File.Create(file);
                var buf = new byte[256 * 1024];
                long done = 0;
                var lastUi = 0L;
                while (true)
                {
                    var n = await input.ReadAsync(buf, cts.Token);
                    if (n <= 0) break;
                    await output.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    done += n;
                    var now = Environment.TickCount64;
                    if (now - lastUi > 200)
                    {
                        lastUi = now;
                        var d = done;
                        progress.Report(total > 0 ? d / (double)total : null,
                            total > 0 ? $"{Format.Size(d)} of {Format.Size(total)}" : Format.Size(d));
                    }
                }
                progress.Report(null, "Verifying download…");
                return Verify(file, package);
            });
            progress.Close();
            if (!ok)
            {
                try { File.Delete(file); } catch { }
                Dialogs.Toast("The downloaded update didn't match the release. Please try again.");
                return;
            }
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) RunSetup(file);
            else Install(file);
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            try { File.Delete(file); } catch { }
        }
        catch (Exception e)
        {
            progress.Close();
            try { File.Delete(file); } catch { }
            Dialogs.Toast($"Update download failed ({e.Message}). Please try again.");
        }
        finally { _active = false; }
    }

    /// Verifies size and SHA-256 against the update feed when they are present.
    private static bool Verify(string file, UpdateChecker.Package package)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists || fi.Length == 0) return false;
        if (package.SizeBytes > 0 && fi.Length != package.SizeBytes) return false;
        var expected = package.Sha256;
        if (expected == null) return true;
        using var s = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        return actual == expected;
    }

    private static string ResultFile => Path.Combine(AppPaths.Data, "updates", "setup-result.txt");

    /// Runs the downloaded setup program silently and quits. The setup closes anything still
    /// holding files (Restart Manager, forced if needed), replaces them, updates Apps & features
    /// and starts the app again; its exit code is read on the next start.
    public static void RunSetup(string setupFile)
    {
        try
        {
            try { File.Delete(ResultFile); } catch { }
            var psi = new ProcessStartInfo(setupFile,
                $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /NOCANCEL /LOG=\"{Path.Combine(AppPaths.Data, "updates", "setup.log")}\"")
            { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setupFile) };
            var p = Process.Start(psi) ?? throw new InvalidOperationException("Windows did not start the setup program");
            // Record the outcome for the next start without holding the app open.
            new Thread(() => { try { p.WaitForExit(); File.WriteAllText(ResultFile, p.ExitCode.ToString()); } catch { } }) { IsBackground = true }.Start();
        }
        catch (Exception e)
        {
            AppLog.E("Update", "setup failed to start", e);
            Dialogs.Alert("Update failed", $"The setup program could not be started: {e.Message}\n\nIt was saved as {setupFile}; you can run it yourself.");
            return;
        }
        AppLog.I("Update", $"started {Path.GetFileName(setupFile)}; closing so it can replace the files");
        Quit();
    }


    /// Called at start-up: reports a setup run that failed after the app had closed.
    public static void ReportSetupResult()
    {
        try
        {
            if (!File.Exists(ResultFile)) return;
            var text = File.ReadAllText(ResultFile).Trim();
            File.Delete(ResultFile);
            if (!int.TryParse(text, out var code) || code == 0) return;
            var why = code switch
            {
                1 => "the new files could not be copied into the app folder (see the log)",
                2 => "it was cancelled",
                3 => "a fatal error occurred while preparing",
                5 => "it could not replace a file that was still in use",
                8 => "it needed a restart of Windows to finish",
                _ => $"setup code {code}",
            };
            AppLog.W("Update", $"setup finished with code {code}");
            Ui.Post(() => Dialogs.Alert("Update didn't finish", $"The last update did not install: {why}. Close the app completely (also from the tray icon), then press Update again."));
        }
        catch { }
    }

    /// Unpacks the package into a staging folder, then starts the NEW version's own exe from
    /// that folder with --apply-update: it waits for this process to exit (ending it if it
    /// lingers), copies the files into the app's folder with retries, and starts the app from
    /// there. Nothing outside the app is involved and every step is logged. If the folder needs
    /// administrator rights the helper asks once.
    public static async void Install(string zipFile)
    {
        var exe = Environment.ProcessPath ?? "";
        var installDir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        var updatesDir = Path.GetDirectoryName(zipFile)!;
        var staged = Path.Combine(updatesDir, "staged");
        if (installDir.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase))
        {
            Dialogs.Alert("Extract the app first", "Stream Arc TV is running from a temporary folder (the zip was opened without extracting it). Extract the zip into a folder of your own, run StreamArcTV.exe from there, and update again.");
            return;
        }
        var progress = Dialogs.Progress("Installing update", "Unpacking…", null);
        string? failure = null;
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(staged)) Directory.Delete(staged, true);
                Directory.CreateDirectory(staged);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipFile, staged, true);
            });
            try { File.Delete(ResultFile); } catch { }
            var helper = Path.Combine(staged, Path.GetFileName(exe));
            if (!File.Exists(helper)) throw new InvalidOperationException("the package has no " + Path.GetFileName(exe));
            var psi = new ProcessStartInfo(helper, $"--apply-update \"{staged}\" \"{installDir}\" {Environment.ProcessId}")
            { UseShellExecute = true, WorkingDirectory = staged };
            if (!Writable(installDir)) psi.Verb = "runas";
            if (Process.Start(psi) == null) throw new InvalidOperationException("Windows did not start the update helper");
        }
        catch (Exception e)
        {
            failure = e.Message;
            AppLog.E("Update", "install failed", e);
        }
        progress.Close();
        if (failure != null)
        {
            Dialogs.Alert("Update failed", $"The update could not be started: {failure}\n\nApp folder: {installDir}\n\nPlease try again later.");
            return;
        }
        AppLog.I("Update", $"handed {Path.GetFileName(zipFile)} to the update helper for {installDir}; closing");
        Quit();
    }

    /// Closes the window and ends the process for certain, so the helper never waits on a
    /// lingering background thread.
    private static void Quit()
    {
        try { Prefs.Flush(); } catch { }
        try { App.Window?.ForceClose(); } catch { }
        try { System.Windows.Application.Current.Shutdown(); } catch { }
        new Thread(() => { Thread.Sleep(3000); try { Prefs.Flush(); } catch { } Environment.Exit(0); }) { IsBackground = true }.Start();
    }

    /// Called at start-up: removes files left behind by the previous update.
    public static void CleanLeftovers()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
            foreach (var f in Directory.EnumerateFiles(dir, "*.old", SearchOption.AllDirectories)) { try { File.Delete(f); } catch { } }
        }
        catch { }
        try
        {
            var staged = Path.Combine(AppPaths.Data, "updates", "staged");
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
        }
        catch { }   // the helper that ran from it may not have exited yet; next start gets it
    }

    /// Entry for the update helper: --apply-update <staged> <installDir> <pid>. Runs from the
    /// staged copy of the new version, so nothing in the app's folder is in use once <pid> is gone.
    public static int ApplyFromArgs(string[] args)
    {
        try
        {
            var i = Array.IndexOf(args, "--apply-update");
            if (i < 0 || i + 2 >= args.Length) return 2;
            var staged = args[i + 1];
            var installDir = args[i + 2];
            var pid = i + 3 < args.Length && int.TryParse(args[i + 3], out var p) ? p : 0;
            AppLog.I("Update", $"helper: staged={staged} target={installDir} wait for pid {pid}");
            if (pid > 0)
            {
                try
                {
                    using var old = Process.GetProcessById(pid);
                    if (!old.WaitForExit(60_000)) { AppLog.W("Update", "helper: app still running after 60 s; ending it"); old.Kill(true); old.WaitForExit(10_000); }
                }
                catch (ArgumentException) { /* already gone */ }
            }
            Thread.Sleep(500);
            Exception? last = null;
            for (var attempt = 1; attempt <= 30; attempt++)
            {
                try { CopyTree(staged, installDir); last = null; break; }
                catch (Exception e) { last = e; AppLog.W("Update", $"helper: copy attempt {attempt} failed: {e.Message}"); Thread.Sleep(1000); }
            }
            if (last != null) throw last;
            var exe = Path.Combine(installDir, Path.GetFileName(Environment.ProcessPath ?? "StreamArcTV.exe"));
            AppLog.I("Update", $"helper: files in place; starting {exe}");
            File.WriteAllText(ResultFile, "0");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = installDir });
            return 0;
        }
        catch (Exception e)
        {
            AppLog.E("Update", "helper failed", e);
            try { File.WriteAllText(ResultFile, "1"); } catch { }
            return 1;
        }
    }

    /// Copies every file under src into dst, replacing what is there (a rename-aside first, so a
    /// file that still refuses to be overwritten is reported rather than half-written).
    private static void CopyTree(string src, string dst)
    {
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                try { File.Delete(target); }
                catch
                {
                    var aside = target + ".old";
                    try { if (File.Exists(aside)) File.Delete(aside); } catch { aside = target + "." + Guid.NewGuid().ToString("N")[..6] + ".old"; }
                    File.Move(target, aside);
                }
            }
            File.Copy(file, target, true);
        }
    }

    private static bool Writable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".update-probe-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
