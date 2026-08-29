using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bmd.Tests.Devices.Videohub;

/// <summary>Minimal in-process Videohub: serves a dump on connect, then reads
/// and discards client lines. Doubles as protocol documentation.</summary>
public sealed class FakeVideohub : IAsyncDisposable
{
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly Task _acceptLoop;
    readonly Func<string> _dump;

    public int Port { get; }

    FakeVideohub(Func<string> dump)
    {
        _dump = dump;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public static FakeVideohub Start(string dump = Fixtures.Dump4x4) => new(() => dump);

    /// <summary>A hub whose output-1 route changes on every connection —
    /// used to prove export verification retries and then fails cleanly.</summary>
    public static FakeVideohub StartChanging()
    {
        var connection = 0;
        return new FakeVideohub(() =>
        {
            var input = connection++ % 4;   // 0,1,2,3 → different route each connect
            return Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", $"VIDEO OUTPUT ROUTING:\n0 {input}");
        });
    }

    async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(_dump()), _cts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync(_cts.Token) is not null) { } // discard until disconnect
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
