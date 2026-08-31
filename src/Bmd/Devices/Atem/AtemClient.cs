using System.Net;
using System.Net.Sockets;

namespace Bmd.Devices.Atem;

public sealed class AtemProtocolException(string message) : Exception(message);

/// <summary>A UDP session with an ATEM switcher: handshake, state dump, and verified commands.
///
/// Four behaviours this exists to get right, all of them observed on real hardware rather than
/// taken from documentation:
///
/// <list type="number">
/// <item>The switcher <b>reassigns the session id</b>. We open with our own; it echoes that in
/// the handshake, then uses an id of its own from the next packet onward. Keep sending ours and
/// it stops understanding us.</item>
/// <item>Every packet flagged AckRequest must be acknowledged, including the 12-byte keepalives
/// that arrive indefinitely after the dump. Stop acknowledging and the session drops.</item>
/// <item><b>Retransmission is routine.</b> The six-second capture contains two, because the
/// capturing script was slow to acknowledge. Applying a resent packet twice would double-apply
/// its blocks, so packets are de-duplicated by sequence id.</item>
/// <item>The dump has <b>no end marker</b>, and the packet that looks like one is not
/// distinguishable from a keepalive — both are 12 bytes flagged AckRequest|Ack. Completion is
/// therefore decided by content, with an idle-gap fallback. See <see cref="DumpIsComplete"/>.</item>
/// </list></summary>
public sealed class AtemClient : IAsyncDisposable
{
    readonly UdpClient _udp;
    readonly CancellationTokenSource _stopping = new();
    readonly HashSet<int> _appliedSequences = [];
    readonly TaskCompletionSource _dumpComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _gate = new();

    /// <summary>How long without a block-bearing packet counts as "the dump has stopped". Only a
    /// fallback: <see cref="DumpIsComplete"/> normally finishes the connect well before this.</summary>
    const int DumpSettleMs = 400;

    Timer? _settle;
    Task? _receiveLoop;
    int _session;
    int _localSequence;

    /// <summary>Raised after each received packet's blocks have been applied to <see cref="State"/>.
    /// Commands wait on this to confirm the device actually applied what they asked for.</summary>
    event Action? StateChanged;

    public AtemState State { get; } = new();
    public string Host { get; }
    public int Port { get; }

    AtemClient(UdpClient udp, string host, int port)
    {
        _udp = udp;
        Host = host;
        Port = port;
    }

    public static async Task<AtemClient> ConnectAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var udp = new UdpClient();
        var client = new AtemClient(udp, host, port);
        try
        {
            udp.Connect(host, port);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await client.HandshakeAsync(deadline.Token, cancellationToken);
            client._settle = new Timer(
                _ => client._dumpComplete.TrySetResult(), null, Timeout.Infinite, Timeout.Infinite);
            client._receiveLoop = Task.Run(() => client.ReceiveLoopAsync(client._stopping.Token));
            await client.WaitAsync(client._dumpComplete.Task, timeout, cancellationToken,
                $"timed out reading the state dump from {host}:{port}");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Three packets: our Hello, the switcher's Hello with a success byte, our Ack.</summary>
    async Task HandshakeAsync(CancellationToken deadline, CancellationToken caller)
    {
        // Any id will do; the switcher replaces it immediately. Derived from the socket's own
        // ephemeral port rather than a random number so a session is reproducible in a trace.
        var opening = (_udp.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0x7A56;
        _session = opening & 0x7FFF;

        var hello = new byte[20];
        AtemPacket.WriteHeaderTo(hello, AtemFlags.Hello, hello.Length, _session, 0, 0);
        hello[12] = 0x01;
        await _udp.SendAsync(hello, deadline);

        UdpReceiveResult reply;
        try
        {
            reply = await _udp.ReceiveAsync(deadline);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            // Silence. Either nothing is there, or something is there that does not speak this.
            throw new TimeoutException($"no response from {Host}:{Port} — is it an ATEM?");
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset)
        {
            // ICMP port-unreachable: the host answered, but nothing is bound to that UDP port.
            // Worth saying plainly — the usual cause is an address that is a Videohub or a
            // MultiView, which listen on TCP 9990 and have nothing on 9910 at all.
            throw new AtemProtocolException(
                $"nothing is listening on {Host}:{Port} (an ATEM listens on UDP {AtemPacket.DefaultPort})");
        }

        if (!AtemPacket.TryReadHeader(reply.Buffer, out var header) || !header.Flags.HasFlag(AtemFlags.Hello))
            throw new AtemProtocolException($"{Host}:{Port} did not answer with an ATEM handshake");

        // The Hello reply still carries OUR id — the switcher echoes it. It switches to an id of
        // its own on the first data packet, which the receive loop adopts. Anything sent with the
        // opening id after that point is acknowledged by nothing and acted on by nothing, which
        // is what makes this worth a comment: the failure is completely silent.
        _session = header.Session;
        await SendAckAsync(0, deadline);
    }

    async Task ReceiveLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp.ReceiveAsync(stopping);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (!AtemPacket.TryReadHeader(received.Buffer, out var header)) continue;
            _session = header.Session;

            // Acknowledge before applying: the device retransmits on a slow ack, and a
            // retransmission storm is harder to reason about than a duplicate we would drop anyway.
            if (header.Flags.HasFlag(AtemFlags.AckRequest))
            {
                try { await SendAckAsync(header.SequenceId, stopping); }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { return; }
            }

            var blocks = AtemBlocks.ReadBlocks(received.Buffer);
            if (blocks.Count == 0) continue;

            // Resent packets repeat their sequence id. Applying one twice would re-apply its
            // blocks; harmless for absolute state, but this also keeps a verify-wait honest.
            bool complete;
            lock (_gate)
            {
                if (!_appliedSequences.Add(header.SequenceId)) continue;
                AtemDumpParser.Apply(State, blocks);
                complete = DumpIsComplete();
            }

            if (complete) _dumpComplete.TrySetResult();
            else _settle?.Change(DumpSettleMs, Timeout.Infinite);   // restart the idle-gap fallback
            StateChanged?.Invoke();
        }
    }

    /// <summary>Whether the dump has delivered everything a command needs. Call under the lock.
    ///
    /// This is decided by content rather than by any packet the device sends, because the device
    /// sends nothing that means "done": the payload-free packet that follows the dump is byte for
    /// byte a keepalive, and keepalives also arrive *during* a long dump. Treating the first
    /// payload-free packet as the end worked on a switcher whose dump fits in five packets and
    /// truncated the state of a larger one at random — a 1 M/E Production Studio 4K intermittently
    /// reported having no auxiliary outputs, moments after correctly listing three.
    ///
    /// The switcher describes its own dump: `_top` says how many sources and auxes exist, so the
    /// dump is complete once that many have arrived alongside the model name. Anything the device
    /// under-reports is caught by the idle-gap fallback instead.</summary>
    bool DumpIsComplete() =>
        ProductNameSeen && State.Topology.Sources > 0
        && State.SourcesById.Count >= State.Topology.Sources
        && State.AuxById.Count >= State.Topology.Auxiliaries;

    bool ProductNameSeen => State.ProductName.Length > 0;

    Task SendAckAsync(int ackedSequence, CancellationToken cancellationToken)
    {
        var ack = AtemPacket.WriteHeader(AtemFlags.Ack, AtemPacket.HeaderSize, _session, ackedSequence, 0);
        return _udp.SendAsync(ack, cancellationToken).AsTask();
    }

    /// <summary>Sends one command block and waits for the switcher to report the change back.
    ///
    /// The wait is the point. The ATEM protocol has no ACK/NAK for commands — a switcher that
    /// does not understand a command simply ignores it — and the payload layouts for commands are
    /// not published. Waiting for the device's own state push turns "we think we sent the right
    /// bytes" into "the device says it applied them", and turns a wrong guess into a clean
    /// timeout error instead of a silent no-op reported as success.</summary>
    public async Task SendCommandAsync(
        string name, byte[] payload, Func<AtemState, bool> applied, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (applied(State)) return;   // already the case; nothing to send
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            bool done;
            lock (_gate) done = applied(State);
            if (done) completion.TrySetResult();
        }

        StateChanged += Check;
        try
        {
            var block = AtemBlocks.Build(name, payload);
            var packet = new byte[AtemPacket.HeaderSize + block.Length];
            AtemPacket.WriteHeaderTo(
                packet, AtemFlags.AckRequest, packet.Length, _session, 0, ++_localSequence);
            block.CopyTo(packet, AtemPacket.HeaderSize);

            await _udp.SendAsync(packet, cancellationToken);
            Check();
            await WaitAsync(completion.Task, timeout, cancellationToken,
                $"{Host} did not report the change after {name}; it may not support this command");
        }
        finally
        {
            StateChanged -= Check;
        }
    }

    async Task WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken, string message)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var finished = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, deadline.Token));
        if (finished != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(message);
        }
        await task;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_settle is not null) await _settle.DisposeAsync();
        _udp.Dispose();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; } catch { /* shutting down */ }
        }
        _stopping.Dispose();
    }
}
