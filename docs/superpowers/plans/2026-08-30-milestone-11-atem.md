# Milestone 11: ATEM transport and read path — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `bmd atem info` / `input list` / `status` against a real Blackmagic ATEM, built on a hand-rolled binary UDP client — and nothing that changes the switcher.

**Architecture:** A new `Devices/Atem/` sharing nothing below the command layer with the Videohub-protocol devices. Pure functions for the 12-byte header codec and the command-block framing, a UDP session on top, then a typed state model over seven block types with every other block retained verbatim. Tests run against a real hardware capture replayed by an in-process fake.

**Tech Stack:** .NET 10, Native AOT, ConsoleAppFramework v5, System.Text.Json source generators, `System.Net.Sockets.UdpClient`, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-30-atem-design.md` — read it first. It records *why* correctness here is empirical rather than specified, which matters for how you treat the documentation.

## Global Constraints

- **Native AOT always on.** No reflection, no `dynamic`, no reflection-based JSON, **no new NuGet packages**. Community ATEM libraries exist and are deliberately not used.
- **JSON: System.Text.Json source generators only**, via `BmdJsonContext`.
- **`--json` emits exactly ONE document on stdout**, camelCase, stable field names. `--json` changes representation, never behavior.
- **Errors:** one clear message to stderr, no stack traces. **Exit codes: 0 success, 1 operation failure, 2 usage/format error.**
- **Help text is the API** — every command, argument and flag documented via XML doc comments.
- **Layering:** `Devices/` never references ConsoleAppFramework or `Commands/`. Command classes stay thin.
- **No environment variables for configuration.** Reads layer flag > local `.bmdconfig` > user config > default; writes go to the user config unless `--project`.
- **TDD.** RED then GREEN evidence in every task report.
- **The site ships with the change.**
- Project is on **xunit 2.9.3** — `TestContext.Current` does not exist (that is xUnit v3).

## Milestone-specific hard rules

- **READ ONLY. No command that changes the switcher.** Not `CPgI`, not `CPvI`, not `DCut`, not `DAut`. Those are milestone 12. A handshake, ACKs and keepalives are the only things ever sent.
- **Do not connect to any real device.** `192.168.4.98` was probed once, under explicit one-off permission that has expired. Everything is tested against the fixture and the fake.
- **Always advance by the block's own length field, never by the payload size you expect.** This is the single most important rule in the milestone — see the evidence in Task 3.
- **Do not validate the block's bytes 2–3 as zero.** On this firmware they are consistently `0x0014`. Skip them.
- **`_top` is the authority on device shape.** Never hardcode a count observed on the HD III; another model reports different numbers.

## Ground truth from the capture

Everything below was observed on a real ATEM Television Studio HD III and is what the tests assert against. It is not derived from documentation.

**Transport**
- Header is 12 bytes. Bytes 0–1 are `(flags << 11) | length`, big-endian: 5 bits of flags, 11 bits of total packet length including the header.
- Flags seen: `0x01` AckRequest, `0x02` Hello, `0x04` Resend, `0x10` Ack.
- Handshake: client sends Hello (20 bytes: header + `01 00 00 00 00 00 00 00`), switcher replies Hello with payload byte 0 = `0x02` for success, client sends Ack.
- **The switcher reassigns the session id.** Probe opened `0x7A56`, got it echoed, then every later packet used `0x8003` (and `0x8004` on a second run). Adopt the session from the switcher's packets.
- Dump arrives as packets with sequence 1..5, flags `0x01`, sizes 1422 / 1420 / 1416 / 1408 / 680.
- **Dump completion is signalled by the first packet with no payload** (length 12). There is no distinctly-flagged end marker.
- After the dump, the switcher sends 12-byte keepalives (flags `0x11`) roughly continuously. Each must be ACKed or the session drops.
- **Retransmission is routine, not exceptional.** In a 6-second capture, packets 8 and 13 arrived flagged `Resend` (`0x05`) repeating sequence 7 and 11, because the probe was slow to ACK. A client that does not de-duplicate by sequence number will apply the same state twice.

**Block framing**
- 2 bytes block length (includes the 8-byte block header), 2 bytes constant `0x0014`, 4 bytes ASCII name, then payload.
- 287 blocks across the 5 data packets, 73 distinct names, parsed with no desync.

**Decoded values the tests assert on**
- `_ver` payload `00 02 00 1e` → protocol 2.30.
- `_pin` → product name `ATEM Television Studio HD`.
- `_top` payload begins `01 18 02 01` → 1 M/E, 24 sources, 2 DSKs, 1 aux.
- `InPr` ×24. Known source id → long name / short name:
  `0` Black / `BLK`, `1` Presenter / `PRES`, `4` Stage / `STG`, `5` Media / `MEDI`,
  `6` Lyrics / `LYRC`, `7` Confidence / `CONF`, `1000` Color Bars / `BARS`,
  `10010` Live On Screens / `PGM`, `10011` Not Live / `PVW`.
- `PrgI` payload `00 00 00 06` → M/E 0, source 6 (Lyrics).
- **`PrvI` payload is 8 bytes on this firmware where the documentation says 4**: `00 01 00 01 00 01 00 2c`. The leading four decode as M/E 0, source 1 (Presenter). The trailing four are undocumented and must be ignored, not rejected.
- Source id ranges (independently confirmed against PyATEMMax's constants): `0` black, `1–8` external inputs, `1000` colour bars, `2001+` colour generators, `3010+` media players, `4010+` key masks, `5010+` DSK masks, `7001+` clean feeds, `8001+` aux, `10010/10011` M/E 1 program / preview.

---

## File structure

**Created**

| File | Responsibility |
|---|---|
| `src/Bmd/Devices/Atem/AtemPacket.cs` | 12-byte header encode/decode. Pure. |
| `src/Bmd/Devices/Atem/AtemCommandBlock.cs` | Block framing and enumeration. Pure. |
| `src/Bmd/Devices/Atem/AtemState.cs` | Typed device model + retained unknown blocks. |
| `src/Bmd/Devices/Atem/AtemDumpParser.cs` | Blocks → state. Pure. |
| `src/Bmd/Devices/Atem/AtemClient.cs` | UDP session: handshake, ACK, dedupe, dump read. |
| `src/Bmd/Commands/Atem/AtemCommands.cs` | The `bmd atem` group. |
| `src/Bmd/Commands/Atem/AtemResults.cs` | `--json` result records. |
| `tests/Bmd.Tests/Devices/Atem/AtemFixtures.cs` | The hardware capture, embedded. |
| `tests/Bmd.Tests/Devices/Atem/FakeAtem.cs` | In-process UDP switcher replaying the capture. |
| `site/atem.html` | The ATEM guide page. |

**Modified:** `BmdJsonContext.cs`, `Program.cs`, `GroupHelp.cs`, `DiscoveredDevice.cs`, `DiscoveredDeviceTests.cs`, `site/index.html`, `CLAUDE.md`, the main spec's milestone list.

---

### Task 1: Embed the hardware capture

This is first because the capture is irreplaceable without hardware access, and every later task tests against it.

**Files:**
- Create: `tests/Bmd.Tests/Devices/Atem/AtemFixtures.cs`
- Source data: `docs/superpowers/plans/assets/atem-hd3-statedump.hex`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `AtemFixtures.StateDumpPackets` — `IReadOnlyList<byte[]>`, 19 packets in arrival order.
  - `AtemFixtures.DataPackets` — `IReadOnlyList<byte[]>`, the 5 packets carrying command blocks.

- [ ] **Step 1: Write the failing test**

Create `tests/Bmd.Tests/Devices/Atem/AtemFixturesTests.cs`:

```csharp
namespace Bmd.Tests.Devices.Atem;

public class AtemFixturesTests
{
    [Fact]
    public void StateDumpPackets_MatchTheCapturedShape()
    {
        var packets = AtemFixtures.StateDumpPackets;

        Assert.Equal(19, packets.Count);
        Assert.Equal(6514, packets.Sum(p => p.Length));
        Assert.Equal([1422, 1420, 1416, 1408, 680], packets.Take(5).Select(p => p.Length));
        // 6..19 are the switcher's 12-byte keepalives.
        Assert.All(packets.Skip(5), p => Assert.Equal(12, p.Length));
    }

    [Fact]
    public void DataPackets_AreTheFiveThatCarryBlocks()
    {
        Assert.Equal(5, AtemFixtures.DataPackets.Count);
        Assert.All(AtemFixtures.DataPackets, p => Assert.True(p.Length > 12));
    }

    [Fact]
    public void FirstPacketStartsWithTheVersionBlock()
    {
        // Ground truth: the dump opens with _ver carrying protocol 2.30.
        var first = AtemFixtures.StateDumpPackets[0];
        Assert.Equal("_ver", System.Text.Encoding.ASCII.GetString(first, 16, 4));
        Assert.Equal(0x00, first[20]);
        Assert.Equal(0x02, first[21]);   // major 2
        Assert.Equal(0x00, first[22]);
        Assert.Equal(0x1e, first[23]);   // minor 30
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemFixturesTests`
Expected: FAIL — compile error, `AtemFixtures` does not exist.

- [ ] **Step 3: Generate the fixture file**

Read `docs/superpowers/plans/assets/atem-hd3-statedump.hex`. It has five `#` comment lines followed by 19 hex lines, one per packet, in arrival order.

Create `tests/Bmd.Tests/Devices/Atem/AtemFixtures.cs` as a C# class holding those 19 hex strings as `const string` values (or one `string[]`), plus the decoding below. Copy the hex verbatim — do not reformat, re-wrap, or "tidy" it.

```csharp
namespace Bmd.Tests.Devices.Atem;

/// <summary>The state dump a real Blackmagic ATEM Television Studio HD III sent on connect,
/// captured read-only on 2026-08-30 (handshake and protocol-required ACKs only).
///
/// This is the milestone's ground truth. The ATEM wire protocol is not published by Blackmagic,
/// so a fixture hand-written from community documentation would only prove that our parser and
/// our reading of those documents agree — which is exactly the failure this capture exists to
/// prevent. It already contains one case the documentation gets wrong (an over-long PrvI block)
/// and one it omits (block bytes 2-3 are 0x0014, not zero).
///
/// It cannot be re-captured without hardware access. Treat it as read-only.</summary>
public static class AtemFixtures
{
    // Packets 1-5 carry the 287 command blocks; 6-19 are 12-byte keepalives, two of them
    // Resend retransmissions the switcher sent when the capture was slow to acknowledge.
    static readonly string[] Hex =
    [
        "…packet 1 hex…",
        // … all 19 lines, in order …
    ];

    public static IReadOnlyList<byte[]> StateDumpPackets { get; } =
        [.. Hex.Select(Convert.FromHexString)];

    /// <summary>Just the packets carrying command blocks — everything longer than a bare header.</summary>
    public static IReadOnlyList<byte[]> DataPackets { get; } =
        [.. StateDumpPackets.Where(p => p.Length > 12)];
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemFixturesTests`
Expected: PASS. If the byte total is not exactly 6514, a hex line was truncated or reordered — fix the fixture, not the test.

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet test` — the baseline is **589 tests**; all stay green.

```bash
git add tests/Bmd.Tests/Devices/Atem/
git commit -m "test(atem): embed the captured ATEM state dump as a fixture"
```

---

### Task 2: AtemPacket — the header codec

**Files:**
- Create: `src/Bmd/Devices/Atem/AtemPacket.cs`
- Test: `tests/Bmd.Tests/Devices/Atem/AtemPacketTests.cs`

**Interfaces:**
- Consumes: `AtemFixtures` (Task 1).
- Produces, in namespace `Bmd.Devices.Atem`:
  - `[Flags] enum AtemFlags { None = 0, AckRequest = 1, Hello = 2, Resend = 4, RequestNext = 8, Ack = 16 }`
  - `readonly record struct AtemHeader(AtemFlags Flags, int Length, int Session, int AckedId, int SequenceId)`
  - `static bool TryReadHeader(ReadOnlySpan<byte> packet, out AtemHeader header)`
  - `static byte[] WriteHeader(AtemFlags flags, int length, int session, int ackedId, int sequenceId)`
  - `const int HeaderSize = 12`

- [ ] **Step 1: Write the failing tests**

Create `tests/Bmd.Tests/Devices/Atem/AtemPacketTests.cs`:

```csharp
using Bmd.Devices.Atem;

namespace Bmd.Tests.Devices.Atem;

public class AtemPacketTests
{
    [Fact]
    public void TryReadHeader_DecodesTheFirstRealDumpPacket()
    {
        // Ground truth from the capture: flags 0x01, length 1422, session 0x8004, sequence 1.
        Assert.True(AtemPacket.TryReadHeader(AtemFixtures.StateDumpPackets[0], out var h));

        Assert.Equal(AtemFlags.AckRequest, h.Flags);
        Assert.Equal(1422, h.Length);
        Assert.Equal(0x8004, h.Session);
        Assert.Equal(0, h.AckedId);
        Assert.Equal(1, h.SequenceId);
    }

    [Fact]
    public void Length_IsTheWholePacketIncludingTheHeader()
    {
        // The length field is 11 bits sharing two bytes with 5 bits of flags. Reading those two
        // bytes as independent fields yields 910 here instead of 1422 - the first available bug.
        foreach (var packet in AtemFixtures.StateDumpPackets)
        {
            Assert.True(AtemPacket.TryReadHeader(packet, out var h));
            Assert.Equal(packet.Length, h.Length);
        }
    }

    [Fact]
    public void TryReadHeader_ReadsTheKeepaliveFlags()
    {
        // Packet 6 is the first bare keepalive: AckRequest | Ack.
        Assert.True(AtemPacket.TryReadHeader(AtemFixtures.StateDumpPackets[5], out var h));
        Assert.Equal(AtemFlags.AckRequest | AtemFlags.Ack, h.Flags);
        Assert.Equal(12, h.Length);
    }

    [Fact]
    public void TryReadHeader_ReadsTheResendFlagOnARetransmission()
    {
        // Packet 8 repeats sequence 7 flagged Resend - the switcher retransmitting because the
        // capture was slow to acknowledge. Routine, not exceptional.
        Assert.True(AtemPacket.TryReadHeader(AtemFixtures.StateDumpPackets[7], out var h));
        Assert.True(h.Flags.HasFlag(AtemFlags.Resend));
        Assert.Equal(7, h.SequenceId);
    }

    [Fact]
    public void TryReadHeader_RejectsAPacketShorterThanAHeader()
    {
        Assert.False(AtemPacket.TryReadHeader(new byte[11], out _));
        Assert.False(AtemPacket.TryReadHeader([], out _));
    }

    [Fact]
    public void WriteHeader_RoundTripsThroughTryReadHeader()
    {
        var bytes = AtemPacket.WriteHeader(AtemFlags.Hello, 20, 0x7A56, 0, 0);

        Assert.Equal(12, bytes.Length);
        Assert.True(AtemPacket.TryReadHeader(bytes, out var h));
        Assert.Equal(AtemFlags.Hello, h.Flags);
        Assert.Equal(20, h.Length);
        Assert.Equal(0x7A56, h.Session);
    }

    [Fact]
    public void WriteHeader_PacksFlagsAndLengthIntoTheSameTwoBytes()
    {
        // 1422 needs 11 bits, so it spills into the byte that also carries the flags.
        var bytes = AtemPacket.WriteHeader(AtemFlags.AckRequest, 1422, 0x8004, 0, 1);
        Assert.Equal(0x0d, bytes[0]);
        Assert.Equal(0x8e, bytes[1]);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemPacketTests`
Expected: FAIL — `AtemPacket` does not exist.

- [ ] **Step 3: Implement**

Create `src/Bmd/Devices/Atem/AtemPacket.cs`:

```csharp
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
    /// <summary>This is a retransmission of a packet the peer did not acknowledge.</summary>
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
/// fields decodes the first real dump packet as length 910 instead of 1422, and everything
/// downstream desynchronises.</summary>
public static class AtemPacket
{
    public const int HeaderSize = 12;

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
        var word = (((int)flags & 0x1F) << 11) | (length & 0x07FF);
        bytes[0] = (byte)(word >> 8);
        bytes[1] = (byte)word;
        bytes[2] = (byte)(session >> 8);
        bytes[3] = (byte)session;
        bytes[4] = (byte)(ackedId >> 8);
        bytes[5] = (byte)ackedId;
        bytes[10] = (byte)(sequenceId >> 8);
        bytes[11] = (byte)sequenceId;
        return bytes;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemPacketTests`
Expected: PASS.

- [ ] **Step 5: Run the full suite and commit**

```bash
git add src/Bmd/Devices/Atem/ tests/Bmd.Tests/Devices/Atem/
git commit -m "feat(atem): 12-byte packet header codec"
```

---

### Task 3: AtemCommandBlock — block framing

**Files:**
- Create: `src/Bmd/Devices/Atem/AtemCommandBlock.cs`
- Test: `tests/Bmd.Tests/Devices/Atem/AtemCommandBlockTests.cs`

**Interfaces:**
- Consumes: `AtemPacket.HeaderSize` (Task 2), `AtemFixtures` (Task 1).
- Produces:
  - `readonly record struct AtemCommandBlock(string Name, ReadOnlyMemory<byte> Payload)`
  - `static IReadOnlyList<AtemCommandBlock> ReadBlocks(ReadOnlyMemory<byte> packet)`

**This task carries the milestone's most important rule.** A block declares its own length. Parse the fields you understand from the front of a payload and ignore anything trailing; advance by the declared length, never by the size you expected. The capture contains a `PrvI` block that is 8 payload bytes where the documentation says 4 — trusting the documented size desynchronises every remaining block in that packet, and a fixture written from the same documentation would have hidden it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bmd.Tests/Devices/Atem/AtemCommandBlockTests.cs`:

```csharp
using System.Text;
using Bmd.Devices.Atem;

namespace Bmd.Tests.Devices.Atem;

public class AtemCommandBlockTests
{
    static List<AtemCommandBlock> AllBlocks() =>
        [.. AtemFixtures.DataPackets.SelectMany(p => AtemCommandBlock.ReadBlocks(p))];

    [Fact]
    public void ReadBlocks_ReadsEveryBlockInTheCaptureWithoutDesynchronising()
    {
        // 287 blocks across the five data packets. A framing bug shows up as a short count or
        // as garbage names, because a desync mid-packet cannot recover.
        var blocks = AllBlocks();

        Assert.Equal(287, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(4, b.Name.Length));
        Assert.All(blocks, b => Assert.True(
            b.Name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'),
            $"block name '{b.Name}' is not plausible ASCII - the parser has desynchronised"));
    }

    [Fact]
    public void ReadBlocks_FindsTheExpectedDistinctCommandTypes()
    {
        var names = AllBlocks().Select(b => b.Name).Distinct().ToList();

        Assert.Equal(73, names.Count);
        foreach (var expected in new[] { "_ver", "_pin", "_top", "InPr", "PrgI", "PrvI", "VidM" })
            Assert.Contains(expected, names);
    }

    [Fact]
    public void ReadBlocks_CountsTheInputBlocks()
    {
        // The HD III reports 24 sources, one InPr each.
        Assert.Equal(24, AllBlocks().Count(b => b.Name == "InPr"));
    }

    [Fact]
    public void ReadBlocks_KeepsAnOverLongPayloadRatherThanTruncatingOrRejectingIt()
    {
        // PrvI is documented as a 4-byte payload; this firmware sends 8. The extra bytes must
        // survive to the parser, which ignores what it does not understand.
        var prvi = Assert.Single(AllBlocks().Where(b => b.Name == "PrvI"));
        Assert.Equal(8, prvi.Payload.Length);
    }

    [Fact]
    public void ReadBlocks_AdvancesByTheDeclaredLengthNotAnAssumedOne()
    {
        // Two blocks: the first declares a payload longer than any fixed guess would allow.
        // If the reader advances by anything other than the declared length, it will not find
        // the second block.
        var packet = new byte[AtemPacket.HeaderSize + 20 + 12];
        // block 1: length 20, constant 0x0014, name "AAAA", 12 payload bytes
        packet[12] = 0x00; packet[13] = 20; packet[14] = 0x00; packet[15] = 0x14;
        Encoding.ASCII.GetBytes("AAAA").CopyTo(packet, 16);
        // block 2: length 12, constant 0x0014, name "BBBB", 4 payload bytes
        packet[32] = 0x00; packet[33] = 12; packet[34] = 0x00; packet[35] = 0x14;
        Encoding.ASCII.GetBytes("BBBB").CopyTo(packet, 36);

        var blocks = AtemCommandBlock.ReadBlocks(packet);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("AAAA", blocks[0].Name);
        Assert.Equal(12, blocks[0].Payload.Length);
        Assert.Equal("BBBB", blocks[1].Name);
    }

    [Fact]
    public void ReadBlocks_IgnoresBytes2To3RatherThanRequiringThemToBeZero()
    {
        // On this firmware those bytes are consistently 0x0014. A parser that validates them as
        // reserved-zero rejects every real packet.
        Assert.All(AtemFixtures.DataPackets, p =>
        {
            Assert.Equal(0x00, p[14]);
            Assert.Equal(0x14, p[15]);
        });
        Assert.NotEmpty(AtemCommandBlock.ReadBlocks(AtemFixtures.DataPackets[0]));
    }

    [Fact]
    public void ReadBlocks_ReturnsNothingForAHeaderOnlyPacket()
    {
        Assert.Empty(AtemCommandBlock.ReadBlocks(AtemFixtures.StateDumpPackets[5]));
    }

    [Fact]
    public void ReadBlocks_StopsCleanlyOnATruncatedTrailingBlock()
    {
        // A block claiming more bytes than remain is dropped rather than throwing: a partial
        // read must not take down the connection.
        var packet = new byte[AtemPacket.HeaderSize + 8];
        packet[12] = 0x00; packet[13] = 60; packet[14] = 0x00; packet[15] = 0x14;
        Encoding.ASCII.GetBytes("CCCC").CopyTo(packet, 16);

        Assert.Empty(AtemCommandBlock.ReadBlocks(packet));
    }

    [Fact]
    public void ReadBlocks_StopsOnAnImpossiblyShortBlockLength()
    {
        var packet = new byte[AtemPacket.HeaderSize + 8];
        packet[12] = 0x00; packet[13] = 4;   // shorter than the 8-byte block header
        Assert.Empty(AtemCommandBlock.ReadBlocks(packet));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemCommandBlockTests`
Expected: FAIL — `AtemCommandBlock` does not exist.

- [ ] **Step 3: Implement**

Create `src/Bmd/Devices/Atem/AtemCommandBlock.cs`:

```csharp
using System.Text;

namespace Bmd.Devices.Atem;

/// <summary>One command block from an ATEM packet: a 4-character ASCII name and its payload.</summary>
public readonly record struct AtemCommandBlock(string Name, ReadOnlyMemory<byte> Payload);

/// <summary>Splits an ATEM packet's body into command blocks.
///
/// Layout: 2 bytes length (counting these 8 header bytes), 2 bytes that are constant 0x0014 on
/// observed firmware, 4 bytes ASCII name, then payload.
///
/// <b>The reader advances by the declared length and nothing else.</b> The captured dump contains
/// a PrvI block carrying 8 payload bytes where the published community documentation says 4;
/// advancing by an assumed size desynchronises every remaining block in that packet. Callers
/// parse the fields they understand from the front of a payload and ignore the rest.</summary>
public static class AtemCommandBlock
{
    const int BlockHeaderSize = 8;

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

            var name = Encoding.ASCII.GetString(span.Slice(offset + 4, 4));
            blocks.Add(new AtemCommandBlock(
                name,
                packet.Slice(offset + BlockHeaderSize, length - BlockHeaderSize)));
            offset += length;
        }
        return blocks;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemCommandBlockTests`
Expected: PASS, including 287 blocks and 73 distinct names.

- [ ] **Step 5: Run the full suite and commit**

```bash
git add src/Bmd/Devices/Atem/ tests/Bmd.Tests/Devices/Atem/
git commit -m "feat(atem): command block framing, advancing by declared length"
```

---

### Task 4: AtemState and AtemDumpParser

**Files:**
- Create: `src/Bmd/Devices/Atem/AtemState.cs`, `src/Bmd/Devices/Atem/AtemDumpParser.cs`
- Test: `tests/Bmd.Tests/Devices/Atem/AtemDumpParserTests.cs`

**Interfaces:**
- Consumes: `AtemCommandBlock.ReadBlocks` (Task 3), `AtemFixtures` (Task 1).
- Produces:
  - `sealed record AtemSource(int Id, string LongName, string ShortName)` — `Id` is the wire source id.
  - `sealed record AtemTopology(int MixEffects, int Sources, int DownstreamKeyers, int Auxiliaries)`
  - `sealed class AtemState` with `string ProductName`, `string ProtocolVersion`, `AtemTopology Topology`, `IReadOnlyList<AtemSource> Sources`, `int ProgramSource`, `int PreviewSource`, `int VideoMode`, `IReadOnlyDictionary<string, int> UnhandledBlockCounts`, and `AtemSource? FindSource(int id)`.
  - `static AtemState Parse(IEnumerable<AtemCommandBlock> blocks)`
  - `static bool IsExternalInput(int sourceId)` — true for ids 1..999, the range PyATEMMax and the hardware agree are physical inputs.

**Derive the payload offsets from the fixture, not from memory.** The tests below assert on decoded values that are known to be true for this capture; if an offset is wrong the values will be wrong and the test will say so. Do not assume the field positions in the spec's summary are exact — the probe that produced them had `InPr` offsets slightly off, which is why the assertions are on names and ids rather than on byte positions.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bmd.Tests/Devices/Atem/AtemDumpParserTests.cs`:

```csharp
using Bmd.Devices.Atem;

namespace Bmd.Tests.Devices.Atem;

public class AtemDumpParserTests
{
    static AtemState State() => AtemDumpParser.Parse(
        AtemFixtures.DataPackets.SelectMany(p => AtemCommandBlock.ReadBlocks(p)));

    [Fact]
    public void Parse_ReadsTheProductNameAndProtocolVersion()
    {
        var state = State();
        Assert.Equal("ATEM Television Studio HD", state.ProductName);
        Assert.Equal("2.30", state.ProtocolVersion);
    }

    [Fact]
    public void Parse_ReadsTheTopology()
    {
        var t = State().Topology;
        Assert.Equal(1, t.MixEffects);
        Assert.Equal(24, t.Sources);
        Assert.Equal(2, t.DownstreamKeyers);
        Assert.Equal(1, t.Auxiliaries);
    }

    [Fact]
    public void Parse_ReadsEverySourceWithItsNames()
    {
        var sources = State().Sources;
        Assert.Equal(24, sources.Count);

        Assert.Equal("Presenter", sources.Single(s => s.Id == 1).LongName);
        Assert.Equal("PRES", sources.Single(s => s.Id == 1).ShortName);
        Assert.Equal("Lyrics", sources.Single(s => s.Id == 6).LongName);
        Assert.Equal("Confidence", sources.Single(s => s.Id == 7).LongName);
        Assert.Equal("Black", sources.Single(s => s.Id == 0).LongName);
        Assert.Equal("Color Bars", sources.Single(s => s.Id == 1000).LongName);
        // The operator renamed program and preview on this switcher.
        Assert.Equal("Live On Screens", sources.Single(s => s.Id == 10010).LongName);
        Assert.Equal("Not Live", sources.Single(s => s.Id == 10011).LongName);
    }

    [Fact]
    public void Parse_ReadsProgramAndPreview()
    {
        var state = State();
        Assert.Equal(6, state.ProgramSource);    // Lyrics
        Assert.Equal(1, state.PreviewSource);    // Presenter
        Assert.Equal("Lyrics", state.FindSource(state.ProgramSource)!.LongName);
        Assert.Equal("Presenter", state.FindSource(state.PreviewSource)!.LongName);
    }

    [Fact]
    public void Parse_IgnoresTheUndocumentedTrailingBytesOnPreview()
    {
        // PrvI carries 8 payload bytes on this firmware where 4 are documented. The leading
        // four decode correctly and the rest must simply be ignored.
        Assert.Equal(1, State().PreviewSource);
    }

    [Fact]
    public void Parse_RetainsACountOfEveryBlockItDoesNotModel()
    {
        // 73 distinct types arrive and 7 are modelled. The other 66 are what milestone 12 needs;
        // discarding them silently would mean re-solving the transport to get them back.
        var unhandled = State().UnhandledBlockCounts;

        Assert.Equal(66, unhandled.Count);
        Assert.Equal(100, unhandled["MPrp"]);   // media player still metadata, the bulkiest
        Assert.DoesNotContain("InPr", unhandled.Keys);
        Assert.DoesNotContain("PrgI", unhandled.Keys);
    }

    [Fact]
    public void Parse_ToleratesADumpMissingEverythingItLooksFor()
    {
        // A different model, or a truncated read, must produce an empty state rather than throw.
        var state = AtemDumpParser.Parse([]);

        Assert.Equal("", state.ProductName);
        Assert.Empty(state.Sources);
        Assert.Null(state.FindSource(1));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(0, false)]        // black
    [InlineData(1000, false)]     // colour bars
    [InlineData(3010, false)]     // media player
    [InlineData(10010, false)]    // program output
    public void IsExternalInput_SeparatesRealInputsFromInternalSources(int id, bool expected)
    {
        Assert.Equal(expected, AtemDumpParser.IsExternalInput(id));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemDumpParserTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement the state model**

Create `src/Bmd/Devices/Atem/AtemState.cs`:

```csharp
namespace Bmd.Devices.Atem;

/// <summary>One video source the switcher can route: a physical input, or an internal source
/// such as colour bars, a media player, or the program output.</summary>
public sealed record AtemSource(int Id, string LongName, string ShortName);

/// <summary>What the switcher says it has. Always read this rather than assuming a model's
/// shape — an ATEM Mini Pro and a 1 M/E Production Studio differ substantially.</summary>
public sealed record AtemTopology(int MixEffects, int Sources, int DownstreamKeyers, int Auxiliaries);

/// <summary>A switcher's state as of the dump read at connect.</summary>
public sealed class AtemState
{
    public string ProductName { get; init; } = "";
    public string ProtocolVersion { get; init; } = "";
    public AtemTopology Topology { get; init; } = new(0, 0, 0, 0);
    public IReadOnlyList<AtemSource> Sources { get; init; } = [];
    public int ProgramSource { get; init; }
    public int PreviewSource { get; init; }
    public int VideoMode { get; init; }

    /// <summary>How many of each block type arrived that this parser does not model, keyed by the
    /// 4-character command name. Seventy-three types arrive and seven are modelled; the rest are
    /// what a later milestone needs, and counting them here means a new command's arrival is
    /// visible rather than silently dropped.</summary>
    public IReadOnlyDictionary<string, int> UnhandledBlockCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public AtemSource? FindSource(int id) => Sources.FirstOrDefault(s => s.Id == id);
}
```

- [ ] **Step 4: Implement the parser**

Create `src/Bmd/Devices/Atem/AtemDumpParser.cs`. Decode each modelled block from the front of its payload, ignoring trailing bytes:

- `_ver` — two big-endian `ushort`s: major, minor. Render as `"{major}.{minor}"`.
- `_pin` — null-padded ASCII product name; trim at the first NUL.
- `_top` — byte 0 mix effects, byte 1 sources, byte 2 downstream keyers, byte 3 auxiliaries.
- `InPr` — big-endian `ushort` source id at offset 0, then a null-padded ASCII long name, then a null-padded ASCII short name. **Derive the two name offsets and lengths from the fixture**: the test asserts source 1 is `Presenter` / `PRES` and source 10010 is `Live On Screens` / `PGM`, which only pass when the offsets are right.
- `PrgI` — byte 0 M/E index, big-endian `ushort` source id at offset 2.
- `PrvI` — same leading layout as `PrgI`; ignore anything after the first four bytes.
- `VidM` — byte 0 video mode.

Every other block name increments a counter in `UnhandledBlockCounts`.

```csharp
    /// <summary>Whether a source id is a physical input rather than an internal source.
    ///
    /// Ids are banded: 0 is black, 1-999 are physical inputs, and everything from 1000 up is
    /// internal - colour bars, colour generators, media players, key and DSK masks, clean feeds,
    /// auxiliaries, and the mix effect program and preview outputs. This banding was confirmed
    /// two ways: observed on hardware, and matching PyATEMMax's independently derived constants.</summary>
    public static bool IsExternalInput(int sourceId) => sourceId is >= 1 and < 1000;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemDumpParserTests`
Expected: PASS. If a name comes out mangled, the `InPr` offsets are wrong — fix the offsets, never the expected value.

- [ ] **Step 6: Run the full suite and commit**

```bash
git add src/Bmd/Devices/Atem/ tests/Bmd.Tests/Devices/Atem/
git commit -m "feat(atem): typed state model and dump parser"
```

---

### Task 5: AtemClient — the UDP session

**Files:**
- Create: `src/Bmd/Devices/Atem/AtemClient.cs`, `tests/Bmd.Tests/Devices/Atem/FakeAtem.cs`
- Test: `tests/Bmd.Tests/Devices/Atem/AtemClientTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces:
  - `sealed class AtemProtocolException(string message) : Exception`
  - `sealed class AtemClient : IAsyncDisposable` with `static Task<AtemClient> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)`, `AtemState State { get; }`, `string Host { get; }`.
  - `FakeAtem` with `static FakeAtem Start()`, `int Port`, `ValueTask DisposeAsync()`, and `static FakeAtem StartSilent()` (accepts the handshake then sends nothing, for the timeout test).

**The four behaviours this task exists to get right:**

1. **Adopt the switcher's session id.** The client opens with a random id; the switcher replies echoing it, then uses an id of its own from the next packet onward. Keep using your own and the switcher stops understanding you.
2. **Acknowledge every packet flagged `AckRequest`**, including the 12-byte keepalives that arrive indefinitely after the dump. Stop acknowledging and the session drops.
3. **De-duplicate by sequence id.** Retransmissions are routine — the capture contains two within six seconds. Applying a resent packet twice corrupts state.
4. **Detect the end of the dump by the first packet carrying no blocks.** There is no distinctly-flagged end marker.

- [ ] **Step 1: Write the fake switcher**

Create `tests/Bmd.Tests/Devices/Atem/FakeAtem.cs`: a `UdpClient` bound to `127.0.0.1` on an ephemeral port that

- answers a `Hello` with a `Hello` whose payload byte 0 is `0x02`, echoing the client's session id;
- after receiving the client's `Ack`, replays `AtemFixtures.DataPackets` **rewritten to carry the fake's own session id** (mirroring the real switcher's reassignment), each flagged `AckRequest` with sequence ids 1..5;
- then sends 12-byte keepalives flagged `AckRequest | Ack` until disposed;
- records every packet the client sends, exposing `IReadOnlyList<AtemHeader> Received` so tests can assert the client acknowledged what it should;
- `StartSilent()` completes the handshake and then sends nothing.

Rewrite only the session id bytes of each fixture packet; leave the payload untouched so the tests still exercise real block data.

- [ ] **Step 2: Write the failing tests**

Create `tests/Bmd.Tests/Devices/Atem/AtemClientTests.cs`:

```csharp
using Bmd.Devices.Atem;

namespace Bmd.Tests.Devices.Atem;

public class AtemClientTests
{
    static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConnectAsync_CompletesTheHandshakeAndReadsTheDump()
    {
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);

        Assert.Equal("ATEM Television Studio HD", client.State.ProductName);
        Assert.Equal(24, client.State.Sources.Count);
        Assert.Equal(6, client.State.ProgramSource);
    }

    [Fact]
    public async Task ConnectAsync_AdoptsTheSessionIdTheSwitcherAssigns()
    {
        // The client opens with its own random id; every packet it sends after the handshake
        // must carry the id the switcher chose, or the switcher stops understanding it.
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);

        var afterHandshake = fake.Received.Skip(1).ToList();
        Assert.NotEmpty(afterHandshake);
        Assert.All(afterHandshake, h => Assert.Equal(fake.AssignedSession, h.Session));
    }

    [Fact]
    public async Task ConnectAsync_AcknowledgesEveryPacketThatAsksForIt()
    {
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);

        // Five dump packets, each flagged AckRequest.
        var acks = fake.Received.Count(h => h.Flags.HasFlag(AtemFlags.Ack));
        Assert.True(acks >= 5, $"expected at least 5 acknowledgements, saw {acks}");
    }

    [Fact]
    public async Task ConnectAsync_IgnoresARetransmittedPacketItAlreadyApplied()
    {
        // Retransmission is routine: the capture contains two inside six seconds. Applying a
        // resent packet twice would double every source in the list.
        await using var fake = FakeAtem.Start(resendPacketIndex: 2);
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);

        Assert.Equal(24, client.State.Sources.Count);
        Assert.Equal(24, client.State.Sources.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public async Task ConnectAsync_TimesOutCleanlyWhenTheSwitcherGoesQuiet()
    {
        await using var fake = FakeAtem.StartSilent();

        await Assert.ThrowsAsync<TimeoutException>(
            () => AtemClient.ConnectAsync("127.0.0.1", fake.Port, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task ConnectAsync_TimesOutWhenNothingIsListening()
    {
        // Nothing answers a handshake on a closed UDP port; this must be a clean timeout rather
        // than a hang or a raw socket exception.
        await Assert.ThrowsAsync<TimeoutException>(
            () => AtemClient.ConnectAsync("127.0.0.1", 1, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task ConnectAsync_KeepsAcknowledgingKeepalivesAfterTheDump()
    {
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);

        var before = fake.Received.Count;
        await Task.Delay(400);
        Assert.True(fake.Received.Count > before,
            "the client stopped acknowledging keepalives, so a real switcher would drop the session");
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemClientTests`
Expected: FAIL — `AtemClient` does not exist.

- [ ] **Step 4: Implement the client**

Create `src/Bmd/Devices/Atem/AtemClient.cs`. Structure:

- `ConnectAsync` opens a `UdpClient`, links a `CancellationTokenSource` with `CancelAfter(timeout)`, sends `Hello`, waits for the reply, sends `Ack`, then reads packets until one arrives carrying no blocks.
- Every received packet flagged `AckRequest` is answered with an `Ack` carrying that packet's sequence id in the acked field and the session id the switcher is using.
- A `HashSet<int>` of applied sequence ids drops duplicates before parsing.
- After the dump completes, a background loop continues receiving and acknowledging keepalives until disposal, so the session stays alive for the lifetime of the client.
- `OperationCanceledException` where the caller's own token has not fired becomes `TimeoutException($"timed out talking to {host}:{port} after {timeout.TotalSeconds:0.#}s")`, matching `VideohubClient`'s wording.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemClientTests`
Expected: PASS. These tests involve timing; if one is flaky, widen the fake's margins rather than the assertion's meaning.

- [ ] **Step 6: Run the full suite and commit**

```bash
git add src/Bmd/Devices/Atem/ tests/Bmd.Tests/Devices/Atem/
git commit -m "feat(atem): UDP session with handshake, acknowledgement and dedupe"
```

---

### Task 6: The `bmd atem` commands

**Files:**
- Create: `src/Bmd/Commands/Atem/AtemResults.cs`, `src/Bmd/Commands/Atem/AtemCommands.cs`
- Modify: `src/Bmd/Output/BmdJsonContext.cs`, `src/Bmd/Program.cs`, `src/Bmd/Commands/GroupHelp.cs`
- Test: `tests/Bmd.Tests/Commands/AtemCommandsTests.cs`

**Interfaces:**
- Consumes: `AtemClient`, `AtemState`, `AtemDumpParser.IsExternalInput`, `ConfigStore`.
- Produces:
  - `AtemInfoResult(string Model, string Protocol, int MixEffects, int Sources, int DownstreamKeyers, int Auxiliaries, int VideoMode)`
  - `AtemSourceEntry(int Id, string Name, string ShortName, bool External)`
  - `AtemStatusResult(int ProgramSource, string ProgramName, int PreviewSource, string PreviewName)`
  - `AtemCommands` with `Info`, `InputList`, `Status`, plus `public AtemCommands()` and `public AtemCommands(Func<ConfigStore> loadConfig)`.

`DeviceSession` is Videohub-protocol plumbing and is **not** reused here — different transport, different lifecycle, no backup story. Resolve `atem.host` / `atem.port` (default **9910**) / `atem.timeout` (default 5) directly, and map failures to one `error:` line with the same exit-code contract.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bmd.Tests/Commands/AtemCommandsTests.cs` following the console-capturing pattern used by `MultiViewCommandsTests` (`[Collection("console")]`, redirect `Console.Out`/`Console.Error` in the constructor, restore in `Dispose`, config rooted in a temp directory). Cover:

```csharp
    [Fact]
    public async Task Info_ReportsTheModelAndTopology()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().Info("127.0.0.1", fake.Port));

        var text = _stdout.ToString();
        Assert.Contains("ATEM Television Studio HD", text);
        Assert.Contains("2.30", text);
        Assert.Contains("Sources:", text);
    }

    [Fact]
    public async Task Info_Json_EmitsOneDocument()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().Info("127.0.0.1", fake.Port, json: true));

        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("ATEM Television Studio HD", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(24, doc.RootElement.GetProperty("sources").GetInt32());
    }

    [Fact]
    public async Task InputList_ShowsOnlyTheRealInputsByDefault()
    {
        // 24 sources arrive; 8 are physical inputs. Listing all 24 buries the ones an operator
        // named and cares about.
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().InputList("127.0.0.1", fake.Port));

        var text = _stdout.ToString();
        Assert.Contains("Presenter", text);
        Assert.Contains("Lyrics", text);
        Assert.DoesNotContain("Color Bars", text);
        Assert.DoesNotContain("Live On Screens", text);
    }

    [Fact]
    public async Task InputList_All_AlsoShowsInternalSources()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().InputList("127.0.0.1", fake.Port, all: true));

        var text = _stdout.ToString();
        Assert.Contains("Presenter", text);
        Assert.Contains("Color Bars", text);
        Assert.Contains("Live On Screens", text);
    }

    [Fact]
    public async Task Status_NamesWhatIsOnProgramAndPreview()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().Status("127.0.0.1", fake.Port));

        var text = _stdout.ToString();
        Assert.Contains("Lyrics", text);      // program, source 6
        Assert.Contains("Presenter", text);   // preview, source 1
    }

    [Fact]
    public async Task Status_Json_CarriesBothIdsAndNames()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().Status("127.0.0.1", fake.Port, json: true));

        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        Assert.Equal(6, doc.RootElement.GetProperty("programSource").GetInt32());
        Assert.Equal("Lyrics", doc.RootElement.GetProperty("programName").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("previewSource").GetInt32());
    }

    [Fact]
    public async Task Commands_ErrorNamesAtemWhenNoHostIsConfigured()
    {
        Assert.Equal(1, await Commands().Info());
        Assert.Contains("bmd config set atem.host", _stderr.ToString());
    }

    [Fact]
    public async Task Commands_UnreachableSwitcher_Exit1_OneCleanLine()
    {
        var exit = await Commands().Info("127.0.0.1", 1, timeout: 1);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task Commands_ReadTheirHostFromTheAtemSection()
    {
        await using var fake = FakeAtem.Start();
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[atem]\nhost = 127.0.0.1\nport = {fake.Port}\n");

        Assert.Equal(0, await Commands().Info());
        Assert.Contains("ATEM Television Studio HD", _stdout.ToString());
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemCommandsTests`
Expected: FAIL — `AtemCommands` does not exist.

- [ ] **Step 3: Implement the result records and register them**

Create `src/Bmd/Commands/Atem/AtemResults.cs` with the three records above, and add to `BmdJsonContext`:

```csharp
[JsonSerializable(typeof(AtemInfoResult))]
[JsonSerializable(typeof(AtemSourceEntry[]))]
[JsonSerializable(typeof(AtemStatusResult))]
```

- [ ] **Step 4: Implement the commands**

Create `src/Bmd/Commands/Atem/AtemCommands.cs`. Three commands, each connecting, reading, formatting and disconnecting. Human output uses `Table.Write`; `--json` serialises through `BmdJsonContext`. XML doc comments carry the help text, and `--all` is documented as showing internal sources such as colour bars, media players and the program and preview outputs.

- [ ] **Step 5: Register**

`Program.cs`:

```csharp
var atem = new AtemCommands();
app.Add("atem info", atem.Info);
app.Add("atem input list", atem.InputList);
app.Add("atem status", atem.Status);
```

`GroupHelp.Commands`:

```csharp
        new("atem info", "Show switcher information (model, protocol version, topology)."),
        new("atem input list", "List the switcher's inputs. --all includes internal sources."),
        new("atem status", "Show what is on program and preview."),
```

and add `"atem"` to `GroupHelp.Groups`.

- [ ] **Step 6: Run the tests, verify help renders, and commit**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~AtemCommandsTests` — PASS.
Run: `dotnet run --project src/Bmd -- atem --help` — the three commands listed, exit 0.
Run: `dotnet test` — full suite green.

```bash
git add src/Bmd/ tests/Bmd.Tests/
git commit -m "feat(atem): bmd atem info, input list and status"
```

---

### Task 7: Discovery, site and docs

**Files:**
- Modify: `src/Bmd/Devices/Discovery/DiscoveredDevice.cs`, `tests/Bmd.Tests/Devices/Discovery/DiscoveredDeviceTests.cs`
- Create: `site/atem.html`
- Modify: `site/index.html`, `CLAUDE.md`, `docs/superpowers/specs/2026-08-29-bmd-cli-design.md`

**This task reverses a deliberate decision.** `DeviceClasses` currently leaves `AtemSwitcher` unmapped *on purpose*, with a comment explaining that mapping it "would offer to configure a device it can't drive", and **two tests assert it maps to null** — `DiscoveredDeviceTests.cs:137` and `:164`. That reasoning expires now that bmd can read an ATEM. Update the comment as well as the table, so the next reader sees why it changed rather than assuming the old comment was wrong.

- [ ] **Step 1: Write the failing discovery tests**

Replace the two assertions that `AtemSwitcher` maps to null, and add:

```csharp
    [Theory]
    [InlineData("AtemSwitcher", "atem")]
    [InlineData("atemswitcher", "atem")]
    [InlineData("ATEMSWITCHER", "atem")]
    public void DeviceTypeFor_RecognisesTheAtemClass(string advertised, string expected)
    {
        // Confirmed against real hardware: four ATEMs on the network all advertise
        // class=AtemSwitcher on port 9910.
        Assert.Equal(expected, DeviceClasses.DeviceTypeFor(advertised));
    }

    [Fact]
    public void DeviceTypeFor_StillRecognisesTheOtherTwoDeviceTypes()
    {
        Assert.Equal("videohub", DeviceClasses.DeviceTypeFor("Videohub"));
        Assert.Equal("multiview", DeviceClasses.DeviceTypeFor("MultiView"));
    }

    [Fact]
    public void DeviceTypeFor_StillDoesNotGuessAtAnUnknownClass()
    {
        Assert.Null(DeviceClasses.DeviceTypeFor("HyperDeck"));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~DiscoveredDeviceTests`
Expected: FAIL — `AtemSwitcher` currently returns null.

- [ ] **Step 3: Map the class**

Add `new("AtemSwitcher", "atem")` to `ClassMap`, and rewrite the doc comment so it records that `Videohub`, `MultiView` and `AtemSwitcher` are all now confirmed against real hardware, while the remaining Videohub spellings stay seed values. Keep the note that HyperDeck and the WebPresenter family answer on port 9977 with no `class=` at all and need a separate identification path.

`bmd discover --add` already writes `new ConfigKey(deviceType, "host")`, so a discovered ATEM writes `atem.host` with no further change — confirm by reading `DiscoverCommands.AddSelected` rather than assuming.

- [ ] **Step 4: Write the site page**

Create `site/atem.html`, mirroring `site/videohub.html`'s structure exactly — same `guide-header`, `back-link`, `page-nav`, numbered sections, `pre.cmd` blocks and footer. Use only classes already in `style.css`. Sections:

1. **Connecting** — `bmd discover --add`, or `bmd config set atem.host 192.168.1.30`. Note port 9910, unlike the 9990 the other devices use.
2. **What bmd can do today** — state plainly that this release reads and does not switch: `info`, `input list`, `status`. Say that switching is deliberately a later step, and why: the protocol is undocumented and an ATEM is usually the most production-critical device in a rack.
3. **Looking at the switcher** — the three commands with real output.
4. **Sources** — that a switcher reports many more sources than physical inputs, that `input list` shows the real ones and `--all` adds colour bars, media players, clean feeds, auxiliaries and the program and preview outputs.
5. **Scripting** — `--json` shapes, linking to the Videohub guide's scripting section for the shared exit-code contract.

Then update `site/index.html`: move ATEM from "not implemented yet" to supported **with an explicit read-only qualifier**, add `bmd atem` rows to the command list, and link `atem.html` from the footer and guide callout. Add the same link to `videohub.html`, `multiview.html` and `install.html` footers.

- [ ] **Step 5: Update the docs**

`CLAUDE.md`: add ATEM to the device list noting it is read-only and speaks a different protocol, and tick milestone 11 on the roadmap.

`docs/superpowers/specs/2026-08-29-bmd-cli-design.md`: add the `bmd atem` commands to the command grammar, and update the milestone list to record 11 as done and 12 (switching) as next. The ATEM design itself lives in its own spec — cross-reference it rather than duplicating it.

- [ ] **Step 6: Full verification and commit**

Run: `dotnet test` — full suite green.
Run: `dotnet publish src/Bmd -c Release -r win-x64` — succeeds with **no `IL2xxx`/`IL3xxx` warnings**. If `vswhere.exe` is not found, add `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to PATH for the session; that is a known local toolchain quirk, not a code problem.
Run: `dotnet run --project src/Bmd -- atem --help`.

```bash
git add src/Bmd/ tests/Bmd.Tests/ site/ CLAUDE.md docs/
git commit -m "feat(atem): discovery, guide page and roadmap"
```

---

## Self-review

**1. Spec coverage.**

| Spec requirement | Task |
|---|---|
| Capture embedded as the fixture, first | 1 |
| Header codec, flags/length bit packing | 2 |
| Block framing, advance by declared length | 3 |
| Bytes 2–3 are `0x0014`, not validated as zero | 3 |
| Seven block types modelled | 4 |
| All other blocks retained rather than discarded | 4 |
| `_top` is the authority on device shape | 4 (parsed), asserted in tests |
| External vs internal source banding | 4, surfaced by `--all` in 6 |
| Session id adoption | 5 |
| Acknowledgement, including keepalives | 5 |
| Retransmission de-duplication | 5 |
| Dump end detected by first payload-free packet | 5 |
| `atem info` / `input list` / `status` | 6 |
| `DeviceSession` deliberately not reused | 6 |
| Config section `atem.host`, port 9910 | 6 |
| Read-only: no `CPgI`/`CPvI`/`DCut`/`DAut` | enforced throughout; nothing sends a command block |
| Discovery maps `AtemSwitcher`, reversing a considered decision | 7 |
| Site ships with the change | 7 |

**2. Placeholder scan.** No TBDs. Three steps deliberately describe rather than transcribe: Task 1 Step 3 (the fixture's 19 hex lines are copied from a named file rather than reproduced here, since duplicating 13 KB of hex invites divergence), Task 5 Step 1 and Step 4 (the fake and the client are described by behaviour with all four required properties enumerated, because the exact socket plumbing should follow `FakeVideohub`'s existing shape rather than a second pattern invented here), and Task 7 Step 4 (the site page mirrors an existing file). Each names the file to read and the exact properties required.

**3. Type consistency.** `AtemFixtures.StateDumpPackets` / `DataPackets` (Task 1) are consumed in Tasks 2–5. `AtemPacket.TryReadHeader` / `WriteHeader` / `HeaderSize` / `AtemFlags` / `AtemHeader` (Task 2) are consumed in Tasks 3 and 5. `AtemCommandBlock.ReadBlocks` (Task 3) in Tasks 4 and 5. `AtemState`, `AtemTopology`, `AtemSource`, `AtemDumpParser.Parse` / `IsExternalInput` (Task 4) in Tasks 5 and 6. `AtemClient.ConnectAsync` / `State` (Task 5) in Task 6. `FakeAtem.Start` / `StartSilent` / `Port` / `Received` / `AssignedSession` (Task 5) in Tasks 5 and 6.

**One risk worth naming.** Task 5 is the only task whose correctness cannot be fully demonstrated by the fixture: retransmission, keepalive and timeout behaviour are exercised against a fake that we wrote, so they prove the client matches our model of the switcher rather than the switcher itself. The capture constrains that model — it contains real retransmissions and real keepalives — but the reviewer should treat Task 5's tests as the weakest evidence in the milestone, and the first thing to re-validate if hardware access returns.
