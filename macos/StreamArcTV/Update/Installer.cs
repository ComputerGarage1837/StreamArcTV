using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using StreamArcTV.Data;
using StreamArcTV.UI;
using StreamArcTV.Util;

namespace StreamArcTV.Update;

/// <summary>
/// Downloads a release zip with a progress dialog, verifies the SHA-256 from the update feed,
/// unpacks it and swaps the new app bundle in for the running one: the old bundle is moved
/// aside, the new one takes its place, and a small helper relaunches the app once this process
/// has exited (and removes the old bundle). Files of a running program may be renamed on macOS,
/// so no elevation and no waiting are needed as long as the bundle's folder is writable.
/// </summary>
public static class Installer
{
    private static bool _active;

    public static async void Download(UpdateChecker.Release release)
    {
        if (_active) { Dialogs.Toast("An update is already downloading."); return; }
        var bundle = Mac.BundlePath;
        if (bundle == null)
        {
            Dialogs.Alert("Not an app bundle", "This copy of Stream Arc TV is not running from an app bundle, so it cannot replace itself. Download the disk image from the releases page instead.");
            return;
        }
        if (bundle.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            Dialogs.Alert("Move the app first", "Stream Arc TV is running from the disk image. Drag it into your Applications folder, open it from there, and update again.");
            return;
        }
        _active = true;
        var package = release.Zip;
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
            Install(file, bundle);
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

    /// Unpacks the zip (with ditto, which keeps the bundle's permissions and symbolic links),
    /// moves the running bundle aside, puts the new one in its place, and hands over to a helper
    /// that relaunches the app after this process exits.
    public static async void Install(string zipFile, string bundle)
    {
        var parent = Path.GetDirectoryName(bundle)!;
        var updatesDir = Path.GetDirectoryName(zipFile)!;
        var staged = Path.Combine(updatesDir, "staged");
        var progress = Dialogs.Progress("Installing update", "Unpacking…", null);
        string? failure = null;
        string? newApp = null;
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(staged)) Directory.Delete(staged, true);
                Directory.CreateDirectory(staged);
                var r = Mac.Run("/usr/bin/ditto", new[] { "-x", "-k", zipFile, staged }, timeoutMs: 600_000);
                if (r.Code != 0) throw new InvalidOperationException("could not unpack the package: " + r.Err.Trim());
                newApp = Directory.GetDirectories(staged, "*.app").FirstOrDefault()
                         ?? throw new InvalidOperationException("the package holds no application bundle");
                if (!Writable(parent)) throw new InvalidOperationException($"the folder {parent} is not writable by your account. Move the app to a folder you own (your home folder's Applications folder, say) and update again, or replace it by hand from the releases page.");
            });
            progress.Report(null, "Installing…");
            await Task.Run(() =>
            {
                var old = OldPath(bundle);
                if (Directory.Exists(old)) Directory.Delete(old, true);
                Directory.Move(bundle, old);
                try { Directory.Move(newApp!, bundle); }
                catch
                {
                    // Different volume: copy instead, then put the old one back on failure.
                    var r = Mac.Run("/usr/bin/ditto", new[] { newApp!, bundle }, timeoutMs: 600_000);
                    if (r.Code != 0) { try { Directory.Move(old, bundle); } catch { } throw new InvalidOperationException("could not copy the new app into place: " + r.Err.Trim()); }
                }
                Mac.Run("/usr/bin/xattr", new[] { "-dr", "com.apple.quarantine", bundle });
            });
        }
        catch (Exception e)
        {
            failure = e.Message;
            AppLog.E("Update", "install failed", e);
        }
        progress.Close();
        if (failure != null)
        {
            Dialogs.Alert("Update failed", $"The update could not be installed: {failure}\n\nApp: {bundle}\n\nYou can download the disk image from the releases page and replace the app by hand (quit it first).");
            return;
        }
        try { Directory.Delete(staged, true); } catch { }
        try { File.Delete(zipFile); } catch { }
        AppLog.I("Update", $"installed {Path.GetFileName(zipFile)} as {bundle}; restarting");
        Relaunch(bundle);
    }

    /// Starts a detached shell that waits for this process to end, deletes the old bundle and opens the new one.
    private static void Relaunch(string bundle)
    {
        try
        {
            var pid = Environment.ProcessId;
            var old = OldPath(bundle);
            var script = $"while kill -0 {pid} 2>/dev/null; do sleep 0.2; done; rm -rf {Sh(old)}; /usr/bin/open {Sh(bundle)}";
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            Process.Start(psi);
        }
        catch (Exception e) { AppLog.E("Update", "relaunch helper failed to start", e); }
        App.ReleaseInstanceLock();
        App.Window?.ForceClose();
        App.Quit();
    }

    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// Hidden sibling folder the previous bundle is parked in until the relaunch helper removes it.
    private static string OldPath(string bundle) => Path.Combine(Path.GetDirectoryName(bundle)!, "." + Path.GetFileName(bundle) + ".old");

    /// Called at start-up: removes a bundle left behind by the previous update.
    public static void CleanLeftovers()
    {
        try
        {
            var bundle = Mac.BundlePath;
            if (bundle == null) return;
            var old = OldPath(bundle);
            if (Directory.Exists(old)) Directory.Delete(old, true);
        }
        catch { }
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
