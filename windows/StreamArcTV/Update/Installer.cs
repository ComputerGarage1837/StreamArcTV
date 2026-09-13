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
/// then hands over to a small script that waits for the app to exit, unpacks the new files over
/// the installed ones, and starts the app again.
/// </summary>
public static class Installer
{
    private static bool _active;

    public static async void Download(UpdateChecker.Release release)
    {
        if (_active) { Dialogs.Toast("An update is already downloading."); return; }
        _active = true;
        var dir = Path.Combine(AppPaths.Data, "updates");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, release.AssetName);
        try { if (File.Exists(file)) File.Delete(file); } catch { }

        var cts = new CancellationTokenSource();
        var progress = Dialogs.Progress($"Downloading v{release.VersionName}", "Starting download…", () => cts.Cancel());
        try
        {
            var ok = await Task.Run(async () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, release.ZipUrl);
                req.Headers.UserAgent.ParseAdd(XtreamApi.USER_AGENT);
                using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if ((int)resp.StatusCode == 404) throw new InvalidOperationException("package not found on the release");
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"server error (HTTP {(int)resp.StatusCode})");
                var total = resp.Content.Headers.ContentLength ?? release.SizeBytes;
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
                return Verify(file, release);
            });
            progress.Close();
            if (!ok)
            {
                try { File.Delete(file); } catch { }
                Dialogs.Toast("The downloaded update didn't match the release. Please try again.");
                return;
            }
            Install(file);
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
    private static bool Verify(string file, UpdateChecker.Release release)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists || fi.Length == 0) return false;
        if (release.SizeBytes > 0 && fi.Length != release.SizeBytes) return false;
        var expected = release.Sha256;
        if (expected == null) return true;
        using var s = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        return actual == expected;
    }

    /// Unpacks the package into a staging folder now (no PowerShell, nothing touched while the app
    /// runs), then hands over to a script that waits for this process to exit, copies the files in
    /// with retries (robocopy), and starts the app again. Asks for administrator rights only when
    /// the app's folder is not writable (for example under Program Files).
    public static async void Install(string zipFile)
    {
        var exe = Environment.ProcessPath ?? "";
        var installDir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        var updatesDir = Path.GetDirectoryName(zipFile)!;
        var staged = Path.Combine(updatesDir, "staged");
        var progress = Dialogs.Progress("Installing update", "Unpacking…", null);
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(staged)) Directory.Delete(staged, true);
                Directory.CreateDirectory(staged);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipFile, staged, true);
            });
        }
        catch (Exception e)
        {
            progress.Close();
            AppLog.E("Update", "unpack failed", e);
            Dialogs.Alert("Update failed", $"The update package could not be unpacked: {e.Message}\n\nYou can download it from the releases page and extract it over the app's folder by hand.");
            return;
        }
        progress.Close();

        var needsAdmin = !Writable(installDir);
        if (installDir.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase))
        {
            Dialogs.Alert("Extract the app first", "Stream Arc TV is running from a temporary folder (the zip was opened without extracting it). Extract the zip into a folder of your own, run StreamArcTV.exe from there, and update again.");
            return;
        }
        var script = Path.Combine(updatesDir, "apply-update.cmd");
        var pid = Environment.ProcessId;
        var cmd = string.Join("\r\n", new[]
        {
            "@echo off",
            "title Stream Arc TV update",
            "echo Waiting for Stream Arc TV to close...",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
            "if not errorlevel 1 ( timeout /t 1 /nobreak >NUL & goto wait )",
            "timeout /t 1 /nobreak >NUL",
            "echo Installing the update...",
            $"robocopy \"{staged}\" \"{installDir}\" /E /IS /IT /R:30 /W:1 /NFL /NDL /NJH /NJS /NP",
            "if errorlevel 8 ( echo. & echo The update could not be copied into: & echo   " + installDir + " & echo Close anything using that folder, or extract the zip over it by hand. & echo. & pause & exit /b 1 )",
            $"rd /s /q \"{staged}\" >NUL 2>&1",
            $"del \"{zipFile}\" >NUL 2>&1",
            $"start \"\" \"{exe}\"",
            "exit /b 0",
            ""
        });
        try
        {
            File.WriteAllText(script, cmd);
            AppLog.I("Update", $"installing {Path.GetFileName(zipFile)} into {installDir} (admin={needsAdmin})");
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized };
            if (needsAdmin) psi.Verb = "runas";
            Process.Start(psi);
            App.Window?.ForceClose();
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception e)
        {
            AppLog.E("Update", "installer start failed", e);
            Dialogs.Alert("Update failed", $"Couldn't start the installer: {e.Message}\n\nThe unpacked update is in:\n{staged}\n\nCopy its contents over the app's folder ({installDir}) after closing the app.");
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
