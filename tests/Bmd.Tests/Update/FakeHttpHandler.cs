using System.Net;

namespace Bmd.Tests.Update;

/// <summary>An in-process stand-in for the GitHub releases host. Routes are matched on the
/// request's absolute URI; anything unrouted answers 404, which is what a real server does and
/// keeps a test that mistypes a URL honest.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    /// <summary>Every absolute URI this handler has been asked for, in order. Tests assert on it
    /// to prove that --check never downloads and that an up-to-date binary fetches nothing beyond
    /// the release metadata.</summary>
    public List<string> Requests { get; } = [];

    public FakeHttpHandler Text(string url, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = () => new HttpResponseMessage(status) { Content = new StringContent(body) };
        return this;
    }

    public FakeHttpHandler Bytes(string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = () => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        return this;
    }

    public FakeHttpHandler Throws(string url, Exception exception)
    {
        _routes[url] = () => throw exception;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);
        if (!_routes.TryGetValue(url, out var respond))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"no route for {url}")
            });
        return Task.FromResult(respond());
    }
}
