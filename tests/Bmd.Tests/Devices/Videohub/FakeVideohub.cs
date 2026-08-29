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
    readonly Func<string>? _dumpFactory;      // set only by StartChanging
    readonly bool _rejectEverything;
    readonly bool _ackFirst;

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

    FakeVideohub(string dump, Func<string>? dumpFactory, bool rejectEverything, bool ackFirst = false)
    {
        _dumpFactory = dumpFactory;
        _rejectEverything = rejectEverything;
        _ackFirst = ackFirst;
        LoadState(dump);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public static FakeVideohub Start(string dump = Fixtures.Dump4x4) => new(dump, null, false);
    public static FakeVideohub StartRejecting(string dump = Fixtures.Dump4x4) => new(dump, null, true);

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
                lock (_gate) _clients.Add(HandleClientAsync(client));
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
                await stream.WriteAsync(Encoding.UTF8.GetBytes(RenderDump()), _cts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                var accumulator = new BlockAccumulator();
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (accumulator.Add(line) is not { } block) continue;
                    await ApplyBlock(block, writer);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    async Task ApplyBlock(ProtocolBlock block, StreamWriter writer)
    {
        string? response;
        lock (_gate)
        {
            response = _rejectEverything ? null : TryApply(block);
        }

        if (response is null)
        {
            await writer.WriteAsync("NAK\n\n");
            return;
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
