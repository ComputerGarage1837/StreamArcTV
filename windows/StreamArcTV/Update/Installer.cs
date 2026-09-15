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

    /// Runs the downloaded setup program silently: it closes the app, replaces the files, updates
    /// the Apps & features entry and starts the app again. Nothing else to do here but quit.
    public static void RunSetup(string setupFile)
    {
        try
        {
            var psi = new ProcessStartInfo(setupFile, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL")
            { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setupFile) };
            if (Process.Start(psi) == null) throw new InvalidOperationException("Windows did not start the setup program");
        }
        catch (Exception e)
        {
            AppLog.E("Update", "setup failed to start", e);
            Dialogs.Alert("Update failed", $"The setup program could not be started: {e.Message}\n\nIt was saved as {setupFile}; you can run it yourself.");
            return;
        }
        AppLog.I("Update", $"started {Path.GetFileName(setupFile)}; closing so it can replace the files");
        App.Window?.ForceClose();
        System.Windows.Application.Current.Shutdown();
    }

    /// Unpacks the package into a staging folder, then swaps the files into the app's own folder
    /// while the app is still running: Windows allows a running program's files to be renamed, so
    /// each old file is moved aside to "*.old" and the new one put in its place, and the app simply
    /// restarts. No scripts, no waiting for the process to exit. Leftover "*.old" files are removed
    /// on the next start. If the folder needs administrator rights, an elevated helper does the swap.
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
            progress.Report(null, "Installing…");
            if (Writable(installDir))
            {
                await Task.Run(() => Swap(staged, installDir));
            }
            else
            {
                // Program Files and the like: run ourselves elevated to do the swap.
                var psi = new ProcessStartInfo(exe, $"--apply-update \"{staged}\" \"{installDir}\"") { UseShellExecute = true, Verb = "runas" };
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("Windows did not start the elevated installer");
                await p.WaitForExitAsync();
                if (p.ExitCode != 0) throw new InvalidOperationException($"the elevated installer reported code {p.ExitCode}");
            }
        }
        catch (Exception e)
        {
            failure = e.Message;
            AppLog.E("Update", "install failed", e);
        }
        progress.Close();
        if (failure != null)
        {
            Dialogs.Alert("Update failed", $"The update could not be installed: {failure}\n\nApp folder: {installDir}\n\nPlease try again later.");
            return;
        }
        try { Directory.Delete(staged, true); } catch { }
        try { File.Delete(zipFile); } catch { }
        AppLog.I("Update", $"installed {Path.GetFileName(zipFile)} into {installDir}; restarting");
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = installDir }); } catch { }
        App.Window?.ForceClose();
        System.Windows.Application.Current.Shutdown();
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
