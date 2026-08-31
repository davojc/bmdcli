using System.Net;
using System.Net.Sockets;
using Bmd.Devices.Atem;

namespace Bmd.Tests.Devices.Atem;

/// <summary>An in-process ATEM that replays the captured state dump over UDP, the same approach
/// as FakeVideohub and for the same reason: the device is not available on demand and CI has no
/// hardware.
///
/// It replays real captured packets rather than packets built from our own understanding of the
/// protocol — only the session id bytes are rewritten, mirroring the reassignment a real switcher
/// performs. A fake assembled from the same documentation the parser was written against could
/// only ever confirm that the two agree.
///
/// Command handling is the one part that is necessarily synthetic: the capture contains nothing a
/// client sent, so what this does with CInL/CAuS/CPgI/CPvI encodes our belief about those
/// layouts. Tests using it prove the client and the command builders agree with each other, not
/// that either matches a real switcher.</summary>
public sealed class FakeAtem : IAsyncDisposable
{
    readonly UdpClient _udp;
    readonly CancellationTokenSource _stopping = new();
    readonly List<AtemHeader> _received = [];
    readonly Lock _gate = new();
    readonly bool _silent;
    readonly int? _resendPacketIndex;
    readonly bool _ignoreCommands;
    readonly int? _keepaliveAfterPacket;
    Task? _loop;
    int _sequence;

    public int Port { get; }
    public int AssignedSession { get; } = 0x8004;

    /// <summary>Headers of every packet the client sent, in order.</summary>
    public IReadOnlyList<AtemHeader> Received { get { lock (_gate) return [.. _received]; } }

    /// <summary>Command blocks the client sent, in order.</summary>
    public List<AtemCommandBlock> Commands { get; } = [];

    FakeAtem(bool silent, int? resendPacketIndex, bool ignoreCommands, int? keepaliveAfterPacket)
    {
        _silent = silent;
        _resendPacketIndex = resendPacketIndex;
        _ignoreCommands = ignoreCommands;
        _keepaliveAfterPacket = keepaliveAfterPacket;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _loop = Task.Run(() => RunAsync(_stopping.Token));
    }

    /// <summary>A switcher that completes the handshake, replays the dump, then keeps sending
    /// keepalives. <paramref name="resendPacketIndex"/> makes it send that dump packet twice, the
    /// second flagged Resend with the same sequence id, as the real device did during capture.
    ///
    /// <paramref name="keepaliveAfterPacket"/> injects a 12-byte keepalive after that dump packet,
    /// which is what a real switcher does during a dump long enough to span one. It is byte for
    /// byte what the device sends *after* the dump, so a client that reads it as an end marker
    /// truncates its own state — the bug an ATEM 1 M/E Production Studio 4K hit.</summary>
    public static FakeAtem Start(
        int? resendPacketIndex = null, bool ignoreCommands = false, int? keepaliveAfterPacket = null) =>
        new(silent: false, resendPacketIndex, ignoreCommands, keepaliveAfterPacket);

    /// <summary>Completes the handshake and then sends nothing at all.</summary>
    public static FakeAtem StartSilent() => new(silent: true, null, false, null);

    async Task RunAsync(CancellationToken stopping)
    {
        IPEndPoint? peer = null;
        try
        {
            // The handshake: the client's Hello, our Hello echoing its session id, its Ack.
            var hello = await _udp.ReceiveAsync(stopping);
            peer = hello.RemoteEndPoint;
            Record(hello.Buffer);
            AtemPacket.TryReadHeader(hello.Buffer, out var opening);

            var reply = new byte[20];
            AtemPacket.WriteHeaderTo(reply, AtemFlags.Hello, reply.Length, opening.Session, 0, 0);
            reply[12] = 0x02;   // success, as the real switcher answers
            await _udp.SendAsync(reply, peer, stopping);

            var ack = await _udp.ReceiveAsync(stopping);
            Record(ack.Buffer);
            if (_silent)
            {
                await ReceiveOnlyAsync(peer, stopping);
                return;
            }

            foreach (var packet in AtemFixtures.DataPackets)
            {
                await SendDumpPacketAsync(packet, peer, ++_sequence, AtemFlags.AckRequest, stopping);
                if (_resendPacketIndex == _sequence - 1)
                    await SendDumpPacketAsync(packet, peer, _sequence,
                        AtemFlags.AckRequest | AtemFlags.Resend, stopping);
                if (_keepaliveAfterPacket == _sequence) await SendKeepaliveAsync(peer, stopping);
            }

            // The payload-free packet that tells the client the dump is done, then keepalives.
            await SendKeepaliveAsync(peer, stopping);
            _ = Task.Run(() => KeepaliveLoopAsync(peer, stopping), stopping);
            await ReceiveOnlyAsync(peer, stopping);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // shutting down
        }
    }

    /// <summary>Receives the client's traffic, acknowledging nothing but recording everything, and
    /// answering a command block with the state block a real switcher would push back.</summary>
    async Task ReceiveOnlyAsync(IPEndPoint peer, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var received = await _udp.ReceiveAsync(stopping);
            Record(received.Buffer);
            foreach (var block in AtemBlocks.ReadBlocks(received.Buffer))
            {
                lock (_gate) Commands.Add(new AtemCommandBlock(block.Name, block.Payload.ToArray()));
                if (_ignoreCommands) continue;
                if (BuildStateReply(block) is { } state)
                    await SendBlocksAsync(state, peer, ++_sequence, stopping);
            }
        }
    }

    /// <summary>The state block a switcher pushes after applying a command. This is where our
    /// belief about the command layouts lives — see the class summary.</summary>
    static byte[]? BuildStateReply(AtemCommandBlock command)
    {
        var payload = command.Payload.Span;
        switch (command.Name)
        {
            case "CInL" when payload.Length >= 28:
            {
                // CInL: mask, pad, id, long[20], short[4] -> InPr: id, long[20], short[4], flags
                var inpr = new byte[36];
                inpr[0] = payload[2];
                inpr[1] = payload[3];
                if ((payload[0] & 1) != 0) payload.Slice(4, 20).CopyTo(inpr.AsSpan(2, 20));
                if ((payload[0] & 2) != 0) payload.Slice(24, 4).CopyTo(inpr.AsSpan(22, 4));
                return AtemBlocks.Build("InPr", inpr);
            }
            case "CAuS" when payload.Length >= 4:
                return AtemBlocks.Build("AuxS", [payload[1], 0, payload[2], payload[3]]);
            case "CPgI" when payload.Length >= 4:
                return AtemBlocks.Build("PrgI", [payload[0], 0, payload[2], payload[3]]);
            case "CPvI" when payload.Length >= 4:
                return AtemBlocks.Build("PrvI", [payload[0], 0, payload[2], payload[3]]);
            default:
                return null;
        }
    }

    async Task SendBlocksAsync(byte[] block, IPEndPoint peer, int sequence, CancellationToken stopping)
    {
        var packet = new byte[AtemPacket.HeaderSize + block.Length];
        AtemPacket.WriteHeaderTo(packet, AtemFlags.AckRequest, packet.Length, AssignedSession, 0, sequence);
        block.CopyTo(packet, AtemPacket.HeaderSize);
        await _udp.SendAsync(packet, peer, stopping);
    }

    /// <summary>Replays a captured packet verbatim except for its header, which carries the
    /// session id this fake assigned — exactly the reassignment a real switcher performs.</summary>
    async Task SendDumpPacketAsync(
        byte[] captured, IPEndPoint peer, int sequence, AtemFlags flags, CancellationToken stopping)
    {
        var packet = (byte[])captured.Clone();
        AtemPacket.WriteHeaderTo(packet, flags, packet.Length, AssignedSession, 0, sequence);
        await _udp.SendAsync(packet, peer, stopping);
    }

    Task SendKeepaliveAsync(IPEndPoint peer, CancellationToken stopping)
    {
        var keepalive = AtemPacket.WriteHeader(
            AtemFlags.AckRequest | AtemFlags.Ack, AtemPacket.HeaderSize, AssignedSession, 0, ++_sequence);
        return _udp.SendAsync(keepalive, peer, stopping).AsTask();
    }

    async Task KeepaliveLoopAsync(IPEndPoint peer, CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(50, stopping);
                await SendKeepaliveAsync(peer, stopping);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // shutting down
        }
    }

    void Record(byte[] packet)
    {
        if (AtemPacket.TryReadHeader(packet, out var header))
            lock (_gate) _received.Add(header);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _udp.Dispose();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* shutting down */ }
            _loop = null;
        }
        _stopping.Dispose();
    }
}
