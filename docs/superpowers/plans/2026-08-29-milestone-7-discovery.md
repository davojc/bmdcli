# bmd Milestone 7: mDNS Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `bmd discover` finds Blackmagic devices on the local network and can write the chosen one straight into config — no hunting for IP addresses.

**Architecture:** A pure DNS message codec (encode a query, parse a response, including name-compression pointers) under a minimal mDNS client that multicasts on 224.0.0.251:5353 and collects responses for a window; a class→device-type table decides what counts as "supported"; two command surfaces list and add.

**Tech Stack:** .NET 10, ConsoleAppFramework v5, System.Text.Json source generators, xUnit. No new packages — the available .NET zeroconf libraries are unverified for Native AOT, so the client is hand-rolled (~300 lines, per the spec).

**Spec:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` ("Device discovery")

## Global Constraints

- `PublishAot=true` stays green: no reflection, no `dynamic`, JSON ONLY via the source-generated contexts. **No new package references.**
- `Devices/` and `Config/` never reference ConsoleAppFramework or `Commands/`.
- Errors are one `error: ...` line on stderr + exit code (0 success / 1 operation failure / 2 usage or format error); stdout carries nothing on failure.
- `--json` emits one JSON document (an array here); watch's JSON Lines exception does not apply.
- Help is the API: full XML doc comments in plain prose (ConsoleAppFramework renders `<paramref>`/`<see>` tags literally, and multi-line `///` summaries render with embedded newlines).
- TDD: failing test → implementation → passing test → commit, every task.

**Discovery-specific rules:**
- **The class→device-type mapping table is unverified data.** Public documentation of Blackmagic's mDNS TXT `class=` values is thin. The table starts with the values we believe Videohubs use, `--all` exists precisely to learn the real ones from hardware, and **the code must degrade gracefully when a class is unrecognized** (show it under `--all`, never crash, never guess it into a supported type).
- **A hostile or malformed packet must never hang or crash the tool.** Name-compression pointers can form loops; RDLENGTH can lie. The parser bounds every read and refuses to follow more than a fixed number of pointers.
- **Discovery is best-effort.** Finding nothing is exit 0 with an empty list (and a helpful note in human mode), not an error — a hub may simply predate mDNS or sit on another subnet.

---

### Task 1: DNS message codec (pure)

**Files:**
- Create: `src/Bmd/Devices/Discovery/DnsMessage.cs`, `tests/Bmd.Tests/Devices/Discovery/DnsMessageTests.cs`

**Interfaces:**
- Produces (namespace `Bmd.Devices.Discovery`):
  - `enum DnsRecordType : ushort { A = 1, Ptr = 12, Txt = 16, Srv = 33 }`
  - `abstract record DnsRecord(string Name)` with `sealed record PtrRecord(string Name, string Target) : DnsRecord`, `SrvRecord(string Name, string Target, int Port)`, `TxtRecord(string Name, IReadOnlyList<string> Entries)`, `ARecord(string Name, IPAddress Address)`, `UnknownRecord(string Name, DnsRecordType Type)`
  - `sealed class DnsFormatException(string message) : Exception(message)`
  - `static class DnsMessage`:
    - `static byte[] EncodeQuery(string serviceName)` — a standard mDNS PTR query: header (id 0, flags 0, QDCOUNT 1), one question with QTYPE=PTR, QCLASS=IN
    - `static IReadOnlyList<DnsRecord> ParseRecords(ReadOnlySpan<byte> message)` — parses all answer/authority/additional records; throws `DnsFormatException` on truncation, a bad pointer, or a pointer loop. Question entries are skipped, not returned.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Discovery/DnsMessageTests.cs`:

```csharp
using System.Net;
using Bmd.Devices.Discovery;

namespace Bmd.Tests.Devices.Discovery;

public class DnsMessageTests
{
    /// <summary>A hand-built mDNS response advertising one Videohub, exercising all three
    /// name-compression forms (full name, pointer to a full name, pointer into RDATA).
    /// Layout is documented byte-by-byte in the plan; offsets: name at 12, instance
    /// RDATA at 46, "local" at 29, SRV target at 77.</summary>
    const string ResponseHex =
        "00008400000000040000000" + "0" +
        "0B5F626C61636B6D61676963" +          // 11 "_blackmagic"
        "045F746370" +                        // 4 "_tcp"
        "056C6F63616C" +                      // 5 "local"
        "00" +
        "000C0001000000780 00D".Replace(" ", "") +
        "0A53747564696F20487562" +            // 10 "Studio Hub"
        "C00C" +                              // → offset 12
        "C02E00210001000000780013" +          // SRV, name → offset 46, rdlength 19
        "000000002706" +                      // priority 0, weight 0, port 9990
        "0A73747564696F2D687562" +            // 10 "studio-hub"
        "C01D" +                              // → offset 29 ("local")
        "C02E0010000100000078001F" +          // TXT, rdlength 31
        "0E636C6173733D566964656F687562" +    // 14 "class=Videohub"
        "0F6E616D653D53747564696F20487562" +  // 15 "name=Studio Hub"
        "C04D0001000100000078000" + "4" +     // A, name → offset 77
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
        // QDCOUNT=1 with the same question the query encodes, then the four answers
        var query = DnsMessage.EncodeQuery("_blackmagic._tcp.local");
        var question = query[12..];                      // name + qtype + qclass
        var answers = Response()[12..];                  // the four answer records
        var bytes = new byte[12 + question.Length + answers.Length];
        Convert.FromHexString("000084000001000400000000").CopyTo(bytes, 0);
        question.CopyTo(bytes, 12);
        answers.CopyTo(bytes, 12 + question.Length);

        // NOTE: the answers' compression pointers reference offsets from the ORIGINAL
        // packet, so this test only asserts that parsing does not throw and that the
        // question itself is not returned as a record.
        var records = DnsMessage.ParseRecords(bytes);
        Assert.DoesNotContain(records, r => r.Name == "" );
    }
}
```

**Implementer note on the last test:** it is deliberately weak (shifting the answers invalidates their pointers). If it proves unreliable, replace it with a hand-built packet whose QDCOUNT is 1 and whose single answer uses no compression at all, asserting exactly one record comes back — and say so in your report.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter DnsMessageTests`
Expected: compilation failure — the types do not exist.

**If the golden fixture does not parse once the codec is written, suspect the plan's offset arithmetic before your code.** The layout is: header 0-11; PTR name 12-35 (`_blackmagic`=12, `_tcp`=24, `local`=29, root=35); PTR record 36-58 with RDATA at 46-58; SRV 59-89 (name pointer→46, RDATA 71-89, target `studio-hub` at 77, pointer→29); TXT 90-132 (RDATA 102-132); A 133-148 (name pointer→77, RDATA 145-148). Total 149 bytes. Fix the fixture, document the correction prominently, and keep the assertions.

- [ ] **Step 3: Implement `src/Bmd/Devices/Discovery/DnsMessage.cs`**

Shape (write the full implementation from this specification):

```csharp
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
public static class DnsMessage { … }
```

Required behavior:
- `EncodeQuery`: 12-byte header (all zero except QDCOUNT = 1), then the name as length-prefixed ASCII labels terminated by a zero byte, then QTYPE = 12 and QCLASS = 1, both big-endian.
- `ParseRecords`: throw `DnsFormatException("message is too short")` if fewer than 12 bytes. Read QDCOUNT/ANCOUNT/NSCOUNT/ARCOUNT. Skip each question (name, then 4 bytes). Then read `ANCOUNT + NSCOUNT + ARCOUNT` records: name, type (2), class (2), TTL (4), RDLENGTH (2), RDATA.
- Name reading handles compression: a length byte with its top two bits set (`>= 0xC0`) means a 14-bit offset built from that byte and the next; the offset **must be strictly less than the position of the pointer itself** (forward and self-pointers throw), and at most 16 pointers may be followed in one name (throws beyond that). After following a pointer, the outer cursor advances past the two pointer bytes and no further.
- Per type: `Ptr` → target name read from RDATA; `Srv` → skip 4 bytes, read port (2), then target name; `Txt` → sequence of length-prefixed ASCII strings within RDLENGTH; `A` → exactly 4 bytes → `IPAddress`; anything else → `UnknownRecord`, skipping RDLENGTH bytes.
- Every read validates it stays within the message, and every record validates RDATA stays within RDLENGTH. Labels are ASCII; a label length above 63 throws.
- Names are returned dot-joined without a trailing dot (`_blackmagic._tcp.local`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter DnsMessageTests`, then the full `dotnet test`.
Expected: all pass (239 existing plus the new ones).

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: minimal DNS wire-format codec for mDNS discovery"
```

---

### Task 2: Discovered device model + class mapping

**Files:**
- Create: `src/Bmd/Devices/Discovery/DiscoveredDevice.cs`, `tests/Bmd.Tests/Devices/Discovery/DiscoveredDeviceTests.cs`

**Interfaces:**
- Consumes: the record types from Task 1.
- Produces (namespace `Bmd.Devices.Discovery`):
  - `sealed record DiscoveredDevice(string Name, string DeviceClass, string? DeviceType, IPAddress Address, int Port)` — `DeviceType` is the bmd device group (`"videohub"`) when the class is recognized, else `null` (unsupported/unknown)
  - `static class DeviceClasses`:
    - `static string? DeviceTypeFor(string deviceClass)` — case-insensitive lookup; returns `"videohub"` for the known Videohub classes, `null` otherwise
    - `static IReadOnlyList<string> KnownVideohubClasses { get; }` — the seed table: `["Videohub", "SmartVideohub", "VideoHub"]`. **This is unverified data** (see Global Constraints); the XML doc must say so and point at `--all`.
  - `static class DeviceAssembler`:
    - `static IReadOnlyList<DiscoveredDevice> FromRecords(IReadOnlyList<DnsRecord> records)` — joins PTR/SRV/TXT/A records into devices: for each SRV, find the matching TXT (same name) for `class=`/`name=`, and the A record whose name equals the SRV target for the address. Entries missing an SRV or an address are skipped (an incomplete announcement is not a device). Device name comes from the TXT `name=` if present, else the instance label of the SRV name (the part before the first `.`).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using Bmd.Devices.Discovery;

namespace Bmd.Tests.Devices.Discovery;

public class DiscoveredDeviceTests
{
    static readonly IPAddress Address = IPAddress.Parse("10.0.0.5");

    static List<DnsRecord> Records(string instance = "Studio Hub._blackmagic._tcp.local",
                                   string target = "studio-hub.local",
                                   string? deviceClass = "Videohub",
                                   string? name = "Studio Hub",
                                   bool withAddress = true)
    {
        var records = new List<DnsRecord>
        {
            new PtrRecord("_blackmagic._tcp.local", instance),
            new SrvRecord(instance, target, 9990),
        };
        var entries = new List<string>();
        if (deviceClass is not null) entries.Add($"class={deviceClass}");
        if (name is not null) entries.Add($"name={name}");
        if (entries.Count > 0) records.Add(new TxtRecord(instance, entries));
        if (withAddress) records.Add(new ARecord(target, Address));
        return records;
    }

    [Fact]
    public void FromRecords_AssemblesASupportedDevice()
    {
        var device = Assert.Single(DeviceAssembler.FromRecords(Records()));
        Assert.Equal("Studio Hub", device.Name);
        Assert.Equal("Videohub", device.DeviceClass);
        Assert.Equal("videohub", device.DeviceType);
        Assert.Equal(Address, device.Address);
        Assert.Equal(9990, device.Port);
    }

    [Fact]
    public void FromRecords_UnknownClass_HasNullDeviceType_ButIsStillReturned()
    {
        var device = Assert.Single(DeviceAssembler.FromRecords(Records(deviceClass: "AtemSwitcher")));
        Assert.Equal("AtemSwitcher", device.DeviceClass);
        Assert.Null(device.DeviceType);
    }

    [Fact]
    public void FromRecords_MissingTxt_StillYieldsADeviceWithUnknownClass()
    {
        var device = Assert.Single(DeviceAssembler.FromRecords(Records(deviceClass: null, name: null)));
        Assert.Equal("Studio Hub", device.Name);      // falls back to the instance label
        Assert.Equal("", device.DeviceClass);
        Assert.Null(device.DeviceType);
    }

    [Fact]
    public void FromRecords_NoAddress_IsSkipped()
    {
        Assert.Empty(DeviceAssembler.FromRecords(Records(withAddress: false)));
    }

    [Fact]
    public void FromRecords_NoSrv_IsSkipped()
    {
        var records = new List<DnsRecord> { new PtrRecord("_blackmagic._tcp.local", "Ghost._blackmagic._tcp.local") };
        Assert.Empty(DeviceAssembler.FromRecords(records));
    }

    [Fact]
    public void FromRecords_MultipleDevices_AreAllReturned()
    {
        var records = Records();
        records.AddRange(Records("Second Hub._blackmagic._tcp.local", "second-hub.local", "Videohub", "Second Hub"));
        records.Add(new ARecord("second-hub.local", IPAddress.Parse("10.0.0.6")));
        var devices = DeviceAssembler.FromRecords(records);
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Name == "Second Hub");
    }

    [Theory]
    [InlineData("Videohub", "videohub")]
    [InlineData("videohub", "videohub")]
    [InlineData("SmartVideohub", "videohub")]
    [InlineData("AtemSwitcher", null)]
    [InlineData("", null)]
    public void DeviceTypeFor_MapsKnownClassesCaseInsensitively(string deviceClass, string? expected)
    {
        Assert.Equal(expected, DeviceClasses.DeviceTypeFor(deviceClass));
    }
}
```

Note the last `Records(...)` call in the multi-device test adds a duplicate A record; the assembler must tolerate that (last or first wins, either is fine — assert only the count and names).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter DiscoveredDeviceTests`

- [ ] **Step 3: Implement**

Straightforward: build lookups (`SrvRecord` list; TXT by name; A by name), then project. Parse TXT entries as `key=value` splitting on the first `=`, case-insensitive keys. `DeviceClasses.DeviceTypeFor` does a case-insensitive scan of `KnownVideohubClasses`.

The XML doc on `KnownVideohubClasses` must state plainly that the values are unverified against hardware and that `bmd discover --all` is how to learn the real ones.

- [ ] **Step 4: Run tests, then the full suite**

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: assemble discovered devices from DNS records with class mapping"
```

---

### Task 3: mDNS client

**Files:**
- Create: `src/Bmd/Devices/Discovery/MdnsClient.cs`, `tests/Bmd.Tests/Devices/Discovery/MdnsClientTests.cs`

**Interfaces:**
- Produces:
  - `static class MdnsServices { const string Blackmagic = "_blackmagic._tcp.local"; const string BmdBlockConfig = "_bmd_blockcfg._tcp.local"; static IReadOnlyList<string> All { get; } }`
  - `sealed class MdnsClient` with `static Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan window, CancellationToken ct = default)` — sends a PTR query for each service on 224.0.0.251:5353 from every suitable local interface, collects datagrams until the window elapses, parses each (ignoring ones that fail to parse), pools all records, and returns `DeviceAssembler.FromRecords`, de-duplicated by (Address, Port).
  - An overload taking an explicit local endpoint and multicast group so tests can drive it on loopback: `static Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(IPEndPoint group, IPAddress localAddress, TimeSpan window, CancellationToken ct = default)`.

**Testability note:** true multicast is unreliable in CI and on locked-down machines. The tests use the explicit-endpoint overload against a loopback UDP responder that replies to any datagram with the golden response packet from Task 1. That exercises send → receive → parse → assemble without depending on real multicast routing. Document in the report that real multicast is therefore **not** covered by tests and is exercised only by the milestone's manual smoke.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Net.Sockets;
using Bmd.Devices.Discovery;

namespace Bmd.Tests.Devices.Discovery;

public class MdnsClientTests
{
    /// <summary>Replies to any datagram with a canned mDNS response.</summary>
    sealed class FakeResponder : IDisposable
    {
        readonly UdpClient _udp;
        readonly CancellationTokenSource _cts = new();
        public int Port { get; }

        public FakeResponder(byte[] response)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var received = await _udp.ReceiveAsync(_cts.Token);
                        await _udp.SendAsync(response, response.Length, received.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
            });
        }

        public void Dispose() { _cts.Cancel(); _udp.Dispose(); _cts.Dispose(); }
    }

    [Fact]
    public async Task Discover_FindsADeviceFromAResponse()
    {
        using var responder = new FakeResponder(GoldenResponse());
        var devices = await MdnsClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, responder.Port), IPAddress.Loopback,
            TimeSpan.FromSeconds(2));

        var device = Assert.Single(devices);
        Assert.Equal("Studio Hub", device.Name);
        Assert.Equal("videohub", device.DeviceType);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), device.Address);
    }

    [Fact]
    public async Task Discover_NoResponders_ReturnsEmptyWithoutThrowing()
    {
        var devices = await MdnsClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, 59999), IPAddress.Loopback,
            TimeSpan.FromMilliseconds(400));
        Assert.Empty(devices);
    }

    [Fact]
    public async Task Discover_GarbageResponse_IsIgnoredNotFatal()
    {
        using var responder = new FakeResponder([1, 2, 3]);
        var devices = await MdnsClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, responder.Port), IPAddress.Loopback,
            TimeSpan.FromMilliseconds(600));
        Assert.Empty(devices);
    }

    [Fact]
    public async Task Discover_Cancellation_ReturnsPromptly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var started = DateTime.UtcNow;
        var devices = await MdnsClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, 59999), IPAddress.Loopback,
            TimeSpan.FromSeconds(30), cts.Token);
        Assert.Empty(devices);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "cancellation must cut the window short");
    }

    static byte[] GoldenResponse() => Convert.FromHexString(/* same hex as DnsMessageTests */ "…");
}
```

Share the golden hex by promoting it to an `internal static class DiscoveryFixtures { public const string ResponseHex = "…"; }` in the test project and referencing it from both test classes — do not duplicate the literal.

- [ ] **Step 2: Run tests to verify they fail**

- [ ] **Step 3: Implement**

- Open a `UdpClient` bound to `localAddress:0`; for the multicast overload, additionally join 224.0.0.251 on each up, multicast-capable, non-loopback interface and set `MulticastLoopback = false`, `Ttl = 255`.
- Send `DnsMessage.EncodeQuery(service)` for each service in `MdnsServices.All` to the group endpoint.
- Loop receiving until the window elapses or cancellation: `await udp.ReceiveAsync(linkedToken)`, `try { records.AddRange(DnsMessage.ParseRecords(result.Buffer)); } catch (DnsFormatException) { }`.
- On timeout/cancel, assemble and return. Cancellation returns what was collected so far rather than throwing.
- `SocketException` while sending on one interface must not abort the whole discovery (a machine can have interfaces that refuse multicast) — collect and continue; if **every** send fails, throw the last `SocketException` so the command reports it.

- [ ] **Step 4: Run tests, then the full suite**

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: minimal mDNS client collecting Blackmagic responses"
```

---

### Task 4: `bmd discover` (list, `--all`, `--json`)

**Files:**
- Modify: `src/Bmd/Commands/` (new `DiscoverCommands.cs`), `src/Bmd/Output/BmdJsonContext.cs`, `src/Bmd/Program.cs`, `src/Bmd/Commands/GroupHelp.cs`
- Create: `tests/Bmd.Tests/Commands/DiscoverCommandsTests.cs`

**Interfaces:**
- Produces:
  - `sealed record DiscoveredDeviceResult(string Name, string DeviceClass, string? DeviceType, string Address, int Port)` registered in `BmdJsonContext` (as an array)
  - `class DiscoverCommands` with a testable seam: constructors `()` and `(Func<TimeSpan, CancellationToken, Task<IReadOnlyList<DiscoveredDevice>>> discover, Func<ConfigStore> loadConfig)`
  - `Task<int> Discover(int? timeout = null, bool all = false, bool json = false, CancellationToken ct = default)` registered as `discover`
- Behavior: default lists only devices with a non-null `DeviceType`; `--all` lists everything found, with a `TYPE` column showing the raw class for unsupported ones. Human output is a table (`NAME`, `TYPE`, `ADDRESS`); empty result prints a short note to **stderr** explaining that nothing answered and that mDNS does not cross subnets, exit 0 with no stdout rows. `--json` always emits an array (`[]` when empty). Timeout default 3 s; non-positive → exit 2.
- `discover` is a top-level command, not a group, so `GroupHelp` needs it listed in the root table only — check how `GroupHelp.Commands` is consumed before adding.

- [ ] **Step 1: Write the failing tests**

Use the console/temp-dir harness from `VideohubRouteSetTests`. Drive the command through the injected discover function (no sockets in command tests):

```csharp
    [Fact] public async Task Discover_ListsOnlySupportedDevicesByDefault()
    [Fact] public async Task Discover_All_ListsUnsupportedOnesToo()
    [Fact] public async Task Discover_Json_IsAnArrayWithCamelCaseFields()
    [Fact] public async Task Discover_NothingFound_Exit0_EmptyJsonArray()
    [Fact] public async Task Discover_NothingFound_Human_NoteOnStderr_NothingOnStdout()
    [Fact] public async Task Discover_NonPositiveTimeout_Exit2()
    [Fact] public async Task Discover_SocketFailure_Exit1_CleanError()
```

Write each fully, asserting exit codes, stdout/stderr split, and JSON fields.

- [ ] **Step 2-5: RED, implement, GREEN, verify help, commit**

```powershell
git add -A; git commit -m "feat: bmd discover listing Blackmagic devices found on the network"
```

---

### Task 5: `bmd discover --add`

**Files:**
- Modify: `src/Bmd/Commands/DiscoverCommands.cs`, `tests/Bmd.Tests/Commands/DiscoverCommandsTests.cs`

**Interfaces:**
- Adds `bool add = false` and `bool global = false` to `Discover`.
- Behavior:
  - `--add` runs discovery, prints the **supported** devices as a numbered list to stderr (so stdout stays clean), prompts `Select a device [1-N] (or q to cancel): ` on stderr, reads a line from stdin.
  - A valid selection writes `<deviceType>.host` (and `.port` when it is not the default 9990) via `ConfigStore.Set`, local unless `--global`, then confirms on stdout: `Set videohub.host = 10.0.0.5 in <file>`. With `--json`, emit the same `ConfigSetResult` shape the config commands use — reuse that record rather than inventing a second shape.
  - `q`/empty/EOF cancels: message on stderr, exit 0, nothing written.
  - Invalid selection (non-numeric or out of range): `error: ...`, exit 2, nothing written.
  - **Non-interactive stdin** (`Console.IsInputRedirected`) → `error: --add needs an interactive terminal; run bmd config set <type>.host <address> instead`, exit 2.
  - Exactly one supported device found: still prompt (consistency, and it confirms intent before touching config).
  - No supported devices found: the same helpful stderr note as plain discover, exit 0, nothing written.
  - `--add --all` is a usage error (exit 2): you can only add a device bmd supports.

- [ ] **Step 1: Write the failing tests**

The harness must redirect `Console.In` (as milestone 5's stdin restore test does). Cover: successful selection writes config and confirms; `q` cancels; out-of-range exits 2; non-numeric exits 2; `--add --all` exits 2; nothing found exits 0 without writing; `--global` writes to the global file. Assert the config file contents, not just the exit code.

Note `Console.IsInputRedirected` is true under the test runner, so the non-interactive guard must be injectable (add a `Func<bool> isInteractive` to the test constructor seam, defaulting to `() => !Console.IsInputRedirected`) — otherwise every `--add` test hits the guard. Add one test that the guard fires when the seam reports non-interactive.

- [ ] **Step 2-5: RED, implement, GREEN, verify help, commit**

```powershell
git add -A; git commit -m "feat: bmd discover --add writing the chosen device into config"
```

---

### Task 6: Milestone proof — AOT publish, smokes, help audit, and a REAL network run

**Files:** none expected

- [ ] **Step 1: Full suite + publish**

```powershell
dotnet test
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/Bmd -c Release -r win-x64
```

Zero IL2xxx/IL3xxx warnings. The AOT-sensitive additions are the new JSON result type and the socket/multicast code.

- [ ] **Step 2: Native smokes and help audit**

```powershell
$exe = "src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe"
& $exe --help                      # discover listed
& $exe discover --help             # --all, --add, --global, --timeout, --json documented
& $exe discover --timeout 0; $LASTEXITCODE          # expect 2
& $exe discover --add --all; $LASTEXITCODE          # expect 2
```

- [ ] **Step 3: A REAL discovery run on this machine**

```powershell
& $exe discover --all --timeout 5 --json
& $exe discover --all --timeout 5
```

This is the one thing tests cannot cover: real multicast against whatever is on this network. Report the **verbatim output**, whatever it is — including finding nothing. If any Blackmagic device answers, **record its exact `deviceClass` string**: that is the empirical data the seed mapping table needs, and the whole reason `--all` exists. If nothing answers, say so plainly; do not present an empty result as a failure or invent devices.

- [ ] **Step 4: Verify no stray state, record size, commit**

```powershell
Test-Path .bmdconfig
(Get-Item src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe).Length / 1MB
git status --short
git add -A; git commit -m "chore: prove milestone 7 (discovery) on Native AOT" --allow-empty
```

---

## Self-Review Notes

- **Spec coverage:** both service types queried; 3 s default window; PTR→SRV/TXT/A resolution; class filtering with `--all` to learn real values; `--add` with numbered prompt, config write, and a clear non-TTY error; hand-rolled client with pure-function packet parsing tested against fixtures; lives in `Devices/Discovery/`.
- **Known coverage gap, stated up front:** real multicast is not unit-testable here — the client tests use a loopback responder via an explicit-endpoint overload, and the only real-network evidence is Task 6's manual run. Milestone 6's reviewer praised an honest limitation statement over a fabricated one; do the same.
- **The mapping table is a guess until hardware says otherwise.** Everything is built so an unrecognized class degrades to "listed under `--all`, `deviceType: null`" rather than an error, so a wrong guess costs nothing but a missing convenience.
- **Deliberate:** finding nothing is exit 0. Discovery is best-effort; a hub may predate mDNS or be on another subnet, and a non-zero exit would make `discover` unusable in a conditional.
- **Not addressed (carried forward):** `SendBlockAsync`'s writes remain unbounded by the timeout (milestone 4 deferral); Ctrl+C during connect isn't plumbed (milestone 6 note); `watch | head` leaves a lingering process (document on the site in milestone 8).
