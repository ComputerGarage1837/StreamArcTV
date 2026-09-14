using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using StreamArcTV.Data;
using StreamArcTV.Util;

namespace StreamArcTV.UI;

/// <summary>
/// Poster / logo loader with a memory and disk cache (the counterpart of Glide): images are
/// downloaded once into the app cache, decoded at a modest size on a worker thread, and shown
/// when they arrive if the target still wants the same URL.
/// </summary>
public static class ImageLoader
{
    private static readonly Dictionary<string, Bitmap> Memory = new();
    private static readonly LinkedList<string> Order = new();
    private const int MaxMemory = 600;
    private static readonly Dictionary<string, Task<Bitmap?>> InFlight = new();
    private static readonly SemaphoreSlim Gate = new(8);
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = TimeSpan.FromSeconds(30) };
    private static string Dir => Path.Combine(AppPaths.Cache, "images");

    public static IImage Placeholder => Ui.Res<IImage>("PlaceholderImage");

    /// Shows the image at url in the target, with the placeholder until it arrives (or on failure).
    public static void Load(Image target, string? url, int decodeWidth = 480, bool placeholder = true)
    {
        target.Tag = url;
        if (string.IsNullOrWhiteSpace(url)) { target.Source = placeholder ? Placeholder : null; return; }
        lock (Memory)
        {
            if (Memory.TryGetValue(url, out var cached)) { target.Source = cached; return; }
        }
        target.Source = placeholder ? Placeholder : null;
        Get(url, decodeWidth).ContinueWith(t =>
        {
            var bmp = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (bmp == null) return;
            Dispatcher.UIThread.Post(() => { if (Equals(target.Tag, url)) target.Source = bmp; });
        });
    }

    /// Loads a bitmap and calls back on the UI thread (used by the drawn guide grid).
    public static void Get(string url, int decodeWidth, Action<Bitmap?> onDone)
    {
        Get(url, decodeWidth).ContinueWith(t =>
        {
            var bmp = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            Dispatcher.UIThread.Post(() => onDone(bmp));
        });
    }

    public static Task<Bitmap?> Get(string url, int decodeWidth)
    {
        lock (Memory) if (Memory.TryGetValue(url, out var cached)) return Task.FromResult<Bitmap?>(cached);
        lock (InFlight)
        {
            if (InFlight.TryGetValue(url, out var existing)) return existing;
            var task = Fetch(url, decodeWidth);
            InFlight[url] = task;
            task.ContinueWith(_ => { lock (InFlight) InFlight.Remove(url); });
            return task;
        }
    }

    private static async Task<Bitmap?> Fetch(string url, int decodeWidth)
    {
        await Gate.WaitAsync();
        try
        {
            var file = CacheFile(url);
            byte[]? bytes = null;
            try { if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file); } catch { }
            if (bytes == null || bytes.Length == 0)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.UserAgent.ParseAdd(XtreamApi.USER_AGENT);
                    using var resp = await Client.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) return null;
                    bytes = await resp.Content.ReadAsByteArrayAsync();
                    if (bytes.Length == 0) return null;
                    Directory.CreateDirectory(Dir);
                    await File.WriteAllBytesAsync(file, bytes);
                }
                catch { return null; }
            }
            var bmp = Decode(bytes, decodeWidth);
            if (bmp == null) { try { File.Delete(file); } catch { } return null; }
            lock (Memory)
            {
                Memory[url] = bmp;
                Order.AddLast(url);
                while (Order.Count > MaxMemory) { var old = Order.First!.Value; Order.RemoveFirst(); Memory.Remove(old); }
            }
            return bmp;
        }
        finally { Gate.Release(); }
    }

    private static Bitmap? Decode(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(ms, decodeWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch { return null; }
    }

    private static string CacheFile(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(Dir, hash);
    }
}
