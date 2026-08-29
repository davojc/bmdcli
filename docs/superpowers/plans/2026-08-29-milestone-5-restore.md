# bmd Milestone 5: Restore Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `bmd videohub restore <file>` converges a Videohub to a snapshot — validating the file against the device first, applying only the differences, printing each change as it is made, and safe to re-run after any failure.

**Architecture:** Snapshot validation hardens `VideohubSnapshot` (internal consistency at parse time, device compatibility at restore time); a pure `RestorePlan` turns snapshot-vs-state into an ordered change list; the `restore` command applies that list one block at a time through milestone 4's mutation methods, under milestone 4's auto-backup seam.

**Tech Stack:** .NET 10, ConsoleAppFramework v5, System.Text.Json source generators, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` ("Snapshot export / restore", "Automatic backup before mutations", "Agents and scripting")

## Global Constraints

- `PublishAot=true` stays green: no reflection, no `dynamic`, JSON ONLY via the source-generated contexts. No new package references.
- `Devices/` and `Config/` never reference ConsoleAppFramework or `Commands/`.
- **All user-facing numbering is 1-based**; the wire stays 0-based behind `VideohubClient`. Every message, every JSON field, every printed change.
- `--json`: one JSON document on stdout, camelCase, stable names; representation only. Errors are one `error: ...` line on stderr + exit code (0 success / 1 operation failure / 2 usage or format error); stdout carries nothing on failure.
- Restore is a mutation: it backs up through `WithBackedUpClientAsync` before changing anything, aborts if the backup fails, honors `--no-backup`/`backup.auto`, and reports the backup path.
- Help is the API: full XML doc comments, plain prose only (ConsoleAppFramework renders `<paramref>`/`<see>` tags literally), stating 1-based numbering and what each flag does.
- TDD: failing test → implementation → passing test → commit, every task.

**Carried-forward binding notes** (from milestone 3 and 4 reviews — requirements, not suggestions):
- **Snapshot index validation.** `VideohubSnapshot.FromJson` validates array *counts* but not individual `n`/`input` values. A snapshot with `n: 0`, `n: 99`, duplicate `n`s, or `input: 999` currently flows into `SetRouteAsync`, which throws `ArgumentOutOfRangeException` — **not in the shared catch filter**, so it would print a stack trace. Restore must reject such a file before touching the device (Task 1).
- **Timeouts are fatal to the connection.** After a `TimeoutException` from `SendBlockAsync`, the block may be half-sent and the reader mid-stream — the framing is undefined. The apply loop must stop immediately on timeout, never continue sending on that connection (Task 3).
- **One line per block.** Do not batch multiple changes into a single protocol block; per-change blocks keep NAK semantics simple and give the spec's per-change progress output.
- **Compute the diff from connect-time state**, and never re-derive remaining work from `client.State` mid-loop (its freshness depends on device echo/ACK ordering, which milestone 4 proved varies).

---

### Task 1: Snapshot validation — internal and against the device

**Files:**
- Modify: `src/Bmd/Devices/Videohub/VideohubSnapshot.cs`
- Create: `tests/Bmd.Tests/Devices/Videohub/VideohubSnapshotValidationTests.cs`

**Interfaces:**
- Consumes: `VideohubDeviceInfo`, `SnapshotFormatException` (existing).
- Produces on `VideohubSnapshot`:
  - internal validation inside `FromJson` (after the existing count checks): every `Inputs[i].N` in `1..VideoInputs` and distinct; every `Outputs[i].N` in `1..VideoOutputs` and distinct; every `Outputs[i].Input` in `1..VideoInputs`. Violations throw `SnapshotFormatException` naming the offending entry and the valid range.
  - `IReadOnlyList<string> IncompatibilityWith(VideohubDeviceInfo device)` — empty when the snapshot may be applied to that device; otherwise one line per problem (model name differs; input count differs; output count differs). Pure, no IO.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/VideohubSnapshotValidationTests.cs`:

```csharp
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubSnapshotValidationTests
{
    static readonly DateTimeOffset Stamp = new(2026, 8, 29, 10, 12, 0, TimeSpan.Zero);

    static VideohubSnapshot Valid() =>
        VideohubSnapshot.FromState(DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4)), Stamp);

    /// <summary>Serializes a snapshot built from raw parts, bypassing FromState's guarantees.</summary>
    static string Json(SnapshotInput[] inputs, SnapshotOutput[] outputs, int videoInputs = 4, int videoOutputs = 4) =>
        new VideohubSnapshot("Blackmagic Smart Videohub", videoInputs, videoOutputs, Stamp, inputs, outputs).ToJson();

    static SnapshotInput[] Inputs(params int[] ns) => ns.Select(n => new SnapshotInput(n, $"In {n}")).ToArray();
    static SnapshotOutput[] Outputs(params (int N, int Input)[] entries) =>
        entries.Select(e => new SnapshotOutput(e.N, $"Out {e.N}", e.Input)).ToArray();

    [Fact]
    public void FromJson_AcceptsAValidSnapshot()
    {
        var parsed = VideohubSnapshot.FromJson(Valid().ToJson());
        Assert.Equal(4, parsed.Outputs.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void FromJson_InputNumberOutOfRange_Throws(int n)
    {
        var json = Json(Inputs(n, 2, 3, 4), Outputs((1, 1), (2, 2), (3, 3), (4, 4)));
        var ex = Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
        Assert.Contains("input", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(n.ToString(), ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void FromJson_OutputNumberOutOfRange_Throws(int n)
    {
        var json = Json(Inputs(1, 2, 3, 4), Outputs((n, 1), (2, 2), (3, 3), (4, 4)));
        var ex = Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
        Assert.Contains("output", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromJson_DuplicateInputNumbers_Throws()
    {
        var json = Json(Inputs(1, 1, 3, 4), Outputs((1, 1), (2, 2), (3, 3), (4, 4)));
        var ex = Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromJson_DuplicateOutputNumbers_Throws()
    {
        var json = Json(Inputs(1, 2, 3, 4), Outputs((2, 1), (2, 2), (3, 3), (4, 4)));
        var ex = Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void FromJson_RouteTargetOutOfRange_Throws(int input)
    {
        var json = Json(Inputs(1, 2, 3, 4), Outputs((1, input), (2, 2), (3, 3), (4, 4)));
        var ex = Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
        Assert.Contains("output 1", ex.Message);
        Assert.Contains(input.ToString(), ex.Message);
    }

    [Fact]
    public void IncompatibilityWith_MatchingDevice_IsEmpty()
    {
        var device = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4)).Device;
        Assert.Empty(Valid().IncompatibilityWith(device));
    }

    [Fact]
    public void IncompatibilityWith_DifferentModel_ReportsIt()
    {
        var device = new VideohubDeviceInfo("Other Hub", null, 4, 4, "2.8");
        var problems = Valid().IncompatibilityWith(device);
        Assert.Contains(problems, p => p.Contains("Other Hub") && p.Contains("Blackmagic Smart Videohub"));
    }

    [Fact]
    public void IncompatibilityWith_DifferentCounts_ReportsBoth()
    {
        var device = new VideohubDeviceInfo("Blackmagic Smart Videohub", null, 20, 20, "2.8");
        var problems = Valid().IncompatibilityWith(device);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("input"));
        Assert.Contains(problems, p => p.Contains("output"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubSnapshotValidationTests`
Expected: compilation failure (`IncompatibilityWith` missing) and, once it compiles, the validation theories fail because `FromJson` accepts the bad files today.

- [ ] **Step 3: Implement**

Extend `FromJson`'s validation block (after the existing count checks) and add the compatibility method:

```csharp
        ValidateEntries(snapshot);
        return snapshot;
    }

    static void ValidateEntries(VideohubSnapshot snapshot)
    {
        var seenInputs = new HashSet<int>();
        foreach (var input in snapshot.Inputs)
        {
            if (input.N < 1 || input.N > snapshot.VideoInputs)
                throw new SnapshotFormatException(
                    $"snapshot has input {input.N}, outside the valid range 1-{snapshot.VideoInputs}");
            if (!seenInputs.Add(input.N))
                throw new SnapshotFormatException($"snapshot has duplicate entries for input {input.N}");
        }

        var seenOutputs = new HashSet<int>();
        foreach (var output in snapshot.Outputs)
        {
            if (output.N < 1 || output.N > snapshot.VideoOutputs)
                throw new SnapshotFormatException(
                    $"snapshot has output {output.N}, outside the valid range 1-{snapshot.VideoOutputs}");
            if (!seenOutputs.Add(output.N))
                throw new SnapshotFormatException($"snapshot has duplicate entries for output {output.N}");
            if (output.Input < 1 || output.Input > snapshot.VideoInputs)
                throw new SnapshotFormatException(
                    $"snapshot routes output {output.N} to input {output.Input}, outside the valid range 1-{snapshot.VideoInputs}");
        }
    }

    /// <summary>Reasons this snapshot cannot be applied to the given device; empty when it can.</summary>
    public IReadOnlyList<string> IncompatibilityWith(VideohubDeviceInfo device)
    {
        var problems = new List<string>();
        if (!string.Equals(device.ModelName, Device, StringComparison.Ordinal))
            problems.Add($"snapshot is from '{Device}' but the device is '{device.ModelName}'");
        if (device.VideoInputs != VideoInputs)
            problems.Add($"snapshot has {VideoInputs} inputs but the device has {device.VideoInputs}");
        if (device.VideoOutputs != VideoOutputs)
            problems.Add($"snapshot has {VideoOutputs} outputs but the device has {device.VideoOutputs}");
        return problems;
    }
```

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`
Expected: all 179 existing plus the new ones pass. If an existing test wrote a snapshot the new rules reject, that is a real finding — report it rather than loosening the rules.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: validate snapshot indices and device compatibility"
```

---

### Task 2: RestorePlan — the pure diff-to-changes layer

**Files:**
- Create: `src/Bmd/Devices/Videohub/RestorePlan.cs`, `tests/Bmd.Tests/Devices/Videohub/RestorePlanTests.cs`

**Interfaces:**
- Consumes: `VideohubSnapshot`, `VideohubState`.
- Produces (namespace `Bmd.Devices.Videohub`):
  - `enum RestoreChangeKind { InputLabel, OutputLabel, Route }`
  - `sealed record RestoreChange(RestoreChangeKind Kind, int N, string From, string To)` with `string Describe()`:
    - `InputLabel` → `input 2 label: 'Cam 2' → 'Camera Two'`
    - `OutputLabel` → `output 4 label: 'Aux' → 'Aux Feed'`
    - `Route` → `route 3: Cam 2 → Cam 1`
    (For `Route`, `From`/`To` hold the input *labels*; the numbers live in a separate field — see below.)
  - Because `Route` needs both the target input number (to apply) and its label (to print), the record is:
    `sealed record RestoreChange(RestoreChangeKind Kind, int N, string From, string To, int TargetInput = 0)` — `TargetInput` is the 1-based input for `Route` changes and 0 otherwise.
  - `static class RestorePlan { static IReadOnlyList<RestoreChange> Compute(VideohubSnapshot snapshot, VideohubState state) }` — ordered: input labels (ascending), then output labels (ascending), then routes (ascending). Only genuine differences appear. Assumes the snapshot is already validated and compatible (Task 1's job).

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/RestorePlanTests.cs`:

```csharp
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class RestorePlanTests
{
    static readonly DateTimeOffset Stamp = new(2026, 8, 29, 10, 12, 0, TimeSpan.Zero);

    static VideohubState State(string dump = Fixtures.Dump4x4) =>
        DumpParser.Parse(BlockReader.ReadBlocks(dump));

    static VideohubSnapshot Snapshot(string dump = Fixtures.Dump4x4) =>
        VideohubSnapshot.FromState(State(dump), Stamp);

    [Fact]
    public void Compute_IdenticalStateAndSnapshot_IsEmpty()
    {
        Assert.Empty(RestorePlan.Compute(Snapshot(), State()));
    }

    [Fact]
    public void Compute_ChangedRoute_ProducesOneRouteChange()
    {
        // device currently has output 1 ← input 1 (wire "0 0"); snapshot says input 4 (wire "0 3")
        var device = State(Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0"));
        var change = Assert.Single(RestorePlan.Compute(Snapshot(), device));
        Assert.Equal(RestoreChangeKind.Route, change.Kind);
        Assert.Equal(1, change.N);
        Assert.Equal(4, change.TargetInput);
        Assert.Equal("Cam 1", change.From);
        Assert.Equal("Cam 4", change.To);
        Assert.Equal("route 1: Cam 1 → Cam 4", change.Describe());
    }

    [Fact]
    public void Compute_ChangedInputLabel_ProducesOneLabelChange()
    {
        var device = State(Fixtures.Dump4x4.Replace("0 Cam 1", "0 Camera One"));
        var change = Assert.Single(RestorePlan.Compute(Snapshot(), device));
        Assert.Equal(RestoreChangeKind.InputLabel, change.Kind);
        Assert.Equal(1, change.N);
        Assert.Equal("Camera One", change.From);
        Assert.Equal("Cam 1", change.To);
        Assert.Equal("input 1 label: 'Camera One' → 'Cam 1'", change.Describe());
    }

    [Fact]
    public void Compute_ChangedOutputLabel_ProducesOneLabelChange()
    {
        var device = State(Fixtures.Dump4x4.Replace("3 Aux", "3 Auxiliary"));
        var change = Assert.Single(RestorePlan.Compute(Snapshot(), device));
        Assert.Equal(RestoreChangeKind.OutputLabel, change.Kind);
        Assert.Equal(4, change.N);
        Assert.Equal("output 4 label: 'Auxiliary' → 'Aux'", change.Describe());
    }

    [Fact]
    public void Compute_OrdersLabelsBeforeRoutes_AndAscendingWithinKind()
    {
        var device = State(Fixtures.Dump4x4
            .Replace("1 Cam 2", "1 Camera Two")
            .Replace("1 Preview", "1 Prev")
            .Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0"));
        var changes = RestorePlan.Compute(Snapshot(), device);
        Assert.Equal(3, changes.Count);
        Assert.Equal(RestoreChangeKind.InputLabel, changes[0].Kind);
        Assert.Equal(RestoreChangeKind.OutputLabel, changes[1].Kind);
        Assert.Equal(RestoreChangeKind.Route, changes[2].Kind);
    }

    [Fact]
    public void Compute_IsIdempotent_AfterApplyingEverything()
    {
        // simulate a converged device by computing against the snapshot's own source state
        var snapshot = Snapshot();
        Assert.Empty(RestorePlan.Compute(snapshot, State()));
        Assert.Empty(RestorePlan.Compute(snapshot, State()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter RestorePlanTests`
Expected: compilation failure — `RestorePlan` does not exist.

- [ ] **Step 3: Implement `src/Bmd/Devices/Videohub/RestorePlan.cs`**

```csharp
namespace Bmd.Devices.Videohub;

public enum RestoreChangeKind { InputLabel, OutputLabel, Route }

/// <summary>One difference between a snapshot and a device, expressed in 1-based terms.
/// For routes, From/To are input labels and TargetInput is the 1-based input to route.</summary>
public sealed record RestoreChange(RestoreChangeKind Kind, int N, string From, string To, int TargetInput = 0)
{
    public string Describe() => Kind switch
    {
        RestoreChangeKind.InputLabel => $"input {N} label: '{From}' → '{To}'",
        RestoreChangeKind.OutputLabel => $"output {N} label: '{From}' → '{To}'",
        _ => $"route {N}: {From} → {To}",
    };
}

/// <summary>Computes the ordered changes that converge a device to a snapshot.
/// The snapshot must already be validated and compatible with the device.</summary>
public static class RestorePlan
{
    public static IReadOnlyList<RestoreChange> Compute(VideohubSnapshot snapshot, VideohubState state)
    {
        var changes = new List<RestoreChange>();

        foreach (var input in snapshot.Inputs.OrderBy(i => i.N))
        {
            var current = state.GetInputLabel(input.N);
            if (current != input.Label)
                changes.Add(new RestoreChange(RestoreChangeKind.InputLabel, input.N, current, input.Label));
        }

        foreach (var output in snapshot.Outputs.OrderBy(o => o.N))
        {
            var current = state.GetOutputLabel(output.N);
            if (current != output.Label)
                changes.Add(new RestoreChange(RestoreChangeKind.OutputLabel, output.N, current, output.Label));
        }

        foreach (var output in snapshot.Outputs.OrderBy(o => o.N))
        {
            var currentInput = state.GetRoute(output.N);
            if (currentInput == output.Input) continue;
            changes.Add(new RestoreChange(
                RestoreChangeKind.Route, output.N,
                state.GetInputLabel(currentInput), state.GetInputLabel(output.Input), output.Input));
        }

        return changes;
    }
}
```

Note the route change's `To` label comes from the **device's** input labels (what that input is called on the hub right now), not the snapshot's — after the label changes above are applied they agree, and this keeps the printed line meaningful even when only routes are being restored.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: restore plan computing ordered changes from a snapshot"
```

---

### Task 3: `bmd videohub restore`

**Files:**
- Modify: `src/Bmd/Commands/Videohub/VideohubCommands.cs`, `src/Bmd/Commands/Videohub/VideohubResults.cs`, `src/Bmd/Output/BmdJsonContext.cs`, `src/Bmd/Program.cs`, `src/Bmd/Commands/GroupHelp.cs`
- Create: `tests/Bmd.Tests/Commands/VideohubRestoreTests.cs`

**Interfaces:**
- Produces:
  - `sealed record RestoreChangeResult(string Kind, int N, string From, string To)` — `Kind` is `"inputLabel"`/`"outputLabel"`/`"route"`
  - `sealed record VideohubRestoreResult(string File, string Device, int Changes, int Applied, bool DryRun, string? Backup, RestoreChangeResult[] Details)`
  - `Task<int> Restore(string file, string? host = null, int? port = null, int? timeout = null, bool dryRun = false, bool noBackup = false, bool json = false)` registered as `videohub restore`, and added to `GroupHelp.Commands`.

**Behavior:**
1. Read the file (`-` reads stdin). Missing/unreadable → exit 1. Malformed or index-invalid JSON → `SnapshotFormatException` → exit 1 (already in the catch filter).
2. Connect + back up (via `WithBackedUpClientAsync`; `--dry-run` skips the backup — nothing will change).
3. `snapshot.IncompatibilityWith(client.State.Device)` — non-empty → `error: snapshot does not match this device` plus one indented line per problem, **exit 2** (a usage/format error: the wrong file was supplied), device untouched.
4. `RestorePlan.Compute(snapshot, client.State)` from **connect-time state**.
5. No changes → `Already matches the snapshot; nothing to do.` (or a JSON result with `changes: 0`), exit 0.
6. `--dry-run` → print each change prefixed `would ` and exit 0 without mutating; JSON reports `dryRun: true`, `applied: 0`.
7. Otherwise apply each change in order, printing it as it is made (`route 3: Cam 2 → Cam 1`). **On `TimeoutException`, stop immediately** — the connection framing is undefined — and report how many changes were applied before failing (exit 1). A `NAK` likewise stops the loop (exit 1) with the device's rejection message.
8. Success → summary line `Restored 7 changes from house-normal.json` plus `Backup: <path>` (or `skipped`). JSON emits the full result object.
9. Re-running after a partial failure recomputes the diff on a fresh connection, so it resumes rather than repeats.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Commands/VideohubRestoreTests.cs` — use the harness from `VideohubRouteSetTests` verbatim (temp dir, console redirect, `SetConfig("backup.dir", BackupDir)`, `[Collection("console")]`), plus a helper that writes a snapshot file from a modified fixture:

```csharp
    /// <summary>Writes a snapshot file describing the fixture's ORIGINAL state.</summary>
    string SnapshotFile()
    {
        var snapshot = VideohubSnapshot.FromState(
            DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4)),
            new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(WorkDir, "snapshot.json");
        File.WriteAllText(path, snapshot.ToJson());
        return path;
    }
```

Tests to write in full:

```csharp
    [Fact] public async Task Restore_DeviceAlreadyMatches_ReportsNothingToDo_Exit0()
        // fake serving Dump4x4 + snapshot of Dump4x4 → stdout contains "nothing to do", exit 0, fake.Routes() unchanged

    [Fact] public async Task Restore_AppliesRouteDifference_AndPrintsIt()
        // fake started from a dump whose output 1 is routed to input 1; snapshot says input 4
        // → exit 0, fake.Routes()[0] == 3, stdout contains "route 1:" and "Backup:"

    [Fact] public async Task Restore_AppliesLabelDifferences()
        // fake started from a dump with "0 Camera One" / "3 Auxiliary" → both restored,
        // fake.InputLabels()[0] == "Cam 1", fake.OutputLabels()[3] == "Aux"

    [Fact] public async Task Restore_DryRun_ChangesNothing_AndSaysWould()
        // divergent fake → exit 0, stdout lines start with "would", fake state UNCHANGED,
        // and no backup directory was created

    [Fact] public async Task Restore_Json_ReportsCountsAndDetails()
        // divergent fake, --json → changes == applied == N, dryRun false, backup non-null,
        // details[0].kind == "route" (or "inputLabel"), 1-based n

    [Fact] public async Task Restore_IncompatibleDevice_Exit2_DeviceUntouched()
        // snapshot claiming a 20x20 hub (hand-built via the record + ToJson) against the 4x4 fake
        // → exit 2, stderr names the mismatch, fake state unchanged, stdout empty

    [Fact] public async Task Restore_InvalidSnapshotIndices_Exit1_BeforeTouchingDevice()
        // file with output routed to input 99 → exit 1, error mentions "99", fake state unchanged

    [Fact] public async Task Restore_MissingFile_Exit1_CleanError()
        // → exit 1, "error:" on stderr, no "   at "

    [Fact] public async Task Restore_Rejected_StopsAndReportsProgress()
        // StartRejecting + divergent snapshot → exit 1, stderr has the rejection, and stdout/stderr
        // make clear nothing (0 changes) was applied

    [Fact] public async Task Restore_IsIdempotent_SecondRunIsNoOp()
        // divergent fake: first restore applies, second restore reports nothing to do, exit 0 both times
```

Write each out fully in the established style, asserting server-side effects via `fake.Routes()/InputLabels()/OutputLabels()`, exit codes, stdout/stderr, and JSON fields.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubRestoreTests`
Expected: compilation failure — `Restore` does not exist.

- [ ] **Step 3: Implement**

Add the two result records, register them in `BmdJsonContext`, add the command (full XML docs, plain prose), register it in `Program.cs` and in `GroupHelp.Commands` with its summary text.

Shape:

```csharp
    /// <summary>Apply a snapshot to the device, changing only what differs. Numbering is 1-based.</summary>
    /// <param name="file">Snapshot file to apply; use - to read from stdin.</param>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="dryRun">Show what would change without touching the device.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Restore(
        [Argument] string file,
        string? host = null, int? port = null, int? timeout = null,
        bool dryRun = false, bool noBackup = false, bool json = false)
    {
        VideohubSnapshot snapshot;
        try
        {
            var text = file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file);
            snapshot = VideohubSnapshot.FromJson(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SnapshotFormatException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return Task.FromResult(1);
        }

        return WithBackedUpClientAsync(host, port, timeout, noBackup || dryRun, async (client, backup) =>
        {
            var problems = snapshot.IncompatibilityWith(client.State.Device);
            if (problems.Count > 0)
            {
                Console.Error.WriteLine("error: snapshot does not match this device");
                foreach (var problem in problems) Console.Error.WriteLine($"  {problem}");
                return 2;
            }

            var changes = RestorePlan.Compute(snapshot, client.State);
            var applied = 0;
            try
            {
                foreach (var change in changes)
                {
                    if (dryRun)
                    {
                        if (!json) Console.WriteLine($"would {change.Describe()}");
                        continue;
                    }
                    switch (change.Kind)
                    {
                        case RestoreChangeKind.InputLabel:
                            await client.RenameInputAsync(change.N, change.To); break;
                        case RestoreChangeKind.OutputLabel:
                            await client.RenameOutputAsync(change.N, change.To); break;
                        default:
                            await client.SetRouteAsync(change.N, change.TargetInput); break;
                    }
                    applied++;
                    if (!json) Console.WriteLine(change.Describe());
                }
            }
            catch (TimeoutException)
            {
                // framing is undefined after a timeout — stop rather than send anything else
                Console.Error.WriteLine(
                    $"error: timed out after applying {applied} of {changes.Count} changes; re-run to resume");
                return 1;
            }

            if (json)
                Console.WriteLine(JsonSerializer.Serialize(
                    new VideohubRestoreResult(
                        file, snapshot.Device, changes.Count, applied, dryRun, backup,
                        changes.Select(c => new RestoreChangeResult(KindWord(c.Kind), c.N, c.From, c.To)).ToArray()),
                    BmdJsonContext.Default.VideohubRestoreResult));
            else if (changes.Count == 0)
                Console.WriteLine("Already matches the snapshot; nothing to do.");
            else if (dryRun)
                Console.WriteLine($"{changes.Count} change(s) would be applied from {file}.");
            else
            {
                Console.WriteLine($"Restored {applied} change(s) from {file}");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });
    }

    static string KindWord(RestoreChangeKind kind) => kind switch
    {
        RestoreChangeKind.InputLabel => "inputLabel",
        RestoreChangeKind.OutputLabel => "outputLabel",
        _ => "route",
    };
```

Implementer notes:
- A `NAK` (`VideohubCommandRejectedException`) is deliberately **not** caught here — it propagates to the shared filter, giving `error: device rejected …` and exit 1. The already-applied changes have been printed, so progress is visible. If a test shows the applied-count needs to appear on the NAK path too, add a `catch (VideohubCommandRejectedException)` mirroring the timeout branch and say so in the report.
- `--dry-run` passes `noBackup: true` into the seam (nothing is changing, so nothing needs backing up) — this is why the dry-run test asserts no backup directory appears.

- [ ] **Step 4: Run all tests, then check help**

Run: `dotnet test`, then `dotnet run --project src/Bmd -- videohub restore --help` and `dotnet run --project src/Bmd -- videohub --help` (restore must appear in the group listing).

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: bmd videohub restore applying only the differences"
```

---

### Task 4: Milestone proof — AOT publish, smokes, help audit

**Files:**
- Modify: none expected

- [ ] **Step 1: Full suite + publish**

```powershell
dotnet test
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/Bmd -c Release -r win-x64
```

Expected: all tests pass; zero IL2xxx/IL3xxx warnings.

- [ ] **Step 2: Native smokes**

```powershell
$exe = "src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe"
& $exe videohub --help                  # restore listed in the group
& $exe videohub restore --help          # file argument, --dry-run, --no-backup, --json documented
& $exe videohub restore missing.json --host 127.0.0.1 --port 1 --timeout 2; $LASTEXITCODE   # expect 1, clean error
'not json' | Out-File -Encoding utf8 bad.json
& $exe videohub restore bad.json --host 127.0.0.1 --port 1 --timeout 2; $LASTEXITCODE       # expect 1, clean error, no connection needed
Remove-Item bad.json
```

Audit each help screen; fix any doc gap (plain prose only) and note it.

- [ ] **Step 3: Verify no stray state, record size, commit**

```powershell
Test-Path .bmdconfig
Test-Path "$env:LOCALAPPDATA\bmd"       # smokes must not have written backups
(Get-Item src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe).Length / 1MB
git status --short
git add -A; git commit -m "chore: prove milestone 5 (restore) on Native AOT" --allow-empty
```

---

## Self-Review Notes

- **Spec coverage (milestone 5):** device validation with refusal (Task 1 + Task 3 step 3); diff-apply printing each change (Tasks 2-3); no-op reporting and exit 0; idempotent re-run; auto-backup before restoring; `-` for stdin. Both carried-forward binding notes are discharged: index validation (Task 1) and timeout-is-fatal (Task 3).
- **Beyond spec, deliberately added:** `--dry-run`. The spec doesn't mention it, but restore is the one command that can change dozens of routes on a live router in one go, and the diff is already computed — previewing it costs ~5 lines and fits the milestone's safety posture. Flag to the user; easy to remove if unwanted.
- **Deliberate choice:** incompatible snapshot exits **2**, not 1 — supplying a snapshot from the wrong device is a usage error, and agents can distinguish "wrong file" from "device or network problem" by exit code alone.
- **Type consistency:** `RestoreChange.TargetInput` (1-based) feeds `SetRouteAsync(output, input)`; `RestoreChangeResult.Kind` words match the JSON contract; `VideohubRestoreResult` registered in `BmdJsonContext`; `GroupHelp.Commands` gains restore (its drift test only checks self-consistency, so this is easy to forget — Task 3's help check catches it).
- **Not addressed here (carried forward):** `SendBlockAsync`'s writes are still unbounded by the timeout (milestone 4 deferral) — restore multiplies writes on one connection, so if a real hub ever backpressures, this is the place it would show. Worth revisiting in milestone 6.
