# bmd — Blackmagic Device CLI: Design

**Date:** 2026-08-29
**Status:** Approved for implementation planning

## Overview

`bmd` is a cross-platform command-line tool for controlling Blackmagic Design
devices over the network, in the noun-verb style of `gh`/`git`. The first
supported device is the **Videohub** router family; HyperDeck and ATEM are
future candidates. Ships as small standalone Native AOT binaries for Windows,
Linux, and macOS.

## Goals

- Full Videohub Ethernet Protocol surface: inspect, route, rename, lock,
  live-watch, and snapshot export/restore.
- Git-style layered configuration (global → local → command line). No
  environment variables.
- Single-file Native AOT binaries (~5 MB), fast cold start, no runtime install.
- Agent-first: AI agents and scripts are first-class users alongside humans.
  Every command supports `--json` structured output, help text is a complete
  API contract, exit codes are meaningful, and export/restore are
  stdout/stdin-friendly. See "Agents and scripting".
- Self-updating: releases built by GitHub Actions on semver tags; `bmd update`
  downloads, verifies, and installs the latest release in place.
- Discoverable: a GitHub Pages site documents the tool per device and gives
  users their initial download without hunting through the repo.
- Zero-friction setup: `bmd discover` finds Blackmagic devices on the local
  network via mDNS and can write the chosen device straight into config — no
  hunting for IP addresses.

## Non-goals (v1)

- Named device registry / multi-device contexts (the config format leaves room:
  git subsection syntax `[videohub "studio-a"]` can add this later).
- HyperDeck/ATEM support (architecture leaves room; no shared abstraction is
  built until a second device exists).
- Shell tab-completion (revisit if demand appears; ConsoleAppFramework does not
  provide it).
- Restoring output locks from snapshots (locks belong to the controller that
  set them; a future explicit `--include-locks` if ever wanted).

## Command grammar

Executable name: **`bmd`**. Device type is the top-level noun group (like
`gh repo …`), then noun, then verb.

```
bmd videohub info                          # model, input/output counts
bmd videohub input list
bmd videohub input rename <n> <name>
bmd videohub output list                   # label, routed input, lock state
bmd videohub output rename <n> <name>
bmd videohub output lock <n>
bmd videohub output unlock <n> [--force]   # --force clears another controller's lock
bmd videohub route list
bmd videohub route set <output> <input>
bmd videohub watch                         # live stream of route/label/lock changes
bmd videohub export [file]                 # snapshot; no file → stdout
bmd videohub restore <file>                # apply snapshot; '-' → stdin
                                           # mutating commands take --no-backup

bmd multiview info                         # model, input/view counts, current layout
bmd multiview input list
bmd multiview input rename <n> <name>
bmd multiview view list                    # label, source, lock state
bmd multiview view rename <n> <name>
bmd multiview view lock <n>
bmd multiview view unlock <n> [--force]
bmd multiview view set <view> <input>      # put a source in a view
bmd multiview watch
bmd multiview export [file]                # snapshot incl. configuration
bmd multiview restore <file>

bmd multiview layout <value>               # e.g. 2x2 — device validates, not bmd
bmd multiview format <value>               # e.g. 1080i5994 — device validates
bmd multiview solo <input> | off           # solo a source, or leave solo mode
bmd multiview show <what> <on|off>         # borders | labels | audio-meters | tally
                                           #   — exactly the four "Display *" properties
bmd multiview take-mode <on|off>           # behaviour, not display: kept out of `show`
bmd multiview widescreen-sd <on|off>
bmd multiview config [--json]              # print the whole CONFIGURATION block

bmd config set <key> <value> [--global]
bmd config get <key>
bmd config unset <key> [--global]
bmd config list [--show-origin]

bmd discover [--timeout <sec>] [--json]    # list supported devices found via mDNS
bmd discover --all                         # list every Blackmagic responder
bmd discover --add [--global]              # discover, pick from list, write config

bmd version                                # embedded version + RID, e.g. "1.2.0 (win-x64)"
bmd update [--check]                       # --check reports only; bare form self-updates
```

Common options on every `videohub` and `multiview` command, reading from that
group's own config section (`videohub.host`, `multiview.host`, …):

| Option | Default | Notes |
|---|---|---|
| `--host <addr>` | from config `<group>.host` | required if not configured |
| `--port <n>` | from config `<group>.port`, else 9990 | |
| `--timeout <sec>` | from config `<group>.timeout`, else 5 | connect + command timeout |
| `--json` | off | every command; machine-readable output (see "Agents and scripting") |

### Numbering rule (hard rule)

The CLI is **1-based** everywhere the user sees a number — matching device
front panels and Blackmagic's Videohub software. The wire protocol is 0-based;
conversion happens at exactly one place, the protocol layer boundary.
Inconsistency here is the most user-hostile bug this tool could have.

## Agents and scripting (interface contract)

AI agents and scripts are first-class users. Two consequences bind every
command, current and future:

**`--json` on every command.** Passing `--json` switches stdout to a single
JSON document (object or array; camelCase, stable field names). It changes
representation only, never behavior. Mutating commands emit a result object
describing what changed, so an agent never needs a follow-up query:

```
bmd config get videohub.host --json    → {"key":"videohub.host","value":"10.0.0.5","origin":"C:\\studio\\.bmdconfig"}
bmd config list --json                 → [{"key":"videohub.host","value":"10.0.0.5","origin":"..."}]
bmd config set videohub.host 10.0.0.9 --json
                                       → {"key":"videohub.host","value":"10.0.0.9","file":"..."}
bmd config unset videohub.host --json  → {"key":"videohub.host","removed":true}
bmd videohub info --json               → {"modelName":"...","videoInputs":20,"videoOutputs":20,...}
bmd videohub output list --json        → [{"n":1,"label":"Monitor","input":4,"inputLabel":"Cam 4","lock":"unlocked"}]
bmd videohub route list --json         → [{"output":1,"outputLabel":"Monitor","input":4,"inputLabel":"Cam 4"}]
```

The one exception is a streaming command (`videohub watch`): a single document
can't represent an unbounded stream, so `--json` there emits JSON Lines — one
compact object per line, flushed per event. Every non-streaming command still
emits exactly one document.

Errors are unaffected by `--json`: always one plain `error: ...` line on
stderr plus the exit code (0 success / 1 operation failure / 2 usage or
format error). Agents branch on exit code and read stderr for the reason;
stdout carries only the result document (nothing on failure). Serialization
uses System.Text.Json **source generators** through one `BmdJsonContext`
(AOT-safe; reflection-based serialization is forbidden).

**Help is part of the API.** Every command and flag carries a complete
description (generated from XML doc comments); argument docs state units,
ranges, and 1-based numbering where relevant. An agent reading `--help`
output alone must be able to construct a correct invocation. Renaming
commands, flags, or JSON fields is a breaking change to be treated like one.

## Configuration

Git-config model: INI files, layered, device-type sections.

```ini
[videohub]
    host = 10.0.0.5
    port = 9990
```

- **Global:** `~/.config/bmd/config` (XDG; honored on Linux/macOS) and
  `%APPDATA%\bmd\config` on Windows.
- **Local:** `.bmdconfig`, discovered by walking up from the current working
  directory (like git finding `.git`). Lets a per-studio/per-project folder pin
  its own device.
- **Precedence:** command-line flag > local `.bmdconfig` > global config >
  built-in default.
- Keys are addressed as `section.key` (e.g. `videohub.host`). Non-device
  sections exist too: `update.check = true|false` (default true) controls the
  passive update notice.
- `bmd config list --show-origin` prints the file each effective value came from.
- The INI parser is hand-written (~50 lines), AOT-safe, zero dependencies.
  Subset supported: `[section]` headers, `key = value`, `#`/`;` comments,
  quoted values. Subsection syntax `[videohub "name"]` is parsed and preserved
  but unused in v1.

## Videohub protocol layer

The Videohub Ethernet Protocol is line-oriented text over **TCP port 9990**.
On connect, the device pushes a full state dump as blank-line-terminated
blocks; afterwards it pushes incremental update blocks whenever state changes
(from any controller), and answers client-sent blocks with `ACK` or `NAK`.

Blocks (v1 relevant):

```
PROTOCOL PREAMBLE:          # Version: 2.x
VIDEOHUB DEVICE:            # Model name, Video inputs, Video outputs, …
INPUT LABELS:               # <index> <label>
OUTPUT LABELS:              # <index> <label>
VIDEO OUTPUT ROUTING:       # <output-index> <input-index>
VIDEO OUTPUT LOCKS:         # <output-index> U|O|L   (unlocked / owned by us / locked by other)
```

Client mutations send the same block shapes (e.g.
`VIDEO OUTPUT ROUTING:\n2 6\n\n`); lock changes send `O` (lock), `U` (unlock
own), `F` (force-unlock another controller's lock).

Design:

- `VideohubClient` — plain async TCP client (`System.Net.Sockets`): connect,
  read initial dump into a `VideohubState` model, expose typed operations
  (`SetRouteAsync`, `RenameInputAsync`, `LockOutputAsync`, …) that send a block
  and await `ACK`/`NAK`. An `IAsyncEnumerable<VideohubUpdate>` surfaces pushed
  updates for `watch`.
- **Protocol parsing/serialization is pure functions over text blocks** —
  no sockets — so it is unit-testable against recorded transcripts.
- 0-based↔1-based conversion lives at this layer's public boundary: the model
  and everything above it are 1-based; the wire code is 0-based.

## MultiView

The Blackmagic MultiView 4 speaks the **same Videohub Ethernet Protocol**, on
the same TCP port 9990, reporting the same protocol version 2.8. This was
established by probing a real device (`192.168.4.96`, firmware 2.2.5): every
existing `bmd videohub` command already works against it unmodified, because
`DumpParser` ignores block headers it does not recognise.

Captured dump, verbatim, which is the fixture the fake MultiView server is
seeded from:

```
PROTOCOL PREAMBLE:          VIDEOHUB DEVICE:
Version: 2.8                Device present: true
                            Model name: Blackmagic MultiView 4
INPUT LABELS:               Friendly name: AV Multiview
0 Stream                    Unique ID: 7C2E0D11C751
1 Screens                   Video inputs: 4
2 Presenter                 Video processing units: 0
3 Confidence                Video outputs: 6
                            Video monitoring outputs: 0
OUTPUT LABELS:              Serial ports: 0
0 View 1
1 View 2                    VIDEO OUTPUT ROUTING:
2 View 3                    0 0    1 1    2 2
3 View 4                    3 3    4 2    5 0
4 Solo Input
5 Audio Input               VIDEO OUTPUT LOCKS:
                            0 U  1 U  2 U  3 U  4 U  5 U
CONFIGURATION:
Layout: 2x2                 Display border: true
Output format: 1080i5994    Display labels: true
Solo enabled: false         Display audio meters: false
Widescreen SD enabled: true Display SDI tally: false
                            Take Mode: true
```

Semantics differ from a router even though the wire format does not: the six
"outputs" are the four multiview windows plus `Solo Input` and `Audio Input`,
so the user-facing noun is **view**, not output. That is why MultiView gets its
own command group rather than being documented as a Videohub.

**The `CONFIGURATION` block is not documented by Blackmagic.** It appears in no
version of the published Videohub Ethernet Protocol PDF; the property list
above is observed, not specified. Two consequences bind the design:

- **bmd never whitelists `Layout` or `Output format` values.** Valid values
  differ by model (MultiView 4 vs 16) and by firmware, and no authoritative list
  exists to track. bmd sends what the user asked for and surfaces the device's
  `NAK` as a clear error. Help text documents observed values as examples and
  says plainly that the device is the authority.
- **The booleans are validated by bmd**, since `true`/`false` is unambiguous:
  solo, widescreen SD, display border, display labels, display audio meters,
  display SDI tally, take mode.

Design:

- `DumpParser` **retains unrecognised blocks** as ordered key/value pairs rather
  than discarding them, so `CONFIGURATION` reaches the model without the
  protocol layer growing MultiView-specific knowledge. Future device blocks
  arrive the same way.
- `Devices/MultiView/MultiViewConfiguration.cs` is the typed view over that
  block plus its serialization for writes. `Devices/Videohub/` stays the home
  of the protocol itself — Blackmagic's own name for it is the *Videohub*
  Ethernet Protocol, and the MultiView speaks it.
- The command bodies shared by both groups (routing, labels, locks, export,
  restore, watch) are extracted into a device-agnostic core. `VideohubCommands`
  and `MultiViewCommands` stay thin and keep **separate XML doc comments**,
  because the vocabulary differs and help text is the API. This is the shared
  device abstraction that was deliberately deferred until a second device type
  existed.
- **Snapshots include configuration** for MultiView: the event workflow is the
  headline use case and a multiviewer's layout is part of its setup. The field
  is optional — a snapshot without it restores exactly as today, and restore
  leaves configuration untouched when it is absent.
- `bmd videohub` continues to work against a MultiView, undocumented and
  unwarned. It works today, it may already be scripted, and a warning printed
  by a working command is noise.

## Device discovery

Blackmagic devices announce themselves via mDNS/DNS-SD: classic devices
(ATEM, older HyperDecks, Videohub) advertise `_blackmagic._tcp.local`, newer
devices `_bmd_blockcfg._tcp.local`. Each responder's TXT record carries a
`class=` identifier (e.g. `AtemSwitcher`) naming the device kind.

- **Mechanism:** query both service types on UDP multicast 224.0.0.251:5353,
  collect responses for the timeout window (default 3 s), resolve each
  responder's PTR → SRV/TXT/A records into model/name, class, IP, port.
- **Filtering:** a class→device-type mapping table decides what counts as
  "supported". Default output lists only supported devices; `--all` lists every
  Blackmagic responder — the tool for verifying discovery works, and for
  learning real-world `class` strings.
  **The mapping table is data to be confirmed empirically against real
  hardware** — public documentation of the class values is thin, so `--all`
  output feeds the table. Classes observed on real hardware so far:
  `Videohub` (Smart Videohub 40x40), `MultiView` (MultiView 4), and
  `AtemSwitcher` (several ATEM models). Note that HyperDeck and the
  WebPresenter/Streaming Bridge family answer on port **9977** and advertise no
  `class=` at all, identifying themselves by a `product id` instead — so
  supporting them later needs a second identification path, not just another
  row in this table.
- **`--add` flow:** discover, print a numbered list of supported devices,
  prompt for a selection, write config exactly as
  `bmd config set videohub.host <ip>` would — local by default, `--global`
  for the user-level file. If stdin is not a TTY: clear error pointing at
  `config set`.
- **Implementation:** minimal hand-rolled mDNS client (~300 lines), per the
  zero-dependency/AOT rule (available .NET zeroconf libraries are unverified
  for AOT). DNS packet encode/parse is pure functions over bytes, unit tested
  against captured packet fixtures — same pattern as the protocol parser.
  Lives in `Devices/Discovery/`.
- **Documented limitations:** mDNS does not cross subnets, and some older
  Videohubs predate mDNS support. Manual `config set` remains the fallback;
  the site's videohub page says so.

## Snapshot export / restore

**Format:** JSON via System.Text.Json **source generators** (AOT-safe,
reflection-free). Self-describing and diffable:

```json
{
  "device": "Blackmagic Smart Videohub 20x20",
  "videoInputs": 20,
  "videoOutputs": 20,
  "exportedAt": "2026-08-29T10:12:00Z",
  "inputs":  [ { "n": 1, "label": "Cam 1" } ],
  "outputs": [ { "n": 1, "label": "Program", "input": 3 } ]
}
```

Captured: input labels, output labels, routing. **Locks are excluded** (see
non-goals).

**Export is verified.** Flow:

1. Capture — read the device's full state dump.
2. Write — serialize to the target file (or stdout).
3. Verify — read the written file back, deserialize, fetch a *fresh* dump from
   the device, compare field-by-field (every label, every route). Proves the
   file round-trips intact **and** still matches the device.
4. On mismatch (e.g. someone routed mid-export): re-capture and retry, up to
   3 attempts, then exit 1 listing the differing entries. For stdout exports,
   verification compares the in-memory serialized bytes instead of re-reading
   a file.
5. Success: `Exported and verified: 20 inputs, 20 outputs, 20 routes → normal.json`.

**Restore** validates the snapshot against the connected device (model +
input/output counts; mismatch → refuse with a clear error), then applies only
the differences, printing each change as it is made
(`route 3: Cam 2 → Cam 1`). No-op restores say so and exit 0. Restore
converges the device to the file, so re-running after a partial failure
(dropped connection mid-restore) is safe and idempotent.

Event workflow this enables:

```
bmd videohub export normal.json     # before the event
…arbitrary changes during the event…
bmd videohub restore normal.json    # back to house config
```

## Automatic backup before mutations

Every mutating command (`route set`, renames, lock/unlock, and `restore`
itself) writes a snapshot of the device's pre-change state first. This is
close to free: `VideohubClient.ConnectAsync` already reads the complete state
dump before any command runs, so a backup costs one local file write and zero
extra network round-trips.

- **Location:** `%LOCALAPPDATA%\bmd\backups\<device>\<timestamp>.json` on
  Windows; `$XDG_STATE_HOME/bmd/backups/...` (else `~/.local/state/bmd/...`)
  elsewhere. `<device>` is a filesystem-safe key derived from host and model,
  so multiple hubs never share a directory. Distinct from the config dir
  (settings) and the cache dir (update checks).
- **Written from the dump already in memory**, then verified by reading the
  file back and comparing to that in-memory state. It does **not** re-query
  the device — the explicit `bmd videohub export` keeps the full fresh-dump
  verification with retries, because that is the snapshot users rely on.
- **A failed backup aborts the mutation** (clear error, exit 1, device
  untouched). Mutating after a failed backup would defeat the purpose.
  `--no-backup` is the escape hatch for scripted bulk changes where one
  snapshot was taken up front.
- **Every mutation reports its backup path** — in human output and in the
  `--json` result object — so a script or agent that just changed something
  holds the exact file that undoes it.
- **Config:** `backup.auto` (default `true`), `backup.keep` (default `10`;
  oldest pruned per device), `backup.dir` (override the location).

## Technology choices

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (LTS) | current LTS; Native AOT mature |
| CLI parsing | **ConsoleAppFramework v5** (Cysharp) | source-generator based: zero dependency, zero reflection, AOT-safe, minimal binary, fastest cold start; subcommands first-class; help generated from XML doc comments. Chosen over System.CommandLine (also AOT-safe but heavier, slower startup; its shell completions are the one feature we give up) |
| JSON | System.Text.Json + source generators | in-box, AOT-safe |
| Config parsing | hand-written INI (~50 lines) | trivial, zero deps, exactly git's UX |
| Output tables | hand-rolled aligned columns | tables are simple (number, label, route, lock); avoids AOT-auditing a rendering lib. Spectre.Console can be reconsidered later |
| Tests | xUnit | standard |

**AOT constraint (project-wide rule):** no reflection-dependent libraries, no
`dynamic`, JSON only via source-gen contexts. `PublishAot=true` stays on from
day one so violations surface immediately, not at release time.

## Architecture

```
src/Bmd/                     # single console project (AssemblyName: bmd)
  Program.cs                 # ConsoleAppFramework root; wires command groups
  Commands/
    ConfigCommands.cs
    DiscoverCommands.cs
    Videohub/                # one thin class per noun: parse → client → format
  Config/                    # layered config: INI parser, resolver, writer
  Devices/
    Discovery/               # mDNS client, DNS packet codec, class mapping
    Videohub/                # VideohubClient, protocol parser, models, snapshot
  Output/                    # table/JSON formatting helpers
tests/Bmd.Tests/
```

Layering rules:

- `Devices/` never references ConsoleAppFramework or `Commands/` — it is a
  standalone client library in waiting.
- `Commands/` classes are thin: resolve config, construct client, call one
  operation, format output. Logic lives below.
- Future devices repeat the pattern (`Commands/Hyperdeck/` +
  `Devices/Hyperdeck/`). No shared `IDevice` abstraction until a second device
  exists and the commonality is real (Videohub/HyperDeck are text-over-TCP;
  ATEM is binary UDP — guessing the abstraction now would guess wrong).

## Error handling & exit codes

| Code | Meaning |
|---|---|
| 0 | success (including no-op restore) |
| 1 | operation failed: connect/timeout, `NAK`, verification failure, snapshot mismatch |
| 2 | usage error (bad arguments) |

Failures print one clear human message to stderr (`error: cannot connect to
10.0.0.5:9990 (timed out after 5s)`), never a stack trace. Missing host
configuration gets a helpful hint (`run: bmd config set videohub.host <addr>`).

## Testing strategy

- **Protocol parser:** pure-function unit tests against recorded/synthetic
  protocol transcripts (dump blocks, update blocks, ACK/NAK, edge cases:
  labels with spaces, empty labels, large routers).
- **Config:** unit tests for INI parse/write, layering precedence, walk-up
  discovery (temp dirs).
- **VideohubClient + commands:** integration tests against an in-process
  **fake Videohub TCP server** (~100 lines: serves a dump, applies mutations,
  ACKs, pushes updates). The fake doubles as protocol documentation and lets
  export-verify, restore-diff, watch, and mid-export-change scenarios run
  without hardware.
- **Discovery:** DNS packet encode/parse unit tested against captured mDNS
  packet fixtures; the collect/filter/add flow tested with a fake responder
  on loopback.
- **Self-update:** semver comparison and checksum verification are pure
  functions, unit tested. Download/replace flow tests against a local fake
  releases HTTP server; the platform-specific swap is exercised on a dummy
  file, not the running test binary.
- TDD throughout (test first, then implementation).

## Distribution & releases

Native AOT, single file, per RID: `win-x64`, `linux-x64`, `linux-arm64`,
`osx-x64`, `osx-arm64`.

```
dotnet publish src/Bmd -c Release -r <rid>
```

`PublishAot=true` is set from the first commit so AOT violations surface
immediately.

**Release pipeline (GitHub Actions):** pushing a semver tag (`v1.2.0`) builds
a release. The tag is the single source of version truth — stamped into the
binary (`-p:Version=1.2.0`), reported by `bmd version`, compared by update
checks. Native AOT must build on the target OS, so a matrix covers all five
RIDs: `windows-latest` (win-x64), `ubuntu-latest` (linux-x64),
`ubuntu-24.04-arm` (linux-arm64), `macos-latest` (osx-arm64 and osx-x64).
Each job uploads `bmd-<rid>.zip` (Windows) or `bmd-<rid>.tar.gz` (Unix); a
final job writes `checksums.txt` (SHA-256 of every archive) and creates the
GitHub Release with all assets. Tags with a pre-release suffix
(`v1.2.0-rc.1`) become GitHub pre-releases, which update checks ignore.

## Self-update

`bmd version` prints the embedded version and RID (both baked in at build
time — the RID determines which release asset to fetch).

**`bmd update --check`** queries the GitHub Releases API for the latest
(non-pre-release) release of `davojc/bmdcli` (public repo, no auth), compares
semver against the embedded version, reports, exits 0.

**`bmd update`** additionally:

1. Downloads the asset matching the embedded RID.
2. Verifies its SHA-256 against the release's `checksums.txt` **before
   touching anything**. Bad checksum → abort, exit 1, nothing changed.
3. Self-replaces:
   - **Unix:** extract next to the current executable, `chmod +x`, atomic
     rename over the current path.
   - **Windows:** a running exe cannot be overwritten but can be renamed —
     `bmd.exe` → `bmd.exe.old`, new binary moved into place; the `.old` file
     is silently cleaned up on a later run (the gh/rustup approach).
   - Any failure restores the original. No write permission on the install
     dir → clear error telling the user to re-run elevated.

**Passive check (gh-style):** during any normal command, at most once per
24 hours, a check runs concurrently with the command and caches the result
in the OS cache dir (`~/.cache/bmd` on Unix, `%LOCALAPPDATA%\bmd` on
Windows — note: cache, distinct from config). The command's own work is
never blocked by it; at exit the process waits at most 500 ms for an
in-flight check before printing, so a fast command still gets a result
rather than killing the task on the way out. Network failures are silent.
If a newer version exists, a two-line notice goes to **stderr after** the
command output:

```
A new release of bmd is available: 1.2.0 → 1.4.1
Run `bmd update` to upgrade.
```

Suppressed when: `update.check = false` in config, stderr is not a TTY,
`--json` was passed, or the command is `bmd update`/`bmd version` itself.

## Website (GitHub Pages)

Static site at `https://davojc.github.io/bmdcli/`, source in `site/` on
`main`, deployed by a GitHub Actions workflow (`actions/deploy-pages`) on any
push to `main` touching `site/`. Hand-written HTML/CSS, no build toolchain.
`docs/` stays internal (specs) and is not published.

```
site/
  index.html        # what bmd is, download section, quick start
  videohub.html     # per-device guide; future devices get sibling pages
  style.css
```

- **index.html:** what the tool is, a terminal-style demo snippet, and a
  Download section listing all five platforms via GitHub's stable
  latest-asset URLs
  (`https://github.com/davojc/bmdcli/releases/latest/download/bmd-<rid>.zip`
  / `.tar.gz`) — these always serve the newest release, so the page never
  goes stale on version bumps. A few lines of JS detect the visitor's OS and
  feature the matching download first (plain list works with JS off). Then a
  30-second quick start (`bmd config set videohub.host …` →
  `bmd videohub route list`) and a note that `bmd update` handles upgrades
  thereafter.
- **videohub.html:** per-device guidance — connecting/config, every command
  group with examples (inspect, routing, renames, locks, watch), and the
  event workflow (export → verify → restore) as a worked example.
- **Maintenance rule:** the site documents user-facing behavior; any change
  that adds or alters a command updates the relevant `site/` page in the same
  change. The site is part of "done".
- One-time manual repo setting: Pages → Source = "GitHub Actions".

## Milestones

1. **Skeleton:** project + ConsoleAppFramework wiring + config subsystem
   (`bmd config …` works end-to-end) + AOT publish proven on one RID.
2. **Read path:** retrofit `--json` onto the config commands (the agent
   contract landed after milestone 1 shipped), then protocol parser +
   `VideohubClient` connect/dump + fake server + `info` / `input list` /
   `output list` / `route list` (+ `--json`).
3. **Export + backup store:** snapshot format, `bmd videohub export` with
   verification/retry, and the backup directory + rotation the write path
   will use. Read-only; deliberately ahead of mutations so no mutation ever
   ships without its safety net.
4. **Write path:** `route set`, renames, lock/unlock/force — with automatic
   pre-mutation backup wired in from the first mutating command, never bolted
   on later. Also restructures command registration so group help
   (`bmd videohub --help`) lists only that group.
5. **Restore:** `bmd videohub restore` (diff-apply, device validation,
   idempotent). Needs the write path from milestone 4, which is why it is
   split from export.
6. **Watch:** pushed-update stream → `watch`.
7. **Discovery:** mDNS client + `bmd discover [--all|--add]` — rounds out the
   local workflow before release plumbing.
8. **Release pipeline + site:** tag-triggered GitHub Actions matrix, all-RID
   archives, checksums, GitHub Release creation, `bmd version`; Pages site
   (index + videohub guide + deploy workflow) — download links only work once
   a release exists, so these land together.
9. **Self-update:** `bmd update [--check]` + passive 24h notice.
10. **MultiView:** the second device type, and therefore the point at which the
    shared device abstraction is finally justified by evidence rather than
    speculation. Retain unrecognised protocol blocks; parse and write
    `CONFIGURATION`; extract the shared router core; add the `bmd multiview`
    group with MultiView vocabulary; teach discovery the `MultiView` class;
    extend snapshots with optional configuration. The protocol work is small —
    the device already speaks Videohub — so most of this milestone is the
    abstraction and the new surface.
