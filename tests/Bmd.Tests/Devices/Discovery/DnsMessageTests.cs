using System.Net;
using Bmd.Devices.Discovery;

namespace Bmd.Tests.Devices.Discovery;

public class DnsMessageTests
{
    /// <summary>A hand-built mDNS response advertising one Videohub, exercising all three
    /// name-compression forms (full name, pointer to a full name, pointer into RDATA).
    /// Layout: header 0-11; PTR name 12-35 ("_blackmagic" at 12, "_tcp" at 24, "local" at 29,
    /// root byte at 35); PTR record 36-58 with RDATA at 46-58; SRV record 59-89 (name is a
    /// pointer to 46, RDATA 71-89, target label "studio-hub" at 77 followed by a pointer to
    /// 29); TXT record 90-132 (RDATA 102-132); A record 133-148 (name is a pointer to 77,
    /// RDATA 145-148). Total 149 bytes. Verified byte-for-byte against this layout.</summary>
    const string ResponseHex =
        "000084000000000400000000" +          // header: ANCOUNT=4
        "0B5F626C61636B6D61676963" +          // 11 "_blackmagic"
        "045F746370" +                        // 4 "_tcp"
        "056C6F63616C" +                      // 5 "local"
        "00" +                                // root label
        "000C000100000078000D" +              // PTR, class IN, ttl 120, rdlength 13
        "0A53747564696F20487562" +            // 10 "Studio Hub"
        "C00C" +                              // → offset 12
        "C02E00210001000000780013" +          // SRV, name → offset 46, rdlength 19
        "000000002706" +                      // priority 0, weight 0, port 9990
        "0A73747564696F2D687562" +            // 10 "studio-hub"
        "C01D" +                              // → offset 29 ("local")
        "C02E0010000100000078001F" +          // TXT, name → offset 46, rdlength 31
        "0E636C6173733D566964656F687562" +    // 14 "class=Videohub"
        "0F6E616D653D53747564696F20487562" +  // 15 "name=Studio Hub"
        "C04D00010001000000780004" +          // A, name → offset 77, rdlength 4
        "0A000005";                           // 10.0.0.5

    static byte[] Response() => Convert.FromHexString(ResponseHex);

    [Fact]
    public void EncodeQuery_ProducesAWellFormedPtrQuestion()
    {
        var query = DnsMessage.EncodeQuery("_blackmagic._tcp.local");

        Assert.Equal(0, query[0]);                       // id high
        Assert.Equal(0, query[1]);                       // id low
        Assert.Equal(0, query[2]);                       // flags: standard query
        Assert.Equal(0, query[3]);
        Assert.Equal(1, (query[4] << 8) | query[5]);     // QDCOUNT
        Assert.Equal(0, (query[6] << 8) | query[7]);     // ANCOUNT

        // question name, then QTYPE=PTR(12), QCLASS=IN(1)
        Assert.Equal(11, query[12]);
        Assert.Equal("_blackmagic", System.Text.Encoding.ASCII.GetString(query, 13, 11));
        var typeOffset = query.Length - 4;
        Assert.Equal((int)DnsRecordType.Ptr, (query[typeOffset] << 8) | query[typeOffset + 1]);
        Assert.Equal(1, (query[typeOffset + 2] << 8) | query[typeOffset + 3]);
    }

    [Fact]
    public void ParseRecords_ReadsAllFourRecordKinds()
    {
        var records = DnsMessage.ParseRecords(Response());
        Assert.Equal(4, records.Count);

        var ptr = Assert.IsType<PtrRecord>(records[0]);
        Assert.Equal("_blackmagic._tcp.local", ptr.Name);
        Assert.Equal("Studio Hub._blackmagic._tcp.local", ptr.Target);

        var srv = Assert.IsType<SrvRecord>(records[1]);
        Assert.Equal("Studio Hub._blackmagic._tcp.local", srv.Name);
        Assert.Equal("studio-hub.local", srv.Target);
        Assert.Equal(9990, srv.Port);

        var txt = Assert.IsType<TxtRecord>(records[2]);
        Assert.Equal(["class=Videohub", "name=Studio Hub"], txt.Entries);

        var a = Assert.IsType<ARecord>(records[3]);
        Assert.Equal("studio-hub.local", a.Name);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), a.Address);
    }

    [Fact]
    public void ParseRecords_UnknownType_IsReportedNotDropped()
    {
        // same header but one AAAA (28) record with 16 bytes of RDATA
        var bytes = Convert.FromHexString(
            "0000840000000001000000000B5F626C61636B6D61676963045F746370056C6F63616C00" +
            "001C000100000078001000000000000000000000000000000001");
        var record = Assert.Single(DnsMessage.ParseRecords(bytes));
        var unknown = Assert.IsType<UnknownRecord>(record);
        Assert.Equal("_blackmagic._tcp.local", unknown.Name);
        Assert.Equal((DnsRecordType)28, unknown.Type);
    }

    [Fact]
    public void ParseRecords_TooShortForHeader_Throws()
    {
        Assert.Throws<DnsFormatException>(() => DnsMessage.ParseRecords(new byte[8]));
    }

    [Fact]
    public void ParseRecords_TruncatedRecord_Throws()
    {
        var truncated = Response()[..^3];   // cut into the A record's RDATA
        Assert.Throws<DnsFormatException>(() => DnsMessage.ParseRecords(truncated));
    }

    [Fact]
    public void ParseRecords_PointerLoop_ThrowsRatherThanHanging()
    {
        // header claiming one answer, whose name is a pointer to itself at offset 12
        var bytes = Convert.FromHexString("000084000000000100000000" + "C00C" + "000C000100000078000100");
        Assert.Throws<DnsFormatException>(() => DnsMessage.ParseRecords(bytes));
    }

    [Fact]
    public void ParseRecords_ForwardPointer_Throws()
    {
        // a pointer must reference an EARLIER offset; this one points past itself
        var bytes = Convert.FromHexString("000084000000000100000000" + "C0FF" + "000C000100000078000100");
        Assert.Throws<DnsFormatException>(() => DnsMessage.ParseRecords(bytes));
    }

    [Fact]
    public void ParseRecords_SkipsQuestionsBeforeAnswers()
    {
        // QDCOUNT=1, ANCOUNT=1: the same question the query encodes, followed by a single
        // A record whose name repeats the full label sequence uncompressed (no pointers),
        // so shifting the answer section past a variable-length question can't disturb it.
        var bytes = Convert.FromHexString(
            "000084000001000100000000" +
            "0B5F626C61636B6D61676963045F746370056C6F63616C00" + // question name
            "000C0001" +                                         // QTYPE=PTR, QCLASS=IN
            "0B5F626C61636B6D61676963045F746370056C6F63616C00" + // answer name (uncompressed)
            "0001000100000078" +                                 // TYPE=A, CLASS=IN, TTL=120
            "0004" +                                              // RDLENGTH=4
            "0A000005");                                          // 10.0.0.5

        var records = DnsMessage.ParseRecords(bytes);

        var a = Assert.IsType<ARecord>(Assert.Single(records));
        Assert.Equal("_blackmagic._tcp.local", a.Name);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), a.Address);
    }

    [Fact]
    public void ParseRecords_NameLongerThan255Bytes_ThrowsInsteadOfWalkingWholeChain()
    {
        // A single run of labels whose DECODED length exceeds 255 bytes (RFC 1035 §2.3.4's
        // limit), reached via one compression hop from the record's name. Proves the
        // per-name length cap fires before the whole chain is walked and joined into a
        // string, regardless of how large a hostile message packs the chain.
        var chain = new List<byte>();
        for (int i = 0; i < 5; i++)
        {
            chain.Add(63);
            chain.AddRange(Enumerable.Repeat((byte)'a', 63));
        }
        chain.Add(0x00); // root terminator

        var bytes = new List<byte>();
        bytes.AddRange(Convert.FromHexString("000084000000000100000000")); // header, ANCOUNT=1
        bytes.AddRange(chain);                                             // offset 12
        bytes.Add(0xC0);
        bytes.Add(0x0C);                                                   // record name: 1 hop to offset 12
        bytes.AddRange(Convert.FromHexString("001C0001000000780000"));     // TYPE=28, CLASS=IN, TTL=120, RDLENGTH=0

        Assert.Throws<DnsFormatException>(() => DnsMessage.ParseRecords(bytes.ToArray()));
    }

    [Fact]
    public void ParseRecords_NameAt255Bytes_ParsesButOneByteOver_Throws()
    {
        // Boundary check for the 255-byte cap: exactly at the limit parses fine (4 labels of
        // 63 bytes plus 3 separating dots = 255); one byte past it throws.
        static byte[] BuildMessage(int[] labelLengths)
        {
            var name = new List<byte>();
            foreach (var length in labelLengths)
            {
                name.Add((byte)length);
                name.AddRange(Enumerable.Repeat((byte)'a', length));
            }
            name.Add(0x00);

            var bytes = new List<byte>();
            bytes.AddRange(Convert.FromHexString("000084000000000100000000")); // header, ANCOUNT=1
            bytes.AddRange(name);
            bytes.AddRange(Convert.FromHexString("001C0001000000780000")); // TYPE=28, CLASS=IN, TTL=120, RDLENGTH=0
            return bytes.ToArray();
        }

        var atLimit = DnsMessage.ParseRecords(BuildMessage([63, 63, 63, 63])); // 63*4 + 3 dots = 255
        Assert.Single(atLimit);

        // 63+63+63+62+1 + 4 dots = 256
        Assert.Throws<DnsFormatException>(() => DnsMessage.ParseRecords(BuildMessage([63, 63, 63, 62, 1])));
    }

    [Fact]
    public void ParseRecords_SixteenHops_ParsesButSeventeenHops_Throws()
    {
        // Record 1 is a dummy (unknown-type) record whose RDATA holds a chain of "anchors"
        // at increasing offsets: anchor[0] is a root byte (0x00, terminal); anchor[k] for
        // k >= 1 is a 2-byte compression pointer to anchor[k-1]. Reading a name that starts
        // at anchor[k] costs exactly k hops. This mirrors how the golden fixture's SRV/A
        // records legitimately point into another record's RDATA. Record 2's name is a
        // single pointer to anchor[k], so it costs 1 + k hops. Every offset below is derived
        // from the byte list's own length as it is built, not hand-computed, to avoid the
        // arithmetic pitfall this whole feature exists to catch.
        byte[] BuildMessage(int hopTargetAnchor)
        {
            var bytes = new List<byte>();
            bytes.AddRange(Convert.FromHexString("000084000000000200000000")); // header, ANCOUNT=2
            bytes.Add(0x00); // record 1 name: root
            bytes.AddRange(Convert.FromHexString("001C000100000078")); // TYPE=28 (unknown), CLASS=IN, TTL=120
            int rdLengthOffset = bytes.Count;
            bytes.Add(0);
            bytes.Add(0); // RDLENGTH placeholder, patched below once the chain size is known

            int rdataStart = bytes.Count; // record 1's RDATA (the anchor chain) starts here
            var anchorPos = new int[17];
            int offset = rdataStart;
            bytes.Add(0x00); // anchor[0]: root
            anchorPos[0] = offset;
            offset += 1;
            for (int k = 1; k <= 16; k++)
            {
                anchorPos[k] = offset;
                int target = anchorPos[k - 1];
                bytes.Add((byte)(0xC0 | (target >> 8)));
                bytes.Add((byte)(target & 0xFF));
                offset += 2;
            }
            int rdLength = bytes.Count - rdataStart;
            bytes[rdLengthOffset] = (byte)(rdLength >> 8);
            bytes[rdLengthOffset + 1] = (byte)(rdLength & 0xFF);

            int hopTarget = anchorPos[hopTargetAnchor];
            bytes.Add((byte)(0xC0 | (hopTarget >> 8)));
            bytes.Add((byte)(hopTarget & 0xFF));                                // record 2 name: 1 hop to anchor[k]
            bytes.AddRange(Convert.FromHexString("001C0001000000780000"));      // TYPE=28, CLASS=IN, TTL=120, RDLENGTH=0
            return bytes.ToArray();
        }

        var legal = DnsMessage.ParseRecords(BuildMessage(15)); // 1 + 15 = 16 hops: allowed
        Assert.Equal(2, legal.Count);

        Assert.Throws<DnsFormatException>(() => DnsMessage.ParseRecords(BuildMessage(16))); // 1 + 16 = 17 hops: rejected
    }
}
