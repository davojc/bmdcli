using System.Net.Sockets;
using System.Text;

namespace Bmd.Devices.Videohub;

/// <summary>Async client for the Videohub Ethernet Protocol (text over TCP 9990).
/// Connects, reads the device's initial state dump, exposes it as 1-based state.</summary>
public sealed class VideohubClient : IAsyncDisposable
{
    readonly TcpClient _tcp;

    public VideohubState State { get; }
    public string Host { get; }
    public int Port { get; }

    VideohubClient(TcpClient tcp, VideohubState state, string host, int port)
    {
        _tcp = tcp;
        State = state;
        Host = host;
        Port = port;
    }

    public static async Task<VideohubClient> ConnectAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            var state = await ReadDumpAsync(tcp, cts.Token);
            return new VideohubClient(tcp, state, host, port);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"timed out talking to {host}:{port} after {timeout.TotalSeconds:0.#}s");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    static async Task<VideohubState> ReadDumpAsync(TcpClient tcp, CancellationToken ct)
    {
        using var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8, leaveOpen: true);
        var acc = new BlockAccumulator();
        var blocks = new List<ProtocolBlock>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (acc.Add(line) is not { } block) continue;
            blocks.Add(block);
            seen.Add(block.Header);
            if (block.Header == "END PRELUDE" || DumpParser.RequiredHeaders.All(seen.Contains))
                return DumpParser.Parse(blocks);
        }
        throw new VideohubProtocolException("connection closed before the device dump completed");
    }

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
