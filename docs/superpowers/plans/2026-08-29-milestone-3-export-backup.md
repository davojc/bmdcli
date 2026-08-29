# bmd Milestone 3: Export + Backup Store Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `bmd videohub export [file]` produces a verified JSON snapshot of a Videohub, and a backup store (paths, rotation, config) stands ready for milestone 4's mutating commands to use.

**Architecture:** A pure snapshot model + source-generated JSON in `Devices/Videohub/` (built from `VideohubState`, no sockets), a `BackupStore` in `Config/` owning locations and rotation, and a thin `export` command reusing milestone 2's `WithClientAsync`. Verification re-reads the written file and compares against a *fresh* device dump, retrying to survive concurrent changes.

**Tech Stack:** .NET 10, ConsoleAppFramework v5, System.Text.Json source generators, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` (see "Snapshot export / restore" and "Automatic backup before mutations")

## Global Constraints

- `PublishAot=true` stays green: no reflection, no `dynamic`, JSON ONLY via source-generated contexts. No new package references.
- `Devices/` and `Config/` never reference ConsoleAppFramework or `Commands/`.
- **User-facing numbering is 1-based** everywhere, including snapshot JSON (`n`, `input`). The wire stays 0-based behind `VideohubState`.
- `--json`: one JSON document on stdout, camelCase, stable names; representation only. Errors are always one `error: ...` line on stderr + exit code (0 success / 1 operation failure / 2 usage or format error); stdout carries nothing on failure.
- Help is the API: every command/argument/flag gets a complete XML doc comment stating defaults, units, and 1-based numbering.
- Snapshot captures input labels, output labels, and routing. **Locks are excluded** (spec non-goal).
- Backup locations: `%LOCALAPPDATA%\bmd\backups\<device>\<timestamp>.json` on Windows; `$XDG_STATE_HOME/bmd/backups/<device>/<timestamp>.json` else `~/.local/state/bmd/backups/...` on Unix. Distinct from config dir and cache dir. Config keys: `backup.auto` (default true), `backup.keep` (default 10), `backup.dir` (override).
- Console-redirecting test classes MUST carry `[Collection("console")]`.
- TDD: failing test → implementation → passing test → commit, every task.

---

### Task 1: Snapshot model + JSON round-trip

**Files:**
- Create: `src/Bmd/Devices/Videohub/VideohubSnapshot.cs`, `src/Bmd/Devices/Videohub/SnapshotJsonContext.cs`, `tests/Bmd.Tests/Devices/Videohub/VideohubSnapshotTests.cs`

**Interfaces:**
- Consumes: `VideohubState`, `VideohubDeviceInfo` (milestone 2), `Fixtures.Dump4x4`, `BlockReader`, `DumpParser` (tests).
- Produces (namespace `Bmd.Devices.Videohub`):
  - `sealed record SnapshotInput(int N, string Label)`
  - `sealed record SnapshotOutput(int N, string Label, int Input)`
  - `sealed record VideohubSnapshot(string Device, int VideoInputs, int VideoOutputs, DateTimeOffset ExportedAt, SnapshotInput[] Inputs, SnapshotOutput[] Outputs)` with:
    - `static VideohubSnapshot FromState(VideohubState state, DateTimeOffset exportedAt)`
    - `string ToJson()` — indented, camelCase, trailing newline
    - `static VideohubSnapshot FromJson(string json)` — throws `SnapshotFormatException` (defined in this file: `sealed class SnapshotFormatException(string message) : Exception(message)`) on malformed JSON or a missing/invalid required field
    - `IReadOnlyList<string> DifferencesFrom(VideohubState state)` — empty when the snapshot matches the device exactly; otherwise one human-readable line per mismatch (used by export verification now, restore later)
  - `SnapshotJsonContext` — source-gen context for `VideohubSnapshot`

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/VideohubSnapshotTests.cs`:

```csharp
using System.Text.Json;
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubSnapshotTests
{
    static readonly DateTimeOffset Stamp = new(2026, 8, 29, 10, 12, 0, TimeSpan.Zero);

    static VideohubState State(string dump = Fixtures.Dump4x4) =>
        DumpParser.Parse(BlockReader.ReadBlocks(dump));

    static VideohubSnapshot Snapshot() => VideohubSnapshot.FromState(State(), Stamp);

    [Fact]
    public void FromState_CapturesOneBasedLabelsAndRoutes()
    {
        var snapshot = Snapshot();
        Assert.Equal("Blackmagic Smart Videohub", snapshot.Device);
        Assert.Equal(4, snapshot.VideoInputs);
        Assert.Equal(4, snapshot.VideoOutputs);
        Assert.Equal(Stamp, snapshot.ExportedAt);

        Assert.Equal(4, snapshot.Inputs.Length);
        Assert.Equal(1, snapshot.Inputs[0].N);
        Assert.Equal("Cam 1", snapshot.Inputs[0].Label);

        Assert.Equal(4, snapshot.Outputs.Length);
        Assert.Equal(1, snapshot.Outputs[0].N);
        Assert.Equal("Program", snapshot.Outputs[0].Label);
        Assert.Equal(4, snapshot.Outputs[0].Input); // wire "0 3" → 1-based input 4
        Assert.Equal(2, snapshot.Outputs[1].Input);
    }

    [Fact]
    public void ToJson_UsesCamelCase_AndEndsWithNewline()
    {
        var json = Snapshot().ToJson();
        Assert.EndsWith("\n", json);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("Blackmagic Smart Videohub", root.GetProperty("device").GetString());
        Assert.Equal(4, root.GetProperty("videoInputs").GetInt32());
        Assert.Equal(4, root.GetProperty("videoOutputs").GetInt32());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("exportedAt").GetString()));
        Assert.Equal(1, root.GetProperty("inputs")[0].GetProperty("n").GetInt32());
        Assert.Equal("Cam 1", root.GetProperty("inputs")[0].GetProperty("label").GetString());
        Assert.Equal(4, root.GetProperty("outputs")[0].GetProperty("input").GetInt32());
        Assert.False(root.EnumerateObject().Any(p => p.Name == "locks"), "locks are excluded by spec");
    }

    [Fact]
    public void FromJson_RoundTripsToJson()
    {
        var original = Snapshot();
        var parsed = VideohubSnapshot.FromJson(original.ToJson());
        Assert.Equal(original.Device, parsed.Device);
        Assert.Equal(original.VideoInputs, parsed.VideoInputs);
        Assert.Equal(original.VideoOutputs, parsed.VideoOutputs);
        Assert.Equal(original.ExportedAt, parsed.ExportedAt);
        Assert.Equal(original.Inputs.Select(i => (i.N, i.Label)), parsed.Inputs.Select(i => (i.N, i.Label)));
        Assert.Equal(original.Outputs.Select(o => (o.N, o.Label, o.Input)), parsed.Outputs.Select(o => (o.N, o.Label, o.Input)));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"device\":\"X\",\"videoInputs\":4}")]
    public void FromJson_Malformed_ThrowsSnapshotFormatException(string json)
    {
        Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
    }

    [Fact]
    public void DifferencesFrom_IdenticalState_IsEmpty()
    {
        Assert.Empty(Snapshot().DifferencesFrom(State()));
    }

    [Fact]
    public void DifferencesFrom_ChangedRoute_ReportsIt()
    {
        var changed = State(Fixtures.Dump4x4.Replace("0 3\n1 1", "0 0\n1 1"));
        var differences = Snapshot().DifferencesFrom(changed);
        var line = Assert.Single(differences);
        Assert.Contains("output 1", line);
    }

    [Fact]
    public void DifferencesFrom_ChangedLabel_ReportsIt()
    {
        var changed = State(Fixtures.Dump4x4.Replace("0 Cam 1", "0 Camera One"));
        var differences = Snapshot().DifferencesFrom(changed);
        var line = Assert.Single(differences);
        Assert.Contains("input 1", line);
    }

    [Fact]
    public void DifferencesFrom_DifferentDeviceSize_ReportsMismatch()
    {
        var smaller = Fixtures.Dump4x4
            .Replace("Video outputs: 4", "Video outputs: 3")
            .Replace("3 Aux\n", "")
            .Replace("3 U\n", "")
            .Replace("3 2\n", "");
        var differences = Snapshot().DifferencesFrom(State(smaller));
        Assert.Contains(differences, d => d.Contains("outputs"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubSnapshotTests`
Expected: compilation failure — the snapshot types do not exist.

- [ ] **Step 3: Implement**

`src/Bmd/Devices/Videohub/SnapshotJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Bmd.Devices.Videohub;

/// <summary>Source-generated JSON for snapshot files (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(VideohubSnapshot))]
public partial class SnapshotJsonContext : JsonSerializerContext;
```

`src/Bmd/Devices/Videohub/VideohubSnapshot.cs`:

```csharp
using System.Text.Json;

namespace Bmd.Devices.Videohub;

public sealed class SnapshotFormatException(string message) : Exception(message);

public sealed record SnapshotInput(int N, string Label);
public sealed record SnapshotOutput(int N, string Label, int Input);

/// <summary>A point-in-time capture of a Videohub's labels and routing.
/// All numbering is 1-based. Locks are deliberately excluded (spec non-goal).</summary>
public sealed record VideohubSnapshot(
    string Device,
    int VideoInputs,
    int VideoOutputs,
    DateTimeOffset ExportedAt,
    SnapshotInput[] Inputs,
    SnapshotOutput[] Outputs)
{
    public static VideohubSnapshot FromState(VideohubState state, DateTimeOffset exportedAt)
    {
        var device = state.Device;
        var inputs = Enumerable.Range(1, device.VideoInputs)
            .Select(n => new SnapshotInput(n, state.GetInputLabel(n))).ToArray();
        var outputs = Enumerable.Range(1, device.VideoOutputs)
            .Select(n => new SnapshotOutput(n, state.GetOutputLabel(n), state.GetRoute(n))).ToArray();
        return new VideohubSnapshot(device.ModelName, device.VideoInputs, device.VideoOutputs, exportedAt, inputs, outputs);
    }

    public string ToJson() =>
        JsonSerializer.Serialize(this, SnapshotJsonContext.Default.VideohubSnapshot) + "\n";

    public static VideohubSnapshot FromJson(string json)
    {
        VideohubSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.VideohubSnapshot);
        }
        catch (JsonException ex)
        {
            throw new SnapshotFormatException($"snapshot is not valid JSON: {ex.Message}");
        }
        if (snapshot is null) throw new SnapshotFormatException("snapshot is empty");
        if (string.IsNullOrEmpty(snapshot.Device)) throw new SnapshotFormatException("snapshot is missing 'device'");
        if (snapshot.Inputs is null || snapshot.Outputs is null)
            throw new SnapshotFormatException("snapshot is missing 'inputs' or 'outputs'");
        if (snapshot.VideoInputs != snapshot.Inputs.Length)
            throw new SnapshotFormatException($"snapshot claims {snapshot.VideoInputs} inputs but lists {snapshot.Inputs.Length}");
        if (snapshot.VideoOutputs != snapshot.Outputs.Length)
            throw new SnapshotFormatException($"snapshot claims {snapshot.VideoOutputs} outputs but lists {snapshot.Outputs.Length}");
        return snapshot;
    }

    /// <summary>Lines describing how the device differs from this snapshot; empty when identical.</summary>
    public IReadOnlyList<string> DifferencesFrom(VideohubState state)
    {
        var differences = new List<string>();
        var device = state.Device;
        if (device.VideoInputs != VideoInputs)
            differences.Add($"device has {device.VideoInputs} inputs, snapshot has {VideoInputs}");
        if (device.VideoOutputs != VideoOutputs)
            differences.Add($"device has {device.VideoOutputs} outputs, snapshot has {VideoOutputs}");
        if (differences.Count > 0) return differences; // sizes differ: per-entry comparison is meaningless

        foreach (var input in Inputs)
        {
            var actual = state.GetInputLabel(input.N);
            if (actual != input.Label)
                differences.Add($"input {input.N} label: device '{actual}', snapshot '{input.Label}'");
        }
        foreach (var output in Outputs)
        {
            var actualLabel = state.GetOutputLabel(output.N);
            if (actualLabel != output.Label)
                differences.Add($"output {output.N} label: device '{actualLabel}', snapshot '{output.Label}'");
            var actualRoute = state.GetRoute(output.N);
            if (actualRoute != output.Input)
                differences.Add($"output {output.N} route: device input {actualRoute}, snapshot input {output.Input}");
        }
        return differences;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter VideohubSnapshotTests`
Expected: all PASS (7 facts + 3 theory rows). Then `dotnet test` for the full suite.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: Videohub snapshot model with JSON round-trip and diffing"
```

---

### Task 2: BackupStore — paths, writing, rotation

**Files:**
- Create: `src/Bmd/Config/BackupStore.cs`, `tests/Bmd.Tests/Config/BackupStoreTests.cs`
- Modify: `src/Bmd/Config/ConfigPaths.cs` (add `StateDirectory`)

**Interfaces:**
- Consumes: `ConfigPaths`, `ConfigStore`, `ConfigKey`, `VideohubSnapshot`.
- Produces:
  - `ConfigPaths.StateDirectory : string` — `%LOCALAPPDATA%\bmd` on Windows; `$XDG_STATE_HOME/bmd` else `~/.local/state/bmd`
  - `Bmd.Config.BackupStore` with:
    - `static BackupStore FromConfig(ConfigStore config)` — reads `backup.auto`/`backup.keep`/`backup.dir`; a non-numeric `backup.keep` throws `ConfigValueException` (defined here: `sealed class ConfigValueException(string message) : Exception(message)`)
    - `bool AutoBackupEnabled { get; }`, `int Keep { get; }`, `string RootDirectory { get; }`
    - `string Write(string deviceKey, VideohubSnapshot snapshot)` — writes `<root>/<deviceKey>/<yyyyMMdd-HHmmss[-N]>.json`, verifies the file round-trips (re-read + `FromJson` + field compare; mismatch → `IOException`), prunes to `Keep`, returns the path written
    - `static string DeviceKey(string host, string modelName)` — filesystem-safe `host_model` (non-alphanumerics → `-`, collapsed, trimmed, lowercased)
    - `IReadOnlyList<string> List(string deviceKey)` — existing backup paths, newest first

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Config/BackupStoreTests.cs`:

```csharp
using Bmd.Config;
using Bmd.Devices.Videohub;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Config;

public class BackupStoreTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");
    string BackupDir => Path.Combine(_root, "backups");

    public BackupStoreTests() => Directory.CreateDirectory(WorkDir);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    ConfigStore Config() => ConfigStore.Load(GlobalPath, WorkDir);

    void SetConfig(string key, string value)
    {
        Assert.True(ConfigKey.TryParse(key, out var parsed));
        Config().Set(parsed, value, global: false);
    }

    BackupStore Store()
    {
        SetConfig("backup.dir", BackupDir);
        return BackupStore.FromConfig(Config());
    }

    static VideohubSnapshot Snapshot(DateTimeOffset stamp) =>
        VideohubSnapshot.FromState(
            DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4)), stamp);

    static VideohubSnapshot Snapshot() => Snapshot(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Defaults_AutoOnKeepTen()
    {
        var store = BackupStore.FromConfig(Config());
        Assert.True(store.AutoBackupEnabled);
        Assert.Equal(10, store.Keep);
        Assert.EndsWith("backups", store.RootDirectory.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Config_TurnsAutoOff_AndSetsKeep()
    {
        SetConfig("backup.auto", "false");
        SetConfig("backup.keep", "3");
        var store = BackupStore.FromConfig(Config());
        Assert.False(store.AutoBackupEnabled);
        Assert.Equal(3, store.Keep);
    }

    [Fact]
    public void Config_InvalidKeep_Throws()
    {
        SetConfig("backup.keep", "many");
        var ex = Assert.Throws<ConfigValueException>(() => BackupStore.FromConfig(Config()));
        Assert.Contains("backup.keep", ex.Message);
    }

    [Fact]
    public void Write_CreatesFileUnderDeviceDirectory_AndRoundTrips()
    {
        var path = Store().Write("hub-a", Snapshot());
        Assert.True(File.Exists(path));
        Assert.Equal(Path.Combine(BackupDir, "hub-a"), Path.GetDirectoryName(path));
        var reread = VideohubSnapshot.FromJson(File.ReadAllText(path));
        Assert.Equal(4, reread.VideoOutputs);
        Assert.Equal(4, reread.Outputs[0].Input);
    }

    [Fact]
    public void Write_TwiceInSameSecond_DoesNotOverwrite()
    {
        var store = Store();
        var stamp = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        var first = store.Write("hub-a", Snapshot(stamp));
        var second = store.Write("hub-a", Snapshot(stamp));
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void Write_PrunesOldestBeyondKeep()
    {
        SetConfig("backup.keep", "2");
        SetConfig("backup.dir", BackupDir);
        var store = BackupStore.FromConfig(Config());
        for (var minute = 0; minute < 4; minute++)
            store.Write("hub-a", Snapshot(new DateTimeOffset(2026, 8, 29, 10, minute, 0, TimeSpan.Zero)));

        var remaining = store.List("hub-a");
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, path => Assert.DoesNotContain("100000", Path.GetFileNameWithoutExtension(path)));
    }

    [Fact]
    public void List_NewestFirst_EmptyWhenNone()
    {
        var store = Store();
        Assert.Empty(store.List("hub-a"));
        store.Write("hub-a", Snapshot(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero)));
        store.Write("hub-a", Snapshot(new DateTimeOffset(2026, 8, 29, 11, 0, 0, TimeSpan.Zero)));
        var listed = store.List("hub-a");
        Assert.Equal(2, listed.Count);
        Assert.Contains("110000", Path.GetFileNameWithoutExtension(listed[0]));
    }

    [Fact]
    public void Write_SeparateDevices_DoNotShareDirectories()
    {
        var store = Store();
        store.Write("hub-a", Snapshot());
        store.Write("hub-b", Snapshot());
        Assert.Single(store.List("hub-a"));
        Assert.Single(store.List("hub-b"));
    }

    [Theory]
    [InlineData("10.0.0.5", "Blackmagic Smart Videohub", "10-0-0-5_blackmagic-smart-videohub")]
    [InlineData("hub.local", "Smart Videohub 20 x 20", "hub-local_smart-videohub-20-x-20")]
    public void DeviceKey_IsFilesystemSafe(string host, string model, string expected)
    {
        Assert.Equal(expected, BackupStore.DeviceKey(host, model));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter BackupStoreTests`
Expected: compilation failure — `BackupStore` does not exist.

- [ ] **Step 3: Implement**

Add to `src/Bmd/Config/ConfigPaths.cs`:

```csharp
    /// <summary>OS state directory for data bmd keeps between runs (backups) —
    /// distinct from config (settings) and cache (disposable).</summary>
    public static string StateDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bmd");
            var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            return string.IsNullOrEmpty(xdg)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "bmd")
                : Path.Combine(xdg, "bmd");
        }
    }
```

`src/Bmd/Config/BackupStore.cs`:

```csharp
using System.Text;
using Bmd.Devices.Videohub;

namespace Bmd.Config;

public sealed class ConfigValueException(string message) : Exception(message);

/// <summary>Stores pre-mutation device snapshots on disk, newest-first, pruned to a keep count.</summary>
public sealed class BackupStore
{
    const int DefaultKeep = 10;

    public bool AutoBackupEnabled { get; }
    public int Keep { get; }
    public string RootDirectory { get; }

    BackupStore(bool autoBackupEnabled, int keep, string rootDirectory)
    {
        AutoBackupEnabled = autoBackupEnabled;
        Keep = keep;
        RootDirectory = rootDirectory;
    }

    public static BackupStore FromConfig(ConfigStore config)
    {
        var auto = Get(config, "backup.auto") is not { } raw || !raw.Equals("false", StringComparison.OrdinalIgnoreCase);
        var keep = DefaultKeep;
        if (Get(config, "backup.keep") is { } keepText)
        {
            if (!int.TryParse(keepText, out keep) || keep < 1)
                throw new ConfigValueException($"config backup.keep must be a positive number, not '{keepText}'");
        }
        var root = Get(config, "backup.dir") ?? Path.Combine(ConfigPaths.StateDirectory, "backups");
        return new BackupStore(auto, keep, root);
    }

    static string? Get(ConfigStore config, string key)
    {
        ConfigKey.TryParse(key, out var parsed);
        return config.GetEffective(parsed);
    }

    /// <summary>Filesystem-safe directory name for a device: host + model, lowercased.</summary>
    public static string DeviceKey(string host, string modelName) =>
        $"{Sanitize(host)}_{Sanitize(modelName)}";

    static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var collapsed = builder.ToString();
        while (collapsed.Contains("--")) collapsed = collapsed.Replace("--", "-");
        return collapsed.Trim('-');
    }

    /// <summary>Writes a snapshot, verifies it reads back intact, prunes old ones. Returns the path.</summary>
    public string Write(string deviceKey, VideohubSnapshot snapshot)
    {
        var directory = Path.Combine(RootDirectory, deviceKey);
        Directory.CreateDirectory(directory);
        var path = UniquePath(directory, snapshot.ExportedAt);
        File.WriteAllText(path, snapshot.ToJson());

        var verification = VideohubSnapshot.FromJson(File.ReadAllText(path));
        if (verification.Outputs.Length != snapshot.Outputs.Length
            || verification.Inputs.Length != snapshot.Inputs.Length
            || verification.Outputs.Where((o, i) => o != snapshot.Outputs[i]).Any()
            || verification.Inputs.Where((input, i) => input != snapshot.Inputs[i]).Any())
        {
            throw new IOException($"backup written to {path} did not read back intact");
        }

        Prune(deviceKey);
        return path;
    }

    /// <summary>Existing backup file paths for a device, newest first.</summary>
    public IReadOnlyList<string> List(string deviceKey)
    {
        var directory = Path.Combine(RootDirectory, deviceKey);
        if (!Directory.Exists(directory)) return [];
        return Directory.GetFiles(directory, "*.json")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    void Prune(string deviceKey)
    {
        foreach (var path in List(deviceKey).Skip(Keep))
        {
            try { File.Delete(path); }
            catch (IOException) { /* a locked old backup is not worth failing the mutation over */ }
        }
    }

    static string UniquePath(string directory, DateTimeOffset stamp)
    {
        var basename = stamp.UtcDateTime.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(directory, $"{basename}.json");
        for (var suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(directory, $"{basename}-{suffix}.json");
        return path;
    }
}
```

Note: `List` sorts by ordinal filename descending, which is chronological because the timestamp format is zero-padded and fixed-width. The `-N` suffix on same-second writes sorts after the bare name, i.e. newest-last within that second — acceptable, and the tests pin only across-second ordering.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter BackupStoreTests`, then `dotnet test`.
Expected: all PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: backup store with device-keyed paths, verification, and rotation"
```

---

### Task 3: `bmd videohub export` with verification and retry

**Files:**
- Modify: `src/Bmd/Commands/Videohub/VideohubCommands.cs` (add `Export`, plus a client-factory seam), `src/Bmd/Commands/Videohub/VideohubResults.cs`, `src/Bmd/Output/BmdJsonContext.cs`, `src/Bmd/Program.cs`, `tests/Bmd.Tests/Commands/VideohubCommandsTests.cs`

**Interfaces:**
- Consumes: `VideohubSnapshot`, `VideohubClient`, `WithClientAsync` (milestone 2).
- Produces:
  - `sealed record VideohubExportResult(string Device, int VideoInputs, int VideoOutputs, int Routes, string? File, bool Verified)` registered in `BmdJsonContext`
  - `Task<int> Export(string? file = null, string? host = null, int? port = null, int? timeout = null, bool json = false)` on `VideohubCommands`, registered as `videohub export`

**Behavior:**
- Capture the connected client's state → snapshot (`ExportedAt` = now, UTC).
- Write: to `file` if given, else stdout (the JSON document itself; with `--json` the *result object* goes to stdout instead and a file is required — see below).
- Verify: re-read the written file, then fetch a **fresh** dump by reconnecting, and compare via `snapshot.DifferencesFrom(freshState)`. On differences, retry the whole capture-write-verify up to 3 attempts; after the third, exit 1 listing the differences. For stdout exports, compare the in-memory serialized bytes (parse them back) against the fresh dump instead of re-reading a file.
- Success (human, to file): `Exported and verified: 4 inputs, 4 outputs, 4 routes → normal.json`. Success (human, to stdout): the snapshot JSON only, no summary line (so `bmd videohub export > f.json` is clean).
- `--json` requires a file argument (otherwise two JSON documents would collide on stdout): without one → `error: --json requires a file argument (the snapshot itself goes to stdout without it)`, exit 2.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Bmd.Tests/Commands/VideohubCommandsTests.cs`:

```csharp
    [Fact]
    public async Task Export_ToFile_WritesVerifiedSnapshot()
    {
        await using var fake = FakeVideohub.Start();
        var path = Path.Combine(WorkDir, "snapshot.json");
        Assert.Equal(0, await Commands().Export(file: path, host: "127.0.0.1", port: fake.Port));

        var snapshot = VideohubSnapshot.FromJson(File.ReadAllText(path));
        Assert.Equal("Blackmagic Smart Videohub", snapshot.Device);
        Assert.Equal(4, snapshot.Outputs.Length);
        Assert.Equal(4, snapshot.Outputs[0].Input);
        Assert.Contains("verified", _stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_ToStdout_EmitsSnapshotJsonOnly()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().Export(host: "127.0.0.1", port: fake.Port));
        var snapshot = VideohubSnapshot.FromJson(_stdout.ToString());
        Assert.Equal(4, snapshot.Inputs.Length);
    }

    [Fact]
    public async Task Export_Json_ReportsSummary()
    {
        await using var fake = FakeVideohub.Start();
        var path = Path.Combine(WorkDir, "snapshot.json");
        Assert.Equal(0, await Commands().Export(file: path, host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal("Blackmagic Smart Videohub", root.GetProperty("device").GetString());
        Assert.Equal(4, root.GetProperty("videoInputs").GetInt32());
        Assert.Equal(4, root.GetProperty("routes").GetInt32());
        Assert.Equal(path, root.GetProperty("file").GetString());
        Assert.True(root.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task Export_JsonWithoutFile_Exit2()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(2, await Commands().Export(host: "127.0.0.1", port: fake.Port, json: true));
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("--json requires a file", _stderr.ToString());
    }

    [Fact]
    public async Task Export_UnwritablePath_Exit1_CleanError()
    {
        await using var fake = FakeVideohub.Start();
        var directoryAsFile = Path.Combine(WorkDir, "blocked");
        Directory.CreateDirectory(directoryAsFile);
        Assert.Equal(1, await Commands().Export(file: directoryAsFile, host: "127.0.0.1", port: fake.Port));
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    [Fact]
    public async Task Export_DeviceChangesEveryAttempt_Exit1_ListsDifferences()
    {
        // fake serves a different route on each connection: verification can never converge
        await using var fake = FakeVideohub.StartChanging();
        var path = Path.Combine(WorkDir, "snapshot.json");
        Assert.Equal(1, await Commands().Export(file: path, host: "127.0.0.1", port: fake.Port));
        Assert.Contains("output", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }
```

Add the imports the file needs at its top: `using Bmd.Devices.Videohub;`.

Add to `tests/Bmd.Tests/Devices/Videohub/FakeVideohub.cs` a second factory that serves a *different* routing on each connection (so export verification never converges):

```csharp
    /// <summary>A hub whose output-1 route changes on every connection —
    /// used to prove export verification retries and then fails cleanly.</summary>
    public static FakeVideohub StartChanging()
    {
        var connection = 0;
        return new FakeVideohub(() =>
        {
            var input = connection++ % 4;   // 0,1,2,3 → different route each connect
            return Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", $"VIDEO OUTPUT ROUTING:\n0 {input}");
        });
    }
```

This requires `FakeVideohub` to hold a `Func<string>` dump factory instead of a fixed string: change the field to `readonly Func<string> _dump;`, the private constructor to take `Func<string> dump`, add the private `FakeVideohub(Func<string> dump)` overload, keep `Start(string dump = Fixtures.Dump4x4)` delegating to it (`new(() => dump)`), and have `HandleClientAsync` call `_dump()` per connection.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubCommandsTests`
Expected: compilation failure — `Export` and `StartChanging` do not exist.

- [ ] **Step 3: Implement**

Add to `VideohubResults.cs`:

```csharp
/// <summary>Result of `videohub export`: what was captured and where it went.</summary>
public sealed record VideohubExportResult(
    string Device, int VideoInputs, int VideoOutputs, int Routes, string? File, bool Verified);
```

Register `VideohubExportResult` in `BmdJsonContext`.

Add `Export` to `VideohubCommands` (full XML docs required, matching the existing commands' pattern — summary states what is captured; `<param>` tags cover file/host/port/timeout/json):

```csharp
    /// <summary>Export a verified snapshot of labels and routing (1-based). Locks are not captured.</summary>
    /// <param name="file">Destination file; omit to write the snapshot JSON to stdout.</param>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit a summary object as JSON on stdout; requires a file.</param>
    public async Task<int> Export(
        [Argument] string? file = null, string? host = null, int? port = null, int? timeout = null, bool json = false)
    {
        if (json && file is null)
        {
            Console.Error.WriteLine("error: --json requires a file argument (the snapshot itself goes to stdout without it)");
            return 2;
        }

        const int attempts = 3;
        IReadOnlyList<string> differences = [];
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            VideohubSnapshot? captured = null;
            var capture = await WithClientAsync(host, port, timeout, client =>
            {
                captured = VideohubSnapshot.FromState(client.State, DateTimeOffset.UtcNow);
                return 0;
            });
            if (capture != 0) return capture;

            var snapshot = captured!;
            var text = snapshot.ToJson();
            if (file is not null) File.WriteAllText(file, text);

            var written = VideohubSnapshot.FromJson(file is not null ? File.ReadAllText(file) : text);
            var verify = await WithClientAsync(host, port, timeout, client =>
            {
                differences = written.DifferencesFrom(client.State);
                return 0;
            });
            if (verify != 0) return verify;
            if (differences.Count > 0) continue;   // device changed mid-export: recapture

            if (file is null) Console.Write(text);
            else if (json)
                Console.WriteLine(JsonSerializer.Serialize(
                    new VideohubExportResult(snapshot.Device, snapshot.VideoInputs, snapshot.VideoOutputs,
                        snapshot.Outputs.Length, file, true),
                    BmdJsonContext.Default.VideohubExportResult));
            else
                Console.WriteLine(
                    $"Exported and verified: {snapshot.VideoInputs} inputs, {snapshot.VideoOutputs} outputs, " +
                    $"{snapshot.Outputs.Length} routes → {file}");
            return 0;
        }

        Console.Error.WriteLine(
            $"error: device kept changing during export; snapshot not verified after {attempts} attempts");
        foreach (var difference in differences) Console.Error.WriteLine($"  {difference}");
        return 1;
    }
```

`WithClientAsync`'s existing catch filter already covers `IOException`/`UnauthorizedAccessException`, but the `File.WriteAllText`/`ReadAllText` calls above sit **outside** it — wrap the body of `Export` (everything after the `--json` check) in the same try/catch shape so an unwritable path yields `error: ...` + exit 1 rather than a stack trace. Extract that catch into a small private helper if it reads better; keep behavior identical to `WithClientAsync`'s.

Register in `Program.cs`: `app.Add("videohub export", videohub.Export);`

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`, then check help: `dotnet run --project src/Bmd -- videohub export --help`
Expected: all pass; help documents the optional file argument, connection options, and the `--json` file requirement.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: bmd videohub export with verification and retry"
```

---

### Task 4: Milestone proof — AOT publish + export round-trip on the native binary

**Files:**
- Modify: none expected (csproj only if the publish surfaces warnings)

**Interfaces:**
- Consumes: everything above.
- Produces: the milestone exit criterion.

- [ ] **Step 1: Full suite + publish**

```powershell
dotnet test
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/Bmd -c Release -r win-x64
```

Expected: all tests pass; publish clean with zero IL2xxx/IL3xxx warnings. The new `SnapshotJsonContext` is the AOT-sensitive addition — any warning naming it is a real defect to fix, never suppress.

- [ ] **Step 2: Native smoke**

```powershell
$exe = "src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe"
& $exe videohub export --help
& $exe videohub export --host 127.0.0.1 --port 1 --timeout 2; $LASTEXITCODE   # expect error: + exit 1, no stack trace
& $exe videohub export out.json --json --host 127.0.0.1 --port 1 --timeout 2; $LASTEXITCODE  # same
& $exe videohub export --json --host 127.0.0.1 --port 9990; $LASTEXITCODE     # expect exit 2 (--json without file), before any connection
Remove-Item out.json -ErrorAction SilentlyContinue
```

Expected: help complete; failures give one `error:` line on stderr with an empty stdout; the `--json`-without-file case exits 2 without attempting a connection.

- [ ] **Step 3: Record size and commit**

```powershell
(Get-Item src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe).Length / 1MB
git add -A; git commit -m "chore: prove milestone 3 (export + backup store) on Native AOT" --allow-empty
```

---

## Self-Review Notes

- **Spec coverage (milestone 3):** snapshot format with 1-based numbering and locks excluded (Task 1); backup locations, rotation, config keys, write-verification (Task 2); export with fresh-dump verification, 3-attempt retry, stdout mode, `--json` summary (Task 3); AOT proof (Task 4). `backup.auto`/`--no-backup` are *consumed* in milestone 4 — this milestone only builds and tests the store, which is why no mutation wiring appears here.
- **Carried-forward constraints from milestone 2's review** (binding for milestone 4, noted here so they are not lost): FakeVideohub must track and await per-client tasks before ACK-dependent tests; `VideohubClient` must reuse one `StreamReader` across the connection lifetime before any post-dump reads. Task 3 touches `FakeVideohub`'s dump plumbing but not its task lifecycle — deliberately, to keep this milestone read-only.
- **Type consistency:** `VideohubSnapshot.FromState/ToJson/FromJson/DifferencesFrom` used identically across Tasks 1-3; `BackupStore.Write` returns the path milestone 4 will report; `BmdJsonContext.Default.VideohubExportResult` follows source-gen naming.
- **Known risk:** `Export` calls `WithClientAsync` twice per attempt (capture, then fresh verify) — two connections per attempt is intended (verification must see a *fresh* dump), but it means an export makes 2-6 connections. Acceptable for a snapshot command; noted in case a reviewer flags it as waste.
