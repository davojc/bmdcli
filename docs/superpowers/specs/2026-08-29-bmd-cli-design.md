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
- Scriptable: `--json` output on read commands, meaningful exit codes,
  stdout/stdin-friendly export/restore.
- Self-updating: releases built by GitHub Actions on semver tags; `bmd update`
  downloads, verifies, and installs the latest release in place.
- Discoverable: a GitHub Pages site documents the tool per device and gives
  users their initial download without hunting through the repo.

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

bmd config set <key> <value> [--global]
bmd config get <key>
bmd config unset <key> [--global]
bmd config list [--show-origin]

bmd version                                # embedded version + RID, e.g. "1.2.0 (win-x64)"
bmd update [--check]                       # --check reports only; bare form self-updates
```

Common options on every `videohub` command:

| Option | Default | Notes |
|---|---|---|
| `--host <addr>` | from config `videohub.host` | required if not configured |
| `--port <n>` | from config `videohub.port`, else 9990 | |
| `--timeout <sec>` | from config `videohub.timeout`, else 5 | connect + command timeout |
| `--json` | off | read commands only; machine-readable output |

### Numbering rule (hard rule)

The CLI is **1-based** everywhere the user sees a number — matching device
front panels and Blackmagic's Videohub software. The wire protocol is 0-based;
conversion happens at exactly one place, the protocol layer boundary.
Inconsistency here is the most user-hostile bug this tool could have.

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
    Videohub/                # one thin class per noun: parse → client → format
  Config/                    # layered config: INI parser, resolver, writer
  Devices/
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
24 hours, a background task fetches the latest version and caches the result
in the OS cache dir (`~/.cache/bmd` on Unix, `%LOCALAPPDATA%\bmd` on
Windows — note: cache, distinct from config). It never delays the command;
network failures are silent. If a newer version exists, a two-line notice
goes to **stderr after** the command output:

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
2. **Read path:** protocol parser + `VideohubClient` connect/dump + fake server
   + `info` / `input list` / `output list` / `route list` (+ `--json`).
3. **Write path:** `route set`, renames, lock/unlock/force.
4. **Watch:** pushed-update stream → `watch`.
5. **Snapshots:** `export` (with verification/retry) + `restore` (diff-apply).
6. **Release pipeline + site:** tag-triggered GitHub Actions matrix, all-RID
   archives, checksums, GitHub Release creation, `bmd version`; Pages site
   (index + videohub guide + deploy workflow) — download links only work once
   a release exists, so these land together.
7. **Self-update:** `bmd update [--check]` + passive 24h notice.
