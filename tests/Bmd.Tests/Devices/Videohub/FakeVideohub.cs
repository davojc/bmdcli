using System.Net;
using System.Net.Sockets;
using System.Text;
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

/// <summary>In-process Videohub: serves its current state on connect, applies mutations,
/// and answers ACK/NAK. Doubles as executable protocol documentation.</summary>
public sealed class FakeVideohub : IAsyncDisposable
{
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly Task _acceptLoop;
    readonly List<Task> _clients = [];
    readonly Lock _gate = new();
    readonly List<StreamWriter> _writers = [];
    readonly Func<string>? _dumpFactory;      // set only by StartChanging
    readonly bool _rejectEverything;
    readonly bool _ackFirst;
    readonly int? _failAfter;                 // set only by StartFailingAfter
    readonly int? _dropAfter;                 // set only by StartDroppingAfter
    readonly int? _stallAfter;                // set only by StartStallingAfter
    int _receivedMutationCount;               // every mutation block read off the wire, any outcome

    // mutable server state (0-based, wire order)
    string _rawDump = "";
    bool _hasFullState;   // false for deliberately incomplete/malformed dumps (used to test connect-time timeouts)
    string _preamble = "";
    string _deviceBlock = "";
    string[] _inputLabels = [];
    string[] _outputLabels = [];
    int[] _routes = [];
    char[] _locks = [];
    int _videoInputs;

    public int Port { get; }

    FakeVideohub(string dump, Func<string>? dumpFactory, bool rejectEverything, bool ackFirst = false,
        int? failAfter = null, int? dropAfter = null, int? stallAfter = null)
    {
        _dumpFactory = dumpFactory;
        _rejectEverything = rejectEverything;
        _ackFirst = ackFirst;
        _failAfter = failAfter;
        _dropAfter = dropAfter;
        _stallAfter = stallAfter;
        LoadState(dump);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public static FakeVideohub Start(string dump = Fixtures.Dump4x4) => new(dump, null, false);
    public static FakeVideohub StartRejecting(string dump = Fixtures.Dump4x4) => new(dump, null, true);

    /// <summary>A hub that ACKs the first <paramref name="successfulMutations"/> mutation blocks
    /// on EACH connection, then NAKs every one after — the allowance resets on reconnect, the
    /// way a real device's per-session behavior would. Used to prove a restore interrupted
    /// partway through resumes (rather than repeats or re-diverges) on the next connection.</summary>
    public static FakeVideohub StartFailingAfter(int successfulMutations, string dump = Fixtures.Dump4x4) =>
        new(dump, null, false, failAfter: successfulMutations);

    /// <summary>A hub that ACKs the first <paramref name="successfulMutations"/> mutation blocks
    /// on the connection, then abruptly closes it instead of replying to the next one — used to
    /// prove a mid-restore disconnect still reports progress and a resume hint.</summary>
    public static FakeVideohub StartDroppingAfter(int successfulMutations, string dump = Fixtures.Dump4x4) =>
        new(dump, null, false, dropAfter: successfulMutations);

    /// <summary>A hub that ACKs the first <paramref name="successfulMutations"/> mutation blocks,
    /// then reads a further block and never replies — used to prove the client's own timeout
    /// fires and that no further block is sent on that connection afterwards.</summary>
    public static FakeVideohub StartStallingAfter(int successfulMutations, string dump = Fixtures.Dump4x4) =>
        new(dump, null, false, stallAfter: successfulMutations);

    /// <summary>A hub that ACKs a mutation BEFORE broadcasting the changed block, the reverse
    /// of the default order — used to prove command reporting does not depend on echo timing.</summary>
    public static FakeVideohub StartAckFirst(string dump = Fixtures.Dump4x4) => new(dump, null, false, ackFirst: true);

    /// <summary>A hub whose output-1 route differs on every connection — used to prove
    /// export verification retries and then fails cleanly. Does not accept mutations.</summary>
    public static FakeVideohub StartChanging()
    {
        var connection = 0;
        return new FakeVideohub(Fixtures.Dump4x4, () =>
        {
            var input = Interlocked.Increment(ref connection) % 4;
            return Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", $"VIDEO OUTPUT ROUTING:\n0 {input}");
        }, false);
    }

    public IReadOnlyList<string> OutputLabels() { lock (_gate) return _outputLabels.ToArray(); }
    public IReadOnlyList<string> InputLabels() { lock (_gate) return _inputLabels.ToArray(); }
    public int[] Routes() { lock (_gate) return _routes.ToArray(); }
    public char[] Locks() { lock (_gate) return _locks.ToArray(); }

    /// <summary>Count of mutation blocks actually read off the wire, regardless of outcome
    /// (ACKed, NAKed, dropped, or stalled) — used to prove no block was sent after a stall.</summary>
    public int ReceivedMutationCount() { lock (_gate) return _receivedMutationCount; }

    void LoadState(string dump)
    {
        _rawDump = dump;
        var blocks = BlockReader.ReadBlocks(dump);
        var byHeader = new Dictionary<string, ProtocolBlock>(StringComparer.Ordinal);
        foreach (var block in blocks) byHeader.TryAdd(block.Header, block);

        // Deliberately incomplete/malformed dumps (e.g. a test proving the client
        // times out waiting for the required blocks) are served verbatim — there is
        // no meaningful mutable state to derive from them.
        if (!DumpParser.RequiredHeaders.All(byHeader.ContainsKey))
        {
            _hasFullState = false;
            return;
        }
        _hasFullState = true;

        if (byHeader.TryGetValue("PROTOCOL PREAMBLE", out var preamble))
            _preamble = RenderBlock(preamble);
        var device = byHeader["VIDEOHUB DEVICE"];
        _deviceBlock = RenderBlock(device);

        var videoInputs = 0;
        var videoOutputs = 0;
        foreach (var line in device.Lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Equals("Video inputs", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out videoInputs);
            if (key.Equals("Video outputs", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out videoOutputs);
        }
        _videoInputs = videoInputs;

        _inputLabels = new string[videoInputs];
        Array.Fill(_inputLabels, "");
        FillIndexed(byHeader["INPUT LABELS"], _inputLabels);

        _outputLabels = new string[videoOutputs];
        Array.Fill(_outputLabels, "");
        FillIndexed(byHeader["OUTPUT LABELS"], _outputLabels);

        _routes = new int[videoOutputs];
        foreach (var line in byHeader["VIDEO OUTPUT ROUTING"].Lines)
        {
            var (index, value) = SplitLine(line);
            if (index is int i && i >= 0 && i < videoOutputs && int.TryParse(value, out var route))
                _routes[i] = route;
        }

        _locks = new char[videoOutputs];
        Array.Fill(_locks, 'U');
        foreach (var line in byHeader["VIDEO OUTPUT LOCKS"].Lines)
        {
            var (index, value) = SplitLine(line);
            if (index is int i && i >= 0 && i < videoOutputs && value.Length > 0)
                _locks[i] = value[0];
        }
    }

    static void FillIndexed(ProtocolBlock block, string[] target)
    {
        foreach (var line in block.Lines)
        {
            var (index, value) = SplitLine(line);
            if (index is int i && i >= 0 && i < target.Length) target[i] = value;
        }
    }

    static (int? Index, string Value) SplitLine(string line)
    {
        var space = line.IndexOf(' ');
        if (space <= 0) return (null, "");
        return int.TryParse(line[..space], out var index) ? (index, line[(space + 1)..]) : (null, "");
    }

    static string RenderBlock(ProtocolBlock block)
    {
        var sb = new StringBuilder();
        sb.Append(block.Header).Append(":\n");
        foreach (var line in block.Lines) sb.Append(line).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    string RenderDump()
    {
        if (_dumpFactory is not null) return _dumpFactory();

        lock (_gate)
        {
            if (!_hasFullState) return _rawDump;


            var sb = new StringBuilder();
            sb.Append(_preamble);
            sb.Append(_deviceBlock);

            sb.Append("INPUT LABELS:\n");
            for (var i = 0; i < _inputLabels.Length; i++) sb.Append(i).Append(' ').Append(_inputLabels[i]).Append('\n');
            sb.Append('\n');

            sb.Append("OUTPUT LABELS:\n");
            for (var i = 0; i < _outputLabels.Length; i++) sb.Append(i).Append(' ').Append(_outputLabels[i]).Append('\n');
            sb.Append('\n');

            sb.Append("VIDEO OUTPUT LOCKS:\n");
            for (var i = 0; i < _locks.Length; i++) sb.Append(i).Append(' ').Append(_locks[i]).Append('\n');
            sb.Append('\n');

            sb.Append("VIDEO OUTPUT ROUTING:\n");
            for (var i = 0; i < _routes.Length; i++) sb.Append(i).Append(' ').Append(_routes[i]).Append('\n');
            sb.Append('\n');

            sb.Append("END PRELUDE:\n\n");
            return sb.ToString();
        }
    }

    async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                // Start the handler OUTSIDE the lock: HandleClientAsync now registers the
                // connection's writer under _gate before its first await, and starting it
                // while already holding _gate here would rely on lock reentrancy to do so
                // (works, but needlessly fragile now that a second registration is involved).
                var handler = HandleClientAsync(client);
                lock (_gate) _clients.Add(handler);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            StreamWriter? writer = null;
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(RenderDump()), _cts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                lock (_gate) _writers.Add(writer);
                var accumulator = new BlockAccumulator();
                // Per-connection allowance for StartFailingAfter — a single-element array so it
                // can be mutated from ApplyBlock (an async method, which can't take a ref parameter).
                var successfulMutations = new int[1];
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (accumulator.Add(line) is not { } block) continue;
                    if (!await ApplyBlock(block, writer, client, successfulMutations)) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally
            {
                if (writer is not null) lock (_gate) _writers.Remove(writer);
            }
        }
    }

    /// <summary>Snapshots the connected clients' writers under <see cref="_gate"/>, then writes
    /// to each OUTSIDE the lock (never hold the lock across an await). Swallows the errors of a
    /// client that has since gone away — a broadcast must never fail just because one listener
    /// disconnected mid-write.</summary>
    async Task BroadcastAsync(string header, string line)
    {
        List<StreamWriter> snapshot;
        lock (_gate) snapshot = [.. _writers];

        var text = $"{header}:\n{line}\n\n";
        foreach (var writer in snapshot)
        {
            try { await writer.WriteAsync(text); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Updates the server's route for wire output <paramref name="output"/> to wire
    /// input <paramref name="input"/> and broadcasts <c>VIDEO OUTPUT ROUTING</c> to every
    /// connected client. A no-op (but does not throw) when no client is connected.</summary>
    public async Task PushRouteAsync(int output, int input)
    {
        lock (_gate) _routes[output] = input;
        await BroadcastAsync("VIDEO OUTPUT ROUTING", $"{output} {input}");
    }

    /// <summary>Renames wire input <paramref name="input"/> and broadcasts <c>INPUT LABELS</c>
    /// to every connected client. A no-op (but does not throw) when no client is connected.</summary>
    public async Task PushInputLabelAsync(int input, string label)
    {
        lock (_gate) _inputLabels[input] = label;
        await BroadcastAsync("INPUT LABELS", $"{input} {label}");
    }

    /// <summary>Renames wire output <paramref name="output"/> and broadcasts <c>OUTPUT LABELS</c>
    /// to every connected client. A no-op (but does not throw) when no client is connected.</summary>
    public async Task PushOutputLabelAsync(int output, string label)
    {
        lock (_gate) _outputLabels[output] = label;
        await BroadcastAsync("OUTPUT LABELS", $"{output} {label}");
    }

    /// <summary>Sets the lock state (<c>U</c>/<c>O</c>/<c>L</c>) of wire output
    /// <paramref name="output"/> and broadcasts <c>VIDEO OUTPUT LOCKS</c> to every connected
    /// client. A no-op (but does not throw) when no client is connected.</summary>
    public async Task PushLockAsync(int output, char letter)
    {
        lock (_gate) _locks[output] = letter;
        await BroadcastAsync("VIDEO OUTPUT LOCKS", $"{output} {letter}");
    }

    /// <summary>Applies (or refuses) one mutation block. Returns false only when the connection
    /// was deliberately dropped (the socket is now closed, so the caller's read loop must stop
    /// rather than read from it again); true otherwise, including the stalled case, where the
    /// loop harmlessly goes back to awaiting a line that a correctly-behaving client never sends.</summary>
    async Task<bool> ApplyBlock(ProtocolBlock block, StreamWriter writer, TcpClient client, int[] successfulMutations)
    {
        string? response;
        bool shouldDrop;
        bool shouldStall;
        lock (_gate)
        {
            _receivedMutationCount++;
            shouldStall = _stallAfter is int stallLimit && successfulMutations[0] >= stallLimit;
            shouldDrop = _dropAfter is int dropLimit && successfulMutations[0] >= dropLimit;
            var overAllowance = _failAfter is int limit && successfulMutations[0] >= limit;
            response = _rejectEverything || overAllowance || shouldDrop || shouldStall ? null : TryApply(block);
            if (response is not null) successfulMutations[0]++;
        }

        if (shouldStall)
        {
            // Never reply, and leave the connection open — the client's own timeout is what
            // unblocks it, exactly as a real stalled device would. The read loop simply goes
            // back to (indefinitely) awaiting a next line; a correctly-behaving client never
            // sends one, and the fake's disposal (cancelling _cts) is what eventually ends it.
            return true;
        }
        if (shouldDrop)
        {
            // Abruptly close the connection instead of replying — simulates the device
            // going away mid-restore.
            client.Close();
            return false;
        }

        if (response is null)
        {
            await writer.WriteAsync("NAK\n\n");
            return true;
        }
        if (_ackFirst)
        {
            await writer.WriteAsync("ACK\n\n");
            await writer.WriteAsync(response);
        }
        else
        {
            await writer.WriteAsync(response);
            await writer.WriteAsync("ACK\n\n");
        }
        return true;
    }

    /// <summary>Validates and applies one block under the caller's lock. Returns the
    /// echoed block text on success (mutation already applied), or null to NAK.</summary>
    string? TryApply(ProtocolBlock block)
    {
        switch (block.Header)
        {
            case "INPUT LABELS":
                return TryApplyLabels(block, _inputLabels);
            case "OUTPUT LABELS":
                return TryApplyLabels(block, _outputLabels);
            case "VIDEO OUTPUT ROUTING":
                return TryApplyRouting(block);
            case "VIDEO OUTPUT LOCKS":
                return TryApplyLocks(block);
            default:
                return null;
        }
    }

    string? TryApplyLabels(ProtocolBlock block, string[] labels)
    {
        var updates = new List<(int Index, string Value)>();
        foreach (var line in block.Lines)
        {
            var (index, value) = SplitLine(line);
            if (index is not int i || i < 0 || i >= labels.Length) return null;
            updates.Add((i, value));
        }
        foreach (var (index, value) in updates) labels[index] = value;
        return RenderChangedBlock(block.Header, updates.Select(u => (u.Index, (string)u.Value)));
    }

    string? TryApplyRouting(ProtocolBlock block)
    {
        var updates = new List<(int Index, int Value)>();
        foreach (var line in block.Lines)
        {
            var (index, value) = SplitLine(line);
            if (index is not int i || i < 0 || i >= _routes.Length) return null;
            if (!int.TryParse(value, out var route) || route < 0 || route >= _videoInputs) return null;
            updates.Add((i, route));
        }
        foreach (var (index, value) in updates) _routes[index] = value;
        return RenderChangedBlock(block.Header, updates.Select(u => (u.Index, u.Value.ToString())));
    }

    string? TryApplyLocks(ProtocolBlock block)
    {
        var updates = new List<(int Index, char Letter)>();
        foreach (var line in block.Lines)
        {
            var (index, value) = SplitLine(line);
            if (index is not int i || i < 0 || i >= _locks.Length) return null;
            if (value is not ("U" or "O" or "L" or "F")) return null;
            updates.Add((i, value[0] == 'F' ? 'U' : value[0]));
        }
        foreach (var (index, letter) in updates) _locks[index] = letter;
        return RenderChangedBlock(block.Header, updates.Select(u => (u.Index, u.Letter.ToString())));
    }

    static string RenderChangedBlock(string header, IEnumerable<(int Index, string Value)> lines)
    {
        var sb = new StringBuilder();
        sb.Append(header).Append(":\n");
        foreach (var (index, value) in lines) sb.Append(index).Append(' ').Append(value).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
        List<Task> snapshot;
        lock (_gate) snapshot = [.. _clients];
        try { await Task.WhenAll(snapshot); } catch { }
        _cts.Dispose();
    }
}
