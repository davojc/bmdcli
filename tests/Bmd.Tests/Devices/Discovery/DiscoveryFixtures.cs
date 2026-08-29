namespace Bmd.Tests.Devices.Discovery;

/// <summary>Shared golden mDNS response fixture, used by both <c>DnsMessageTests</c> (wire-format
/// parsing) and <c>MdnsClientTests</c> (send/receive/assemble). Kept in one place so the two test
/// classes can't drift apart on what the "one Videohub" response actually looks like on the wire.</summary>
internal static class DiscoveryFixtures
{
    /// <summary>A hand-built mDNS response advertising one Videohub, exercising all three
    /// name-compression forms (full name, pointer to a full name, pointer into RDATA).
    /// Layout: header 0-11; PTR name 12-35 ("_blackmagic" at 12, "_tcp" at 24, "local" at 29,
    /// root byte at 35); PTR record 36-58 with RDATA at 46-58; SRV record 59-89 (name is a
    /// pointer to 46, RDATA 71-89, target label "studio-hub" at 77 followed by a pointer to
    /// 29); TXT record 90-132 (RDATA 102-132); A record 133-148 (name is a pointer to 77,
    /// RDATA 145-148). Total 149 bytes. Verified byte-for-byte against this layout.</summary>
    public const string ResponseHex =
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
}
