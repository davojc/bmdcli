using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
    /// Update blocks that arrive first are folded into <see cref="State"/>.
    /// <paramref name="description"/>, when supplied, replaces the raw (0-based wire) rejection
    /// message with a human, 1-based phrase — callers that talk to the device on the user's
    /// behalf must never let a NAK message leak wire indices back to the user.</summary>
    public async Task SendBlockAsync(string header, IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default, string? description = null)
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
                        throw new VideohubCommandRejectedException(
                            $"device rejected {description ?? $"{header} ({string.Join("; ", lines)})"}");
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

    /// <summary>Streams changes as the device reports them, updating <see cref="State"/> as it goes.
    /// Waits indefinitely between updates — the client's own timeout does not apply here, since a
    /// watch may legitimately sit idle for hours. Cancelling <paramref name="cancellationToken"/>
    /// ends the sequence cleanly (no exception out of the enumerator); a closed connection ends it
    /// by throwing <see cref="VideohubProtocolException"/> (the caller decides what that means).
    /// Not safe to call while a mutation is in flight on the same client: both consume the
    /// connection's single reader.</summary>
    public async IAsyncEnumerable<VideohubUpdate> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var accumulator = new BlockAccumulator();
        while (true)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;      // cancellation ends the stream cleanly
            }
            if (line is null)
                throw new VideohubProtocolException($"connection to {Host}:{Port} closed");

            if (accumulator.Add(line) is not { } block) continue;
            if (block.Header is "ACK" or "NAK") continue;   // not ours to interpret here

            var before = _state;
            ApplyUpdate(block);
            foreach (var update in VideohubUpdate.Diff(before, _state))
                yield return update;
        }
    }

    /// <summary>Routes <paramref name="input"/> to <paramref name="output"/> (both 1-based).</summary>
    public Task SetRouteAsync(int output, int input, CancellationToken cancellationToken = default)
    {
        CheckOutput(output);
        CheckInput(input);
        return SendBlockAsync("VIDEO OUTPUT ROUTING", [$"{output - 1} {input - 1}"], cancellationToken,
            description: $"routing input {input} to output {output}");
    }

    /// <summary>Renames input <paramref name="input"/> (1-based).</summary>
    public Task RenameInputAsync(int input, string label, CancellationToken cancellationToken = default)
    {
        CheckInput(input);
        CheckLabel(label);
        return SendBlockAsync("INPUT LABELS", [$"{input - 1} {label}"], cancellationToken,
            description: $"renaming input {input}");
    }

    /// <summary>Renames output <paramref name="output"/> (1-based).</summary>
    public Task RenameOutputAsync(int output, string label, CancellationToken cancellationToken = default)
    {
        CheckOutput(output);
        CheckLabel(label);
        return SendBlockAsync("OUTPUT LABELS", [$"{output - 1} {label}"], cancellationToken,
            description: $"renaming output {output}");
    }

    /// <summary>Takes the lock on output <paramref name="output"/> (1-based).</summary>
    public Task LockOutputAsync(int output, CancellationToken cancellationToken = default)
    {
        CheckOutput(output);
        return SendBlockAsync("VIDEO OUTPUT LOCKS", [$"{output - 1} O"], cancellationToken,
            description: $"locking output {output}");
    }

    /// <summary>Releases the lock on output <paramref name="output"/> (1-based).
    /// <paramref name="force"/> clears a lock owned by another controller.</summary>
    public Task UnlockOutputAsync(int output, bool force = false, CancellationToken cancellationToken = default)
    {
        CheckOutput(output);
        return SendBlockAsync("VIDEO OUTPUT LOCKS", [$"{output - 1} {(force ? 'F' : 'U')}"], cancellationToken,
            description: force ? $"unlocking output {output} (force)" : $"unlocking output {output}");
    }

    void CheckInput(int input)
    {
        if (input < 1 || input > _state.Device.VideoInputs)
            throw new ArgumentOutOfRangeException(nameof(input), input,
                $"input must be between 1 and {_state.Device.VideoInputs}");
    }

    void CheckOutput(int output)
    {
        if (output < 1 || output > _state.Device.VideoOutputs)
            throw new ArgumentOutOfRangeException(nameof(output), output,
                $"output must be between 1 and {_state.Device.VideoOutputs}");
    }

    static void CheckLabel(string label)
    {
        if (label.Contains('\n') || label.Contains('\r'))
            throw new ArgumentException("label must not contain newlines", nameof(label));
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        _reader.Dispose();
        _tcp.Dispose();
    }
}
