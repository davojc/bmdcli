# ATEM switcher support — design

**Status:** implemented, with a scope change. Milestone 11 was designed read-only; the user
asked for the write path — input labels and aux routing above all, with switching explicitly
secondary — so the read path and those writes shipped together. The read-only reasoning below is
kept as written, because it explains what the writes are trading against rather than being
superseded by them. See "Scope: milestone 11 is read-only" and its amendment.
**Relates to:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` (the main design; ATEM was
listed there as a future device). This document covers what that one deferred.

## Why this is different from every device bmd already speaks to

Videohub and MultiView share one protocol: line-oriented text over TCP 9990, published by
Blackmagic as the *Videohub Ethernet Protocol*. Adding MultiView was mostly vocabulary, because the
transport was already there.

ATEM shares none of it. Binary UDP on port 9910, a session handshake, sequence numbers,
acknowledgements and retransmission — and **no published protocol at all**.

## The documentation situation, stated plainly

**Blackmagic does not publish the ATEM wire protocol.** Their supported route is the ATEM SDK, a
C++/COM object model for local control. It describes *what a switcher is* — inputs, mix effects,
keyers — but not a single byte of what crosses the wire. There is no equivalent of the Videohub
Ethernet Protocol PDF.

Everything public is community reverse-engineering: [OpenSwitcher](https://docs.openswitcher.org/),
[node-atem](https://github.com/miyukki/node-atem/blob/master/specification.md), and
[PyATEMMax](https://clvlabs.github.io/PyATEMMax/docs/data/protocol/), among others.

**Consequence: correctness here is empirical, not specified.** For Videohub, the PDF was the
authority and the device was the check. Here the device is the authority and the community docs are
the hypothesis. That inversion shapes every decision below.

**Validation method.** Rather than trust one source, this design was built by triangulating three:
a capture from real hardware, and two independently-derived community references. Where they agree,
confidence is high. Where they disagree, the hardware wins and the disagreement is recorded.

**The write path was settled the same way, by experiment.** A capture cannot contain a client's own
commands, so each command was sent to a real switcher in several candidate shapes and the shape it
acted on — pushing the corresponding state block back — is the one kept. Two findings came out of
that, both of which had already produced a silent failure:

- **Payload length is exact and differs per command.** `CInL` requires 32 bytes and is ignored at
  28 (the size of its own fields) or 27. `CAuS`, `CPgI` and `CPvI` require exactly 4 and are
  ignored at 8 or 12. There is no NAK in this protocol, so a wrong length is indistinguishable
  from an unsupported command.
- **The session reassignment happens on the first data packet, not in the handshake.** The Hello
  reply echoes the client's own opening id; the switcher switches to an id of its own immediately
  afterwards. A client that adopts the id from the Hello reply and stops looking gets everything
  it sends silently ignored — acknowledged by nothing, acted on by nothing.

## What was established against real hardware

A read-only probe of an **ATEM Television Studio HD III** (192.168.4.98) — handshake plus the
protocol-required ACKs, nothing that changes switcher state.

**The transport works exactly as the community documents it.**

- Three-packet handshake: client SYN → switcher Hello with status byte `0x02` (success) → client ACK.
- **The switcher reassigns the session ID.** The probe opened with `0x7A56`; the reply echoed it;
  every packet afterwards used `0x8003`. A client that keeps using its own ID is simply not
  understood. This is documented, easy to miss, and now confirmed.
- State dump on connect: **19 packets, 6,514 bytes, 287 command blocks, 73 distinct types**,
  terminated by an empty packet.
- Block framing — 2-byte length, 2 reserved, 4-character ASCII name, payload — parsed all 287
  blocks with no desync. The framing is not in doubt.

**Source IDs are structured, and two independent sources agree.** Every range observed on the
hardware matches PyATEMMax's independently-derived constants:

| Range | Meaning | Observed on the HD III |
|---|---|---|
| 0 | Black | yes |
| 1–8 | External inputs | 8 of them, user-named |
| 1000 | Colour bars | yes |
| 2001–2002 | Colour generators | yes |
| 3010–3021 | Media players and their key signals | yes |
| 4010 | Key mask | yes |
| 5010–5020 | DSK masks | yes |
| 7001–7002 | Clean feeds | yes |
| 8001 | Auxiliary | yes |
| 10010 / 10011 | M/E 1 program / preview | yes |

That agreement is what justifies the input-listing decision below: the split between real inputs and
internal sources is a genuine structural boundary, not an inference.

**The device does not zero its send buffer, and that shapes the whole parser.** Names must be
read to the first NUL and never to the field width: the padding after input 2's (empty) name
contains the bytes `MPrp`, a real command name left over from an earlier block. `AuxS` reads
`00 56 00 06` — aux 0, source 6 — where `0x56` is the same garbage sitting between the index and
the source id, not a field. And a block's bytes 2-3 are garbage too: zero in 96 of the capture's
287 blocks and arbitrary in the rest. They look like a constant `0x0014` if you inspect only the
first block of each packet, which is exactly the mistake this design originally made; nothing may
validate them, and bmd sends zero.

**One documented layout is wrong for this firmware.** `PrgI` matched exactly — payload
`00 00 00 06`, meaning M/E 0, source 6. But `PrvI` is documented as a 4-byte payload and this
switcher sends **8**: `00 01 00 01 00 01 00 2c`. The leading four parse correctly (M/E 0, source 1);
the trailing four are undocumented.

**This produces a hard parsing rule, and it is the most important line in this document:**

> **Always advance by the block's own length field. Never by the payload size you expect.**

Parse the fields you understand from the front of a payload and ignore anything trailing. An
implementation that trusted the documented 4 bytes would desync every remaining block in the
packet — and would have passed its tests, because a fake built from the same documentation would
have made the same mistake.

## Scope: milestone 11 is read-only

> **Amended after implementation.** The user's priority was the opposite of this section's
> ordering: naming inputs and routing auxes first, switching "secondary". Writes therefore shipped
> in milestone 11. What survives from the reasoning below is the *risk*, not the sequencing — and
> it is answered by a mechanism rather than by deferral. Because the capture contains nothing a
> client sent, no command layout can be verified from it; so `AtemClient.SendCommandAsync` sends a
> command and then **waits for the device to push the corresponding state block back**, treating
> silence as failure. A wrong layout surfaces as `did not report the change`, never as a silent
> no-op reported as success. `bmd atem program set` is the one command that cuts on air, and the
> site guide says so in those words.

No command that changes the switcher. Not program selection, not cut, not auto.

The transport is where essentially all the risk lives — session lifecycle, sequence numbers,
acknowledgement, retransmission, and a dump terminated by a packet needing a specific ACK. Getting
that wrong makes every later command unreliable in ways that are hard to attribute. Proving it by
reading costs nothing operationally: a handshake and state dump are precisely what ATEM Software
Control does every time it connects.

An ATEM is also usually the most production-critical device in a rack. Earning trust in the
transport before writing to one is worth a milestone.

Switching commands (`CPgI`, `CPvI`, `DCut`, `DAut`) are milestone 12.

## Architecture

### `Devices/Atem/` — new, sharing nothing below the command layer

- **`AtemPacket`** — the 12-byte header codec. Flags and length share the first two bytes as 5 bits
  and 11 bits, big-endian; treating them as two independent bytes is the first available mistake.
  Pure functions over `byte[]`, unit-testable against the capture.
- **`AtemCommandBlock`** — block framing and enumeration. Pure. Enforces the advance-by-length rule.
- **`AtemClient`** — the UDP session: handshake, adopting the reassigned session ID, tracking
  sequence numbers, ACKing reliable packets, and reading the dump to completion.
- **`AtemState`** — the typed model.
- **`AtemDumpParser`** — blocks to state.

**Only seven block types are modelled:** `_pin` (product name), `_ver` (protocol version), `_top`
(topology), `InPr` (input properties), `PrgI` (program), `PrvI` (preview), `VidM` (video mode).

**Every other block is retained verbatim**, keyed by its 4-character name — the same decision made
for MultiView's `CONFIGURATION`, for the same reason. 73 types arrive and 7 are modelled; the other
66 are exactly what milestone 12 needs, and discarding them would mean re-solving the transport to
get them back.

### `Commands/Atem/` — thin, as every command group is

```
bmd atem info                    # product name, protocol version, topology, video mode
bmd atem input list [--all]      # the switcher's inputs
bmd atem status                  # what is on program and preview
```

Config section `atem.host`, `atem.port` (default 9910), `atem.timeout`.

**`--all` follows the `discover` precedent.** The HD III reports 24 sources, of which 8 are real
inputs and 16 are internal — colour generators, media players, DSK masks, clean feeds, aux, and the
program/preview outputs. Listing all 24 by default buries the 8 that a user named and cares about.
Default shows external inputs; `--all` shows everything, mirroring `bmd discover --all`.

### What is deliberately *not* shared

`DeviceSession` carries connect/backup/error plumbing for **Videohub-protocol** devices. ATEM has a
different transport, a different session lifecycle, and no snapshot/backup story in this milestone.
It gets its own session type rather than a forced generalisation.

This is the same judgement made when MultiView arrived, reached the other way: there, two devices
genuinely shared a protocol and the abstraction was justified by evidence. Here they do not.

**Backups do not apply.** The project rule that mutations snapshot first is about device state that
can be restored. Milestone 11 has no mutations. When milestone 12 adds them, whether an ATEM
snapshot/restore is even coherent is its own design question — a switcher's live state is not a
router's routing table.

## Testing

**The capture is the fixture.** 19 packets of real switcher traffic, replayed by an in-process fake
ATEM over UDP — the same approach as `FakeVideohub`, and the same reason: the device is not
available on demand, and CI has no hardware.

The capture currently exists only as session scratch (`atem-capture/`: one `.bin` per packet plus
`dump.hex`, 58 KB). **It is irreplaceable without hardware access, so embedding it is the first
task of milestone 11, not a later step** — as a hex string constant in the test project, mirroring
how `Fixtures.DumpMultiView4` embeds the MultiView transcript. No binary files need to enter the
repository.

**The pure layers are tested directly against real bytes.** Header codec and block framing are pure
functions; the capture gives them non-synthetic input, including the over-long `PrvI` that a
docs-derived fixture would have got wrong.

**A fake built from the same documentation cannot validate that documentation.** This is the trap
this milestone must avoid, and it is why the fixture is a hardware capture rather than hand-written
packets. Where a test needs a shape the capture does not contain, that shape is synthetic and must
be labelled as such, so nobody later mistakes it for observed behaviour.

## Risks

1. **Model variation.** Findings come from one switcher. An ATEM Mini Pro and a 1 M/E Production
   Studio 4K differ in inputs, M/Es and available commands far more than a Videohub 40×40 differs
   from a MultiView 4. `_top` reports topology and should be read rather than assumed, and nothing
   should hardcode a count observed on the HD III.
2. **Firmware variation.** `PrvI` already disagrees with the documentation on this firmware. Others
   may on others. The advance-by-length rule contains the blast radius.
3. **UDP reliability is the client's problem.** Retransmission and ordering are not free, and are
   the hardest thing here to test convincingly against a fake.
4. **No dependency escape hatch.** Community C# implementations exist, but the zero-dependency and
   Native AOT rules have held through a hand-written INI parser, mDNS client and table renderer.
   A reflection-heavy dependency would cost the single-binary, no-runtime property that makes bmd
   what it is. Hand-rolled, as with everything else.

## Milestones

- **11 — transport, read path, and the write path the user asked for.** ✅ Header codec, block
  framing, session, state dump, retained unknown blocks; `atem info` / `input list [--all]` /
  `status` / `aux list`; `input rename`, `aux set`, `program set`, `preview set`, each backed up
  first and each verified against the device's own state push.

  Discovery needs a deliberate reversal here. `DeviceClasses` currently leaves `AtemSwitcher`
  unmapped *on purpose*, with a comment explaining that mapping it "would offer to configure a
  device it can't drive", and **two tests assert it maps to null**
  (`DiscoveredDeviceTests.cs:137` and `:164`). Once bmd can read an ATEM that reasoning expires, so
  milestone 11 maps the class and inverts both tests. Whoever does it should understand they are
  reversing a considered decision rather than fixing an oversight.
- **12 — the rest.** `atem export` / `restore` / `watch` (the snapshot type and backup path
  already exist, so export is mostly plumbing), then `DCut` / `DAut` and transitions.
- Later, if wanted: transitions and keyers, audio, media players, camera control. Each is its own
  milestone; "ATEM support" is a programme, not a feature.
