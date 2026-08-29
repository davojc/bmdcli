# bmd Milestone 6: Watch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `bmd videohub watch` streams route, label, and lock changes as they happen — including changes made by other controllers or the front panel — until interrupted.

**Architecture:** A pure `VideohubUpdate.Diff(before, after)` turns any two states into a change list; `VideohubClient.WatchAsync` becomes a continuous, idle-tolerant read loop yielding those updates as blocks arrive; the fake device gains the ability to push updates so every path is tested without hardware. A separate task closes the milestone-5 deferral about backups on no-op restores.

**Tech Stack:** .NET 10, ConsoleAppFramework v5, System.Text.Json source generators, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` ("Videohub protocol layer" — the `IAsyncEnumerable<VideohubUpdate>` for watch; "Agents and scripting")

## Global Constraints

- `PublishAot=true` stays green: no reflection, no `dynamic`, JSON ONLY via the source-generated contexts. No new package references.
- `Devices/` and `Config/` never reference ConsoleAppFramework or `Commands/`.
- **All user-facing numbering is 1-based.**
- Errors are one `error: ...` line on stderr + exit code (0 success / 1 operation failure / 2 usage or format error); stdout carries only the stream.
- Help is the API: full XML doc comments in plain prose (ConsoleAppFramework renders `<paramref>`/`<see>` tags literally, and multi-line `///` summaries render with embedded newlines).
- TDD: failing test → implementation → passing test → commit, every task.

**Watch-specific rules (these are the ones that make or break this milestone):**
- **Watch must never time out while idle.** The client's configured timeout bounds *connect* and *command acknowledgement*; a watch may legitimately sit silent for hours. Its read loop takes only the caller's cancellation token.
- **`--json` emits JSON Lines** — one compact JSON object per line, flushed per event. A single JSON document cannot represent an unbounded stream; this is the standard shape for streaming CLIs and is the deliberate reading of the spec's `--json` rule. Every other command keeps emitting one document.
- **Ctrl+C is the normal way to stop a watch**, so it exits **0**, not an error code. Partial output already written stays written.
- **Do not run `WatchAsync` concurrently with a mutation** on the same client — both consume the single shared reader. Watch is read-only, so nothing in this milestone does; document it on the method.

---

### Task 1: VideohubUpdate + Diff (pure)

**Files:**
- Create: `src/Bmd/Devices/Videohub/VideohubUpdate.cs`, `tests/Bmd.Tests/Devices/Videohub/VideohubUpdateTests.cs`

**Interfaces:**
- Consumes: `VideohubState`, `LockState`.
- Produces (namespace `Bmd.Devices.Videohub`):
  - `enum VideohubUpdateKind { InputLabel, OutputLabel, Route, Lock }`
  - `sealed record VideohubUpdate(VideohubUpdateKind Kind, int N, string From, string To)` with `string Describe()`:
    - `InputLabel` → `input 2 label: 'Cam 2' → 'Camera Two'`
    - `OutputLabel` → `output 4 label: 'Aux' → 'Aux Feed'`
    - `Route` → `route 3: Cam 2 → Cam 1`
    - `Lock` → `output 1 lock: unlocked → owned`
  - `static IReadOnlyList<VideohubUpdate> Diff(VideohubState before, VideohubState after)` — every difference, ordered input labels → output labels → routes → locks, ascending within each kind. When the two states describe different device sizes (should not happen on one connection), compare only the overlapping range and ignore the rest rather than throwing.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/VideohubUpdateTests.cs`:

```csharp
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubUpdateTests
{
    static VideohubState State(string dump = Fixtures.Dump4x4) =>
        DumpParser.Parse(BlockReader.ReadBlocks(dump));

    [Fact]
    public void Diff_IdenticalStates_IsEmpty()
    {
        Assert.Empty(VideohubUpdate.Diff(State(), State()));
    }

    [Fact]
    public void Diff_RouteChange_IsReportedWithLabels()
    {
        var after = State(Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0"));
        var update = Assert.Single(VideohubUpdate.Diff(State(), after));
        Assert.Equal(VideohubUpdateKind.Route, update.Kind);
        Assert.Equal(1, update.N);
        Assert.Equal("Cam 4", update.From);
        Assert.Equal("Cam 1", update.To);
        Assert.Equal("route 1: Cam 4 → Cam 1", update.Describe());
    }

    [Fact]
    public void Diff_InputLabelChange_IsReported()
    {
        var after = State(Fixtures.Dump4x4.Replace("1 Cam 2", "1 Camera Two"));
        var update = Assert.Single(VideohubUpdate.Diff(State(), after));
        Assert.Equal(VideohubUpdateKind.InputLabel, update.Kind);
        Assert.Equal(2, update.N);
        Assert.Equal("input 2 label: 'Cam 2' → 'Camera Two'", update.Describe());
    }

    [Fact]
    public void Diff_OutputLabelChange_IsReported()
    {
        var after = State(Fixtures.Dump4x4.Replace("3 Aux", "3 Aux Feed"));
        var update = Assert.Single(VideohubUpdate.Diff(State(), after));
        Assert.Equal(VideohubUpdateKind.OutputLabel, update.Kind);
        Assert.Equal(4, update.N);
        Assert.Equal("output 4 label: 'Aux' → 'Aux Feed'", update.Describe());
    }

    [Fact]
    public void Diff_LockChange_IsReportedWithWords()
    {
        var after = State(Fixtures.Dump4x4.Replace("VIDEO OUTPUT LOCKS:\n0 U", "VIDEO OUTPUT LOCKS:\n0 O"));
        var update = Assert.Single(VideohubUpdate.Diff(State(), after));
        Assert.Equal(VideohubUpdateKind.Lock, update.Kind);
        Assert.Equal(1, update.N);
        Assert.Equal("output 1 lock: unlocked → owned", update.Describe());
    }

    [Fact]
    public void Diff_MultipleChanges_AreOrderedByKindThenNumber()
    {
        var after = State(Fixtures.Dump4x4
            .Replace("1 Cam 2", "1 Camera Two")
            .Replace("3 Aux", "3 Aux Feed")
            .Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0")
            .Replace("VIDEO OUTPUT LOCKS:\n0 U", "VIDEO OUTPUT LOCKS:\n0 O"));
        var updates = VideohubUpdate.Diff(State(), after);
        Assert.Equal(4, updates.Count);
        Assert.Equal(VideohubUpdateKind.InputLabel, updates[0].Kind);
        Assert.Equal(VideohubUpdateKind.OutputLabel, updates[1].Kind);
        Assert.Equal(VideohubUpdateKind.Route, updates[2].Kind);
        Assert.Equal(VideohubUpdateKind.Lock, updates[3].Kind);
    }

    [Fact]
    public void Diff_DifferentDeviceSizes_ComparesOverlapOnly_DoesNotThrow()
    {
        var smaller = Fixtures.Dump4x4
            .Replace("Video outputs: 4", "Video outputs: 3")
            .Replace("3 Aux\n", "").Replace("3 U\n", "").Replace("3 2\n", "");
        var updates = VideohubUpdate.Diff(State(), State(smaller));
        Assert.Empty(updates);   // the overlapping 1..3 are identical
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubUpdateTests`
Expected: compilation failure — the type does not exist.

- [ ] **Step 3: Implement `src/Bmd/Devices/Videohub/VideohubUpdate.cs`**

```csharp
namespace Bmd.Devices.Videohub;

public enum VideohubUpdateKind { InputLabel, OutputLabel, Route, Lock }

/// <summary>One observed change on a device, in 1-based terms.</summary>
public sealed record VideohubUpdate(VideohubUpdateKind Kind, int N, string From, string To)
{
    public string Describe() => Kind switch
    {
        VideohubUpdateKind.InputLabel => $"input {N} label: '{From}' → '{To}'",
        VideohubUpdateKind.OutputLabel => $"output {N} label: '{From}' → '{To}'",
        VideohubUpdateKind.Route => $"route {N}: {From} → {To}",
        _ => $"output {N} lock: {From} → {To}",
    };

    /// <summary>Every difference between two states, ordered input labels, output labels,
    /// routes, then locks. Differing device sizes compare only the overlapping range.</summary>
    public static IReadOnlyList<VideohubUpdate> Diff(VideohubState before, VideohubState after)
    {
        var updates = new List<VideohubUpdate>();
        var inputs = Math.Min(before.Device.VideoInputs, after.Device.VideoInputs);
        var outputs = Math.Min(before.Device.VideoOutputs, after.Device.VideoOutputs);

        for (var n = 1; n <= inputs; n++)
            if (before.GetInputLabel(n) != after.GetInputLabel(n))
                updates.Add(new VideohubUpdate(
                    VideohubUpdateKind.InputLabel, n, before.GetInputLabel(n), after.GetInputLabel(n)));

        for (var n = 1; n <= outputs; n++)
            if (before.GetOutputLabel(n) != after.GetOutputLabel(n))
                updates.Add(new VideohubUpdate(
                    VideohubUpdateKind.OutputLabel, n, before.GetOutputLabel(n), after.GetOutputLabel(n)));

        for (var n = 1; n <= outputs; n++)
        {
            var wasRoutedTo = before.GetRoute(n);
            var isRoutedTo = after.GetRoute(n);
            if (wasRoutedTo == isRoutedTo) continue;
            updates.Add(new VideohubUpdate(
                VideohubUpdateKind.Route, n,
                LabelOf(before, wasRoutedTo, inputs), LabelOf(after, isRoutedTo, inputs)));
        }

        for (var n = 1; n <= outputs; n++)
            if (before.GetLock(n) != after.GetLock(n))
                updates.Add(new VideohubUpdate(
                    VideohubUpdateKind.Lock, n, Word(before.GetLock(n)), Word(after.GetLock(n))));

        return updates;
    }

    static string LabelOf(VideohubState state, int input, int knownInputs) =>
        input >= 1 && input <= knownInputs ? state.GetInputLabel(input) : $"input {input}";

    /// <summary>The lock words used across the CLI: unlocked, owned, locked.</summary>
    public static string Word(LockState lockState) => lockState switch
    {
        LockState.Owned => "owned",
        LockState.Locked => "locked",
        _ => "unlocked",
    };
}
```

Note: `VideohubCommands` already has a private `LockWord` helper with identical wording. Leave it alone for now — Task 3's reviewer will see both; if it reads as duplication worth removing, do it there where both call sites are visible.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all 215 existing plus the new ones pass.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: VideohubUpdate diffing two device states"
```

---

### Task 2: Fake device can push updates

**Files:**
- Modify: `tests/Bmd.Tests/Devices/Videohub/FakeVideohub.cs`
- Create: `tests/Bmd.Tests/Devices/Videohub/FakeVideohubPushTests.cs`

**Interfaces:**
- Produces on `FakeVideohub` (all arguments in WIRE 0-based indices, matching the fake's existing server-side accessors):
  - `Task PushRouteAsync(int output, int input)` — updates server state and broadcasts `VIDEO OUTPUT ROUTING:` to every connected client
  - `Task PushInputLabelAsync(int input, string label)` — broadcasts `INPUT LABELS:`
  - `Task PushOutputLabelAsync(int output, string label)` — broadcasts `OUTPUT LABELS:`
  - `Task PushLockAsync(int output, char letter)` — broadcasts `VIDEO OUTPUT LOCKS:` (`U`/`O`/`L`)
  - Each is a no-op (but must not throw) when no client is connected.
- The fake must track connected clients' writers so it can broadcast; guard the collection with the existing `_gate`, and remove a client's writer when its handler ends.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/FakeVideohubPushTests.cs`:

```csharp
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class FakeVideohubPushTests
{
    static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PushedRoute_ReachesAConnectedClient()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);

        await fake.PushRouteAsync(output: 0, input: 1);   // wire indices

        // the client only folds updates in while reading; read one update through the watch loop
        using var cts = new CancellationTokenSource(Timeout5);
        await foreach (var update in client.WatchAsync(cts.Token))
        {
            Assert.Equal(VideohubUpdateKind.Route, update.Kind);
            Assert.Equal(1, update.N);          // 1-based
            Assert.Equal("Cam 2", update.To);   // wire input 1 → 1-based input 2
            break;
        }
        Assert.Equal(1, fake.Routes()[0]);
    }

    [Fact]
    public async Task PushedLabelsAndLocks_ReachAConnectedClient()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);

        await fake.PushInputLabelAsync(0, "Camera One");
        await fake.PushLockAsync(0, 'O');

        var seen = new List<VideohubUpdate>();
        using var cts = new CancellationTokenSource(Timeout5);
        await foreach (var update in client.WatchAsync(cts.Token))
        {
            seen.Add(update);
            if (seen.Count == 2) break;
        }

        Assert.Contains(seen, u => u.Kind == VideohubUpdateKind.InputLabel && u.To == "Camera One");
        Assert.Contains(seen, u => u.Kind == VideohubUpdateKind.Lock && u.To == "owned");
    }

    [Fact]
    public async Task Push_WithNoClientConnected_DoesNotThrow()
    {
        await using var fake = FakeVideohub.Start();
        await fake.PushRouteAsync(0, 1);
        Assert.Equal(1, fake.Routes()[0]);
    }
}
```

(These tests exercise `WatchAsync` from Task 3's client work — like milestone 4's Tasks 2+3, this pair is mutually dependent. The controller may dispatch Tasks 2 and 3's client half together; see the plan's Self-Review Notes.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FakeVideohubPushTests`
Expected: compilation failure — the push methods (and `WatchAsync`) do not exist.

- [ ] **Step 3: Implement**

In `FakeVideohub`:
- Add `readonly List<StreamWriter> _writers = [];` guarded by `_gate`.
- In `HandleClientAsync`, after creating the connection's `StreamWriter`, add it under the lock; remove it in a `finally` when the handler ends.
- Add a private `async Task BroadcastAsync(string header, string line)` that snapshots the writer list under `_gate`, then writes `"{header}:\n{line}\n\n"` to each outside the lock (never hold the lock across an await), swallowing `IOException`/`ObjectDisposedException` for writers whose client has gone.
- The four public `Push*Async` methods each mutate server state under `_gate` (same arrays the mutation handlers use) and then call `BroadcastAsync`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all pass (after Task 3's `WatchAsync` exists).

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "test: fake Videohub can push updates to connected clients"
```

---

### Task 3: VideohubClient.WatchAsync

**Files:**
- Modify: `src/Bmd/Devices/Videohub/VideohubClient.cs`
- Create: `tests/Bmd.Tests/Devices/Videohub/VideohubClientWatchTests.cs`

**Interfaces:**
- Produces on `VideohubClient`:
  - `IAsyncEnumerable<VideohubUpdate> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)` — reads blocks continuously, folds each into `State`, and yields every resulting `VideohubUpdate`. **No timeout is applied**: it waits indefinitely for the next block. Cancellation ends the sequence cleanly (no exception out of the enumerator). A closed connection ends the sequence by throwing `VideohubProtocolException` (the caller decides what that means).
  - XML doc must state: not safe to run concurrently with a mutation on the same client (both consume the single reader).

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/VideohubClientWatchTests.cs`:

```csharp
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubClientWatchTests
{
    static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Watch_YieldsUpdatesAsTheyArrive()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        using var cts = new CancellationTokenSource(Timeout5);

        var updates = new List<VideohubUpdate>();
        var watching = Task.Run(async () =>
        {
            await foreach (var update in client.WatchAsync(cts.Token))
            {
                updates.Add(update);
                if (updates.Count == 2) break;
            }
        }, cts.Token);

        await fake.PushRouteAsync(0, 0);
        await fake.PushOutputLabelAsync(1, "Preview B");
        await watching;

        Assert.Equal(2, updates.Count);
        Assert.Contains(updates, u => u.Kind == VideohubUpdateKind.Route && u.N == 1);
        Assert.Contains(updates, u => u.Kind == VideohubUpdateKind.OutputLabel && u.To == "Preview B");
    }

    [Fact]
    public async Task Watch_UpdatesClientState()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        using var cts = new CancellationTokenSource(Timeout5);

        await fake.PushRouteAsync(2, 3);
        await foreach (var _ in client.WatchAsync(cts.Token)) break;

        Assert.Equal(4, client.State.GetRoute(3));   // wire (2,3) → 1-based output 3 ← input 4
    }

    [Fact]
    public async Task Watch_DoesNotTimeOutWhileIdle()
    {
        // a client whose configured timeout is short must still watch quietly past it
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync(
            "127.0.0.1", fake.Port, TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var watching = Task.Run(async () =>
        {
            await foreach (var update in client.WatchAsync(cts.Token)) return update;
            return null;
        }, cts.Token);

        await Task.Delay(900, cts.Token);            // three times the client's timeout
        Assert.False(watching.IsCompleted, "watch must not end while merely idle");

        await fake.PushRouteAsync(0, 1);
        var seen = await watching;
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task Watch_Cancellation_EndsTheSequenceCleanly()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        using var cts = new CancellationTokenSource();

        var watching = Task.Run(async () =>
        {
            await foreach (var _ in client.WatchAsync(cts.Token)) { }
        });

        await Task.Delay(100);
        await cts.CancelAsync();
        await watching;   // must complete without throwing
    }

    [Fact]
    public async Task Watch_ConnectionClosed_ThrowsProtocolException()
    {
        var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        using var cts = new CancellationTokenSource(Timeout5);

        var watching = Task.Run(async () =>
        {
            await foreach (var _ in client.WatchAsync(cts.Token)) { }
        }, cts.Token);

        await Task.Delay(100);
        await fake.DisposeAsync();   // server goes away

        await Assert.ThrowsAsync<VideohubProtocolException>(() => watching);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubClientWatchTests`
Expected: compilation failure — `WatchAsync` does not exist.

- [ ] **Step 3: Implement**

```csharp
    /// <summary>Streams changes as the device reports them, updating State as it goes.
    /// Waits indefinitely between updates — the client's timeout does not apply here.
    /// Not safe to call while a mutation is in flight on the same client: both consume
    /// the connection's single reader.</summary>
    public async IAsyncEnumerable<VideohubUpdate> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var accumulator = new BlockAccumulator();
        while (true)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;      // cancellation ends the stream cleanly
            }
            if (line is null)
                throw new VideohubProtocolException($"connection to {Host}:{Port} closed");

            if (accumulator.Add(line) is not { } block) continue;
            if (block.Header is "ACK" or "NAK") continue;   // not ours to interpret here

            var before = _state;
            ApplyUpdate(block);
            foreach (var update in VideohubUpdate.Diff(before, _state))
                yield return update;
        }
    }
```

Add `using System.Runtime.CompilerServices;` for `[EnumeratorCancellation]`.

Implementer notes:
- `ApplyUpdate` can throw `VideohubProtocolException` for a malformed routing block; let it propagate — a corrupt stream is worth surfacing.
- Do NOT wrap the read in a timeout-linked token; that is the whole point of the idle test.

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`
Expected: all pass, including Task 2's push tests.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: VideohubClient.WatchAsync streaming device updates"
```

---

### Task 4: `bmd videohub watch`

**Files:**
- Modify: `src/Bmd/Commands/Videohub/VideohubCommands.cs`, `src/Bmd/Commands/Videohub/VideohubResults.cs`, `src/Bmd/Output/BmdJsonContext.cs`, `src/Bmd/Program.cs`, `src/Bmd/Commands/GroupHelp.cs`
- Create: `tests/Bmd.Tests/Commands/VideohubWatchTests.cs`

**Interfaces:**
- Produces:
  - `sealed record VideohubUpdateResult(string Kind, int N, string From, string To)` — `Kind` is `"inputLabel"`/`"outputLabel"`/`"route"`/`"lock"` — registered in `BmdJsonContext`
  - `Task<int> Watch(string? host = null, int? port = null, int? timeout = null, bool json = false, CancellationToken cancellationToken = default)` registered as `videohub watch`, and added to `GroupHelp.Commands`.

**Behavior:**
- Connect through the existing seam (the timeout still bounds *connecting*).
- Print a one-line header to **stderr** (`Watching <host>:<port> — press Ctrl+C to stop`) so stdout stays pure for piping; suppress it in `--json` mode.
- For each update: human mode prints `update.Describe()`; `--json` prints one compact JSON object per line (JSON Lines).
- Ctrl+C (the command's `CancellationToken`) ends the stream and returns **0**.
- The connection closing mid-watch surfaces as `VideohubProtocolException` → the shared catch filter → `error: ...`, exit 1.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Commands/VideohubWatchTests.cs` — reuse the console/temp-dir harness from `VideohubRouteSetTests`. Because `Watch` runs until cancelled, each test drives it on a background task with a `CancellationTokenSource`:

```csharp
    [Fact] public async Task Watch_PrintsUpdatesAsTheyArrive()
        // start Watch on a task with a CTS; push a route change and a label change;
        // poll _stdout until both lines appear (bounded wait, e.g. 5s); cancel; assert exit 0
        // and that stdout contains "route 1:" and the new label

    [Fact] public async Task Watch_Json_EmitsOneObjectPerLine()
        // same, with json: true; parse EACH non-empty stdout line with JsonDocument;
        // assert every line is a complete object with kind/n/from/to, and that
        // kind values are the camelCase words; assert stderr has NO header line

    [Fact] public async Task Watch_Human_WritesHeaderToStderrNotStdout()
        // assert stderr contains "Watching" and the host, and stdout does NOT

    [Fact] public async Task Watch_Cancelled_Exit0()
        // cancel immediately after the first update; assert the returned exit code is 0

    [Fact] public async Task Watch_ServerDisappears_Exit1_CleanError()
        // dispose the fake mid-watch; assert exit 1, stderr starts with "error:",
        // stderr has no "   at "

    [Fact] public async Task Watch_NoHostConfigured_Exit1_WithHint()
        // no host anywhere; assert exit 1 and the config hint, same as other commands
```

Write these out fully. For the polling waits use a helper like:

```csharp
    static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("condition not met in time");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubWatchTests`
Expected: compilation failure — `Watch` does not exist.

- [ ] **Step 3: Implement**

Add the result record, register it, and implement (full XML docs in plain prose):

```csharp
    /// <summary>Stream device changes as they happen, including changes made by other controllers. Numbering is 1-based.</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection timeout in seconds; defaults to config videohub.timeout, else 5. Watching itself never times out.</param>
    /// <param name="json">Emit one JSON object per line as updates arrive.</param>
    /// <param name="cancellationToken">Cancelled by Ctrl+C.</param>
    public Task<int> Watch(
        string? host = null, int? port = null, int? timeout = null, bool json = false,
        CancellationToken cancellationToken = default)
        => RunWithClientAsync(host, port, timeout, async client =>
        {
            if (!json)
                Console.Error.WriteLine($"Watching {client.Host}:{client.Port} — press Ctrl+C to stop");

            await foreach (var update in client.WatchAsync(cancellationToken))
            {
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(
                        new VideohubUpdateResult(KindWord(update.Kind), update.N, update.From, update.To),
                        BmdJsonContext.Default.VideohubUpdateResult));
                else
                    Console.WriteLine(update.Describe());
            }
            return 0;
        });

    static string KindWord(VideohubUpdateKind kind) => kind switch
    {
        VideohubUpdateKind.InputLabel => "inputLabel",
        VideohubUpdateKind.OutputLabel => "outputLabel",
        VideohubUpdateKind.Route => "route",
        _ => "lock",
    };
```

Implementer notes:
- Verify ConsoleAppFramework 5.7.13 binds a `CancellationToken` parameter to Ctrl+C automatically (it supports `PosixSignalRegistration`). If it does NOT, wire a `Console.CancelKeyPress` handler in `Program.cs` that cancels a shared source, pass that token in, and record the deviation prominently. Either way, Ctrl+C must exit 0.
- `--json` writes with `Console.WriteLine`, which flushes per line by default for a redirected stream in .NET; if a test shows buffering delays, set `Console.Out.Flush()` after each line and say so.
- Register in `Program.cs` and add to `GroupHelp.Commands`.

- [ ] **Step 4: Run all tests, then exercise help**

Run: `dotnet test`, then `dotnet run --project src/Bmd -- videohub watch --help` and `dotnet run --project src/Bmd -- videohub --help` (watch must be listed).

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: bmd videohub watch streaming live device changes"
```

---

### Task 5: Don't spend a backup on a no-op restore

**Files:**
- Modify: `src/Bmd/Commands/Videohub/VideohubCommands.cs`
- Modify: `tests/Bmd.Tests/Commands/VideohubRestoreTests.cs`

**Background (deferred from milestone 5's final review):** `Restore` backs up inside `WithBackedUpClientAsync` — before the compatibility check and before the plan is computed. So a restore that changes nothing, or that is rejected as incompatible, still writes a backup and triggers rotation. Ten no-op re-runs can age a genuine pre-event backup out of the default `backup.keep = 10`.

**Interfaces:**
- Produces: restore writes its backup only when it is about to apply at least one change. The backup still happens **before any mutation** — that invariant is not negotiable.
- Add to `VideohubCommands`: `Task<int> WithDeferredBackupClientAsync(string? host, int? port, int? timeout, bool noBackup, Func<VideohubClient, Func<Task<string?>>, Task<int>> action)` — connects as usual and hands the action a **backup thunk** it calls once it knows work is needed. The thunk is idempotent (a second call returns the same path without writing again) and returns `null` when backups are disabled.
- `Restore` calls the thunk immediately before its first mutation; `--dry-run`, no-op, and incompatible paths never call it.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Bmd.Tests/Commands/VideohubRestoreTests.cs`:

```csharp
    [Fact]
    public async Task Restore_NoOp_WritesNoBackup()
    {
        await using var fake = FakeVideohub.Start();
        var path = SnapshotFile();                       // snapshot of the fake's current state
        Assert.Equal(0, await Commands().Restore(path, host: "127.0.0.1", port: fake.Port));
        Assert.False(Directory.Exists(BackupDir), "a restore that changes nothing must not spend a backup");
    }

    [Fact]
    public async Task Restore_Incompatible_WritesNoBackup()
    {
        await using var fake = FakeVideohub.Start();
        var path = IncompatibleSnapshotFile();           // 20x20 snapshot vs the 4x4 fake
        Assert.Equal(2, await Commands().Restore(path, host: "127.0.0.1", port: fake.Port));
        Assert.False(Directory.Exists(BackupDir));
    }

    [Fact]
    public async Task Restore_WithChanges_StillWritesBackupBeforeMutating()
    {
        await using var fake = FakeVideohub.Start(DivergentDump);   // needs at least one change
        var path = SnapshotFile();
        Assert.Equal(0, await Commands().Restore(path, host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        var backup = root.GetProperty("backup").GetString();
        Assert.False(string.IsNullOrEmpty(backup));
        Assert.True(File.Exists(backup));
        // the backup must describe the PRE-change state
        var saved = VideohubSnapshot.FromJson(File.ReadAllText(backup!));
        Assert.NotEqual(saved.Outputs[0].Input, root.GetProperty("details")[0].GetProperty("n").GetInt32());
    }
```

Adapt the last assertion to whatever your divergent fixture makes true — the point is to prove the saved snapshot is the state *before* the restore, not after. Reuse or add `DivergentDump`/`IncompatibleSnapshotFile` helpers alongside the existing `SnapshotFile`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubRestoreTests`
Expected: the two "no backup" tests fail — a backup directory is created today.

- [ ] **Step 3: Implement**

Add the deferred-backup seam next to `WithBackedUpClientAsync` (keep the existing one: the five milestone-4 mutations still use it, and their eager backup is correct because they always intend to mutate):

```csharp
    /// <summary>Connects and hands the action a thunk that writes the pre-change backup on demand.
    /// Call the thunk immediately before the first mutation: a command that turns out to have
    /// nothing to do must not spend a backup slot.</summary>
    Task<int> WithDeferredBackupClientAsync(
        string? host, int? port, int? timeout, bool noBackup,
        Func<VideohubClient, Func<Task<string?>>, Task<int>> action)
        => RunWithClientAsync(host, port, timeout, client =>
        {
            string? written = null;
            var done = false;
            Func<Task<string?>> backup = () =>
            {
                if (done) return Task.FromResult(written);
                done = true;
                if (!noBackup)
                {
                    var store = BackupStore.FromConfig(_loadConfig());
                    if (store.AutoBackupEnabled)
                    {
                        var snapshot = VideohubSnapshot.FromState(client.State, DateTimeOffset.UtcNow);
                        written = store.Write(
                            BackupStore.DeviceKey(client.Host, client.State.Device.ModelName), snapshot);
                    }
                }
                return Task.FromResult(written);
            };
            return action(client, backup);
        });
```

Then switch `Restore` to it: compatibility check and `RestorePlan.Compute` first; if `changes.Count == 0` or `dryRun`, never call the thunk; otherwise `var backup = await ensureBackup();` immediately before the apply loop, and use that value in the output exactly as today.

**Note the ordering requirement:** the snapshot handed to `BackupStore.Write` must still be the pre-change state. Because the thunk is called before the first mutation and `client.State` is only mutated by our own sends, this holds — but say so in the report, and make sure a test proves the saved backup is the pre-change state (the third test above).

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`
Expected: all pass, including milestone 5's existing restore tests (the ones asserting a backup path is reported for a restore that does change something).

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "fix: only spend a backup when a restore will actually change something"
```

---

### Task 6: Milestone proof — AOT publish, smokes, help audit

**Files:**
- Modify: none expected

- [ ] **Step 1: Full suite + publish**

```powershell
dotnet test
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/Bmd -c Release -r win-x64
```

Expected: all tests pass; zero IL2xxx/IL3xxx warnings. The AOT-sensitive additions this milestone are the async iterator in `WatchAsync` and the new JSON result type — any warning naming either is a real defect to fix.

- [ ] **Step 2: Native smokes**

```powershell
$exe = "src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe"
& $exe videohub --help                 # watch listed in the group
& $exe videohub watch --help           # host/port/timeout/--json documented; timeout wording notes watching never times out
& $exe videohub watch --host 127.0.0.1 --port 1 --timeout 2; $LASTEXITCODE   # expect error: + exit 1
& $exe videohub restore --help         # unchanged
```

**Ctrl+C check (do this manually and report exactly what happened):** there is no live device to watch here, so the connect fails before the stream starts — state plainly in the report that the interactive Ctrl+C path could NOT be exercised against real hardware in this environment, and that its coverage is the xUnit cancellation tests. Do not fabricate an interactive transcript.

- [ ] **Step 3: Verify no stray state, record size, commit**

```powershell
Test-Path .bmdconfig
Test-Path "$env:LOCALAPPDATA\bmd"
(Get-Item src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe).Length / 1MB
git status --short
git add -A; git commit -m "chore: prove milestone 6 (watch) on Native AOT" --allow-empty
```

---

## Self-Review Notes

- **Spec coverage (milestone 6):** the spec's `IAsyncEnumerable<VideohubUpdate>` for watch (Task 3), pushed-update streaming end to end (Tasks 1-4), plus the milestone-5 backup deferral (Task 5).
- **Tasks 2 and 3 are mutually dependent** (the push tests call `WatchAsync`; the watch tests need a fake that pushes) — exactly milestone 4's Tasks 2+3 situation. The controller should dispatch them together as one unit and expect a single combined commit, or accept a red intermediate state.
- **Deliberate interpretation flagged for review:** `--json` on watch emits JSON Lines rather than one document. Every other command still emits a single document; the spec's rule assumed bounded output.
- **Deliberate:** Ctrl+C exits 0. A watch is *meant* to be interrupted; treating the normal stop as failure would make `bmd videohub watch | head` useless in scripts.
- **Not addressed (carried forward):** `SendBlockAsync`'s writes still take no cancellation token (milestone 4 deferral). Watch does not send, so this milestone does not make it worse.
- **Known limitation to state in the docs later:** watch does not reconnect if the hub drops — it reports and exits 1. Auto-reconnect is a reasonable milestone-7+ addition but is not in the spec.
