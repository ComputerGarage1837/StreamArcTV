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

    /// Unpacks the package over the installed files once this process has exited, then relaunches.
    public static void Install(string zipFile)
    {
        var exe = Environment.ProcessPath ?? "";
        var installDir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        var script = Path.Combine(Path.GetDirectoryName(zipFile)!, "apply-update.cmd");
        var pid = Environment.ProcessId;
        var ps = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                 $"\"Expand-Archive -LiteralPath '{zipFile.Replace("'", "''")}' -DestinationPath '{installDir.Replace("'", "''")}' -Force\"";
        var cmd = string.Join("\r\n", new[]
        {
            "@echo off",
            "title Stream Arc TV update",
            "echo Waiting for Stream Arc TV to close...",
            $":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
            "if not errorlevel 1 ( timeout /t 1 /nobreak >NUL & goto wait )",
            "echo Installing the update...",
            ps,
            "if errorlevel 1 ( echo The update could not be unpacked. Press any key to close. & pause >NUL & exit /b 1 )",
            $"del \"{zipFile}\" >NUL 2>&1",
            $"start \"\" \"{exe}\"",
            "exit /b 0",
            ""
        });
        try
        {
            File.WriteAllText(script, cmd);
            AppLog.I("Update", $"installing {Path.GetFileName(zipFile)} into {installDir}");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception e)
        {
            Dialogs.Toast($"Couldn't start the installer: {e.Message}");
        }
    }
}
