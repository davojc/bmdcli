# bmd Milestone 2: JSON Retrofit + Videohub Read Path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every command speaks `--json` (retrofitting config), and `bmd videohub info / input list / output list / route list` work against a real or fake Videohub over TCP 9990.

**Architecture:** A pure protocol layer (`Devices/Videohub/`: block reader, dump parser, 1-based state model) under an async `VideohubClient`; thin `Commands/Videohub/` on top resolving host/port/timeout from flags→config; one source-generated `BmdJsonContext` for all JSON output; an in-process fake Videohub TCP server drives integration tests.

**Tech Stack:** .NET 10, ConsoleAppFramework v5 (already wired), System.Text.Json source generators (in-box), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` (see "Agents and scripting" and "Videohub protocol layer")

## Global Constraints

- `PublishAot=true` stays green: no reflection, no `dynamic`, JSON ONLY via the source-generated `BmdJsonContext`. No new package references.
- `Devices/` and `Config/` never reference ConsoleAppFramework or `Commands/`.
- **User-facing numbering is 1-based; the wire is 0-based.** Conversion happens at exactly one place: the public API of `VideohubState` (and command args later). Nothing above `Devices/Videohub/` ever sees a 0-based number.
- `--json`: one JSON document on stdout (camelCase, stable names); representation only, never behavior. Errors are always one `error: ...` line on stderr + exit code (0 success / 1 operation failure / 2 usage or format error) — with or without `--json`, stdout carries nothing on failure.
- Help is the API: every command/argument/flag gets a complete XML doc comment; argument docs state 1-based numbering and units (seconds) where relevant.
- Videohub protocol: text over TCP 9990; blank-line-terminated blocks; device pushes a full dump on connect. Defaults: port 9990, timeout 5 s. Missing host → `error: no host configured for videohub` + hint `run: bmd config set videohub.host <addr>`, exit 1.
- Console-redirecting test classes MUST share the xUnit collection `"console"` (Console.SetOut is process-global; parallel classes would race).
- TDD: failing test → implementation → passing test → commit, every task.

---

### Task 1: BmdJsonContext + `--json` on the config commands

**Files:**
- Create: `src/Bmd/Output/BmdJsonContext.cs`, `src/Bmd/Commands/ConfigResults.cs`, `tests/Bmd.Tests/ConsoleTestCollection.cs`, `tests/Bmd.Tests/Commands/ConfigCommandsJsonTests.cs`
- Modify: `src/Bmd/Commands/ConfigCommands.cs` (add `json` flag to all four verbs), `tests/Bmd.Tests/Commands/ConfigCommandsTests.cs` (add `[Collection("console")]`)

**Interfaces:**
- Consumes: `ConfigStore`, `ConfigKey`, `ConfigEntry` (milestone 1).
- Produces: `Bmd.Output.BmdJsonContext` (source-gen `JsonSerializerContext`, camelCase) — later tasks register their result types here. Records in `Bmd.Commands`: `ConfigGetResult(string Key, string Value, string? Origin)`, `ConfigSetResult(string Key, string Value, string File)`, `ConfigUnsetResult(string Key, bool Removed)`. Test infra: `ConsoleTestCollection` defining xUnit collection `"console"`.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/ConsoleTestCollection.cs`:

```csharp
namespace Bmd.Tests;

[CollectionDefinition("console")]
public class ConsoleTestCollection;
```

Add `[Collection("console")]` above `public class ConfigCommandsTests` in the existing test file (namespace import `using Bmd.Tests;` if needed).

`tests/Bmd.Tests/Commands/ConfigCommandsJsonTests.cs`:

```csharp
using System.Text.Json;
using Bmd.Commands;
using Bmd.Config;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class ConfigCommandsJsonTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");

    public ConfigCommandsJsonTests()
    {
        Directory.CreateDirectory(WorkDir);
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Directory.Delete(_root, recursive: true);
    }

    ConfigCommands Commands() => new(() => ConfigStore.Load(GlobalPath, WorkDir));

    JsonElement Root() => JsonDocument.Parse(_stdout.ToString()).RootElement;

    [Fact]
    public void Set_Json_ReportsKeyValueFile()
    {
        Assert.Equal(0, Commands().Set("videohub.host", "10.0.0.5", json: true));
        var root = Root();
        Assert.Equal("videohub.host", root.GetProperty("key").GetString());
        Assert.Equal("10.0.0.5", root.GetProperty("value").GetString());
        Assert.Equal(Path.Combine(WorkDir, ConfigPaths.LocalFileName), root.GetProperty("file").GetString());
    }

    [Fact]
    public void Get_Json_ReportsKeyValueOrigin()
    {
        Commands().Set("videohub.host", "10.0.0.5");
        _stdout.GetStringBuilder().Clear();
        Assert.Equal(0, Commands().Get("videohub.host", json: true));
        var root = Root();
        Assert.Equal("videohub.host", root.GetProperty("key").GetString());
        Assert.Equal("10.0.0.5", root.GetProperty("value").GetString());
        Assert.Equal(Path.Combine(WorkDir, ConfigPaths.LocalFileName), root.GetProperty("origin").GetString());
    }

    [Fact]
    public void Get_Json_MissingKey_Exit1_NothingOnStdout()
    {
        Assert.Equal(1, Commands().Get("videohub.host", json: true));
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("not set", _stderr.ToString());
    }

    [Fact]
    public void List_Json_IsArrayOfEntries()
    {
        Commands().Set("videohub.host", "10.0.0.5");
        Commands().Set("update.check", "false", global: true);
        _stdout.GetStringBuilder().Clear();
        Assert.Equal(0, Commands().List(json: true));
        var root = Root();
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());
        var host = root.EnumerateArray().Single(e => e.GetProperty("key").GetString() == "videohub.host");
        Assert.Equal("10.0.0.5", host.GetProperty("value").GetString());
        Assert.False(string.IsNullOrEmpty(host.GetProperty("origin").GetString()));
    }

    [Fact]
    public void Unset_Json_ReportsRemoved()
    {
        Commands().Set("videohub.host", "10.0.0.5");
        _stdout.GetStringBuilder().Clear();
        Assert.Equal(0, Commands().Unset("videohub.host", json: true));
        var root = Root();
        Assert.Equal("videohub.host", root.GetProperty("key").GetString());
        Assert.True(root.GetProperty("removed").GetBoolean());
    }

    [Fact]
    public void HumanOutput_Unchanged_WhenJsonOmitted()
    {
        Commands().Set("videohub.host", "10.0.0.5");
        _stdout.GetStringBuilder().Clear();
        Assert.Equal(0, Commands().Get("videohub.host"));
        Assert.Equal("10.0.0.5", _stdout.ToString().Trim());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ConfigCommandsJsonTests`
Expected: compilation failure — `json` parameters and result records do not exist.

- [ ] **Step 3: Implement**

`src/Bmd/Commands/ConfigResults.cs`:

```csharp
namespace Bmd.Commands;

public sealed record ConfigGetResult(string Key, string Value, string? Origin);
public sealed record ConfigSetResult(string Key, string Value, string File);
public sealed record ConfigUnsetResult(string Key, bool Removed);
```

`src/Bmd/Output/BmdJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;
using Bmd.Commands;
using Bmd.Config;

namespace Bmd.Output;

/// <summary>Single source-generated JSON context for all --json output (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigGetResult))]
[JsonSerializable(typeof(ConfigSetResult))]
[JsonSerializable(typeof(ConfigUnsetResult))]
[JsonSerializable(typeof(ConfigEntry[]))]
public partial class BmdJsonContext : JsonSerializerContext;
```

In `src/Bmd/Commands/ConfigCommands.cs`: add `using System.Text.Json;` and `using Bmd.Output;`, then extend each verb. The pattern (existing logic unchanged, JSON branch added; `RunGuarded` wrapping stays exactly as-is):

```csharp
/// <summary>Set a configuration value.</summary>
/// <param name="key">Configuration key, e.g. videohub.host.</param>
/// <param name="value">Value to assign.</param>
/// <param name="global">-g, Write to the global config file instead of local .bmdconfig.</param>
/// <param name="json">Emit the result as JSON on stdout.</param>
public int Set([Argument] string key, [Argument] string value, bool global = false, bool json = false)
    => RunGuarded(() =>
    {
        if (!TryKey(key, out var k)) return 2;
        if (!TryValue(value)) return 2;
        var file = _load().Set(k, value, global);
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new ConfigSetResult(k.ToString(), value, file), BmdJsonContext.Default.ConfigSetResult));
        return 0;
    });
```

- `Get(key, json)`: on success, JSON branch finds the entry's origin via `_load().ListEffective().FirstOrDefault(e => e.Key == k.ToString())?.Origin` and serializes `ConfigGetResult(k.ToString(), value, origin)`; human branch prints the bare value as today. Missing key: unchanged (stderr + exit 1, stdout empty in both modes).
- `Unset(key, global, json)`: on success, JSON branch serializes `ConfigUnsetResult(k.ToString(), true)`; failure path unchanged.
- `List(showOrigin, json)`: JSON branch serializes `_load().ListEffective().ToArray()` with `BmdJsonContext.Default.ConfigEntryArray` (ignore `showOrigin` in JSON mode — origin is always in the objects); human branch unchanged.

Exact method bodies follow the existing file's style (expression-bodied `RunGuarded` lambdas); adapt the snippets to it rather than restructuring.

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`
Expected: all pass (55 existing + 6 new = 61).

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: --json output on all config commands via source-gen BmdJsonContext"
```

---

### Task 2: Protocol blocks — reader and accumulator (pure)

**Files:**
- Create: `src/Bmd/Devices/Videohub/ProtocolBlock.cs`, `tests/Bmd.Tests/Devices/Videohub/ProtocolBlockTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `Bmd.Devices.Videohub`):
  - `sealed record ProtocolBlock(string Header, IReadOnlyList<string> Lines)` — `Header` has NO trailing colon (`"VIDEO OUTPUT ROUTING"`, or `"ACK"`/`"NAK"` with empty `Lines`)
  - `static class BlockReader { static IReadOnlyList<ProtocolBlock> ReadBlocks(string text) }` — split a whole transcript (tests, fixtures)
  - `sealed class BlockAccumulator { ProtocolBlock? Add(string line) }` — feed lines one at a time (the client's incremental path); returns a completed block when a blank line closes one, else null

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/ProtocolBlockTests.cs`:

```csharp
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class ProtocolBlockTests
{
    [Fact]
    public void ReadBlocks_SplitsOnBlankLines_AndStripsHeaderColon()
    {
        var text = "INPUT LABELS:\n0 Cam 1\n1 Cam 2\n\nVIDEO OUTPUT ROUTING:\n0 1\n\n";
        var blocks = BlockReader.ReadBlocks(text);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("INPUT LABELS", blocks[0].Header);
        Assert.Equal(["0 Cam 1", "1 Cam 2"], blocks[0].Lines);
        Assert.Equal("VIDEO OUTPUT ROUTING", blocks[1].Header);
        Assert.Equal(["0 1"], blocks[1].Lines);
    }

    [Fact]
    public void ReadBlocks_AckNak_AreHeaderOnlyBlocks()
    {
        var blocks = BlockReader.ReadBlocks("ACK\n\nNAK\n\n");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("ACK", blocks[0].Header);
        Assert.Empty(blocks[0].Lines);
        Assert.Equal("NAK", blocks[1].Header);
    }

    [Fact]
    public void ReadBlocks_ToleratesCrlf()
    {
        var blocks = BlockReader.ReadBlocks("VIDEOHUB DEVICE:\r\nModel name: X\r\n\r\n");
        var block = Assert.Single(blocks);
        Assert.Equal("VIDEOHUB DEVICE", block.Header);
        Assert.Equal(["Model name: X"], block.Lines);
    }

    [Fact]
    public void ReadBlocks_IgnoresLeadingBlankLines_AndUnterminatedTrailingBlock()
    {
        var blocks = BlockReader.ReadBlocks("\n\nEND PRELUDE:\n\nPARTIAL:\nno blank after");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("END PRELUDE", blocks[0].Header);
        Assert.Equal("PARTIAL", blocks[1].Header); // trailing block completes at end-of-text
    }

    [Fact]
    public void Accumulator_EmitsBlockOnBlankLine_NullOtherwise()
    {
        var acc = new BlockAccumulator();
        Assert.Null(acc.Add("INPUT LABELS:"));
        Assert.Null(acc.Add("0 Cam 1"));
        var block = acc.Add("");
        Assert.NotNull(block);
        Assert.Equal("INPUT LABELS", block!.Header);
        Assert.Equal(["0 Cam 1"], block.Lines);
        Assert.Null(acc.Add("")); // stray extra blank line between blocks is ignored
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ProtocolBlockTests`
Expected: compilation failure — types do not exist.

- [ ] **Step 3: Implement `src/Bmd/Devices/Videohub/ProtocolBlock.cs`**

```csharp
namespace Bmd.Devices.Videohub;

/// <summary>One blank-line-terminated block of the Videohub text protocol.
/// Header carries no trailing colon; ACK/NAK are header-only blocks.</summary>
public sealed record ProtocolBlock(string Header, IReadOnlyList<string> Lines);

/// <summary>Feed protocol lines one at a time; a blank line completes and returns a block.</summary>
public sealed class BlockAccumulator
{
    readonly List<string> _lines = [];

    public ProtocolBlock? Add(string line)
    {
        if (line.Length > 0)
        {
            _lines.Add(line);
            return null;
        }
        if (_lines.Count == 0) return null; // stray blank line
        var header = _lines[0].TrimEnd();
        if (header.EndsWith(':')) header = header[..^1];
        var block = new ProtocolBlock(header, _lines.Skip(1).ToArray());
        _lines.Clear();
        return block;
    }

    /// <summary>Completes a trailing block that was never closed by a blank line.</summary>
    public ProtocolBlock? Flush() => _lines.Count == 0 ? null : Add("");
}

public static class BlockReader
{
    public static IReadOnlyList<ProtocolBlock> ReadBlocks(string text)
    {
        var acc = new BlockAccumulator();
        var blocks = new List<ProtocolBlock>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            if (acc.Add(raw) is { } block) blocks.Add(block);
        if (acc.Flush() is { } trailing) blocks.Add(trailing);
        return blocks;
    }
}
```

(Note `Flush` handles the unterminated-trailing-block test; `Add("")` inside `Flush` reuses the completion path.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ProtocolBlockTests`
Expected: 5/5 PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: Videohub protocol block reader and accumulator"
```

---

### Task 3: VideohubState + DumpParser (1-based boundary)

**Files:**
- Create: `src/Bmd/Devices/Videohub/VideohubState.cs`, `src/Bmd/Devices/Videohub/DumpParser.cs`, `tests/Bmd.Tests/Devices/Videohub/DumpParserTests.cs`

**Interfaces:**
- Consumes: `ProtocolBlock` (Task 2).
- Produces (namespace `Bmd.Devices.Videohub`):
  - `sealed record VideohubDeviceInfo(string ModelName, string? FriendlyName, int VideoInputs, int VideoOutputs, string ProtocolVersion)`
  - `enum LockState { Unlocked, Owned, Locked }` (wire `U`/`O`/`L`)
  - `sealed class VideohubState` — **ALL public members 1-based**: `VideohubDeviceInfo Device { get; }`, `string GetInputLabel(int input)`, `string GetOutputLabel(int output)`, `int GetRoute(int output)` (returns 1-based input), `LockState GetLock(int output)`. Arguments outside `[1..count]` throw `ArgumentOutOfRangeException`.
  - `static class DumpParser { static VideohubState Parse(IReadOnlyList<ProtocolBlock> blocks) }` — throws `VideohubProtocolException` (also defined here: `sealed class VideohubProtocolException(string message) : Exception(message)`) on missing/invalid required blocks
  - `DumpParser.RequiredHeaders : IReadOnlyList<string>` = `["VIDEOHUB DEVICE", "INPUT LABELS", "OUTPUT LABELS", "VIDEO OUTPUT ROUTING", "VIDEO OUTPUT LOCKS"]` (the client uses this to know when a dump is complete)
  - Test fixture (also used by Tasks 4-7): `tests/Bmd.Tests/Devices/Videohub/Fixtures.cs` with `public static class Fixtures { public const string Dump4x4 = ...; }`

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/Fixtures.cs`:

```csharp
namespace Bmd.Tests.Devices.Videohub;

public static class Fixtures
{
    /// <summary>Realistic 4x4 initial dump. Wire indices are 0-based:
    /// output0←input3, output1←input1, output2←input0, output3←input2.</summary>
    public const string Dump4x4 =
        "PROTOCOL PREAMBLE:\n" +
        "Version: 2.8\n" +
        "\n" +
        "VIDEOHUB DEVICE:\n" +
        "Device present: true\n" +
        "Model name: Blackmagic Smart Videohub\n" +
        "Friendly name: Test Hub\n" +
        "Video inputs: 4\n" +
        "Video outputs: 4\n" +
        "\n" +
        "INPUT LABELS:\n" +
        "0 Cam 1\n" +
        "1 Cam 2\n" +
        "2 Cam 3\n" +
        "3 Cam 4\n" +
        "\n" +
        "OUTPUT LABELS:\n" +
        "0 Program\n" +
        "1 Preview\n" +
        "2 Monitor\n" +
        "3 Aux\n" +
        "\n" +
        "VIDEO OUTPUT LOCKS:\n" +
        "0 U\n" +
        "1 O\n" +
        "2 L\n" +
        "3 U\n" +
        "\n" +
        "VIDEO OUTPUT ROUTING:\n" +
        "0 3\n" +
        "1 1\n" +
        "2 0\n" +
        "3 2\n" +
        "\n" +
        "END PRELUDE:\n" +
        "\n";
}
```

`tests/Bmd.Tests/Devices/Videohub/DumpParserTests.cs`:

```csharp
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class DumpParserTests
{
    static VideohubState Parse() => DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));

    [Fact]
    public void Parse_DeviceInfo()
    {
        var device = Parse().Device;
        Assert.Equal("Blackmagic Smart Videohub", device.ModelName);
        Assert.Equal("Test Hub", device.FriendlyName);
        Assert.Equal(4, device.VideoInputs);
        Assert.Equal(4, device.VideoOutputs);
        Assert.Equal("2.8", device.ProtocolVersion);
    }

    [Fact]
    public void Labels_AreOneBased_AndPreserveSpaces()
    {
        var state = Parse();
        Assert.Equal("Cam 1", state.GetInputLabel(1));
        Assert.Equal("Cam 4", state.GetInputLabel(4));
        Assert.Equal("Program", state.GetOutputLabel(1));
        Assert.Equal("Aux", state.GetOutputLabel(4));
    }

    [Fact]
    public void Routes_AreOneBased()
    {
        var state = Parse();
        Assert.Equal(4, state.GetRoute(1)); // wire: 0 3
        Assert.Equal(2, state.GetRoute(2)); // wire: 1 1
        Assert.Equal(1, state.GetRoute(3)); // wire: 2 0
        Assert.Equal(3, state.GetRoute(4)); // wire: 3 2
    }

    [Fact]
    public void Locks_MapWireLetters()
    {
        var state = Parse();
        Assert.Equal(LockState.Unlocked, state.GetLock(1));
        Assert.Equal(LockState.Owned, state.GetLock(2));
        Assert.Equal(LockState.Locked, state.GetLock(3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void OutOfRange_Throws(int n)
    {
        var state = Parse();
        Assert.Throws<ArgumentOutOfRangeException>(() => state.GetInputLabel(n));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.GetRoute(n));
    }

    [Fact]
    public void Parse_MissingRequiredBlock_Throws()
    {
        var withoutRouting = BlockReader.ReadBlocks(Fixtures.Dump4x4)
            .Where(b => b.Header != "VIDEO OUTPUT ROUTING").ToArray();
        var ex = Assert.Throws<VideohubProtocolException>(() => DumpParser.Parse(withoutRouting));
        Assert.Contains("VIDEO OUTPUT ROUTING", ex.Message);
    }

    [Fact]
    public void Parse_MissingFriendlyName_IsNull()
    {
        var blocks = BlockReader.ReadBlocks(Fixtures.Dump4x4.Replace("Friendly name: Test Hub\n", ""));
        Assert.Null(DumpParser.Parse(blocks).Device.FriendlyName);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter DumpParserTests`
Expected: compilation failure.

- [ ] **Step 3: Implement**

`src/Bmd/Devices/Videohub/VideohubState.cs`:

```csharp
namespace Bmd.Devices.Videohub;

public sealed record VideohubDeviceInfo(
    string ModelName, string? FriendlyName, int VideoInputs, int VideoOutputs, string ProtocolVersion);

public enum LockState { Unlocked, Owned, Locked }

public sealed class VideohubProtocolException(string message) : Exception(message);

/// <summary>Snapshot of a Videohub's state. ALL public numbering is 1-based
/// (matching front panels); internal storage mirrors the 0-based wire.</summary>
public sealed class VideohubState
{
    readonly string[] _inputLabels;
    readonly string[] _outputLabels;
    readonly int[] _routes;      // 0-based: _routes[out] = in
    readonly LockState[] _locks;

    public VideohubDeviceInfo Device { get; }

    internal VideohubState(VideohubDeviceInfo device, string[] inputLabels, string[] outputLabels, int[] routes, LockState[] locks)
    {
        Device = device;
        _inputLabels = inputLabels;
        _outputLabels = outputLabels;
        _routes = routes;
        _locks = locks;
    }

    public string GetInputLabel(int input) => _inputLabels[CheckIndex(input, Device.VideoInputs, nameof(input))];
    public string GetOutputLabel(int output) => _outputLabels[CheckIndex(output, Device.VideoOutputs, nameof(output))];
    public int GetRoute(int output) => _routes[CheckIndex(output, Device.VideoOutputs, nameof(output))] + 1;
    public LockState GetLock(int output) => _locks[CheckIndex(output, Device.VideoOutputs, nameof(output))];

    static int CheckIndex(int n, int count, string name) =>
        n >= 1 && n <= count ? n - 1 : throw new ArgumentOutOfRangeException(name, n, $"must be between 1 and {count}");
}
```

`src/Bmd/Devices/Videohub/DumpParser.cs`:

```csharp
namespace Bmd.Devices.Videohub;

public static class DumpParser
{
    public static readonly IReadOnlyList<string> RequiredHeaders =
        ["VIDEOHUB DEVICE", "INPUT LABELS", "OUTPUT LABELS", "VIDEO OUTPUT ROUTING", "VIDEO OUTPUT LOCKS"];

    public static VideohubState Parse(IReadOnlyList<ProtocolBlock> blocks)
    {
        var byHeader = new Dictionary<string, ProtocolBlock>(StringComparer.Ordinal);
        foreach (var block in blocks) byHeader.TryAdd(block.Header, block);
        foreach (var required in RequiredHeaders)
            if (!byHeader.ContainsKey(required))
                throw new VideohubProtocolException($"device dump is missing the {required} block");

        var device = ParseDevice(byHeader["VIDEOHUB DEVICE"],
            byHeader.TryGetValue("PROTOCOL PREAMBLE", out var preamble) ? preamble : null);
        var inputLabels = ParseIndexed(byHeader["INPUT LABELS"], device.VideoInputs);
        var outputLabels = ParseIndexed(byHeader["OUTPUT LABELS"], device.VideoOutputs);
        var routes = new int[device.VideoOutputs];
        foreach (var (index, value) in ParsePairs(byHeader["VIDEO OUTPUT ROUTING"], device.VideoOutputs))
            routes[index] = ParseInt(value, "VIDEO OUTPUT ROUTING");
        var locks = new LockState[device.VideoOutputs];
        foreach (var (index, value) in ParsePairs(byHeader["VIDEO OUTPUT LOCKS"], device.VideoOutputs))
            locks[index] = value switch
            {
                "U" => LockState.Unlocked,
                "O" => LockState.Owned,
                "L" => LockState.Locked,
                _ => throw new VideohubProtocolException($"unknown lock state '{value}'"),
            };
        return new VideohubState(device, inputLabels, outputLabels, routes, locks);
    }

    static VideohubDeviceInfo ParseDevice(ProtocolBlock device, ProtocolBlock? preamble)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in device.Lines)
        {
            var colon = line.IndexOf(':');
            if (colon > 0) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var version = "";
        if (preamble is not null)
            foreach (var line in preamble.Lines)
                if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                    version = line["Version:".Length..].Trim();
        return new VideohubDeviceInfo(
            fields.GetValueOrDefault("Model name", "unknown"),
            fields.TryGetValue("Friendly name", out var friendly) && friendly.Length > 0 ? friendly : null,
            ParseInt(fields.GetValueOrDefault("Video inputs", "0"), "Video inputs"),
            ParseInt(fields.GetValueOrDefault("Video outputs", "0"), "Video outputs"),
            version);
    }

    static string[] ParseIndexed(ProtocolBlock block, int count)
    {
        var values = new string[count];
        Array.Fill(values, "");
        foreach (var (index, value) in ParsePairs(block, count)) values[index] = value;
        return values;
    }

    /// <summary>Parses "&lt;0-based index&gt; &lt;rest of line&gt;" entries, skipping out-of-range indices.</summary>
    static IEnumerable<(int Index, string Value)> ParsePairs(ProtocolBlock block, int count)
    {
        foreach (var line in block.Lines)
        {
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var index = ParseInt(line[..space], block.Header);
            if (index >= 0 && index < count) yield return (index, line[(space + 1)..]);
        }
    }

    static int ParseInt(string text, string context) =>
        int.TryParse(text, out var value)
            ? value
            : throw new VideohubProtocolException($"invalid number '{text}' in {context} block");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter DumpParserTests`
Expected: 8/8 PASS (7 facts + 2 theory rows counted per row).

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: Videohub dump parser and 1-based state model"
```

---

### Task 4: FakeVideohub TCP server (test infrastructure)

**Files:**
- Create: `tests/Bmd.Tests/Devices/Videohub/FakeVideohub.cs`, `tests/Bmd.Tests/Devices/Videohub/FakeVideohubTests.cs`

**Interfaces:**
- Consumes: `Fixtures.Dump4x4` (Task 3).
- Produces (namespace `Bmd.Tests.Devices.Videohub`):
  - `sealed class FakeVideohub : IAsyncDisposable` with `static FakeVideohub Start(string dump = Fixtures.Dump4x4)`, `int Port { get; }`. Listens on 127.0.0.1 (ephemeral port), accepts any number of connections; on each connect writes the dump verbatim, then keeps the connection open reading (and discarding) client lines until disconnect. Milestone 3 will extend it to ACK mutations; keep it minimal now.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/FakeVideohubTests.cs`:

```csharp
using System.Net.Sockets;
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class FakeVideohubTests
{
    [Fact]
    public async Task ServesDumpOnConnect()
    {
        await using var fake = FakeVideohub.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", fake.Port);
        using var reader = new StreamReader(tcp.GetStream());

        var acc = new BlockAccumulator();
        var blocks = new List<ProtocolBlock>();
        while (blocks.Count < 7 && await reader.ReadLineAsync() is { } line)
            if (acc.Add(line) is { } block) blocks.Add(block);

        Assert.Contains(blocks, b => b.Header == "VIDEOHUB DEVICE");
        Assert.Contains(blocks, b => b.Header == "END PRELUDE");
    }

    [Fact]
    public async Task SupportsSequentialConnections()
    {
        await using var fake = FakeVideohub.Start();
        for (var i = 0; i < 2; i++)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", fake.Port);
            using var reader = new StreamReader(tcp.GetStream());
            var first = await reader.ReadLineAsync();
            Assert.Equal("PROTOCOL PREAMBLE:", first);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FakeVideohubTests`
Expected: compilation failure — `FakeVideohub` does not exist.

- [ ] **Step 3: Implement `tests/Bmd.Tests/Devices/Videohub/FakeVideohub.cs`**

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bmd.Tests.Devices.Videohub;

/// <summary>Minimal in-process Videohub: serves a dump on connect, then reads
/// and discards client lines. Doubles as protocol documentation.</summary>
public sealed class FakeVideohub : IAsyncDisposable
{
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly Task _acceptLoop;
    readonly string _dump;

    public int Port { get; }

    FakeVideohub(string dump)
    {
        _dump = dump;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public static FakeVideohub Start(string dump = Fixtures.Dump4x4) => new(dump);

    async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(_dump), _cts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync(_cts.Token) is not null) { } // discard until disconnect
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FakeVideohubTests`
Expected: 2/2 PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "test: in-process fake Videohub TCP server"
```

---

### Task 5: VideohubClient (connect, timeout, dump)

**Files:**
- Create: `src/Bmd/Devices/Videohub/VideohubClient.cs`, `tests/Bmd.Tests/Devices/Videohub/VideohubClientTests.cs`

**Interfaces:**
- Consumes: `BlockAccumulator`, `DumpParser` (+ `RequiredHeaders`), `VideohubState`, `FakeVideohub`.
- Produces (namespace `Bmd.Devices.Videohub`):
  - `sealed class VideohubClient : IAsyncDisposable` with `static Task<VideohubClient> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)` and `VideohubState State { get; }`. Throws `TimeoutException` (connect or dump-read exceeding `timeout`), `SocketException` (refused/unreachable), `VideohubProtocolException` (bad dump). Dump is complete at `END PRELUDE` or once all `DumpParser.RequiredHeaders` have arrived (older firmware sends no END PRELUDE).

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/VideohubClientTests.cs`:

```csharp
using System.Net.Sockets;
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubClientTests
{
    static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Connect_ReadsDumpIntoState()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        Assert.Equal("Blackmagic Smart Videohub", client.State.Device.ModelName);
        Assert.Equal(4, client.State.GetRoute(1));
        Assert.Equal("Program", client.State.GetOutputLabel(1));
    }

    [Fact]
    public async Task Connect_WithoutEndPrelude_CompletesOnRequiredBlocks()
    {
        var dump = Fixtures.Dump4x4.Replace("END PRELUDE:\n\n", "");
        await using var fake = FakeVideohub.Start(dump);
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        Assert.Equal(LockState.Owned, client.State.GetLock(2));
    }

    [Fact]
    public async Task Connect_Refused_ThrowsSocketException()
    {
        // an ephemeral port with no listener
        await Assert.ThrowsAsync<SocketException>(
            () => VideohubClient.ConnectAsync("127.0.0.1", 1, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Connect_IncompleteDump_TimesOut()
    {
        // fake serves only the preamble: required blocks never arrive → dump read must time out
        await using var fake = FakeVideohub.Start("PROTOCOL PREAMBLE:\nVersion: 2.8\n\n");
        await Assert.ThrowsAsync<TimeoutException>(
            () => VideohubClient.ConnectAsync("127.0.0.1", fake.Port, TimeSpan.FromMilliseconds(500)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubClientTests`
Expected: compilation failure.

- [ ] **Step 3: Implement `src/Bmd/Devices/Videohub/VideohubClient.cs`**

```csharp
using System.Net.Sockets;
using System.Text;

namespace Bmd.Devices.Videohub;

/// <summary>Async client for the Videohub Ethernet Protocol (text over TCP 9990).
/// Connects, reads the device's initial state dump, exposes it as 1-based state.</summary>
public sealed class VideohubClient : IAsyncDisposable
{
    readonly TcpClient _tcp;

    public VideohubState State { get; }

    VideohubClient(TcpClient tcp, VideohubState state)
    {
        _tcp = tcp;
        State = state;
    }

    public static async Task<VideohubClient> ConnectAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            var state = await ReadDumpAsync(tcp, cts.Token);
            return new VideohubClient(tcp, state);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"timed out talking to {host}:{port} after {timeout.TotalSeconds:0.#}s");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    static async Task<VideohubState> ReadDumpAsync(TcpClient tcp, CancellationToken ct)
    {
        using var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8, leaveOpen: true);
        var acc = new BlockAccumulator();
        var blocks = new List<ProtocolBlock>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (acc.Add(line) is not { } block) continue;
            blocks.Add(block);
            seen.Add(block.Header);
            if (block.Header == "END PRELUDE" || DumpParser.RequiredHeaders.All(seen.Contains))
                return DumpParser.Parse(blocks);
        }
        throw new VideohubProtocolException("connection closed before the device dump completed");
    }

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

Note: `StreamReader` disposal with `leaveOpen: true` keeps the socket usable for milestone 3+ (writes); today nothing else reads it.

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`
Expected: all pass. If `Connect_Refused_ThrowsSocketException` proves flaky because port 1 filters instead of refusing on some machines, bind-and-close a listener to obtain a genuinely dead ephemeral port inside the test — do not loosen the assertion.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: VideohubClient connect/timeout/dump"
```

---

### Task 6: `videohub info` + connection resolution + wiring

**Files:**
- Create: `src/Bmd/Commands/Videohub/VideohubCommands.cs`, `src/Bmd/Commands/Videohub/VideohubResults.cs`, `tests/Bmd.Tests/Commands/VideohubCommandsTests.cs`
- Modify: `src/Bmd/Program.cs` (register `videohub info`), `src/Bmd/Output/BmdJsonContext.cs` (register `VideohubInfoResult`)

**Interfaces:**
- Consumes: `VideohubClient`, `VideohubState`, `ConfigStore`, `ConfigKey`, `BmdJsonContext`, `FakeVideohub` (tests).
- Produces (namespace `Bmd.Commands.Videohub`):
  - `sealed record VideohubInfoResult(string ModelName, string? FriendlyName, string ProtocolVersion, int VideoInputs, int VideoOutputs)`
  - `class VideohubCommands` with ctors `()` and `(Func<ConfigStore> loadConfig)` (same seam as ConfigCommands) and `Task<int> Info(string? host = null, int? port = null, int? timeout = null, bool json = false)`
  - Private helper `Task<int> WithClientAsync(string? host, int? port, int? timeout, Func<VideohubClient, int> action)` that later tasks' commands reuse: resolves host (flag → config `videohub.host` → error+hint, exit 1), port (flag → config `videohub.port` → 9990), timeout seconds (flag → config `videohub.timeout` → 5); invalid numeric config value → `error: config videohub.port is not a number: '<v>'`, exit 1; catches `SocketException`, `IOException`, `TimeoutException`, `VideohubProtocolException` → `error: {message}` on stderr, exit 1.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Commands/VideohubCommandsTests.cs`:

```csharp
using System.Text.Json;
using Bmd.Commands.Videohub;
using Bmd.Config;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubCommandsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");

    public VideohubCommandsTests()
    {
        Directory.CreateDirectory(WorkDir);
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Directory.Delete(_root, recursive: true);
    }

    VideohubCommands Commands() => new(() => ConfigStore.Load(GlobalPath, WorkDir));

    [Fact]
    public async Task Info_Json_ReportsDeviceFields()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().Info(host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal("Blackmagic Smart Videohub", root.GetProperty("modelName").GetString());
        Assert.Equal("Test Hub", root.GetProperty("friendlyName").GetString());
        Assert.Equal("2.8", root.GetProperty("protocolVersion").GetString());
        Assert.Equal(4, root.GetProperty("videoInputs").GetInt32());
        Assert.Equal(4, root.GetProperty("videoOutputs").GetInt32());
    }

    [Fact]
    public async Task Info_Human_PrintsReadableFields()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().Info(host: "127.0.0.1", port: fake.Port));
        var text = _stdout.ToString();
        Assert.Contains("Blackmagic Smart Videohub", text);
        Assert.Contains("4", text);
    }

    [Fact]
    public async Task Info_HostFromConfig()
    {
        await using var fake = FakeVideohub.Start();
        var commands = Commands();
        var store = ConfigStore.Load(GlobalPath, WorkDir);
        Assert.True(ConfigKey.TryParse("videohub.host", out var hostKey));
        Assert.True(ConfigKey.TryParse("videohub.port", out var portKey));
        store.Set(hostKey, "127.0.0.1", global: false);
        store.Set(portKey, fake.Port.ToString(), global: false);
        Assert.Equal(0, await commands.Info(json: true));
    }

    [Fact]
    public async Task Info_NoHostAnywhere_Exit1_WithHint()
    {
        Assert.Equal(1, await Commands().Info());
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("no host configured", _stderr.ToString());
        Assert.Contains("bmd config set videohub.host", _stderr.ToString());
    }

    [Fact]
    public async Task Info_ConnectionRefused_Exit1_CleanError()
    {
        Assert.Equal(1, await Commands().Info(host: "127.0.0.1", port: 1, timeout: 2));
        Assert.Equal("", _stdout.ToString());
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    [Fact]
    public async Task Info_InvalidPortInConfig_Exit1()
    {
        var store = ConfigStore.Load(GlobalPath, WorkDir);
        Assert.True(ConfigKey.TryParse("videohub.host", out var hostKey));
        Assert.True(ConfigKey.TryParse("videohub.port", out var portKey));
        store.Set(hostKey, "127.0.0.1", global: false);
        store.Set(portKey, "not-a-number", global: false);
        Assert.Equal(1, await Commands().Info());
        Assert.Contains("videohub.port", _stderr.ToString());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubCommandsTests`
Expected: compilation failure.

- [ ] **Step 3: Implement**

`src/Bmd/Commands/Videohub/VideohubResults.cs`:

```csharp
namespace Bmd.Commands.Videohub;

public sealed record VideohubInfoResult(
    string ModelName, string? FriendlyName, string ProtocolVersion, int VideoInputs, int VideoOutputs);
```

`src/Bmd/Commands/Videohub/VideohubCommands.cs`:

```csharp
using System.Net.Sockets;
using System.Text.Json;
using Bmd.Config;
using Bmd.Devices.Videohub;
using Bmd.Output;

namespace Bmd.Commands.Videohub;

/// <summary>bmd videohub — control a Blackmagic Videohub over the network.</summary>
public class VideohubCommands
{
    readonly Func<ConfigStore> _loadConfig;

    public VideohubCommands() : this(ConfigStore.LoadDefault) { }
    public VideohubCommands(Func<ConfigStore> loadConfig) => _loadConfig = loadConfig;

    /// <summary>Show device information (model, protocol version, input/output counts).</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Info(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => WithClientAsync(host, port, timeout, client =>
        {
            var device = client.State.Device;
            if (json)
            {
                var result = new VideohubInfoResult(
                    device.ModelName, device.FriendlyName, device.ProtocolVersion,
                    device.VideoInputs, device.VideoOutputs);
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.VideohubInfoResult));
            }
            else
            {
                Console.WriteLine($"Model:          {device.ModelName}");
                if (device.FriendlyName is not null)
                    Console.WriteLine($"Friendly name:  {device.FriendlyName}");
                Console.WriteLine($"Protocol:       {device.ProtocolVersion}");
                Console.WriteLine($"Video inputs:   {device.VideoInputs}");
                Console.WriteLine($"Video outputs:  {device.VideoOutputs}");
            }
            return 0;
        });

    async Task<int> WithClientAsync(string? host, int? port, int? timeout, Func<VideohubClient, int> action)
    {
        try
        {
            var store = _loadConfig();
            var resolvedHost = host ?? GetConfig(store, "videohub.host");
            if (resolvedHost is null)
            {
                Console.Error.WriteLine("error: no host configured for videohub (run: bmd config set videohub.host <addr>)");
                return 1;
            }
            var resolvedPort = port ?? GetConfigInt(store, "videohub.port") ?? 9990;
            var resolvedTimeout = timeout ?? GetConfigInt(store, "videohub.timeout") ?? 5;
            await using var client = await VideohubClient.ConnectAsync(
                resolvedHost, resolvedPort, TimeSpan.FromSeconds(resolvedTimeout));
            return action(client);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException
                                       or TimeoutException or VideohubProtocolException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (ConfigValueFormatException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static string? GetConfig(ConfigStore store, string key)
    {
        ConfigKey.TryParse(key, out var parsed);
        return store.GetEffective(parsed);
    }

    static int? GetConfigInt(ConfigStore store, string key)
    {
        var value = GetConfig(store, key);
        if (value is null) return null;
        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new ConfigValueFormatException($"config {key} is not a number: '{value}'");
    }

    sealed class ConfigValueFormatException(string message) : Exception(message);
}
```

Register in `src/Bmd/Program.cs` (delegate style, same as config): create `var videohub = new VideohubCommands();` and `app.Add("videohub info", videohub.Info);`.

Add `[JsonSerializable(typeof(VideohubInfoResult))]` (with `using Bmd.Commands.Videohub;`) to `BmdJsonContext`.

- [ ] **Step 4: Run all tests to verify they pass, then check help**

Run: `dotnet test`, then `dotnet run --project src/Bmd -- videohub info --help`
Expected: tests pass; help documents host/port/timeout (with defaults and units) and --json.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: bmd videohub info with config-resolved connection"
```

---

### Task 7: `input list`, `output list`, `route list` + table output

**Files:**
- Create: `src/Bmd/Output/Table.cs`, `tests/Bmd.Tests/Output/TableTests.cs`
- Modify: `src/Bmd/Commands/Videohub/VideohubCommands.cs` (three new commands), `src/Bmd/Commands/Videohub/VideohubResults.cs` (three records), `src/Bmd/Output/BmdJsonContext.cs` (register arrays), `src/Bmd/Program.cs` (register commands), `tests/Bmd.Tests/Commands/VideohubCommandsTests.cs` (new tests)

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `Bmd.Output.Table`: `static void Write(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)` — left-aligned columns, two-space gap, uppercase headers as given, no borders, one line per row to `Console.WriteLine`
  - Records in `Bmd.Commands.Videohub`: `VideohubInputEntry(int N, string Label)`, `VideohubOutputEntry(int N, string Label, int Input, string InputLabel, string Lock)` (`Lock` ∈ `"unlocked"|"owned"|"locked"`), `VideohubRouteEntry(int Output, string OutputLabel, int Input, string InputLabel)`
  - Commands on `VideohubCommands` (same option set as `Info`): `Task<int> InputList(...)`, `Task<int> OutputList(...)`, `Task<int> RouteList(...)`

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Output/TableTests.cs`:

```csharp
using Bmd.Output;

namespace Bmd.Tests.Output;

[Collection("console")]
public class TableTests : IDisposable
{
    readonly StringWriter _stdout = new();
    readonly TextWriter _origOut = Console.Out;

    public TableTests() => Console.SetOut(_stdout);
    public void Dispose() => Console.SetOut(_origOut);

    [Fact]
    public void Write_AlignsColumnsToWidestCell()
    {
        Table.Write(["N", "LABEL"], [["1", "Cam 1"], ["10", "X"]]);
        var lines = _stdout.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal("N   LABEL", lines[0]);
        Assert.Equal("1   Cam 1", lines[1]);
        Assert.Equal("10  X", lines[2]);
    }

    [Fact]
    public void Write_NoRows_PrintsHeaderOnly()
    {
        Table.Write(["A"], []);
        Assert.Equal("A", _stdout.ToString().TrimEnd());
    }
}
```

Add to `tests/Bmd.Tests/Commands/VideohubCommandsTests.cs`:

```csharp
    [Fact]
    public async Task InputList_Json_OneBasedEntries()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().InputList(host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(4, root.GetArrayLength());
        var first = root[0];
        Assert.Equal(1, first.GetProperty("n").GetInt32());
        Assert.Equal("Cam 1", first.GetProperty("label").GetString());
    }

    [Fact]
    public async Task OutputList_Json_IncludesRouteAndLock()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().OutputList(host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        var first = root[0]; // output 1: wire route 0←3, lock U
        Assert.Equal(1, first.GetProperty("n").GetInt32());
        Assert.Equal("Program", first.GetProperty("label").GetString());
        Assert.Equal(4, first.GetProperty("input").GetInt32());
        Assert.Equal("Cam 4", first.GetProperty("inputLabel").GetString());
        Assert.Equal("unlocked", first.GetProperty("lock").GetString());
        Assert.Equal("owned", root[1].GetProperty("lock").GetString());
        Assert.Equal("locked", root[2].GetProperty("lock").GetString());
    }

    [Fact]
    public async Task RouteList_Json_OneBasedBothSides()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().RouteList(host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        var first = root[0];
        Assert.Equal(1, first.GetProperty("output").GetInt32());
        Assert.Equal("Program", first.GetProperty("outputLabel").GetString());
        Assert.Equal(4, first.GetProperty("input").GetInt32());
        Assert.Equal("Cam 4", first.GetProperty("inputLabel").GetString());
    }

    [Fact]
    public async Task RouteList_Human_IsTable()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().RouteList(host: "127.0.0.1", port: fake.Port));
        var text = _stdout.ToString();
        Assert.Contains("OUT", text);
        Assert.Contains("Program", text);
        Assert.Contains("Cam 4", text);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "TableTests|VideohubCommandsTests"`
Expected: compilation failure.

- [ ] **Step 3: Implement**

`src/Bmd/Output/Table.cs`:

```csharp
namespace Bmd.Output;

/// <summary>Minimal aligned-column table: uppercase headers, two-space gap, no borders.</summary>
public static class Table
{
    public static void Write(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var widths = new int[headers.Count];
        for (var c = 0; c < headers.Count; c++)
        {
            widths[c] = headers[c].Length;
            foreach (var row in rows) widths[c] = Math.Max(widths[c], row[c].Length);
        }
        Console.WriteLine(Format(headers, widths));
        foreach (var row in rows) Console.WriteLine(Format(row, widths));
    }

    static string Format(IReadOnlyList<string> cells, int[] widths)
    {
        var parts = new string[cells.Count];
        for (var c = 0; c < cells.Count; c++)
            parts[c] = c == cells.Count - 1 ? cells[c] : cells[c].PadRight(widths[c]);
        return string.Join("  ", parts).TrimEnd();
    }
}
```

Add to `VideohubResults.cs`:

```csharp
public sealed record VideohubInputEntry(int N, string Label);
public sealed record VideohubOutputEntry(int N, string Label, int Input, string InputLabel, string Lock);
public sealed record VideohubRouteEntry(int Output, string OutputLabel, int Input, string InputLabel);
```

Register `VideohubInputEntry[]`, `VideohubOutputEntry[]`, `VideohubRouteEntry[]` in `BmdJsonContext`.

Add to `VideohubCommands` (all reuse `WithClientAsync`; XML docs follow the `Info` pattern — every command documents 1-based numbering, defaults, and `--json`):

```csharp
    /// <summary>List inputs (1-based) with their labels.</summary>
    public Task<int> InputList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => WithClientAsync(host, port, timeout, client =>
        {
            var device = client.State.Device;
            var entries = Enumerable.Range(1, device.VideoInputs)
                .Select(n => new VideohubInputEntry(n, client.State.GetInputLabel(n))).ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.VideohubInputEntryArray));
            else
                Table.Write(["N", "LABEL"], entries.Select(e => (IReadOnlyList<string>)[e.N.ToString(), e.Label]).ToArray());
            return 0;
        });

    /// <summary>List outputs (1-based) with label, routed input, and lock state.</summary>
    public Task<int> OutputList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            var entries = Enumerable.Range(1, state.Device.VideoOutputs)
                .Select(n => new VideohubOutputEntry(
                    n, state.GetOutputLabel(n), state.GetRoute(n),
                    state.GetInputLabel(state.GetRoute(n)), LockWord(state.GetLock(n))))
                .ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.VideohubOutputEntryArray));
            else
                Table.Write(["N", "LABEL", "INPUT", "INPUT LABEL", "LOCK"],
                    entries.Select(e => (IReadOnlyList<string>)
                        [e.N.ToString(), e.Label, e.Input.ToString(), e.InputLabel, e.Lock]).ToArray());
            return 0;
        });

    /// <summary>List the current routing (1-based): which input feeds each output.</summary>
    public Task<int> RouteList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            var entries = Enumerable.Range(1, state.Device.VideoOutputs)
                .Select(n => new VideohubRouteEntry(
                    n, state.GetOutputLabel(n), state.GetRoute(n), state.GetInputLabel(state.GetRoute(n))))
                .ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.VideohubRouteEntryArray));
            else
                Table.Write(["OUT", "OUTPUT LABEL", "IN", "INPUT LABEL"],
                    entries.Select(e => (IReadOnlyList<string>)
                        [e.Output.ToString(), e.OutputLabel, e.Input.ToString(), e.InputLabel]).ToArray());
            return 0;
        });

    static string LockWord(LockState lockState) => lockState switch
    {
        LockState.Owned => "owned",
        LockState.Locked => "locked",
        _ => "unlocked",
    };
```

(Full XML doc comments — including `<param>` tags matching `Info`'s — are required on all three; the snippets above elide them for brevity, the implementation must not.)

Register in `Program.cs`:

```csharp
app.Add("videohub input list", videohub.InputList);
app.Add("videohub output list", videohub.OutputList);
app.Add("videohub route list", videohub.RouteList);
```

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: videohub input/output/route list with tables and --json"
```

---

### Task 8: Milestone proof — AOT publish + help audit

**Files:**
- Modify: none expected (csproj only if the publish surfaces warnings)

**Interfaces:**
- Consumes: everything.
- Produces: the milestone exit criterion — a native binary exposing the new commands with complete help and correct failure behavior.

- [ ] **Step 1: Full suite + publish**

```powershell
dotnet test
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/Bmd -c Release -r win-x64
```

Expected: all tests pass; publish clean with zero IL2xxx/IL3xxx warnings (fix, never suppress).

- [ ] **Step 2: Native smoke + help audit**

```powershell
$exe = "src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe"
& $exe --help                       # lists config + videohub command groups
& $exe videohub --help              # lists info / input list / output list / route list
& $exe videohub info --help         # documents host/port/timeout defaults, units, --json
& $exe videohub route list --help
& $exe videohub info --host 127.0.0.1 --port 1 --timeout 2; $LASTEXITCODE   # expect error: + exit 1, no stack trace
& $exe config list --json           # expect [] or entries as JSON
```

Audit each help screen against the "Help is the API" constraint: every argument documented, 1-based numbering stated where numbers appear, defaults visible. Fix any gaps (doc comments) before committing.

- [ ] **Step 3: Commit**

```powershell
git add -A; git commit -m "chore: prove milestone 2 (read path + agent JSON) on Native AOT" --allow-empty
```

---

## Self-Review Notes

- **Spec coverage (milestone 2):** `--json` retrofit incl. mutating commands (Task 1); protocol blocks (Task 2); dump parse + 1-based boundary (Task 3); fake server (Task 4); client with END PRELUDE fallback + timeout semantics (Task 5); config-resolved connection, missing-host hint, `info` (Task 6); the three list commands with tables + JSON (Task 7); AOT + help-contract proof (Task 8). Watch/mutations/export are later milestones by design.
- **Type consistency:** `BmdJsonContext.Default.<TypeName>` properties follow source-gen naming (`VideohubInputEntryArray` for `VideohubInputEntry[]`); `DumpParser.RequiredHeaders` consumed in Task 5; `Fixtures.Dump4x4` consumed by Tasks 4-7; `WithClientAsync` signature consistent across Tasks 6-7; lock words `unlocked|owned|locked` consistent between Task 7 code and spec examples.
- **Known API risk (documented for implementers):** ConsoleAppFramework's handling of `int?` optional flags and `Task<int>` delegate registration is expected to work on 5.7.13; if it doesn't, adapt minimally (e.g. `int port = -1` sentinel) and record the deviation — the controller rules on it.
