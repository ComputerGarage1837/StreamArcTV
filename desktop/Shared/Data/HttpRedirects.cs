using System.Net;
using System.Net.Http;

namespace StreamArcTV.Data;

/// <summary>
/// Sends a request and follows redirects by hand. .NET's automatic redirects refuse to go from an
/// https:// address to a plain http:// one, but Xtream panels routinely answer a stream address
/// with "302 Found" pointing at an http:// file server, so the download must follow it itself.
/// Headers (user agent, Range, Connection: close) are carried over to every hop.
/// </summary>
public static class HttpRedirects
{
    private const int MaxHops = 10;

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
    {
        var req = request;
        for (var hop = 0; ; hop++)
        {
            var resp = await client.SendAsync(req, option, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var location = resp.Headers.Location;
            if (code is not (301 or 302 or 303 or 307 or 308) || location == null || hop >= MaxHops) return resp;
            var target = location.IsAbsoluteUri ? location : new Uri(req.RequestUri!, location);
            resp.Dispose();
            var next = new HttpRequestMessage(HttpMethod.Get, target);
            foreach (var h in req.Headers) next.Headers.TryAddWithoutValidation(h.Key, h.Value);
            req = next;
        }
    }

    public static HttpResponseMessage Send(HttpClient client, HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct) =>
        SendAsync(client, request, option, ct).GetAwaiter().GetResult();
}
