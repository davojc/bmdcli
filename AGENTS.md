# bmd for agents

`bmd` is a command-line tool for controlling Blackmagic Design broadcast hardware over a network:
video routers (Videohub), multiviewers (MultiView), and vision mixers (ATEM). It is a single
binary with no runtime dependencies, and every command speaks JSON.

This document is the part that `--help` cannot tell you: the contracts that hold across all
commands, and the handful of places where the hardware's own conventions will surprise you. It
deliberately does **not** list flags or arguments — those live in `--help`, which is generated
from the same source as the code and therefore cannot go stale:

```
bmd --help                    # the device groups
bmd atem --help               # one group's commands
bmd atem aux set --help       # one command's arguments, flags, units and defaults
```

Written for agents, but nothing here is agent-specific — it is just the manual a careful operator
would want on one page.

## The contract

These hold for every command, and are the things worth relying on.

**Exit codes.** `0` success. `1` the operation failed (device unreachable, refused, not
configured). `2` you asked for something impossible (bad arguments, a value out of range, a
missing required flag). A `2` means change the command; a `1` means change the world or try again.

**`--json` gives exactly one JSON document on stdout.** Every command accepts it. Field names are
camelCase and stable — renaming one is treated as a breaking change. The single exception is
`watch`, which streams **JSON Lines**: one object per line, indefinitely, until interrupted.

**Errors are always plain text on stderr**, beginning `error: `, whether or not `--json` was
passed. Never parse stdout to find out whether something failed — use the exit code. With `--json`,
stdout is either one valid document or empty.

**Anything that changes a device snapshots it first** and prints where the backup went. A failed
backup aborts the change. `--no-backup` skips it. Backups rotate per `backup.keep`, live in the OS
state directory, and are plain JSON you can read.

**Commands that would change nothing say so and do nothing.** Re-running a command is safe and
cheap; it will not write to the device or spend a backup.

## Choosing which device a command talks to

Address, port and timeout come from config, with a command-line flag winning over it:

```
bmd config set videohub.host 192.168.1.50
bmd discover --add                          # or pick one off the network
bmd videohub info --host 192.168.1.51       # one-off, ignores config
```

**Contexts name a second device of the same type.** Without them, a second ATEM has nowhere to live
and `bmd discover --add` overwrites the first.

```
bmd config set atem.host 192.168.1.31 --context gallery
bmd atem context list
bmd atem context set gallery
```

The choice persists until changed. A named context never falls back to the default device: if it
has no host configured, that is an error naming the context, not a quiet switch to a different
switcher. Every command that *changes* a device announces which one it acted on — on stderr, so
`--json` stdout is unaffected — whenever a named context is active.

`bmd videohub context list`, `bmd multiview context list` and `bmd atem context list` each show
their own type's devices and which is selected.

## Numbering

**Everything is 1-based**, matching the numbers printed on the hardware: Videohub inputs and
outputs, MultiView views and sources, ATEM auxiliary outputs.

**The exception is ATEM source ids**, which are the switcher's own and are not a dense range —
physical inputs are 1–8, but colour bars are 1000, media player 1 is 3010, and the program output
is 10010. bmd does not renumber them, because its front panel and its own software control do not
either. You can give a **name** instead of an id anywhere an ATEM source is expected, which is
usually easier:

```
bmd atem aux set 1 Stage
bmd atem preview set "Color Bars"
```

Long name or short name, case-insensitive. Ids win over names, so a source literally called `4`
cannot shadow source 4.

## Videohub — routing and labels

A matrix router: any input to any output. `bmd videohub info` reports the size.

```
bmd videohub route list
bmd videohub route set 3 7        # destination FIRST: output 3 now shows input 7
bmd videohub input rename 2 "Camera 2"
bmd videohub output lock 5
bmd videohub watch                # stream changes, including other controllers'
```

**Argument order is destination-first**, matching the front panel. On a square router the two
numbers are interchangeable to a validator, so nothing can detect a swapped pair — get it right.

Locks are per-output and shared across controllers: `U` unlocked, `O` owned by you, `L` held by
someone else. `--force` overrides another controller's lock; the device decides whether to allow
it. `bmd videohub export` captures labels and routing but **not** locks, deliberately — a lock
belongs to whoever holds it.

## MultiView — windows on one screen

Speaks the same protocol as a Videohub, so it works the same way, with different vocabulary: the
things the wire calls outputs are **views**.

```
bmd multiview view list
bmd multiview view set 1 3        # destination first again: view 1 now shows source 3
bmd multiview layout 2x2
bmd multiview solo 3
bmd multiview show borders on
```

On a MultiView 4 the last two "views" are the Solo and Audio inputs rather than windows.

`layout` and `format` are **not validated by bmd** — the valid values differ by model and firmware
and are undocumented, so the device accepts or rejects the value and bmd reports what it said.

`bmd multiview export` captures labels, routing *and* the device's CONFIGURATION block (layout,
format, overlays, solo), so `bmd multiview restore` can put all of it back.

## ATEM — vision mixers

A different protocol entirely (binary UDP, port 9910), but the same command shapes.

```
bmd atem info
bmd atem input list               # physical inputs; --all adds internal sources
bmd atem status                   # what is on program and preview
bmd atem aux list
bmd atem input rename 2 "Camera Two" --short CAM2
bmd atem aux set 1 Stage
bmd atem preview set Stage
bmd atem program set Stage        # CUTS TO AIR — see below
```

**`bmd atem program set` cuts to air immediately.** It asks for confirmation, and where there is no
terminal to ask at it refuses with exit 2 rather than proceeding. Pass `--force` to mean it. This
is the only command in bmd guarded this way, because it is the only one that is both instantaneous
and visible to an audience. `bmd atem preview set` changes nothing on air and is unguarded.

Renaming writes the name **to the switcher**, so it appears in its multiviewer, in ATEM Software
Control and in every other controller — it is not a local alias.

Blackmagic does not publish this protocol. bmd's understanding of it came from capturing a real
switcher and testing against two different models, and every command waits for the device to
confirm the change rather than assuming it landed — so a command that silently does nothing
reports an error rather than success.

**Not yet supported for ATEM:** export, restore, watch, transitions, keyers, media players, audio.

## Config

Git-style layering, read in this order: command-line flag → `.bmdconfig` found by walking up from
the current directory → user config → built-in default.

```
bmd config list
bmd config get videohub.host
bmd config set videohub.host 192.168.1.50
```

**Writes go to the user config unless `--project` is given** — deliberately unlike git, because a
device address belongs to the network rather than to the directory you happen to be standing in.
Use `--project` to pin one directory tree to one device.

## Recipes

**Find what is on the network.**

```
bmd discover --json                  # recognised devices
bmd discover --all --json            # everything answering, including unsupported types
```

**Name every unnamed input on an ATEM.**

```
bmd atem input list --json | jq -r '.[] | select(.name == "") | .id'
```

**Snapshot before a show, restore after.**

```
bmd videohub export before-show.json
bmd videohub restore before-show.json --dry-run     # see what would change
bmd videohub restore before-show.json
```

**React to changes as they happen** (JSON Lines, one object per line):

```
bmd videohub watch --json
```

**Check a device is reachable** without changing anything: `bmd videohub info` and read the exit
code.

## What bmd will not do

It does not support HyperDeck or Web Presenter hardware. It has no ATEM transitions, keyers, media
player or audio control, and no ATEM export, restore or watch. It will not guess at a device type
it does not recognise during discovery. It has no interactive TUI — every command does one thing
and exits.

When something is missing, `bmd <group> --help` is the authoritative list of what exists.
