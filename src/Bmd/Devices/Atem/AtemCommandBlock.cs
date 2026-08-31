using System.Text;

namespace Bmd.Devices.Atem;

/// <summary>One command block from an ATEM packet: a 4-character ASCII name and its payload.</summary>
public readonly record struct AtemCommandBlock(string Name, ReadOnlyMemory<byte> Payload);

/// <summary>Splits an ATEM packet's body into command blocks, and builds blocks to send.
///
/// Layout: 2 bytes block length (counting these 8 header bytes), 2 bytes that are constant
/// 0x0014 on observed firmware — <b>not</b> reserved-zero, so never validate them as such —
/// 4 bytes ASCII name, then payload.
///
/// <b>The reader advances by the declared length and nothing else.</b> The captured dump contains
/// a PrvI block carrying 8 payload bytes where published community documentation says 4;
/// advancing by an assumed size desynchronises every remaining block in that packet. Callers
/// parse the fields they understand from the front of a payload and ignore the rest.</summary>
public static class AtemBlocks
{
    public const int BlockHeaderSize = 8;

    /// <summary>Bytes 2-3 of a block. Sent as zero.
    ///
    /// Not a constant, despite appearances: across the capture's 287 blocks this field is zero in
    /// 96 and arbitrary in the rest — the same uninitialised send-buffer content that shows up in
    /// name padding. It reads as a constant 0x0014 only if you look at the first block of each
    /// packet and no further. Zero is both the commonest observed value and the safe thing to
    /// send, since the device plainly does not rely on it.</summary>
    const int BlockReserved = 0x0000;

    public static IReadOnlyList<AtemCommandBlock> ReadBlocks(ReadOnlyMemory<byte> packet)
    {
        var blocks = new List<AtemCommandBlock>();
        if (packet.Length <= AtemPacket.HeaderSize) return blocks;

        var span = packet.Span;
        var offset = AtemPacket.HeaderSize;
        while (offset + BlockHeaderSize <= packet.Length)
        {
            var length = (span[offset] << 8) | span[offset + 1];
            // Too short to contain its own header, or claiming more than remains: stop rather
            // than throw. A truncated tail must not take down a connection.
            if (length < BlockHeaderSize || offset + length > packet.Length) break;

            blocks.Add(new AtemCommandBlock(
                Encoding.ASCII.GetString(span.Slice(offset + 4, 4)),
                packet.Slice(offset + BlockHeaderSize, length - BlockHeaderSize)));
            offset += length;
        }
        return blocks;
    }

    /// <summary>Blocks with bytes 2-3 exposed, for the test that proves that field is garbage
    /// rather than the constant it resembles. Production code has no reason to look at it.</summary>
    internal static List<(string Name, int Reserved)> ReadBlocksWithReserved(ReadOnlyMemory<byte> packet)
    {
        var found = new List<(string, int)>();
        var span = packet.Span;
        var offset = AtemPacket.HeaderSize;
        while (offset + BlockHeaderSize <= packet.Length)
        {
            var length = (span[offset] << 8) | span[offset + 1];
            if (length < BlockHeaderSize || offset + length > packet.Length) break;
            found.Add((
                Encoding.ASCII.GetString(span.Slice(offset + 4, 4)),
                (span[offset + 2] << 8) | span[offset + 3]));
            offset += length;
        }
        return found;
    }

    /// <summary>Frames one command block for sending: length, reserved, 4-character name, payload.</summary>
    public static byte[] Build(string name, ReadOnlySpan<byte> payload)
    {
        if (name.Length != 4) throw new ArgumentException("command name must be 4 characters", nameof(name));

        var block = new byte[BlockHeaderSize + payload.Length];
        var length = block.Length;
        block[0] = (byte)(length >> 8);
        block[1] = (byte)length;
        block[2] = (byte)(BlockReserved >> 8);
        block[3] = (byte)BlockReserved;
        Encoding.ASCII.GetBytes(name).CopyTo(block, 4);
        payload.CopyTo(block.AsSpan(BlockHeaderSize));
        return block;
    }

    /// <summary>Writes an ASCII string into a fixed-width field, NUL-padded and NUL-terminated,
    /// truncating anything longer. The device reads names to the first NUL — its own padding
    /// after that terminator contains uninitialised buffer content, so ours being zeroed is a
    /// courtesy, not a requirement.</summary>
    public static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        var count = Math.Min(value.Length, destination.Length);
        for (var i = 0; i < count; i++)
        {
            var c = value[i];
            destination[i] = c is >= (char)0x20 and < (char)0x7F ? (byte)c : (byte)'?';
        }
    }

    /// <summary>Reads an ASCII string from a fixed-width field, stopping at the first NUL.
    ///
    /// Never read to the field width: the capture's input 2 has an empty name whose padding
    /// contains the bytes "MPrp" — a real command name left over in the device's send buffer.
    /// Reading past the terminator would surface that as the input's label.</summary>
    public static string ReadFixedAscii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        if (end < 0) end = field.Length;
        return Encoding.ASCII.GetString(field[..end]).Trim();
    }
}
