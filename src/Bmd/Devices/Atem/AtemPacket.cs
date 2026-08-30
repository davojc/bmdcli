namespace Bmd.Devices.Atem;

/// <summary>Flags carried in the top 5 bits of an ATEM packet's first two bytes.</summary>
[Flags]
public enum AtemFlags
{
    None = 0,
    /// <summary>The peer must acknowledge this packet.</summary>
    AckRequest = 1,
    /// <summary>Connection handshake.</summary>
    Hello = 2,
    /// <summary>A retransmission of a packet the peer did not acknowledge in time.</summary>
    Resend = 4,
    /// <summary>Asking the peer to retransmit from a sequence id.</summary>
    RequestNext = 8,
    /// <summary>Acknowledgement.</summary>
    Ack = 16,
}

public readonly record struct AtemHeader(
    AtemFlags Flags, int Length, int Session, int AckedId, int SequenceId);

/// <summary>The 12-byte header every ATEM packet carries.
///
/// The one thing to get right: bytes 0-1 are <c>(flags &lt;&lt; 11) | length</c> big-endian, so
/// 5 bits of flags and 11 bits of length share those two bytes. Reading them as two independent
/// fields decodes the first real dump packet as length 910 instead of 1422, and every block
/// after it desynchronises.</summary>
public static class AtemPacket
{
    public const int HeaderSize = 12;

    /// <summary>The port an ATEM listens on. Not 9990 — that is the Videohub protocol's port,
    /// which this device does not speak.</summary>
    public const int DefaultPort = 9910;

    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out AtemHeader header)
    {
        header = default;
        if (packet.Length < HeaderSize) return false;

        var word = (packet[0] << 8) | packet[1];
        header = new AtemHeader(
            (AtemFlags)((word >> 11) & 0x1F),
            word & 0x07FF,
            (packet[2] << 8) | packet[3],
            (packet[4] << 8) | packet[5],
            (packet[10] << 8) | packet[11]);
        return true;
    }

    public static byte[] WriteHeader(
        AtemFlags flags, int length, int session, int ackedId, int sequenceId)
    {
        var bytes = new byte[HeaderSize];
        WriteHeaderTo(bytes, flags, length, session, ackedId, sequenceId);
        return bytes;
    }

    public static void WriteHeaderTo(
        Span<byte> destination, AtemFlags flags, int length, int session, int ackedId, int sequenceId)
    {
        var word = (((int)flags & 0x1F) << 11) | (length & 0x07FF);
        destination[0] = (byte)(word >> 8);
        destination[1] = (byte)word;
        destination[2] = (byte)(session >> 8);
        destination[3] = (byte)session;
        destination[4] = (byte)(ackedId >> 8);
        destination[5] = (byte)ackedId;
        destination[6] = 0;
        destination[7] = 0;
        destination[8] = 0;
        destination[9] = 0;
        destination[10] = (byte)(sequenceId >> 8);
        destination[11] = (byte)sequenceId;
    }
}
