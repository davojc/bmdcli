# bmd — Blackmagic Device CLI

Cross-platform .NET CLI for controlling Blackmagic Design devices over the
network, gh/git-style (`bmd videohub route set 3 7`). First device: Videohub.
Second device: MultiView (`bmd multiview view set 1 3`) — it speaks the same
Videohub Ethernet Protocol, so it shares the client and session plumbing;
what differs is vocabulary (views, not outputs) and its own CONFIGURATION
block (layout, output format, overlays, solo).
Third device: ATEM (`bmd atem input rename 2 "Camera Two"`) — shares nothing
below the command layer. Binary UDP 9910, session handshake, sequence numbers
and ACKs, and **no published protocol at all**. Read layouts come from a
hardware capture (`docs/superpowers/plans/assets/atem-hd3-statedump.hex`);
command layouts cannot (a capture holds only what the device sent), so each was
found by experiment against real hardware. Two traps, both of which fail
silently because the protocol has no NAK: **payload length is exact and differs
per command** (CInL 32, CAuS/CPgI/CPvI 4), and **the session id is reassigned on
the first data packet, not in the handshake reply**. Every mutation waits for
the device to report the change back rather than assuming it landed. See
`docs/superpowers/specs/2026-08-30-atem-design.md`.
Design spec: `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` — read it
before architectural changes; update it when a design decision changes.

## Stack

- .NET 10, single console project `src/Bmd` (AssemblyName `bmd`), tests in
  `tests/Bmd.Tests` (xUnit).
- CLI parsing: **ConsoleAppFramework v5** (source-generator; help text comes
  from XML doc comments on command methods).
- JSON: System.Text.Json **source generators only**.
- Config files: hand-written INI parser in `Config/` — no config library.
- Output tables: hand-rolled aligned columns in `Output/` — no rendering library.

## Hard rules

- **Native AOT always on.** `PublishAot=true` from the first commit. No
  reflection-dependent libraries, no `dynamic`, no reflection-based JSON.
  A dependency that isn't AOT/trim-safe doesn't get added.
- **User-facing numbering is 1-based** (matches device front panels). The wire
  protocol is 0-based. Conversion happens in exactly one place: the public
  boundary of `Devices/Videohub/`. Everything above that boundary is 1-based.
- **No environment variables for configuration.** Reads are layered:
  command-line flag > local `.bmdconfig` (walk-up discovery) > user config
  (`~/.config/bmd/config`, `%APPDATA%\bmd\config` on Windows) > built-in
  default. **Writes go to the user config unless `--project` is passed** —
  unlike git, because a device address belongs to the network, not to the
  directory you ran the command from.
- **Contexts address a second device of the same type.** A context is a
  git-style INI subsection (`[atem "gallery"]`); the active one is
  `<type>.context` in the unlabelled section, chosen by
  `bmd <type> context set` and listed by `context list`. Resolution is
  active context -> unlabelled section -> error: **a named context never
  falls back**, because falling back means acting on a device the user did
  not select. `default` is reserved as the name of the unlabelled context.
  Mutations announce the context on stderr when a named one is active, so
  `--json` stdout stays exactly one document.
- **Layering:** `Devices/` never references ConsoleAppFramework or `Commands/`.
  Command classes stay thin (resolve config → call client → format). No shared
  device abstraction until a second device type exists.
- **TDD.** Protocol parsing is pure functions tested against transcript
  fixtures; client/commands test against the in-process fake Videohub TCP
  server in the test project — never against real hardware in CI.
- Errors: one clear message to stderr, no stack traces. Exit codes: 0 success,
  1 operation failure, 2 usage/format error.
- **`AGENTS.md` is the user-facing agent guide** (distinct from this file, which is for agents
  working *on* bmd). It is embedded in the binary via an `EmbeddedResource`, printed by
  `bmd agents`, installed as an agent skill by `bmd agents --skill`, and shipped as a release
  asset. **It installs as a skill, never into `CLAUDE.md`/`AGENTS.md`/`GEMINI.md`** — those are a
  project's own instructions, hand-written and loaded for every task; a manual for a tool the
  project merely uses would both bloat that context and risk overwriting it. `--write <path>` puts
  the plain document somewhere for a tool that reads files directly. It documents
  contracts and conventions and deliberately **never lists flags** — `--help` owns those, and a
  doc that restates them is a second source of truth that goes stale. A test checks every command
  it names still exists in `Program.cs`.
- **Agent-first interface.** Every command supports `--json`: one JSON
  document on stdout (camelCase, stable field names, via the source-gen
  `BmdJsonContext` — never reflection serialization). `--json` changes
  representation, never behavior; errors stay plain `error:` on stderr with
  the exit-code contract regardless. Help text is the API: every command,
  argument, and flag fully documented via XML doc comments (units, ranges,
  1-based numbering). Renaming commands/flags/JSON fields is a breaking
  change.
- **Releases are tag-driven.** Pushing a semver tag (`v1.2.0`) triggers the
  GitHub Actions release matrix (AOT builds on each target OS — never
  cross-OS). The tag is the only version source: stamped via `-p:Version`,
  shown by `bmd version`, compared by `bmd update`. Updates verify SHA-256
  from `checksums.txt` before self-replacing; pre-release tags are ignored by
  update checks. Passive update notice: stderr only, after output, ≤1/24h,
  cached in the OS cache dir, off via `update.check = false`.
- **Mutations back up first.** Every device-changing command snapshots the
  pre-change state (from the dump already read at connect — no extra round
  trip) before acting; a failed backup aborts the mutation with exit 1.
  Backups live in the OS state dir (distinct from config and cache), rotate
  per `backup.keep`, honor `backup.auto`/`backup.dir`, and every mutation
  reports its backup path in both human and `--json` output.
- **The Pages site ships with the change.** `site/` (plain HTML/CSS, deployed
  to https://davojc.github.io/bmdcli/ by Actions on push to main) documents
  user-facing behavior: any commit that adds or changes a command updates the
  relevant `site/` page too. Download links use the stable
  `releases/latest/download/...` URLs — never hardcode a version. `docs/` is
  internal and never published.

## Videohub protocol (quick reference)

Text protocol, TCP **9990**. Blank-line-terminated blocks; device pushes a full
dump on connect (`VIDEOHUB DEVICE:`, `INPUT LABELS:`, `OUTPUT LABELS:`,
`VIDEO OUTPUT ROUTING:`, `VIDEO OUTPUT LOCKS:`), pushes incremental updates on
any change, answers client blocks with `ACK`/`NAK`. Lock states: `U` unlocked,
`O` owned by us, `L` locked by another controller; send `F` to force-unlock.
Full details and the export/restore/verify semantics are in the spec.

## Commands

```powershell
dotnet build
dotnet test
dotnet run --project src/Bmd -- videohub route list   # run from source
dotnet publish src/Bmd -c Release -r win-x64          # AOT binary (also: linux-x64, linux-arm64, osx-x64, osx-arm64)
```

## Roadmap

Milestones (detail in spec): 1 skeleton+config ✅ → 2 read path + agent JSON ✅
→ 3 export + backup store → 4 write path (with auto-backup) → 5 restore →
6 watch → 7 mDNS discovery → 8 tag-driven release pipeline + Pages site →
9 self-update ✅ → 10 MultiView (second device: `bmd multiview`, discovery,
site guide) ✅ → 11 ATEM (third device, its own binary/UDP protocol:
`bmd atem` info/input list/status/aux list, input rename, aux set,
program+preview set) ✅. Future, and now promised on the site's device strip: HyperDeck
(text/TCP, port 9993), Web Presenter, and camera control. The first two both
advertise on 9977 with **no `class=`**, identifying themselves by `product id`
(BE73/BE74/BE8B/BE8C observed), so discovery needs a second identification path
before either can be added. Camera control is not its own network protocol —
Blackmagic cameras are controlled *through* an ATEM over SDI, so it is an
extension of `Devices/Atem/` rather than a fourth device family. Also future:
ATEM export/restore/watch, transitions and keyers.
