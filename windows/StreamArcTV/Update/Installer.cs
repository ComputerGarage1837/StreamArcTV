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

    /// Runs the downloaded setup program silently, but only once this process has fully exited:
    /// a small PowerShell helper waits for our process id (and ends it if it lingers), then runs
    /// the setup, which replaces the files, updates Apps & features and starts the app again.
    /// Running the setup while the app is still shutting down made it find files in use and give up.
    public static void RunSetup(string setupFile)
    {
        try
        {
            try { File.Delete(ResultFile); } catch { }
            var pid = Environment.ProcessId;
            var script =
                $"try {{ Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue }} catch {{}}; " +
                $"Stop-Process -Id {pid} -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800; " +
                $"$p = Start-Process -FilePath '{Ps(setupFile)}' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS','/FORCECLOSEAPPLICATIONS','/NOCANCEL' -PassThru -Wait; " +
                $"Set-Content -Path '{Ps(ResultFile)}' -Value $p.ExitCode";
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(setupFile) };
            if (Process.Start(psi) == null) throw new InvalidOperationException("Windows did not start the update helper");
        }
        catch (Exception e)
        {
            AppLog.E("Update", "setup failed to start", e);
            Dialogs.Alert("Update failed", $"The setup program could not be started: {e.Message}\n\nIt was saved as {setupFile}; you can run it yourself.");
            return;
        }
        AppLog.I("Update", $"handed {Path.GetFileName(setupFile)} to the update helper; closing");
        Quit();
    }

    private static string Ps(string s) => s.Replace("'", "''");

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
                1 => "the new files could not be copied into the app folder",
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

    /// Unpacks the package into a staging folder, then hands over to a helper that waits for
    /// this process to exit (ending it if it lingers), copies the new files over the app's folder
    /// with retries, and starts the app again. Nothing is touched while the app is running, so
    /// no file can be "in use". If the folder needs administrator rights the helper asks once.
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
            var pid = Environment.ProcessId;
            var script =
                $"try {{ Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue }} catch {{}}; " +
                $"Stop-Process -Id {pid} -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800; " +
                $"$ok = $false; $tries = 0; while (-not $ok -and $tries -lt 30) {{ try {{ Copy-Item -Path '{Ps(staged)}\\*' -Destination '{Ps(installDir)}' -Recurse -Force -ErrorAction Stop; $ok = $true }} catch {{ $tries++; Start-Sleep -Seconds 1 }} }}; " +
                $"if ($ok) {{ Remove-Item -LiteralPath '{Ps(staged)}' -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{Ps(zipFile)}' -Force -ErrorAction SilentlyContinue; Set-Content -Path '{Ps(ResultFile)}' -Value 0; Start-Process -FilePath '{Ps(exe)}' -WorkingDirectory '{Ps(installDir)}' }} else {{ Set-Content -Path '{Ps(ResultFile)}' -Value 1 }}";
            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"";
            var psi = Writable(installDir)
                ? new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = updatesDir }
                : new ProcessStartInfo("powershell.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = updatesDir };
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

    /// Moves every staged file into place, renaming the existing (possibly running) file aside first.
    public static void Swap(string staged, string installDir)
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
            var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
            foreach (var f in Directory.EnumerateFiles(dir, "*.old", SearchOption.AllDirectories)) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    /// Entry for the elevated helper started with --apply-update <staged> <installDir>.
    public static int ApplyFromArgs(string[] args)
    {
        try
        {
            var i = Array.IndexOf(args, "--apply-update");
            if (i < 0 || i + 2 >= args.Length) return 2;
            Swap(args[i + 1], args[i + 2]);
            return 0;
        }
        catch (Exception e)
        {
            try { File.WriteAllText(Path.Combine(AppPaths.Data, "updates", "elevated-error.txt"), e.ToString()); } catch { }
            return 1;
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
