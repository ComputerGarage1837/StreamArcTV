using System.Net;
using System.Net.Http;

namespace TheChocolateRabbit.Data;

public static class Http
{
    public const string USER_AGENT = "TheChocolateRabbit";

    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };
}
