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

    /// <summary>A 200 response whose body stream emits <paramref name="initialBytes"/> and then
    /// throws <paramref name="exception"/> on the next read — a connection dropping mid-transfer,
    /// which for a multi-megabyte archive is far likelier than a header-phase failure.</summary>
    public FakeHttpHandler FailingBody(string url, byte[] initialBytes, Exception exception)
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new FailingStreamContent(initialBytes, exception)
        };
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

/// <summary>Content backed by a stream that reads <paramref name="initialBytes"/> and then
/// throws. Both of HttpContent's two read paths are overridden because .NET routes them
/// differently: <c>ReadAsStreamAsync</c> (used by <c>DownloadToFileAsync</c>) goes through
/// <c>CreateContentReadStreamAsync</c>, while <c>ReadAsStringAsync</c> (used by
/// <c>GetTextAsync</c>) buffers via <c>SerializeToStreamAsync</c> instead — leaving either one
/// throwing <c>NotSupportedException</c> would make that caller's failure look like a bug in
/// this fake rather than the dropped connection it is meant to simulate.</summary>
sealed class FailingStreamContent(byte[] initialBytes, Exception exception) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await stream.WriteAsync(initialBytes);
        throw exception;
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult<Stream>(new FailingStream(initialBytes, exception));
}

/// <summary>Emits <paramref name="initialBytes"/> then throws on every read after that.</summary>
sealed class FailingStream(byte[] initialBytes, Exception exception) : Stream
{
    int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= initialBytes.Length) throw exception;
        var toCopy = Math.Min(count, initialBytes.Length - _position);
        Array.Copy(initialBytes, _position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer, offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= initialBytes.Length) throw exception;
        var toCopy = Math.Min(buffer.Length, initialBytes.Length - _position);
        initialBytes.AsSpan(_position, toCopy).CopyTo(buffer.Span);
        _position += toCopy;
        return ValueTask.FromResult(toCopy);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
