using System.Net;
using System.Text;
using Jellyfin.Plugin.Vidy.Core;

namespace Jellyfin.Plugin.Vidy.Tests;

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<string> Paths { get; } = new();

    public int Calls => Paths.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Paths)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
        }

        return Task.FromResult(_responder(request));
    }
}

internal static class T
{
    public const string Key = "secret-key-123";

    public static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(int code) => new((HttpStatusCode)code) { Content = new StringContent("{}") };

    public static TmdbClient Client(FakeHandler h, string key = Key, TimeSpan? ttl = null, bool adult = false)
    {
        var http = new HttpClient(h);
        return new TmdbClient(
            http,
            () => new TmdbOptions(key, adult, 5, ttl ?? TimeSpan.FromMinutes(5), 100),
            new TtlCache()) { MaxRetryDelay = TimeSpan.Zero };
    }

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vidy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static readonly string UserA = Guid.NewGuid().ToString();
    public static readonly string UserB = Guid.NewGuid().ToString();
}
