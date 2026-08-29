using System.Net;
using System.Text;

namespace Bmd.Devices.Discovery;

public enum DnsRecordType : ushort { A = 1, Ptr = 12, Txt = 16, Srv = 33 }

public sealed class DnsFormatException(string message) : Exception(message);

public abstract record DnsRecord(string Name);
public sealed record PtrRecord(string Name, string Target) : DnsRecord(Name);
public sealed record SrvRecord(string Name, string Target, int Port) : DnsRecord(Name);
public sealed record TxtRecord(string Name, IReadOnlyList<string> Entries) : DnsRecord(Name);
public sealed record ARecord(string Name, IPAddress Address) : DnsRecord(Name);
public sealed record UnknownRecord(string Name, DnsRecordType Type) : DnsRecord(Name);

/// <summary>Minimal DNS wire-format codec, enough for mDNS service discovery.
/// Every read is bounds-checked; malformed input throws rather than misbehaving.</summary>
public static class DnsMessage
{
    const int MaxPointerHops = 16;

    /// <summary>Builds a standard mDNS PTR query: header (id 0, flags 0, QDCOUNT 1),
    /// one question with QTYPE=PTR, QCLASS=IN.</summary>
    public static byte[] EncodeQuery(string serviceName)
    {
        var labels = serviceName.Split('.');
        var buffer = new List<byte>(32);

        // header: id, flags, QDCOUNT=1, ANCOUNT/NSCOUNT/ARCOUNT=0
        buffer.AddRange([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]);

        foreach (var label in labels)
        {
            var ascii = Encoding.ASCII.GetBytes(label);
            if (ascii.Length > 63) throw new DnsFormatException($"label '{label}' exceeds 63 bytes");
            buffer.Add((byte)ascii.Length);
            buffer.AddRange(ascii);
        }
        buffer.Add(0); // root label

        buffer.Add((byte)((int)DnsRecordType.Ptr >> 8));
        buffer.Add((byte)((int)DnsRecordType.Ptr & 0xFF));
        buffer.Add(0);
        buffer.Add(1); // QCLASS = IN

        return buffer.ToArray();
    }

    /// <summary>Parses all answer/authority/additional records from an mDNS/DNS message.
    /// Question entries are skipped, not returned. Throws <see cref="DnsFormatException"/>
    /// on truncation, a bad compression pointer, or a pointer loop.</summary>
    public static IReadOnlyList<DnsRecord> ParseRecords(ReadOnlySpan<byte> message)
    {
        if (message.Length < 12) throw new DnsFormatException("message is too short");

        int qdCount = ReadUInt16(message, 4);
        int anCount = ReadUInt16(message, 6);
        int nsCount = ReadUInt16(message, 8);
        int arCount = ReadUInt16(message, 10);

        int pos = 12;
        for (int i = 0; i < qdCount; i++)
        {
            ReadName(message, ref pos);
            RequireBytes(message, pos, 4); // QTYPE + QCLASS
            pos += 4;
        }

        var records = new List<DnsRecord>(anCount + nsCount + arCount);
        int total = anCount + nsCount + arCount;
        for (int i = 0; i < total; i++)
            records.Add(ReadRecord(message, ref pos));

        return records;
    }

    static DnsRecord ReadRecord(ReadOnlySpan<byte> message, ref int pos)
    {
        var name = ReadName(message, ref pos);

        RequireBytes(message, pos, 10); // TYPE(2) CLASS(2) TTL(4) RDLENGTH(2)
        var type = (DnsRecordType)ReadUInt16(message, pos);
        pos += 8; // skip TYPE, CLASS, TTL
        int rdLength = ReadUInt16(message, pos);
        pos += 2;

        RequireBytes(message, pos, rdLength);
        var rdata = message.Slice(pos, rdLength);
        int rdataEnd = pos + rdLength;

        DnsRecord record = type switch
        {
            DnsRecordType.Ptr => ReadPtrRecord(message, name, pos, rdataEnd),
            DnsRecordType.Srv => ReadSrvRecord(message, name, pos, rdataEnd),
            DnsRecordType.Txt => ReadTxtRecord(rdata, name),
            DnsRecordType.A => ReadARecord(rdata, name),
            _ => new UnknownRecord(name, type),
        };

        pos = rdataEnd;
        return record;
    }

    static PtrRecord ReadPtrRecord(ReadOnlySpan<byte> message, string name, int rdataStart, int rdataEnd)
    {
        int cursor = rdataStart;
        var target = ReadName(message, ref cursor);
        if (cursor > rdataEnd) throw new DnsFormatException("PTR target overruns RDATA");
        return new PtrRecord(name, target);
    }

    static SrvRecord ReadSrvRecord(ReadOnlySpan<byte> message, string name, int rdataStart, int rdataEnd)
    {
        RequireBytes(message, rdataStart, 6); // priority(2) weight(2) port(2)
        int port = ReadUInt16(message, rdataStart + 4);
        int cursor = rdataStart + 6;
        var target = ReadName(message, ref cursor);
        if (cursor > rdataEnd) throw new DnsFormatException("SRV target overruns RDATA");
        return new SrvRecord(name, target, port);
    }

    static TxtRecord ReadTxtRecord(ReadOnlySpan<byte> rdata, string name)
    {
        var entries = new List<string>();
        int pos = 0;
        while (pos < rdata.Length)
        {
            int length = rdata[pos];
            pos += 1;
            if (pos + length > rdata.Length) throw new DnsFormatException("TXT entry overruns RDATA");
            entries.Add(Encoding.ASCII.GetString(rdata.Slice(pos, length)));
            pos += length;
        }
        return new TxtRecord(name, entries);
    }

    static ARecord ReadARecord(ReadOnlySpan<byte> rdata, string name)
    {
        if (rdata.Length != 4) throw new DnsFormatException("A record RDATA must be exactly 4 bytes");
        return new ARecord(name, new IPAddress(rdata));
    }

    /// <summary>Reads a (possibly compressed) DNS name starting at <paramref name="pos"/>,
    /// advancing it past the name as it appears in the outer record (i.e. past the two
    /// bytes of a pointer, never following the jump for the outer cursor).</summary>
    static string ReadName(ReadOnlySpan<byte> message, ref int pos)
    {
        var labels = new List<string>();
        int cursor = pos;
        bool jumped = false;
        int hops = 0;

        while (true)
        {
            RequireBytes(message, cursor, 1);
            int lengthByte = message[cursor];

            if ((lengthByte & 0xC0) == 0xC0)
            {
                RequireBytes(message, cursor, 2);
                int pointerPos = cursor;
                int offset = ((lengthByte & 0x3F) << 8) | message[cursor + 1];

                if (!jumped) pos = cursor + 2; // outer cursor advances past the pointer, exactly once
                jumped = true;

                if (offset >= pointerPos)
                    throw new DnsFormatException("DNS name pointer does not point strictly backward");

                hops++;
                if (hops > MaxPointerHops) throw new DnsFormatException("too many DNS name compression pointers");

                cursor = offset;
                continue;
            }

            if (lengthByte > 63) throw new DnsFormatException("DNS label length exceeds 63 bytes");

            if (lengthByte == 0)
            {
                cursor += 1;
                if (!jumped) pos = cursor;
                break;
            }

            RequireBytes(message, cursor + 1, lengthByte);
            labels.Add(Encoding.ASCII.GetString(message.Slice(cursor + 1, lengthByte)));
            cursor += 1 + lengthByte;
            if (!jumped) pos = cursor;
        }

        return string.Join('.', labels);
    }

    static int ReadUInt16(ReadOnlySpan<byte> message, int pos) => (message[pos] << 8) | message[pos + 1];

    static void RequireBytes(ReadOnlySpan<byte> message, int pos, int count)
    {
        if (pos < 0 || count < 0 || pos + count > message.Length)
            throw new DnsFormatException("message is truncated");
    }
}
