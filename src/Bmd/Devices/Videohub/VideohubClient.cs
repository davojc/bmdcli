using System.Net.Sockets;
using System.Text;

namespace Bmd.Devices.Videohub;

/// <summary>Async client for the Videohub Ethernet Protocol (text over TCP 9990).
/// Connects, reads the device's initial state dump, exposes it as 1-based state.</summary>
public sealed class VideohubClient : IAsyncDisposable
{
    readonly TcpClient _tcp;
    readonly StreamReader _reader;
    readonly StreamWriter _writer;
    readonly TimeSpan _timeout;
    VideohubState _state;

    public string Host { get; }
    public int Port { get; }
    public VideohubState State => _state;

    VideohubClient(TcpClient tcp, StreamReader reader, StreamWriter writer,
                   TimeSpan timeout, string host, int port, VideohubState state)
    {
        _tcp = tcp; _reader = reader; _writer = writer;
        _timeout = timeout; Host = host; Port = port; _state = state;
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
            var stream = tcp.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var state = await ReadDumpAsync(reader, cts.Token);
            return new VideohubClient(tcp, reader, writer, timeout, host, port, state);
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

    static async Task<VideohubState> ReadDumpAsync(StreamReader reader, CancellationToken ct)
    {
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

    /// <summary>Sends one protocol block and waits for the device's ACK.
    /// Update blocks that arrive first are folded into <see cref="State"/>.</summary>
    public async Task SendBlockAsync(string header, IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        try
        {
            await _writer.WriteAsync($"{header}:\n");
            foreach (var line in lines) await _writer.WriteAsync($"{line}\n");
            await _writer.WriteAsync("\n");

            var accumulator = new BlockAccumulator();
            while (await _reader.ReadLineAsync(cts.Token) is { } received)
            {
                if (accumulator.Add(received) is not { } block) continue;
                switch (block.Header)
                {
                    case "ACK": return;
                    case "NAK":
                        throw new VideohubCommandRejectedException($"device rejected {header} ({string.Join("; ", lines)})");
                    default:
                        ApplyUpdate(block);
                        break;
                }
            }
            throw new VideohubProtocolException($"connection closed while awaiting acknowledgement of {header}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"timed out awaiting acknowledgement from {Host}:{Port} after {_timeout.TotalSeconds:0.#}s");
        }
    }

    /// <summary>Folds a pushed update block into the current state. Unknown blocks are ignored.</summary>
    internal void ApplyUpdate(ProtocolBlock block) => _state = DumpParser.ApplyUpdate(_state, block);

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        _reader.Dispose();
        _tcp.Dispose();
    }
}
