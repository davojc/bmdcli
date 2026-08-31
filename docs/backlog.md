# Backlog

Open work, in the order I would take it, with what is already done and what each would
actually cost. Written at v0.6.0.

This is deliberately not in `CLAUDE.md`: that file is read at the start of every session, so
it stays short. This one is read when someone is choosing what to do next.

---

## 1. HyperDeck

**The cheapest device left, by a distance.** Everything that made ATEM expensive is absent here.

- **Blackmagic publishes this protocol.** It is in the HyperDeck manual. ATEM's is not published
  at all, which is why every layout there had to be captured from hardware and confirmed by
  experiment.
- **Text over TCP 9993**, request/response with numeric status codes. Closer in shape to the
  Videohub protocol than to ATEM's binary UDP.
- **Discovery is already done** (v0.5.0). `_hyperdeck_ctrl._tcp` carries a proper
  `class=HyperDeck` and `name=`, so a deck is fully identified today — it is simply not driveable.
- **One protocol, two devices.** An ATEM Television Studio HD8 ISO has a recorder built in and
  answers on the same service. Record and transport control would reach the switcher as well as
  the deck.
- **It carries `friendly name`** in its connect banner, so this is a configuration surface as
  well as a transport one.

Observed banners:

```
192.168.4.184:9993          192.168.4.123:9993
500 connection info:        500 connection info:
protocol version: 1.12      protocol version: 1.0
model: HyperDeck Studio     model: ATEM Television Studio HD8 ISO
      4K Pro                friendly name: ATEM Television Studio HD8 ISO
                            unique id: b0f2ada03f5048c0a54a15abb63e5987
```

Note the protocol versions differ (1.12 vs 1.0) between a deck and a switcher's built-in
recorder. Same model-variation problem the two ATEMs had, same answer: read what the device
reports rather than assume.

## 2. ATEM export, restore and watch

**The workflows the site sells exist only for Videohub and MultiView.** *Save the room, put it
back, see what changed* is the pitch on the landing page, and an ATEM can currently be backed up
per-mutation but never snapshotted deliberately or restored.

Most of the work is done: `AtemSnapshot` exists, the backup path writes it, and
`BackupStore.Write` has an ATEM overload. `atem export` is largely wiring. `restore` needs a
converge plan like the Videohub one. `watch` needs the client to surface state changes, which its
receive loop already applies.

## 3. Native AOT publish in CI

Trim and AOT warnings still surface first at tag time. Six releases have built clean, so it has
not bitten — but it is the one remaining gap in a gate that is otherwise thorough, and an AOT
failure discovered during a release is discovered at the worst moment.

The cost is CI minutes: an AOT compile is minutes where the test suite is seconds. One RID on
push to `main` would probably be enough.

## 4. MultiView's `view list`

v0.4.0 split `videohub output list` (labels and locks) from `route list` (routing), because the
two returned the same table under two sets of field names. MultiView was left alone: `view list`
is still the combined shape and there is no `multiview route list`.

Two coherent answers. Split it the same way for consistency, or decide that a MultiView's views
*are* defined by what they show and leave it combined. The second is defensible — this is a
decision to make deliberately rather than let drift.

---

## Leads worth remembering

### `_bmd_blockcfg._tcp` on port 9977

Spoken by the HyperDeck, both Web Presenters and the ATEM Streaming Bridge — **four devices, one
service**. If it is a naming and configuration protocol, understanding it would make the quiet
half of a Blackmagic network legible in one go, which is exactly the theme the project keeps
returning to. Undocumented, so it is a lead rather than a plan, but it is the highest-leverage
unknown observed on a real network.

### Discovery by MAC vendor prefix

Blackmagic hardware is identifiable by its OUI `7C:2E:0D` **without the device cooperating at
all**. Two Blackmagic Studio Cameras on a test network advertised nothing over mDNS, served a
bare 404 on port 80, and never completed a TLS handshake on 443 — but ARP found them instantly.

A fallback sweep, when mDNS finds nothing or behind a flag, would list them as "Blackmagic
device, unidentified" with an address. That is honest about what it does not know and is the only
mechanism that will ever see a device that quiet. It is active probing rather than passive
listening, which is a different character from what `discover` does today, so it deserves a
deliberate decision.

### Cameras are naming, not control

Investigated and closed for now. A connected Blackmagic Studio Camera cannot be driven over the
network: no mDNS, no usable HTTP API, and the documented `/control/api/v1/…` endpoints answer 404.
Camera control travels over SDI from an ATEM by design, and camera configuration is normally done
on the camera's own touchscreen or over USB-C.

What matters for the network being legible is the **ATEM input label**, which bmd already sets.
That is what the multiviewer prints and what an assistant reads. If this is revisited, the things
that would change the answer are the exact model, whether the camera's Setup menu has a network
or remote-control toggle, whether Blackmagic Camera Setup sees it over Ethernet at all, and the
firmware version.

---

## Smaller things

- **The MultiView write path has never run against real hardware.** Reads are validated on a
  device; `layout set`, `format`, `solo`, `show` and the rest are proven only against the fake.
  Backups do capture `CONFIGURATION`, so a mistake is recoverable — but the first real write is
  still a first.
- **`videohub restore` and `multiview restore` duplicate their converge-and-report policy.**
  Worth extracting once a third device gains restore, not before.
- **MultiView export/restore failure output leaks Videohub vocabulary** in places, saying outputs
  where the rest of the group says views.
- **`bmd videohub info` against a device answering `Device present: false`** reports "device dump
  is missing the INPUT LABELS block", which is correct but unhelpful. A PC running Videohub
  control software answers on 9990 this way. Worth a clearer message naming the likely cause.
