# Milestone 10: MultiView Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `bmd multiview` — full control of a Blackmagic MultiView 4, including the `CONFIGURATION` block (layout, format, solo, display toggles) that no existing command can reach.

**Architecture:** The MultiView speaks the same Videohub Ethernet Protocol on the same port, so there is almost no new protocol work. `DumpParser` stops discarding unrecognised blocks; a typed `MultiViewConfiguration` reads and writes the one MultiView-specific block; the connection/backup/error plumbing is extracted from `VideohubCommands` into a `DeviceSession` parameterised by config section; and a new thin `MultiViewCommands` group provides MultiView vocabulary (**views**, not outputs) on top.

**Tech Stack:** .NET 10, Native AOT, ConsoleAppFramework v5, System.Text.Json source generators, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` — sections "MultiView", "Command grammar", "Agents and scripting", "Device discovery", "Snapshot, export, restore".

## Global Constraints

Copied from `CLAUDE.md` and the spec. Every task's requirements implicitly include this section.

- **Native AOT always on.** No reflection, no `dynamic`, no reflection-based JSON, **no new NuGet packages**.
- **JSON: System.Text.Json source generators only.** `--json` output goes through `BmdJsonContext`; snapshots through `SnapshotJsonContext`. The context-less `JsonSerializer.Serialize(object)` / `Deserialize<T>(string)` overloads are forbidden.
- **User-facing numbering is 1-based.** The wire is 0-based. Conversion happens only at the public boundary of `Devices/Videohub/`. Everything above is 1-based.
- **No environment variables for configuration.** Config comes from the layered store: flag > local `.bmdconfig` > global > built-in default.
- **Layering:** `Devices/` never references ConsoleAppFramework or `Commands/`. Command classes stay thin.
- **Agent-first:** every command supports `--json` — exactly one JSON document on stdout, camelCase, stable field names. `--json` changes representation, never behavior. Errors stay plain `error:` on stderr regardless.
- **Help text is the API.** Every command, argument and flag documented via XML doc comments, with units and ranges.
- **Errors:** one clear message to stderr, no stack traces. **Exit codes: 0 success, 1 operation failure, 2 usage/format error.**
- **Mutations back up first.** Every device-changing command snapshots pre-change state from the dump already in memory; a failed backup aborts with exit 1; the backup path is reported in both human and `--json` output.
- **TDD.** Failing test, observe the failure, implement, observe the pass.
- **The site ships with the change.** Any commit adding or changing a command updates `site/` in the same change.

## Hard constraints specific to this milestone

- **Do not touch `192.168.4.96`.** The user's MultiView is in use. Everything is built and tested against the in-process fake. No task may connect to it, read or write.
- **bmd never whitelists `Layout` or `Output format` values.** The `CONFIGURATION` block is undocumented by Blackmagic; valid values vary by model and firmware. bmd sends the value and surfaces the device's `NAK`. Help text gives observed values as *examples* and states the device is the authority. **Booleans are validated by bmd** (`on`/`off`), since those are unambiguous.
- **`bmd videohub` must keep working against a MultiView**, unwarned and undocumented. Several existing tests and the user's own scripts depend on it.
- **The MultiView noun is "view", not "output"** — in command names, table headers, JSON field names and help text.

## Reference: the captured device dump

Taken read-only from the real MultiView 4 (firmware 2.2.5). This is the fixture. Reproduce it byte-exactly in Task 4.

```
PROTOCOL PREAMBLE:
Version: 2.8

VIDEOHUB DEVICE:
Device present: true
Model name: Blackmagic MultiView 4
Friendly name: AV Multiview
Unique ID: 7C2E0D11C751
Video inputs: 4
Video processing units: 0
Video outputs: 6
Video monitoring outputs: 0
Serial ports: 0

INPUT LABELS:
0 Stream
1 Screens
2 Presenter
3 Confidence

OUTPUT LABELS:
0 View 1
1 View 2
2 View 3
3 View 4
4 Solo Input
5 Audio Input

VIDEO OUTPUT LOCKS:
0 U
1 U
2 U
3 U
4 U
5 U

VIDEO OUTPUT ROUTING:
0 0
1 1
2 2
3 3
4 2
5 0

CONFIGURATION:
Layout: 2x2
Output format: 1080i5994
Solo enabled: false
Widescreen SD enabled: true
Display border: true
Display labels: true
Display audio meters: false
Display SDI tally: false
Take Mode: true

END PRELUDE:

```

---

## File structure

**Created:**

| File | Responsibility |
|---|---|
| `src/Bmd/Devices/MultiView/MultiViewConfiguration.cs` | Typed read/write of the `CONFIGURATION` block. Pure. |
| `src/Bmd/Commands/DeviceSession.cs` | Connect / backup / error-mapping plumbing, parameterised by config section. |
| `src/Bmd/Commands/MultiView/MultiViewCommands.cs` | The `bmd multiview` group. |
| `src/Bmd/Commands/MultiView/MultiViewResults.cs` | `--json` result records in view vocabulary. |
| `site/multiview.html` | The MultiView guide page. |
| `tests/Bmd.Tests/Devices/MultiView/MultiViewConfigurationTests.cs` | |
| `tests/Bmd.Tests/Commands/MultiViewCommandsTests.cs` | |
| `tests/Bmd.Tests/Commands/MultiViewConfigCommandsTests.cs` | |

**Modified:** `VideohubState.cs` (extra blocks), `DumpParser.cs` (retain + update them), `VideohubSnapshot.cs` (+ optional configuration), `SnapshotJsonContext.cs`, `BmdJsonContext.cs`, `VideohubCommands.cs` (delegate plumbing), `Program.cs`, `GroupHelp.cs`, `DiscoveredDevice.cs`, `FakeVideohub.cs`, `Fixtures.cs`, `site/index.html`, `CLAUDE.md`.

---

### Task 1: Retain unrecognised protocol blocks

The MultiView's `CONFIGURATION` block currently reaches `DumpParser` and is thrown away by the `default:` case. Keep it, generically — every future device block arrives the same way.

**Files:**
- Modify: `src/Bmd/Devices/Videohub/VideohubState.cs`
- Modify: `src/Bmd/Devices/Videohub/DumpParser.cs`
- Test: `tests/Bmd.Tests/Devices/Videohub/DumpParserTests.cs` (append)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `VideohubState.ExtraBlocks` — `IReadOnlyDictionary<string, IReadOnlyList<string>>`, keyed by block header with no trailing colon, ordinal comparison. Empty for a plain Videohub.
  - `VideohubState.WithExtraBlock(string header, IReadOnlyList<string> lines)` — `internal`, returns a new state with that block replaced or added.
  - `DumpParser.RecognisedHeaders` — `IReadOnlyList<string>`, the headers that are parsed into typed state and therefore never land in `ExtraBlocks`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Bmd.Tests/Devices/Videohub/DumpParserTests.cs`, inside the existing class (keep its namespace, usings and class name):

```csharp
    const string MultiViewConfigBlock =
        "CONFIGURATION:\n" +
        "Layout: 2x2\n" +
        "Output format: 1080i5994\n" +
        "Solo enabled: false\n" +
        "Take Mode: true\n\n";

    [Fact]
    public void Parse_KeepsUnrecognisedBlocks()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4 + MultiViewConfigBlock));

        Assert.True(state.ExtraBlocks.ContainsKey("CONFIGURATION"));
        Assert.Equal(
            ["Layout: 2x2", "Output format: 1080i5994", "Solo enabled: false", "Take Mode: true"],
            state.ExtraBlocks["CONFIGURATION"]);
    }

    [Fact]
    public void Parse_LeavesExtraBlocksEmptyForAPlainVideohub()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));
        Assert.Empty(state.ExtraBlocks);
    }

    [Fact]
    public void Parse_DoesNotTreatPreambleOrEndPreludeAsExtra()
    {
        // Both appear in every real dump and are already accounted for; neither is device-specific.
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4 + MultiViewConfigBlock));
        Assert.DoesNotContain("PROTOCOL PREAMBLE", state.ExtraBlocks.Keys);
        Assert.DoesNotContain("END PRELUDE", state.ExtraBlocks.Keys);
    }

    [Fact]
    public void ApplyUpdate_ReplacesAnExtraBlockWholesale()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4 + MultiViewConfigBlock));
        var update = BlockReader.ReadBlocks("CONFIGURATION:\nLayout: 3x1\n\n")[0];

        var updated = DumpParser.ApplyUpdate(state, update);

        // The device pushes the properties that changed, so the block is replaced, not merged.
        Assert.Equal(["Layout: 3x1"], updated.ExtraBlocks["CONFIGURATION"]);
        Assert.Equal(["Layout: 2x2", "Output format: 1080i5994", "Solo enabled: false", "Take Mode: true"],
            state.ExtraBlocks["CONFIGURATION"]); // original untouched
    }

    [Fact]
    public void ApplyUpdate_AddsAnExtraBlockThatWasNotInTheDump()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));
        var update = BlockReader.ReadBlocks("CONFIGURATION:\nLayout: 2x2\n\n")[0];

        var updated = DumpParser.ApplyUpdate(state, update);

        Assert.Equal(["Layout: 2x2"], updated.ExtraBlocks["CONFIGURATION"]);
    }

    [Theory]
    [InlineData("ACK")]
    [InlineData("NAK")]
    public void ApplyUpdate_NeverStoresAcknowledgements(string header)
    {
        // ACK/NAK are header-only control blocks handled by the client, not device state.
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));
        var updated = DumpParser.ApplyUpdate(state, new ProtocolBlock(header, []));
        Assert.Empty(updated.ExtraBlocks);
    }

    [Fact]
    public void ApplyUpdate_StillLeavesTypedStateAloneForAnExtraBlock()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));
        var updated = DumpParser.ApplyUpdate(state, BlockReader.ReadBlocks("CONFIGURATION:\nLayout: 2x2\n\n")[0]);

        Assert.Equal(state.GetRoute(1), updated.GetRoute(1));
        Assert.Equal(state.GetInputLabel(1), updated.GetInputLabel(1));
        Assert.Equal(state.GetLock(1), updated.GetLock(1));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~DumpParserTests`
Expected: FAIL — compile error, `VideohubState.ExtraBlocks` does not exist.

- [ ] **Step 3: Add ExtraBlocks to VideohubState**

In `src/Bmd/Devices/Videohub/VideohubState.cs`, add the field, constructor parameter, property and wither. Replace the fields/constructor/withers region with:

```csharp
    readonly string[] _inputLabels;
    readonly string[] _outputLabels;
    readonly int[] _routes;      // 0-based: _routes[out] = in
    readonly LockState[] _locks;
    readonly Dictionary<string, IReadOnlyList<string>> _extraBlocks;

    public VideohubDeviceInfo Device { get; }

    /// <summary>Blocks the parser does not interpret, kept verbatim and keyed by header (no
    /// trailing colon). A plain Videohub has none; a MultiView carries CONFIGURATION here.
    /// Retaining them rather than discarding them is what lets a device-specific layer above
    /// read a block the protocol layer knows nothing about.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExtraBlocks => _extraBlocks;

    internal VideohubState(
        VideohubDeviceInfo device, string[] inputLabels, string[] outputLabels, int[] routes,
        LockState[] locks, Dictionary<string, IReadOnlyList<string>>? extraBlocks = null)
    {
        Device = device;
        _inputLabels = inputLabels;
        _outputLabels = outputLabels;
        _routes = routes;
        _locks = locks;
        _extraBlocks = extraBlocks ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    }

    public string GetInputLabel(int input) => _inputLabels[CheckIndex(input, Device.VideoInputs, nameof(input))];
    public string GetOutputLabel(int output) => _outputLabels[CheckIndex(output, Device.VideoOutputs, nameof(output))];
    public int GetRoute(int output) => _routes[CheckIndex(output, Device.VideoOutputs, nameof(output))] + 1;
    public LockState GetLock(int output) => _locks[CheckIndex(output, Device.VideoOutputs, nameof(output))];

    internal VideohubState WithInputLabels(string[] inputLabels) =>
        new(Device, inputLabels, _outputLabels, _routes, _locks, _extraBlocks);
    internal VideohubState WithOutputLabels(string[] outputLabels) =>
        new(Device, _inputLabels, outputLabels, _routes, _locks, _extraBlocks);
    internal VideohubState WithRoutes(int[] routes) =>
        new(Device, _inputLabels, _outputLabels, routes, _locks, _extraBlocks);
    internal VideohubState WithLocks(LockState[] locks) =>
        new(Device, _inputLabels, _outputLabels, _routes, locks, _extraBlocks);

    /// <summary>Replaces (or adds) one unrecognised block. The device pushes only the properties
    /// that changed, but it pushes them as a whole block, so this replaces rather than merges.</summary>
    internal VideohubState WithExtraBlock(string header, IReadOnlyList<string> lines)
    {
        var copy = new Dictionary<string, IReadOnlyList<string>>(_extraBlocks, StringComparer.Ordinal)
        {
            [header] = lines,
        };
        return new VideohubState(Device, _inputLabels, _outputLabels, _routes, _locks, copy);
    }
```

- [ ] **Step 4: Retain and update extra blocks in DumpParser**

In `src/Bmd/Devices/Videohub/DumpParser.cs`, add the recognised-header set beneath `RequiredHeaders`:

```csharp
    /// <summary>Headers the parser turns into typed state, plus the protocol's own control and
    /// framing blocks. Anything outside this set is device-specific and is kept verbatim in
    /// <see cref="VideohubState.ExtraBlocks"/> instead of being discarded.</summary>
    public static readonly IReadOnlyList<string> RecognisedHeaders =
        [.. RequiredHeaders, "PROTOCOL PREAMBLE", "END PRELUDE", "ACK", "NAK"];

    static bool IsRecognised(string header)
    {
        foreach (var known in RecognisedHeaders)
            if (string.Equals(known, header, StringComparison.Ordinal)) return true;
        return false;
    }
```

In `Parse`, immediately before the `return new VideohubState(...)` line, collect the extras and pass them in:

```csharp
        var extras = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var block in blocks)
            if (!IsRecognised(block.Header)) extras.TryAdd(block.Header, block.Lines);
        return new VideohubState(device, inputLabels, outputLabels, routes, locks, extras);
```

In `ApplyUpdate`, replace the `default:` case:

```csharp
            default:
                // A block this parser has no typed model for. Keep it so a device-specific layer
                // can read it; control blocks are the client's business, not device state.
                return IsRecognised(block.Header)
                    ? state
                    : state.WithExtraBlock(block.Header, block.Lines);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~DumpParserTests`
Expected: PASS.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS — baseline is **474 tests**; all stay green plus the new ones.

```bash
git add src/Bmd/Devices/Videohub/ tests/Bmd.Tests/Devices/Videohub/DumpParserTests.cs
git commit -m "feat(protocol): retain unrecognised blocks instead of discarding them"
```

---

### Task 2: MultiViewConfiguration

A typed read/write view over the `CONFIGURATION` block. Pure — no sockets, no state.

**Files:**
- Create: `src/Bmd/Devices/MultiView/MultiViewConfiguration.cs`
- Test: `tests/Bmd.Tests/Devices/MultiView/MultiViewConfigurationTests.cs`

**Interfaces:**
- Consumes: nothing (operates on `IReadOnlyList<string>`).
- Produces, all in namespace `Bmd.Devices.MultiView`:
  - `const string BlockHeader = "CONFIGURATION"`
  - `sealed record MultiViewConfiguration(string? Layout, string? OutputFormat, bool? SoloEnabled, bool? WidescreenSdEnabled, bool? DisplayBorder, bool? DisplayLabels, bool? DisplayAudioMeters, bool? DisplaySdiTally, bool? TakeMode, IReadOnlyList<KeyValuePair<string,string>> Raw)`
  - `static MultiViewConfiguration FromLines(IReadOnlyList<string> lines)`
  - `static MultiViewConfiguration Empty { get; }`
  - `static IReadOnlyList<string> LinesFor(string protocolProperty, string value)` — the body of a one-property write block.
  - `static string? ProtocolNameFor(string cliName)` — maps a CLI setting name to its protocol property name.
  - `static bool TryParseOnOff(string text, out bool value)`

Every property is nullable because the block is undocumented and varies by model: absent means "this device did not report it", which must never be confused with `false`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bmd.Tests/Devices/MultiView/MultiViewConfigurationTests.cs`:

```csharp
using Bmd.Devices.MultiView;

namespace Bmd.Tests.Devices.MultiView;

public class MultiViewConfigurationTests
{
    static readonly string[] RealBlock =
    [
        "Layout: 2x2",
        "Output format: 1080i5994",
        "Solo enabled: false",
        "Widescreen SD enabled: true",
        "Display border: true",
        "Display labels: true",
        "Display audio meters: false",
        "Display SDI tally: false",
        "Take Mode: true",
    ];

    [Fact]
    public void FromLines_ReadsEveryPropertyTheRealDeviceReports()
    {
        var config = MultiViewConfiguration.FromLines(RealBlock);

        Assert.Equal("2x2", config.Layout);
        Assert.Equal("1080i5994", config.OutputFormat);
        Assert.False(config.SoloEnabled);
        Assert.True(config.WidescreenSdEnabled);
        Assert.True(config.DisplayBorder);
        Assert.True(config.DisplayLabels);
        Assert.False(config.DisplayAudioMeters);
        Assert.False(config.DisplaySdiTally);
        Assert.True(config.TakeMode);
    }

    [Fact]
    public void FromLines_KeepsEveryPropertyVerbatimInRawIncludingUnknownOnes()
    {
        // The block is undocumented and varies by model; `bmd multiview config` shows everything
        // the device sent, not only the properties this type happens to know about.
        var config = MultiViewConfiguration.FromLines([.. RealBlock, "Some Future Setting: 42"]);

        Assert.Equal(10, config.Raw.Count);
        Assert.Contains(config.Raw, p => p.Key == "Some Future Setting" && p.Value == "42");
        Assert.Equal("Layout", config.Raw[0].Key); // order preserved as received
    }

    [Fact]
    public void FromLines_TreatsAnAbsentPropertyAsUnknownRatherThanFalse()
    {
        var config = MultiViewConfiguration.FromLines(["Layout: 2x2"]);

        Assert.Equal("2x2", config.Layout);
        Assert.Null(config.SoloEnabled);
        Assert.Null(config.TakeMode);
        Assert.Null(config.OutputFormat);
    }

    [Fact]
    public void FromLines_MatchesPropertyNamesCaseInsensitively()
    {
        // The real device sends "Take Mode" with a capital M while every other property is
        // sentence case; matching exactly would silently drop it.
        var config = MultiViewConfiguration.FromLines(["take mode: true", "LAYOUT: 4x1"]);

        Assert.True(config.TakeMode);
        Assert.Equal("4x1", config.Layout);
    }

    [Fact]
    public void FromLines_IgnoresLinesWithNoColon()
    {
        var config = MultiViewConfiguration.FromLines(["Layout: 2x2", "nonsense", ""]);

        Assert.Equal("2x2", config.Layout);
        Assert.Single(config.Raw);
    }

    [Fact]
    public void FromLines_KeepsAValueContainingAColon()
    {
        var config = MultiViewConfiguration.FromLines(["Output format: 1080i59:94"]);
        Assert.Equal("1080i59:94", config.OutputFormat);
    }

    [Fact]
    public void Empty_HasNothingSet()
    {
        Assert.Null(MultiViewConfiguration.Empty.Layout);
        Assert.Empty(MultiViewConfiguration.Empty.Raw);
    }

    [Fact]
    public void LinesFor_ProducesASinglePropertyBlockBody()
    {
        Assert.Equal(["Layout: 3x1"], MultiViewConfiguration.LinesFor("Layout", "3x1"));
    }

    [Theory]
    [InlineData("borders", "Display border")]
    [InlineData("labels", "Display labels")]
    [InlineData("audio-meters", "Display audio meters")]
    [InlineData("tally", "Display SDI tally")]
    [InlineData("take-mode", "Take Mode")]
    [InlineData("widescreen-sd", "Widescreen SD enabled")]
    [InlineData("solo", "Solo enabled")]
    [InlineData("layout", "Layout")]
    [InlineData("format", "Output format")]
    public void ProtocolNameFor_MapsCliNamesToProtocolProperties(string cli, string expected)
    {
        Assert.Equal(expected, MultiViewConfiguration.ProtocolNameFor(cli));
    }

    [Fact]
    public void ProtocolNameFor_ReturnsNullForAnUnknownName()
    {
        Assert.Null(MultiViewConfiguration.ProtocolNameFor("brightness"));
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData("true", true)]
    [InlineData("off", false)]
    [InlineData("false", false)]
    public void TryParseOnOff_AcceptsTheDocumentedSpellings(string text, bool expected)
    {
        Assert.True(MultiViewConfiguration.TryParseOnOff(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("maybe")]
    public void TryParseOnOff_RejectsAnythingElse(string text)
    {
        Assert.False(MultiViewConfiguration.TryParseOnOff(text, out _));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~MultiViewConfigurationTests`
Expected: FAIL — compile error, `MultiViewConfiguration` does not exist.

- [ ] **Step 3: Implement MultiViewConfiguration**

Create `src/Bmd/Devices/MultiView/MultiViewConfiguration.cs`:

```csharp
namespace Bmd.Devices.MultiView;

/// <summary>The MultiView's <c>CONFIGURATION</c> block: layout, output format, and the display
/// and behaviour toggles.
///
/// This block is <b>not documented by Blackmagic</b> — it appears in no published version of the
/// Videohub Ethernet Protocol. Everything here is modelled from a dump captured off a real
/// MultiView 4 running firmware 2.2.5. Two consequences shape this type:
///
/// Every property is nullable, because a device that did not report a property must never be
/// confused with one that reported it as false — a MultiView 16 or a later firmware may send a
/// different set.
///
/// <see cref="Raw"/> keeps every property exactly as received, including ones this type has never
/// heard of, so `bmd multiview config` can show the truth rather than only the fields that
/// happened to be known when this was written.</summary>
public sealed record MultiViewConfiguration(
    string? Layout,
    string? OutputFormat,
    bool? SoloEnabled,
    bool? WidescreenSdEnabled,
    bool? DisplayBorder,
    bool? DisplayLabels,
    bool? DisplayAudioMeters,
    bool? DisplaySdiTally,
    bool? TakeMode,
    IReadOnlyList<KeyValuePair<string, string>> Raw)
{
    /// <summary>The protocol block header, without its trailing colon.</summary>
    public const string BlockHeader = "CONFIGURATION";

    public static MultiViewConfiguration Empty { get; } =
        new(null, null, null, null, null, null, null, null, null, []);

    // Protocol property names, spelled as the device sends them. Matching is case-insensitive
    // because the real device is itself inconsistent: every property is sentence case except
    // "Take Mode".
    const string PropLayout = "Layout";
    const string PropFormat = "Output format";
    const string PropSolo = "Solo enabled";
    const string PropWidescreen = "Widescreen SD enabled";
    const string PropBorder = "Display border";
    const string PropLabels = "Display labels";
    const string PropAudioMeters = "Display audio meters";
    const string PropTally = "Display SDI tally";
    const string PropTakeMode = "Take Mode";

    /// <summary>CLI setting name to protocol property. The CLI names are kebab-case and drop the
    /// "Display" prefix, because `bmd multiview show borders on` reads better than the protocol's
    /// own spelling — but the mapping is explicit so the two vocabularies stay decoupled.</summary>
    static readonly KeyValuePair<string, string>[] CliNames =
    [
        new("layout", PropLayout),
        new("format", PropFormat),
        new("solo", PropSolo),
        new("widescreen-sd", PropWidescreen),
        new("borders", PropBorder),
        new("labels", PropLabels),
        new("audio-meters", PropAudioMeters),
        new("tally", PropTally),
        new("take-mode", PropTakeMode),
    ];

    public static string? ProtocolNameFor(string cliName)
    {
        foreach (var (cli, protocol) in CliNames)
            if (string.Equals(cli, cliName, StringComparison.OrdinalIgnoreCase)) return protocol;
        return null;
    }

    public static MultiViewConfiguration FromLines(IReadOnlyList<string> lines)
    {
        var raw = new List<KeyValuePair<string, string>>(lines.Count);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            raw.Add(new KeyValuePair<string, string>(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        string? Text(string property)
        {
            foreach (var (key, value) in raw)
                if (string.Equals(key, property, StringComparison.OrdinalIgnoreCase)) return value;
            return null;
        }

        bool? Flag(string property) =>
            Text(property) is { } text && TryParseOnOff(text, out var value) ? value : null;

        return new MultiViewConfiguration(
            Text(PropLayout), Text(PropFormat),
            Flag(PropSolo), Flag(PropWidescreen), Flag(PropBorder), Flag(PropLabels),
            Flag(PropAudioMeters), Flag(PropTally), Flag(PropTakeMode),
            raw);
    }

    /// <summary>The body of a block that sets one property. The device accepts a partial
    /// CONFIGURATION block and applies only what it contains.</summary>
    public static IReadOnlyList<string> LinesFor(string protocolProperty, string value) =>
        [$"{protocolProperty}: {value}"];

    /// <summary>Accepts the CLI's <c>on</c>/<c>off</c> and the protocol's own
    /// <c>true</c>/<c>false</c>, so the same parser reads both a user's argument and a device's
    /// reply. Nothing else is accepted: "yes" and "1" are rejected rather than guessed at.</summary>
    public static bool TryParseOnOff(string text, out bool value)
    {
        if (string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~MultiViewConfigurationTests`
Expected: PASS.

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet test`

```bash
git add src/Bmd/Devices/MultiView/ tests/Bmd.Tests/Devices/MultiView/
git commit -m "feat(multiview): typed read/write of the CONFIGURATION block"
```

---

### Task 3: Extract DeviceSession

A pure refactor with **no behavior change**. `VideohubCommands`'s private plumbing — connect, resolve host/port/timeout from config, back up, map failures to one stderr line — is device-agnostic except for three config key strings. Lift it out so `MultiViewCommands` can reuse it.

**Files:**
- Create: `src/Bmd/Commands/DeviceSession.cs`
- Modify: `src/Bmd/Commands/Videohub/VideohubCommands.cs` (private helper region only; every public method keeps its exact signature and body)
- Test: `tests/Bmd.Tests/Commands/DeviceSessionTests.cs`

**Interfaces:**
- Consumes: `ConfigStore`, `BackupStore`, `VideohubClient`, `VideohubSnapshot` (all existing).
- Produces `Bmd.Commands.DeviceSession`:
  - `DeviceSession(string configSection, Func<ConfigStore> loadConfig)`
  - `string ConfigSection { get; }`
  - `Task<int> RunWithClientAsync(string? host, int? port, int? timeout, Func<VideohubClient, Task<int>> action)`
  - `Task<int> WithClientAsync(string? host, int? port, int? timeout, Func<VideohubClient, int> action)`
  - `Task<int> WithBackedUpClientAsync(string? host, int? port, int? timeout, bool noBackup, Func<VideohubClient, string?, Task<int>> action)`
  - `Task<int> WithDeferredBackupClientAsync(string? host, int? port, int? timeout, bool noBackup, Func<VideohubClient, Func<Task<string?>>, Task<int>> action)`
  - `static Task<int> RunCatchingAsync(Func<Task<int>> body)`

**Critical:** the "no host configured" message must remain section-specific and must keep naming the exact command to run. For videohub it must stay byte-identical to today (`error: no host configured for videohub (run: bmd config set videohub.host <addr>)`) — existing tests assert on it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bmd.Tests/Commands/DeviceSessionTests.cs`:

```csharp
using Bmd.Commands;
using Bmd.Config;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class DeviceSessionTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), $"bmd-session-{Guid.NewGuid():N}");
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public DeviceSessionTests()
    {
        Directory.CreateDirectory(_directory);
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    ConfigStore ConfigWith(params string[] lines)
    {
        var path = Path.Combine(_directory, "config");
        if (lines.Length > 0) File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return ConfigStore.Load(path, _directory);
    }

    [Theory]
    [InlineData("videohub")]
    [InlineData("multiview")]
    public async Task RunWithClientAsync_NamesItsOwnSectionWhenNoHostIsConfigured(string section)
    {
        var session = new DeviceSession(section, () => ConfigWith());

        var exit = await session.RunWithClientAsync(null, null, null, _ => Task.FromResult(0));

        Assert.Equal(1, exit);
        Assert.Equal(
            $"error: no host configured for {section} (run: bmd config set {section}.host <addr>)",
            _stderr.ToString().TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task RunWithClientAsync_ReadsHostFromItsOwnSectionOnly()
    {
        // A configured videohub.host must not satisfy a multiview session.
        var session = new DeviceSession("multiview", () => ConfigWith("[videohub]", "host = 10.0.0.1"));

        var exit = await session.RunWithClientAsync(null, null, null, _ => Task.FromResult(0));

        Assert.Equal(1, exit);
        Assert.Contains("multiview.host", _stderr.ToString());
    }

    [Fact]
    public async Task RunWithClientAsync_RejectsANonPositiveTimeoutWithExitTwo()
    {
        var session = new DeviceSession("multiview", () => ConfigWith("[multiview]", "host = 10.0.0.1"));

        var exit = await session.RunWithClientAsync(null, null, 0, _ => Task.FromResult(0));

        Assert.Equal(2, exit);
        Assert.Contains("timeout must be a positive number", _stderr.ToString());
    }

    [Fact]
    public async Task RunCatchingAsync_MapsAnExpectedFailureToOneStderrLineAndExitOne()
    {
        var exit = await DeviceSession.RunCatchingAsync(
            () => throw new TimeoutException("device did not answer"));

        Assert.Equal(1, exit);
        Assert.Equal("error: device did not answer", _stderr.ToString().TrimEnd('\r', '\n'));
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~DeviceSessionTests`
Expected: FAIL — compile error, `DeviceSession` does not exist.

- [ ] **Step 3: Create DeviceSession by moving the helpers verbatim**

Create `src/Bmd/Commands/DeviceSession.cs`. Move the bodies of `RunWithClientAsync`, `WithClientAsync`, `WithBackedUpClientAsync`, `WithDeferredBackupClientAsync`, `RunCatchingAsync`, `GetConfig` and `GetConfigInt` out of `VideohubCommands` **unchanged**, except that the three hardcoded `"videohub.*"` keys and the error message become section-relative:

```csharp
using System.Net.Sockets;
using Bmd.Config;
using Bmd.Devices.Videohub;

namespace Bmd.Commands;

/// <summary>Everything a device command group needs around the edges of its own logic:
/// resolving the connection from flags then config, connecting, taking the pre-mutation backup,
/// and mapping every expected failure to one stderr line plus an exit code.
///
/// Parameterised by config section — `videohub`, `multiview` — because that is genuinely the
/// only thing that differed between the two groups. Both talk the same protocol to the same
/// port with the same client; only the key they read their address from changes.</summary>
public sealed class DeviceSession(string configSection, Func<ConfigStore> loadConfig)
{
    public string ConfigSection { get; } = configSection;

    /// <summary>Synchronous-action convenience over <see cref="RunWithClientAsync"/>.</summary>
    public Task<int> WithClientAsync(string? host, int? port, int? timeout, Func<VideohubClient, int> action)
        => RunWithClientAsync(host, port, timeout, client => Task.FromResult(action(client)));

    /// <summary>Connects, backs up the pre-change state unless disabled, then runs the action.
    /// A failed backup aborts before the action runs.</summary>
    public Task<int> WithBackedUpClientAsync(
        string? host, int? port, int? timeout, bool noBackup,
        Func<VideohubClient, string?, Task<int>> action)
        => RunWithClientAsync(host, port, timeout, async client =>
        {
            string? backupPath = null;
            if (!noBackup)
            {
                var store = BackupStore.FromConfig(loadConfig());
                if (store.AutoBackupEnabled)
                {
                    var snapshot = VideohubSnapshot.FromState(client.State, DateTimeOffset.UtcNow);
                    backupPath = store.Write(
                        BackupStore.DeviceKey(client.Host, client.State.Device.ModelName), snapshot);
                }
            }
            return await action(client, backupPath);
        });

    /// <summary>Connects and hands the action a thunk that writes the pre-change backup on demand.
    /// Call the thunk immediately before the first mutation: a command that turns out to have
    /// nothing to do — or turns out not to apply at all — must not spend a backup slot.
    /// The thunk is idempotent and returns null when backups are disabled.</summary>
    public Task<int> WithDeferredBackupClientAsync(
        string? host, int? port, int? timeout, bool noBackup,
        Func<VideohubClient, Func<Task<string?>>, Task<int>> action)
        => RunWithClientAsync(host, port, timeout, client =>
        {
            string? written = null;
            var done = false;
            Task<string?> Backup()
            {
                if (done) return Task.FromResult(written);
                done = true;
                if (!noBackup)
                {
                    var store = BackupStore.FromConfig(loadConfig());
                    if (store.AutoBackupEnabled)
                    {
                        var snapshot = VideohubSnapshot.FromState(client.State, DateTimeOffset.UtcNow);
                        written = store.Write(
                            BackupStore.DeviceKey(client.Host, client.State.Device.ModelName), snapshot);
                    }
                }
                return Task.FromResult(written);
            }
            return action(client, Backup);
        });

    /// <summary>Resolves the connection from flags then config, connects, runs the action,
    /// and maps every expected failure to one stderr line plus an exit code.</summary>
    public Task<int> RunWithClientAsync(
        string? host, int? port, int? timeout, Func<VideohubClient, Task<int>> action)
        => RunCatchingAsync(async () =>
        {
            var store = loadConfig();
            var resolvedHost = host ?? GetConfig(store, $"{ConfigSection}.host");
            if (resolvedHost is null)
            {
                Console.Error.WriteLine(
                    $"error: no host configured for {ConfigSection} " +
                    $"(run: bmd config set {ConfigSection}.host <addr>)");
                return 1;
            }
            var resolvedPort = port ?? GetConfigInt(store, $"{ConfigSection}.port") ?? 9990;
            var resolvedTimeout = timeout ?? GetConfigInt(store, $"{ConfigSection}.timeout") ?? 5;
            if (resolvedTimeout <= 0)
            {
                Console.Error.WriteLine("error: timeout must be a positive number of seconds");
                return 2;
            }
            await using var client = await VideohubClient.ConnectAsync(
                resolvedHost, resolvedPort, TimeSpan.FromSeconds(resolvedTimeout));
            return await action(client);
        });

    /// <summary>Runs <paramref name="body"/>, mapping every expected failure to one stderr line
    /// plus exit code 1.</summary>
    public static async Task<int> RunCatchingAsync(Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException
                                       or TimeoutException or VideohubProtocolException
                                       or VideohubCommandRejectedException
                                       or SnapshotFormatException or ConfigValueException
                                       or ConfigValueFormatException)
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
}
```

- [ ] **Step 4: Point VideohubCommands at the session**

In `src/Bmd/Commands/Videohub/VideohubCommands.cs`:

1. Replace the field and constructors:

```csharp
    readonly Func<ConfigStore> _loadConfig;
    readonly DeviceSession _session;

    public VideohubCommands() : this(ConfigStore.LoadDefault) { }

    public VideohubCommands(Func<ConfigStore> loadConfig)
    {
        _loadConfig = loadConfig;
        _session = new DeviceSession("videohub", loadConfig);
    }
```

2. **Delete** the private `WithClientAsync`, `WithBackedUpClientAsync`, `WithDeferredBackupClientAsync`, `RunWithClientAsync`, `RunCatchingAsync`, `GetConfig` and `GetConfigInt` members — they now live on `DeviceSession`.

3. Prefix every remaining call to those helpers with `_session.`. There are calls in `Info`, `InputList`, `OutputList`, `RouteList`, `Watch`, `Export`, `Restore`, `RouteSet`, `InputRename`, `OutputRename`, `OutputLock`, `OutputUnlock`. **Change nothing else in those methods.**

4. Re-point the two test seams, keeping their signatures exactly as they are:

```csharp
    /// <summary>Test seam: exercises the backup path with a supplied action.</summary>
    internal Task<int> BackupProbeAsync(
        string host, int port, bool noBackup, Func<VideohubClient, string?, Task<int>> action)
        => _session.WithBackedUpClientAsync(host, port, null, noBackup, action);

    /// <summary>Test seam: runs the shared failure filter directly against a supplied exception.</summary>
    internal Task<int> ThrowingProbeAsync(Exception exception)
        => DeviceSession.RunCatchingAsync(() => throw exception);
```

`_loadConfig` stays on the class because `Export` and `Restore` read `BackupStore.FromConfig(_loadConfig())` directly. If after the edit the compiler reports `_loadConfig` as unused, delete the field — do not keep a dead one.

- [ ] **Step 5: Run the full suite — this is the real test of the refactor**

Run: `dotnet test`
Expected: PASS, **all 474+ existing tests unchanged**. This task adds behavior nowhere; a single existing test failing means the move was not faithful. Do not adjust an existing test to make it pass — fix the refactor.

- [ ] **Step 6: Commit**

```bash
git add src/Bmd/Commands/ tests/Bmd.Tests/Commands/DeviceSessionTests.cs
git commit -m "refactor(commands): extract DeviceSession from VideohubCommands"
```

---

### Task 4: `bmd multiview` read commands

The group appears, with MultiView vocabulary, backed by a fixture of the real device.

**Files:**
- Modify: `tests/Bmd.Tests/Devices/Videohub/Fixtures.cs` (add `DumpMultiView4`)
- Create: `src/Bmd/Commands/MultiView/MultiViewResults.cs`
- Create: `src/Bmd/Commands/MultiView/MultiViewCommands.cs`
- Modify: `src/Bmd/Output/BmdJsonContext.cs`, `src/Bmd/Program.cs`, `src/Bmd/Commands/GroupHelp.cs`
- Test: `tests/Bmd.Tests/Commands/MultiViewCommandsTests.cs`

**Interfaces:**
- Consumes: `DeviceSession` (Task 3), `MultiViewConfiguration` (Task 2), `VideohubState.ExtraBlocks` (Task 1).
- Produces:
  - `Fixtures.DumpMultiView4` — `const string`, the captured dump.
  - `MultiViewInfoResult(string Model, string? FriendlyName, string Protocol, int Inputs, int Views, string? Layout, string? OutputFormat)`
  - `MultiViewInputEntry(int N, string Label)`
  - `MultiViewViewEntry(int N, string Label, int Input, string InputLabel, string Lock)`
  - `MultiViewConfigEntry(string Name, string Value)`
  - `MultiViewCommands` with `Info`, `InputList`, `ViewList`, `Config`, plus `public MultiViewCommands()` and `public MultiViewCommands(Func<ConfigStore> loadConfig)`.
  - `MultiViewCommands.ReadConfiguration(VideohubState)` — `internal static MultiViewConfiguration`, used by Tasks 6 and 7.

- [ ] **Step 1: Add the device fixture**

Append to `tests/Bmd.Tests/Devices/Videohub/Fixtures.cs`, inside the existing static class:

```csharp
    /// <summary>A real Blackmagic MultiView 4 dump (firmware 2.2.5), captured read-only from
    /// hardware. Six "outputs": four multiview windows plus Solo Input and Audio Input. Carries
    /// the CONFIGURATION block, which no Videohub sends.</summary>
    public const string DumpMultiView4 =
        "PROTOCOL PREAMBLE:\nVersion: 2.8\n\n" +
        "VIDEOHUB DEVICE:\n" +
        "Device present: true\n" +
        "Model name: Blackmagic MultiView 4\n" +
        "Friendly name: AV Multiview\n" +
        "Unique ID: 7C2E0D11C751\n" +
        "Video inputs: 4\n" +
        "Video processing units: 0\n" +
        "Video outputs: 6\n" +
        "Video monitoring outputs: 0\n" +
        "Serial ports: 0\n\n" +
        "INPUT LABELS:\n0 Stream\n1 Screens\n2 Presenter\n3 Confidence\n\n" +
        "OUTPUT LABELS:\n0 View 1\n1 View 2\n2 View 3\n3 View 4\n4 Solo Input\n5 Audio Input\n\n" +
        "VIDEO OUTPUT LOCKS:\n0 U\n1 U\n2 U\n3 U\n4 U\n5 U\n\n" +
        "VIDEO OUTPUT ROUTING:\n0 0\n1 1\n2 2\n3 3\n4 2\n5 0\n\n" +
        "CONFIGURATION:\n" +
        "Layout: 2x2\n" +
        "Output format: 1080i5994\n" +
        "Solo enabled: false\n" +
        "Widescreen SD enabled: true\n" +
        "Display border: true\n" +
        "Display labels: true\n" +
        "Display audio meters: false\n" +
        "Display SDI tally: false\n" +
        "Take Mode: true\n\n" +
        "END PRELUDE:\n\n";
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Bmd.Tests/Commands/MultiViewCommandsTests.cs`:

```csharp
using System.Text.Json;
using Bmd.Commands.MultiView;
using Bmd.Config;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class MultiViewCommandsTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), $"bmd-mv-{Guid.NewGuid():N}");
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public MultiViewCommandsTests()
    {
        Directory.CreateDirectory(_directory);
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    MultiViewCommands Commands() =>
        new(() => ConfigStore.Load(Path.Combine(_directory, "config"), _directory));

    [Fact]
    public async Task Info_ReportsViewsAndTheCurrentLayout()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Info("127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        var text = _stdout.ToString();
        Assert.Contains("Blackmagic MultiView 4", text);
        Assert.Contains("AV Multiview", text);
        Assert.Contains("2x2", text);
        Assert.Contains("1080i5994", text);
        // Views, not outputs: the noun is the whole reason this group exists.
        Assert.Contains("Views:", text);
        Assert.DoesNotContain("Video outputs:", text);
    }

    [Fact]
    public async Task Info_Json_EmitsOneDocumentWithViewVocabulary()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Info("127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("Blackmagic MultiView 4", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("inputs").GetInt32());
        Assert.Equal(6, doc.RootElement.GetProperty("views").GetInt32());
        Assert.Equal("2x2", doc.RootElement.GetProperty("layout").GetString());
    }

    [Fact]
    public async Task ViewList_ShowsEachViewItsSourceAndLock()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().ViewList("127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        var text = _stdout.ToString();
        Assert.Contains("VIEW", text);
        Assert.DoesNotContain("OUT ", text);
        Assert.Contains("View 1", text);
        Assert.Contains("Solo Input", text);
        Assert.Contains("Confidence", text);
    }

    [Fact]
    public async Task ViewList_Json_UsesViewNotOutputAsTheFieldName()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        await Commands().ViewList("127.0.0.1", device.Port, json: true);

        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        var first = doc.RootElement[0];
        Assert.Equal(1, first.GetProperty("n").GetInt32());
        Assert.Equal("View 1", first.GetProperty("label").GetString());
        Assert.Equal("Stream", first.GetProperty("inputLabel").GetString());
        Assert.Equal(6, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task InputList_ShowsTheFourSources()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().InputList("127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Contains("Stream", _stdout.ToString());
        Assert.Contains("Confidence", _stdout.ToString());
    }

    [Fact]
    public async Task Config_PrintsEveryPropertyTheDeviceReported()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Config("127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        var text = _stdout.ToString();
        Assert.Contains("Layout", text);
        Assert.Contains("2x2", text);
        Assert.Contains("Take Mode", text);
        Assert.Contains("Display SDI tally", text);
    }

    [Fact]
    public async Task Config_Json_EmitsOneDocumentOfNameValuePairs()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        await Commands().Config("127.0.0.1", device.Port, json: true);

        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal(9, doc.RootElement.GetArrayLength());
        Assert.Equal("Layout", doc.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("2x2", doc.RootElement[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Config_SaysSoWhenTheDeviceSendsNoConfigurationBlock()
    {
        // A plain Videohub answered on the multiview address: report it rather than print nothing.
        await using var device = FakeVideohub.Start(Fixtures.Dump4x4);

        var exit = await Commands().Config("127.0.0.1", device.Port);

        Assert.Equal(1, exit);
        Assert.Contains("no CONFIGURATION", _stderr.ToString());
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task Commands_ReadTheirHostFromTheMultiviewSection()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[multiview]\nhost = 127.0.0.1\nport = {device.Port}\n");

        var exit = await Commands().Info();

        Assert.Equal(0, exit);
        Assert.Contains("Blackmagic MultiView 4", _stdout.ToString());
    }

    [Fact]
    public async Task Commands_ErrorNamesMultiviewWhenNoHostIsConfigured()
    {
        var exit = await Commands().Info();

        Assert.Equal(1, exit);
        Assert.Contains("bmd config set multiview.host", _stderr.ToString());
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~MultiViewCommandsTests`
Expected: FAIL — compile error, `MultiViewCommands` does not exist.

- [ ] **Step 4: Add the result records**

Create `src/Bmd/Commands/MultiView/MultiViewResults.cs`:

```csharp
namespace Bmd.Commands.MultiView;

/// <summary>`bmd multiview info`. The device reports six "video outputs" on the wire; they are
/// the multiview's windows plus its Solo and Audio inputs, so the field is named views.</summary>
public sealed record MultiViewInfoResult(
    string Model, string? FriendlyName, string Protocol, int Inputs, int Views,
    string? Layout, string? OutputFormat);

public sealed record MultiViewInputEntry(int N, string Label);

/// <summary>One multiview window: its label, the source feeding it, and its lock state.</summary>
public sealed record MultiViewViewEntry(int N, string Label, int Input, string InputLabel, string Lock);

/// <summary>One CONFIGURATION property, named exactly as the device spelled it.</summary>
public sealed record MultiViewConfigEntry(string Name, string Value);
```

- [ ] **Step 5: Register them on the JSON context**

In `src/Bmd/Output/BmdJsonContext.cs`, add alongside the existing registrations, and add `using Bmd.Commands.MultiView;` at the top:

```csharp
[JsonSerializable(typeof(MultiViewInfoResult))]
[JsonSerializable(typeof(MultiViewInputEntry[]))]
[JsonSerializable(typeof(MultiViewViewEntry[]))]
[JsonSerializable(typeof(MultiViewConfigEntry[]))]
```

- [ ] **Step 6: Implement the read commands**

Create `src/Bmd/Commands/MultiView/MultiViewCommands.cs`:

```csharp
using System.Text.Json;
using Bmd.Config;
using Bmd.Devices.MultiView;
using Bmd.Devices.Videohub;
using Bmd.Output;

namespace Bmd.Commands.MultiView;

/// <summary>bmd multiview — control a Blackmagic MultiView over the network.
///
/// A MultiView speaks the same Videohub Ethernet Protocol as a router, so this group shares the
/// client and the session plumbing. What differs is vocabulary and one extra protocol block: the
/// six things the wire calls "video outputs" are four multiview windows plus a Solo and an Audio
/// input, so everything a user sees here says <b>view</b>.</summary>
public class MultiViewCommands
{
    readonly DeviceSession _session;

    public MultiViewCommands() : this(ConfigStore.LoadDefault) { }
    public MultiViewCommands(Func<ConfigStore> loadConfig) => _session = new DeviceSession("multiview", loadConfig);

    /// <summary>Reads the CONFIGURATION block out of a connected device's state, or
    /// <see cref="MultiViewConfiguration.Empty"/> when the device never sent one.</summary>
    internal static MultiViewConfiguration ReadConfiguration(VideohubState state) =>
        state.ExtraBlocks.TryGetValue(MultiViewConfiguration.BlockHeader, out var lines)
            ? MultiViewConfiguration.FromLines(lines)
            : MultiViewConfiguration.Empty;

    /// <summary>Show device information: model, protocol version, source and view counts, and the current layout and output format.</summary>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Info(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var device = client.State.Device;
            var config = ReadConfiguration(client.State);
            if (json)
            {
                var result = new MultiViewInfoResult(
                    device.ModelName, device.FriendlyName, device.ProtocolVersion,
                    device.VideoInputs, device.VideoOutputs, config.Layout, config.OutputFormat);
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewInfoResult));
            }
            else
            {
                Console.WriteLine($"Model:          {device.ModelName}");
                if (device.FriendlyName is not null)
                    Console.WriteLine($"Friendly name:  {device.FriendlyName}");
                Console.WriteLine($"Protocol:       {device.ProtocolVersion}");
                Console.WriteLine($"Inputs:         {device.VideoInputs}");
                Console.WriteLine($"Views:          {device.VideoOutputs}");
                if (config.Layout is not null) Console.WriteLine($"Layout:         {config.Layout}");
                if (config.OutputFormat is not null) Console.WriteLine($"Output format:  {config.OutputFormat}");
            }
            return 0;
        });

    /// <summary>List sources (1-based) with their labels.</summary>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> InputList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var device = client.State.Device;
            var entries = Enumerable.Range(1, device.VideoInputs)
                .Select(n => new MultiViewInputEntry(n, client.State.GetInputLabel(n))).ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.MultiViewInputEntryArray));
            else
                Table.Write(["N", "LABEL"], entries.Select(e => (IReadOnlyList<string>)[e.N.ToString(), e.Label]).ToArray());
            return 0;
        });

    /// <summary>List views (1-based) with label, the source feeding each, and lock state. On a MultiView 4 the last two entries are the Solo and Audio inputs rather than windows.</summary>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ViewList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            var entries = Enumerable.Range(1, state.Device.VideoOutputs)
                .Select(n => new MultiViewViewEntry(
                    n, state.GetOutputLabel(n), state.GetRoute(n),
                    state.GetInputLabel(state.GetRoute(n)), VideohubUpdate.Word(state.GetLock(n))))
                .ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.MultiViewViewEntryArray));
            else
                Table.Write(["VIEW", "LABEL", "IN", "SOURCE", "LOCK"],
                    entries.Select(e => (IReadOnlyList<string>)
                        [e.N.ToString(), e.Label, e.Input.ToString(), e.InputLabel, e.Lock]).ToArray());
            return 0;
        });

    /// <summary>Print the device's CONFIGURATION block: layout, output format, and the display and behaviour toggles, exactly as the device reports them. Properties bmd does not recognise are shown too — the block is undocumented and varies by model and firmware.</summary>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="json">Emit the result as a JSON array of name/value pairs on stdout.</param>
    public Task<int> Config(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var config = ReadConfiguration(client.State);
            if (config.Raw.Count == 0)
            {
                Console.Error.WriteLine(
                    $"error: {client.State.Device.ModelName} sent no CONFIGURATION block — " +
                    "it is probably a Videohub rather than a MultiView (try: bmd videohub info)");
                return 1;
            }
            if (json)
            {
                var entries = config.Raw.Select(p => new MultiViewConfigEntry(p.Key, p.Value)).ToArray();
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.MultiViewConfigEntryArray));
            }
            else
            {
                Table.Write(["SETTING", "VALUE"],
                    config.Raw.Select(p => (IReadOnlyList<string>)[p.Key, p.Value]).ToArray());
            }
            return 0;
        });
}
```

- [ ] **Step 7: Register the commands**

In `src/Bmd/Program.cs`, after the `videohub` block, add (and `using Bmd.Commands.MultiView;` at the top):

```csharp
var multiview = new MultiViewCommands();
app.Add("multiview info", multiview.Info);
app.Add("multiview input list", multiview.InputList);
app.Add("multiview view list", multiview.ViewList);
app.Add("multiview config", multiview.Config);
```

In `src/Bmd/Commands/GroupHelp.cs`, add to the `Commands` array after the videohub entries:

```csharp
        new("multiview info", "Show device information (model, protocol version, source and view counts, layout)."),
        new("multiview input list", "List sources (1-based) with their labels."),
        new("multiview view list", "List views (1-based) with label, source, and lock state."),
        new("multiview config", "Print the device's CONFIGURATION block exactly as reported."),
```

and add `"multiview"` to the `Groups` array so `bmd multiview --help` lists the group.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~MultiViewCommandsTests`
Expected: PASS.

- [ ] **Step 9: Verify the group renders and commit**

Run: `dotnet run --project src/Bmd -- multiview --help`
Expected: a filtered listing of the four commands, exit 0.

Run: `dotnet test`
Expected: PASS.

```bash
git add src/Bmd/ tests/Bmd.Tests/
git commit -m "feat(multiview): bmd multiview read commands in view vocabulary"
```

---

### Task 5: `bmd multiview` write commands

Routing, labels and locks, in view vocabulary, on the shared backup path.

**Files:**
- Modify: `src/Bmd/Commands/MultiView/MultiViewCommands.cs`, `src/Bmd/Commands/MultiView/MultiViewResults.cs`
- Modify: `src/Bmd/Output/BmdJsonContext.cs`, `src/Bmd/Program.cs`, `src/Bmd/Commands/GroupHelp.cs`
- Test: `tests/Bmd.Tests/Commands/MultiViewCommandsTests.cs` (append)

**Interfaces:**
- Consumes: `DeviceSession.WithBackedUpClientAsync` (Task 3), `VideohubClient.SetRouteAsync/RenameInputAsync/RenameOutputAsync/LockOutputAsync/UnlockOutputAsync`.
- Produces:
  - `MultiViewRouteSetResult(int View, string ViewLabel, int Input, string InputLabel, string? Backup)`
  - `MultiViewRenameResult(string Kind, int N, string OldLabel, string NewLabel, string? Backup)`
  - `MultiViewLockResult(int View, string ViewLabel, string Lock, string? Backup)`
  - `MultiViewCommands.ViewSet`, `InputRename`, `ViewRename`, `ViewLock`, `ViewUnlock`

- [ ] **Step 1: Write the failing tests**

Append to `MultiViewCommandsTests`:

```csharp
    [Fact]
    public async Task ViewSet_PutsASourceInAViewAndReportsTheChange()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewSet(1, 3, "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal(2, device.Routes()[0]);          // 1-based view 1 → 0-based input index 2
        var text = _stdout.ToString();
        Assert.Contains("View 1", text);
        Assert.Contains("Presenter", text);
    }

    [Fact]
    public async Task ViewSet_Json_ReportsTheViewAndItsBackupPath()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewSet(2, 4, "127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal(2, doc.RootElement.GetProperty("view").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("input").GetInt32());
        Assert.Equal("Confidence", doc.RootElement.GetProperty("inputLabel").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("backup").GetString()));
    }

    [Fact]
    public async Task ViewSet_RejectsAViewNumberOutsideTheDeviceWithExitTwo()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().ViewSet(7, 1, "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("view", _stderr.ToString());
        Assert.Contains("1", _stderr.ToString());
        Assert.Contains("6", _stderr.ToString());
    }

    [Fact]
    public async Task ViewSet_RejectsASourceNumberOutsideTheDeviceWithExitTwo()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().ViewSet(1, 9, "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("input", _stderr.ToString());
    }

    [Fact]
    public async Task ViewRename_ChangesTheWindowLabel()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewRename(1, "Programme", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("Programme", device.OutputLabels()[0]);
    }

    [Fact]
    public async Task InputRename_ChangesTheSourceLabel()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().InputRename(2, "Desk Feed", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("Desk Feed", device.InputLabels()[1]);
    }

    [Fact]
    public async Task ViewLock_ThenUnlock_RoundTrips()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        Assert.Equal(0, await Commands().ViewLock(3, "127.0.0.1", device.Port));
        Assert.Equal('O', device.Locks()[2]);

        Assert.Equal(0, await Commands().ViewUnlock(3, "127.0.0.1", device.Port));
        Assert.Equal('U', device.Locks()[2]);
    }

    [Fact]
    public async Task Mutations_ReportADeviceRejectionAsOneErrorLine()
    {
        await using var device = FakeVideohub.StartRejecting(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewSet(1, 2, "127.0.0.1", device.Port);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~MultiViewCommandsTests`
Expected: FAIL — `ViewSet` does not exist.

- [ ] **Step 3: Add the result records**

Append to `src/Bmd/Commands/MultiView/MultiViewResults.cs`:

```csharp
public sealed record MultiViewRouteSetResult(
    int View, string ViewLabel, int Input, string InputLabel, string? Backup);

/// <summary>Kind is "input" or "view", so one record covers both renames.</summary>
public sealed record MultiViewRenameResult(
    string Kind, int N, string OldLabel, string NewLabel, string? Backup);

public sealed record MultiViewLockResult(int View, string ViewLabel, string Lock, string? Backup);
```

Register all three on `BmdJsonContext`:

```csharp
[JsonSerializable(typeof(MultiViewRouteSetResult))]
[JsonSerializable(typeof(MultiViewRenameResult))]
[JsonSerializable(typeof(MultiViewLockResult))]
```

- [ ] **Step 4: Implement the write commands**

Append to `MultiViewCommands`:

```csharp
    /// <summary>Range check shared by every command taking a view or source number. Returns an
    /// error message, or null when the value is in range. 1-based throughout, matching the
    /// device's own front panel.</summary>
    static string? OutOfRange(string noun, int value, int count) =>
        value >= 1 && value <= count ? null : $"{noun} must be between 1 and {count}, not {value}";

    /// <summary>Put a source in a view. Both numbers are 1-based, matching the device. On a MultiView 4, views 5 and 6 are the Solo and Audio inputs rather than windows.</summary>
    /// <param name="view">Which view to change (1-based).</param>
    /// <param name="input">Which source to show in it (1-based).</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ViewSet(
        [Argument] int view, [Argument] int input,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var state = client.State;
            if (OutOfRange("view", view, state.Device.VideoOutputs) is { } viewError)
            {
                Console.Error.WriteLine($"error: {viewError}");
                return 2;
            }
            if (OutOfRange("input", input, state.Device.VideoInputs) is { } inputError)
            {
                Console.Error.WriteLine($"error: {inputError}");
                return 2;
            }

            await client.SetRouteAsync(view, input);

            var result = new MultiViewRouteSetResult(
                view, state.GetOutputLabel(view), input, state.GetInputLabel(input), backup);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewRouteSetResult));
            else
            {
                Console.WriteLine($"view {view} ({result.ViewLabel}) ← input {input} ({result.InputLabel})");
                if (backup is not null) Console.WriteLine($"Backup: {backup}");
            }
            return 0;
        });

    /// <summary>Rename a source (1-based) on the device itself, so its label matches on the front panel and in other controllers.</summary>
    /// <param name="input">Which source to rename (1-based).</param>
    /// <param name="label">The new label.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> InputRename(
        [Argument] int input, [Argument] string label,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => RenameAsync("input", input, label, host, port, timeout, noBackup, json);

    /// <summary>Rename a view (1-based) on the device itself.</summary>
    /// <param name="view">Which view to rename (1-based).</param>
    /// <param name="label">The new label.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ViewRename(
        [Argument] int view, [Argument] string label,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => RenameAsync("view", view, label, host, port, timeout, noBackup, json);

    Task<int> RenameAsync(
        string kind, int n, string label,
        string? host, int? port, int? timeout, bool noBackup, bool json)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var state = client.State;
            var isInput = kind == "input";
            var count = isInput ? state.Device.VideoInputs : state.Device.VideoOutputs;
            if (OutOfRange(kind, n, count) is { } error)
            {
                Console.Error.WriteLine($"error: {error}");
                return 2;
            }

            var old = isInput ? state.GetInputLabel(n) : state.GetOutputLabel(n);
            if (isInput) await client.RenameInputAsync(n, label);
            else await client.RenameOutputAsync(n, label);

            var result = new MultiViewRenameResult(kind, n, old, label, backup);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewRenameResult));
            else
            {
                Console.WriteLine($"{kind} {n}: {old} → {label}");
                if (backup is not null) Console.WriteLine($"Backup: {backup}");
            }
            return 0;
        });

    /// <summary>Take the lock on a view (1-based), preventing other controllers from changing its source or taking it over without --force.</summary>
    /// <param name="view">Which view to lock (1-based).</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ViewLock(
        [Argument] int view,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => LockAsync(view, force: false, unlock: false, host, port, timeout, noBackup, json);

    /// <summary>Release the lock on a view (1-based). Without --force, releasing a lock held by another controller is left to the device to accept or refuse.</summary>
    /// <param name="view">Which view to unlock (1-based).</param>
    /// <param name="force">Clear a lock held by another controller.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ViewUnlock(
        [Argument] int view, bool force = false,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => LockAsync(view, force, unlock: true, host, port, timeout, noBackup, json);

    Task<int> LockAsync(
        int view, bool force, bool unlock,
        string? host, int? port, int? timeout, bool noBackup, bool json)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var state = client.State;
            if (OutOfRange("view", view, state.Device.VideoOutputs) is { } error)
            {
                Console.Error.WriteLine($"error: {error}");
                return 2;
            }

            if (unlock) await client.UnlockOutputAsync(view, force);
            else await client.LockOutputAsync(view);

            var word = unlock ? "unlocked" : "locked";
            var result = new MultiViewLockResult(view, state.GetOutputLabel(view), word, backup);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewLockResult));
            else
            {
                Console.WriteLine($"view {view} ({result.ViewLabel}): {word}");
                if (backup is not null) Console.WriteLine($"Backup: {backup}");
            }
            return 0;
        });
```

Add `using ConsoleAppFramework;` to the top of the file for `[Argument]`.

- [ ] **Step 5: Register the commands**

In `src/Bmd/Program.cs`:

```csharp
app.Add("multiview view set", multiview.ViewSet);
app.Add("multiview input rename", multiview.InputRename);
app.Add("multiview view rename", multiview.ViewRename);
app.Add("multiview view lock", multiview.ViewLock);
app.Add("multiview view unlock", multiview.ViewUnlock);
```

In `GroupHelp.Commands`:

```csharp
        new("multiview view set", "Put a source in a view (both 1-based)."),
        new("multiview input rename", "Rename a source (1-based)."),
        new("multiview view rename", "Rename a view (1-based)."),
        new("multiview view lock", "Take the lock on a view (1-based)."),
        new("multiview view unlock", "Release the lock on a view (1-based)."),
```

- [ ] **Step 6: Run the tests and commit**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~MultiViewCommandsTests`
Expected: PASS.

Run: `dotnet test`
Expected: PASS.

```bash
git add src/Bmd/ tests/Bmd.Tests/
git commit -m "feat(multiview): routing, renames and locks in view vocabulary"
```

---

### Task 6: CONFIGURATION writes

The genuinely new capability: layout, output format, solo, and the display and behaviour toggles.

**Files:**
- Modify: `tests/Bmd.Tests/Devices/Videohub/FakeVideohub.cs` (accept CONFIGURATION mutations)
- Modify: `src/Bmd/Commands/MultiView/MultiViewCommands.cs`, `MultiViewResults.cs`, `BmdJsonContext.cs`, `Program.cs`, `GroupHelp.cs`
- Test: `tests/Bmd.Tests/Commands/MultiViewConfigCommandsTests.cs`

**Interfaces:**
- Consumes: `MultiViewConfiguration.ProtocolNameFor`, `LinesFor`, `TryParseOnOff` (Task 2); `VideohubClient.SendBlockAsync(string header, IReadOnlyList<string> lines, string description, CancellationToken)`.
- Produces:
  - `FakeVideohub.Configuration()` — `IReadOnlyList<KeyValuePair<string,string>>`, the fake's current CONFIGURATION.
  - `MultiViewConfigSetResult(string Setting, string Value, string? Backup)`
  - `MultiViewCommands.Layout`, `Format`, `Solo`, `Show`, `TakeMode`, `WidescreenSd`

- [ ] **Step 1: Teach the fake to apply CONFIGURATION**

In `tests/Bmd.Tests/Devices/Videohub/FakeVideohub.cs`, add a configuration store parsed from the dump and a mutation branch. Add beside the other state fields:

```csharp
    readonly List<KeyValuePair<string, string>> _configuration = [];

    /// <summary>The fake's CONFIGURATION properties, in the order the dump listed them.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Configuration()
    {
        lock (_gate) return _configuration.ToArray();
    }
```

In the constructor, after the existing dump parsing, seed it:

```csharp
        // Seed CONFIGURATION from the dump so a MultiView fixture round-trips: the fake must be
        // able to answer with the same block it was given, then mutate it.
        foreach (var block in BlockReader.ReadBlocks(dump))
        {
            if (block.Header != "CONFIGURATION") continue;
            foreach (var line in block.Lines)
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                    _configuration.Add(new KeyValuePair<string, string>(
                        line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
        }
```

In the method that handles an incoming client block (the one that already branches on `VIDEO OUTPUT ROUTING`, `INPUT LABELS`, `OUTPUT LABELS`, `VIDEO OUTPUT LOCKS`), add a branch before the default rejection:

```csharp
            case "CONFIGURATION":
            {
                lock (_gate)
                {
                    foreach (var line in block.Lines)
                    {
                        var colon = line.IndexOf(':');
                        if (colon <= 0) continue;
                        var name = line[..colon].Trim();
                        var value = line[(colon + 1)..].Trim();
                        var index = _configuration.FindIndex(
                            p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0) _configuration[index] = new KeyValuePair<string, string>(_configuration[index].Key, value);
                        else _configuration.Add(new KeyValuePair<string, string>(name, value));
                    }
                }
                break;
            }
```

Follow whatever the surrounding code already does to send `ACK` and count the mutation — do not invent a second path.

- [ ] **Step 2: Write the failing tests**

Create `tests/Bmd.Tests/Commands/MultiViewConfigCommandsTests.cs`:

```csharp
using System.Text.Json;
using Bmd.Commands.MultiView;
using Bmd.Config;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class MultiViewConfigCommandsTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), $"bmd-mvc-{Guid.NewGuid():N}");
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public MultiViewConfigCommandsTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    MultiViewCommands Commands() =>
        new(() => ConfigStore.Load(Path.Combine(_directory, "config"), _directory));

    static string? Value(FakeVideohub device, string name) =>
        device.Configuration().FirstOrDefault(p => p.Key == name).Value;

    [Fact]
    public async Task Layout_SendsWhateverValueTheUserAsksFor()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Layout("3x1", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("3x1", Value(device, "Layout"));
    }

    [Fact]
    public async Task Layout_DoesNotWhitelistValues()
    {
        // The CONFIGURATION block is undocumented and valid layouts vary by model and firmware,
        // so bmd must not decide what is valid — the device does, by ACK or NAK.
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Layout("7x7", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("7x7", Value(device, "Layout"));
    }

    [Fact]
    public async Task Layout_ReportsADeviceRejectionAsOneErrorLine()
    {
        await using var device = FakeVideohub.StartRejecting(Fixtures.DumpMultiView4);

        var exit = await Commands().Layout("9x9", "127.0.0.1", device.Port);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    [Fact]
    public async Task Format_SetsTheOutputFormat()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        Assert.Equal(0, await Commands().Format("2160p25", "127.0.0.1", device.Port));
        Assert.Equal("2160p25", Value(device, "Output format"));
    }

    [Fact]
    public async Task Solo_WithASourceNumberEnablesSoloAndRoutesTheSoloInput()
    {
        // Solo is two things on the wire: the enable flag, and which source the Solo Input view
        // is fed from. One command does both so the user does not have to know that.
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Solo("3", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("true", Value(device, "Solo enabled"));
        Assert.Equal(2, device.Routes()[4]);   // view 5 (Solo Input) ← 0-based input 2
    }

    [Fact]
    public async Task Solo_Off_DisablesSoloAndLeavesRoutingAlone()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        var before = device.Routes()[4];

        var exit = await Commands().Solo("off", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("false", Value(device, "Solo enabled"));
        Assert.Equal(before, device.Routes()[4]);
    }

    [Fact]
    public async Task Solo_RejectsAnythingThatIsNeitherOffNorASourceNumber()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Solo("banana", "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("off", _stderr.ToString());
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task Solo_RejectsASourceNumberOutsideTheDevice()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Solo("9", "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("1 and 4", _stderr.ToString());
    }

    [Theory]
    [InlineData("borders", "Display border")]
    [InlineData("labels", "Display labels")]
    [InlineData("audio-meters", "Display audio meters")]
    [InlineData("tally", "Display SDI tally")]
    public async Task Show_TogglesEachDisplaySetting(string cliName, string protocolName)
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        Assert.Equal(0, await Commands().Show(cliName, "on", "127.0.0.1", device.Port));
        Assert.Equal("true", Value(device, protocolName));

        Assert.Equal(0, await Commands().Show(cliName, "off", "127.0.0.1", device.Port));
        Assert.Equal("false", Value(device, protocolName));
    }

    [Fact]
    public async Task Show_RejectsAnUnknownSettingWithExitTwoAndListsTheValidOnes()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Show("brightness", "on", "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("borders", _stderr.ToString());
        Assert.Contains("tally", _stderr.ToString());
    }

    [Fact]
    public async Task Show_RejectsAValueThatIsNotOnOrOff()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Show("labels", "maybe", "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("on", _stderr.ToString());
        Assert.Contains("off", _stderr.ToString());
    }

    [Fact]
    public async Task TakeMode_AndWidescreenSd_AreTheirOwnCommandsNotDisplaySettings()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        Assert.Equal(0, await Commands().TakeMode("off", "127.0.0.1", device.Port));
        Assert.Equal("false", Value(device, "Take Mode"));

        Assert.Equal(0, await Commands().WidescreenSd("off", "127.0.0.1", device.Port));
        Assert.Equal("false", Value(device, "Widescreen SD enabled"));
    }

    [Fact]
    public async Task ConfigWrites_Json_EmitOneDocumentWithTheBackupPath()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Layout("4x1", "127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("Layout", doc.RootElement.GetProperty("setting").GetString());
        Assert.Equal("4x1", doc.RootElement.GetProperty("value").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("backup").GetString()));
    }
```

Close the class with `}`.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~MultiViewConfigCommandsTests`
Expected: FAIL — `Layout` does not exist.

- [ ] **Step 4: Add the result record**

Append to `MultiViewResults.cs` and register on `BmdJsonContext`:

```csharp
/// <summary>One CONFIGURATION property write. Setting is the protocol's own property name, so
/// the output says exactly what was sent to the device.</summary>
public sealed record MultiViewConfigSetResult(string Setting, string Value, string? Backup);
```

```csharp
[JsonSerializable(typeof(MultiViewConfigSetResult))]
```

- [ ] **Step 5: Implement the configuration commands**

Append to `MultiViewCommands`:

```csharp
    /// <summary>The display settings `show` accepts, for both validation and the error message.</summary>
    static readonly string[] ShowSettings = ["borders", "labels", "audio-meters", "tally"];

    /// <summary>Set the multiview window layout, for example 2x2. bmd does not validate the value: the CONFIGURATION block is undocumented and valid layouts differ by model and firmware, so the device decides and any rejection is reported.</summary>
    /// <param name="value">The layout to set, e.g. 2x2. Observed on a MultiView 4: 2x2.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Layout(
        [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => SetPropertyAsync("Layout", value, host, port, timeout, noBackup, json);

    /// <summary>Set the multiview output video format, for example 1080i5994. As with layout, bmd sends the value and lets the device accept or reject it.</summary>
    /// <param name="value">The format to set, e.g. 1080i5994. Observed on a MultiView 4: 1080i5994.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Format(
        [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => SetPropertyAsync("Output format", value, host, port, timeout, noBackup, json);

    /// <summary>Show one source full-screen, or leave solo mode. Passing a source number both enables solo and points the Solo Input at that source; passing off disables solo and leaves routing untouched.</summary>
    /// <param name="value">A source number (1-based) to solo, or "off" to leave solo mode.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Solo(
        [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var state = client.State;
            var off = string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
            int? source = null;
            if (!off)
            {
                if (!int.TryParse(value, out var parsed))
                {
                    Console.Error.WriteLine($"error: solo takes a source number (1-based) or 'off', not '{value}'");
                    return 2;
                }
                if (OutOfRange("input", parsed, state.Device.VideoInputs) is { } error)
                {
                    Console.Error.WriteLine($"error: {error}");
                    return 2;
                }
                source = parsed;
            }

            // The Solo Input is the second-to-last "output" on a MultiView 4. Route it first so
            // that by the time solo is enabled the correct source is already showing.
            if (source is { } input)
            {
                var soloView = state.Device.VideoOutputs - 1;
                await client.SetRouteAsync(soloView, input);
            }
            await client.SendBlockAsync(
                MultiViewConfiguration.BlockHeader,
                MultiViewConfiguration.LinesFor("Solo enabled", off ? "false" : "true"),
                $"CONFIGURATION (Solo enabled: {(off ? "false" : "true")})");

            var result = new MultiViewConfigSetResult("Solo enabled", off ? "false" : "true", backup);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewConfigSetResult));
            else
            {
                Console.WriteLine(off
                    ? "solo: off"
                    : $"solo: input {source} ({state.GetInputLabel(source!.Value)})");
                if (backup is not null) Console.WriteLine($"Backup: {backup}");
            }
            return 0;
        });

    /// <summary>Turn one of the multiview's on-screen overlays on or off: borders, labels, audio-meters, or tally.</summary>
    /// <param name="setting">Which overlay: borders, labels, audio-meters, or tally.</param>
    /// <param name="value">on or off.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Show(
        [Argument] string setting, [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
    {
        if (!ShowSettings.Contains(setting, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"error: unknown setting '{setting}' — expected one of: {string.Join(", ", ShowSettings)}");
            return Task.FromResult(2);
        }
        return SetFlagAsync(MultiViewConfiguration.ProtocolNameFor(setting)!, value, host, port, timeout, noBackup, json);
    }

    /// <summary>Turn take mode on or off. In take mode the device holds a requested change until it is confirmed, rather than cutting immediately.</summary>
    /// <param name="value">on or off.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> TakeMode(
        [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => SetFlagAsync("Take Mode", value, host, port, timeout, noBackup, json);

    /// <summary>Turn widescreen SD on or off. Only affects standard-definition sources.</summary>
    /// <param name="value">on or off.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> WidescreenSd(
        [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => SetFlagAsync("Widescreen SD enabled", value, host, port, timeout, noBackup, json);

    /// <summary>A boolean CONFIGURATION property. bmd validates on/off here because true/false is
    /// unambiguous — unlike layout and format, where it deliberately does not.</summary>
    Task<int> SetFlagAsync(
        string protocolProperty, string value,
        string? host, int? port, int? timeout, bool noBackup, bool json)
    {
        if (!MultiViewConfiguration.TryParseOnOff(value, out var flag))
        {
            Console.Error.WriteLine($"error: expected 'on' or 'off', not '{value}'");
            return Task.FromResult(2);
        }
        return SetPropertyAsync(protocolProperty, flag ? "true" : "false", host, port, timeout, noBackup, json);
    }

    Task<int> SetPropertyAsync(
        string protocolProperty, string value,
        string? host, int? port, int? timeout, bool noBackup, bool json)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            await client.SendBlockAsync(
                MultiViewConfiguration.BlockHeader,
                MultiViewConfiguration.LinesFor(protocolProperty, value),
                $"CONFIGURATION ({protocolProperty}: {value})");

            var result = new MultiViewConfigSetResult(protocolProperty, value, backup);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewConfigSetResult));
            else
            {
                Console.WriteLine($"{protocolProperty}: {value}");
                if (backup is not null) Console.WriteLine($"Backup: {backup}");
            }
            return 0;
        });
```

Check `VideohubClient.SendBlockAsync`'s exact parameter list before writing these calls and match it; if its last parameter is a `CancellationToken` with a default, the calls above are already correct.

- [ ] **Step 6: Register the commands**

In `src/Bmd/Program.cs`:

```csharp
app.Add("multiview layout", multiview.Layout);
app.Add("multiview format", multiview.Format);
app.Add("multiview solo", multiview.Solo);
app.Add("multiview show", multiview.Show);
app.Add("multiview take-mode", multiview.TakeMode);
app.Add("multiview widescreen-sd", multiview.WidescreenSd);
```

In `GroupHelp.Commands`:

```csharp
        new("multiview layout", "Set the window layout, e.g. 2x2. The device validates the value, not bmd."),
        new("multiview format", "Set the output video format, e.g. 1080i5994. The device validates the value."),
        new("multiview solo", "Show one source full-screen, or 'off' to leave solo mode."),
        new("multiview show", "Turn an on-screen overlay on or off: borders, labels, audio-meters, tally."),
        new("multiview take-mode", "Turn take mode on or off."),
        new("multiview widescreen-sd", "Turn widescreen SD on or off."),
```

- [ ] **Step 7: Run the tests and commit**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~MultiView`
Expected: PASS.

Run: `dotnet test`
Expected: PASS.

```bash
git add src/Bmd/ tests/Bmd.Tests/
git commit -m "feat(multiview): layout, format, solo and display settings"
```

---

### Task 7: Snapshots with configuration, plus export/restore/watch

**Files:**
- Modify: `src/Bmd/Devices/Videohub/VideohubSnapshot.cs`, `src/Bmd/Devices/Videohub/SnapshotJsonContext.cs`
- Modify: `src/Bmd/Commands/MultiView/MultiViewCommands.cs`, `Program.cs`, `GroupHelp.cs`
- Test: `tests/Bmd.Tests/Devices/Videohub/VideohubSnapshotTests.cs` (append), `tests/Bmd.Tests/Commands/MultiViewCommandsTests.cs` (append)

**Interfaces:**
- Consumes: `MultiViewConfiguration` (Task 2), `MultiViewCommands.ReadConfiguration` (Task 4).
- Produces:
  - `SnapshotConfiguration(string? Layout, string? OutputFormat, bool? SoloEnabled, bool? WidescreenSdEnabled, bool? DisplayBorder, bool? DisplayLabels, bool? DisplayAudioMeters, bool? DisplaySdiTally, bool? TakeMode)`
  - `VideohubSnapshot` gains a trailing `SnapshotConfiguration? Configuration = null` parameter.
  - `VideohubSnapshot.FromState(VideohubState, DateTimeOffset, bool includeConfiguration = false)`
  - `MultiViewCommands.Export`, `Restore`, `Watch`

**Backwards compatibility is a hard requirement:** the new field is last and optional, so every snapshot written before this task still deserializes and still restores identically.

- [ ] **Step 1: Write the failing snapshot tests**

Append to `tests/Bmd.Tests/Devices/Videohub/VideohubSnapshotTests.cs`:

```csharp
    [Fact]
    public void FromState_OmitsConfigurationUnlessAsked()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.DumpMultiView4));
        var snapshot = VideohubSnapshot.FromState(state, DateTimeOffset.UnixEpoch);
        Assert.Null(snapshot.Configuration);
    }

    [Fact]
    public void FromState_CapturesConfigurationWhenAsked()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.DumpMultiView4));

        var snapshot = VideohubSnapshot.FromState(state, DateTimeOffset.UnixEpoch, includeConfiguration: true);

        Assert.NotNull(snapshot.Configuration);
        Assert.Equal("2x2", snapshot.Configuration!.Layout);
        Assert.Equal("1080i5994", snapshot.Configuration.OutputFormat);
        Assert.True(snapshot.Configuration.TakeMode);
        Assert.False(snapshot.Configuration.DisplayAudioMeters);
    }

    [Fact]
    public void FromState_LeavesConfigurationNullWhenTheDeviceSentNoBlock()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));
        var snapshot = VideohubSnapshot.FromState(state, DateTimeOffset.UnixEpoch, includeConfiguration: true);
        Assert.Null(snapshot.Configuration);
    }

    [Fact]
    public void Configuration_RoundTripsThroughJson()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.DumpMultiView4));
        var snapshot = VideohubSnapshot.FromState(state, DateTimeOffset.UnixEpoch, includeConfiguration: true);

        var restored = VideohubSnapshot.FromJson(snapshot.ToJson());

        Assert.Equal("2x2", restored.Configuration!.Layout);
        Assert.True(restored.Configuration.DisplayLabels);
    }

    [Fact]
    public void ASnapshotWrittenBeforeConfigurationExistedStillLoads()
    {
        // Every backup already on disk was written without this field; none of them may break.
        const string legacy = """
            {"device":"Blackmagic Smart Videohub 40 x 40","videoInputs":2,"videoOutputs":2,
             "exportedAt":"2026-08-29T14:35:12+00:00",
             "inputs":[{"n":1,"label":"A"},{"n":2,"label":"B"}],
             "outputs":[{"n":1,"label":"X","input":1},{"n":2,"label":"Y","input":2}]}
            """;

        var snapshot = VideohubSnapshot.FromJson(legacy);

        Assert.Null(snapshot.Configuration);
        Assert.Equal(2, snapshot.Outputs.Length);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~VideohubSnapshotTests`
Expected: FAIL — `Configuration` does not exist.

- [ ] **Step 3: Extend the snapshot**

In `src/Bmd/Devices/Videohub/VideohubSnapshot.cs`, add the record and extend the snapshot. Note the `using Bmd.Devices.MultiView;`:

```csharp
/// <summary>A MultiView's CONFIGURATION at export time. Absent on a Videohub, and absent from
/// every snapshot written before this existed — which is why it is last and optional.
/// Every property is nullable so "the device never reported this" stays distinguishable from
/// "the device reported false".</summary>
public sealed record SnapshotConfiguration(
    string? Layout, string? OutputFormat, bool? SoloEnabled, bool? WidescreenSdEnabled,
    bool? DisplayBorder, bool? DisplayLabels, bool? DisplayAudioMeters, bool? DisplaySdiTally,
    bool? TakeMode);
```

Change the record header and `FromState`:

```csharp
public sealed record VideohubSnapshot(
    string Device,
    int VideoInputs,
    int VideoOutputs,
    DateTimeOffset ExportedAt,
    SnapshotInput[] Inputs,
    SnapshotOutput[] Outputs,
    SnapshotConfiguration? Configuration = null)
{
    public static VideohubSnapshot FromState(
        VideohubState state, DateTimeOffset exportedAt, bool includeConfiguration = false)
    {
        var device = state.Device;
        var inputs = Enumerable.Range(1, device.VideoInputs)
            .Select(n => new SnapshotInput(n, state.GetInputLabel(n))).ToArray();
        var outputs = Enumerable.Range(1, device.VideoOutputs)
            .Select(n => new SnapshotOutput(n, state.GetOutputLabel(n), state.GetRoute(n))).ToArray();

        SnapshotConfiguration? configuration = null;
        if (includeConfiguration &&
            state.ExtraBlocks.TryGetValue(MultiViewConfiguration.BlockHeader, out var lines))
        {
            var c = MultiViewConfiguration.FromLines(lines);
            configuration = new SnapshotConfiguration(
                c.Layout, c.OutputFormat, c.SoloEnabled, c.WidescreenSdEnabled,
                c.DisplayBorder, c.DisplayLabels, c.DisplayAudioMeters, c.DisplaySdiTally, c.TakeMode);
        }

        return new VideohubSnapshot(
            device.ModelName, device.VideoInputs, device.VideoOutputs, exportedAt, inputs, outputs,
            configuration);
    }
```

Register `SnapshotConfiguration` on `SnapshotJsonContext`:

```csharp
[JsonSerializable(typeof(SnapshotConfiguration))]
```

- [ ] **Step 4: Write the failing command tests**

Append to `MultiViewCommandsTests`:

```csharp
    [Fact]
    public async Task Export_WritesConfigurationAlongsideLabelsAndRouting()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        var file = Path.Combine(_directory, "show.json");

        var exit = await Commands().Export(file, "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("2x2", doc.RootElement.GetProperty("configuration").GetProperty("layout").GetString());
        Assert.Equal(6, doc.RootElement.GetProperty("outputs").GetArrayLength());
    }

    [Fact]
    public async Task Restore_DryRun_ReportsTheConfigurationItWouldChangeAndChangesNothing()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        var file = Path.Combine(_directory, "show.json");
        Assert.Equal(0, await Commands().Export(file, "127.0.0.1", device.Port));

        var edited = File.ReadAllText(file).Replace("\"layout\": \"2x2\"", "\"layout\": \"4x1\"");
        File.WriteAllText(file, edited);
        _stdout.GetStringBuilder().Clear();

        var exit = await Commands().Restore(file, "127.0.0.1", device.Port, dryRun: true);

        Assert.Equal(0, exit);
        Assert.Contains("4x1", _stdout.ToString());
        Assert.Equal("2x2", device.Configuration().First(p => p.Key == "Layout").Value);
    }

    [Fact]
    public async Task Restore_AppliesConfigurationDifferencesOnly()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        var file = Path.Combine(_directory, "show.json");
        Assert.Equal(0, await Commands().Export(file, "127.0.0.1", device.Port));
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"layout\": \"2x2\"", "\"layout\": \"4x1\""));

        var exit = await Commands().Restore(file, "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("4x1", device.Configuration().First(p => p.Key == "Layout").Value);
        // Unchanged properties are not re-sent.
        Assert.Equal("1080i5994", device.Configuration().First(p => p.Key == "Output format").Value);
    }

    [Fact]
    public async Task Restore_LeavesConfigurationAloneWhenTheSnapshotHasNone()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        var file = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(file, """
            {"device":"Blackmagic MultiView 4","videoInputs":4,"videoOutputs":6,
             "exportedAt":"2026-08-29T14:35:12+00:00",
             "inputs":[{"n":1,"label":"Stream"},{"n":2,"label":"Screens"},
                       {"n":3,"label":"Presenter"},{"n":4,"label":"Confidence"}],
             "outputs":[{"n":1,"label":"View 1","input":1},{"n":2,"label":"View 2","input":2},
                        {"n":3,"label":"View 3","input":3},{"n":4,"label":"View 4","input":4},
                        {"n":5,"label":"Solo Input","input":3},{"n":6,"label":"Audio Input","input":1}]}
            """);

        var exit = await Commands().Restore(file, "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("2x2", device.Configuration().First(p => p.Key == "Layout").Value);
    }

    [Fact]
    public async Task Watch_StreamsAViewChangeAndStopsOnCancellation()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        using var cts = new CancellationTokenSource();

        var watching = Commands().Watch("127.0.0.1", device.Port, cancellationToken: cts.Token);
        await Task.Delay(150, CancellationToken.None);
        await device.PushRouteAsync(1, 3);
        await Task.Delay(150, CancellationToken.None);
        await cts.CancelAsync();

        var exit = await watching.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("View 1", _stdout.ToString());
    }
```

- [ ] **Step 5: Implement export, restore and watch**

Append to `MultiViewCommands`. Model `Export` and `Restore` on the existing `VideohubCommands` implementations — read them first and mirror their verify-and-retry and diff-apply behaviour rather than writing new logic — with these MultiView differences: pass `includeConfiguration: true` when building the snapshot, and after the route/label plan is applied, send a `CONFIGURATION` block for each property in the snapshot that differs from the device's current value. `Watch` is a direct copy of the Videohub implementation with view wording in its output.

Register in `Program.cs` and `GroupHelp.Commands`:

```csharp
app.Add("multiview export", multiview.Export);
app.Add("multiview restore", multiview.Restore);
app.Add("multiview watch", multiview.Watch);
```

```csharp
        new("multiview export", "Export a verified snapshot of sources, views and configuration."),
        new("multiview restore", "Apply a snapshot, changing only what differs."),
        new("multiview watch", "Stream device changes as they happen."),
```

- [ ] **Step 6: Run the tests and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add src/Bmd/ tests/Bmd.Tests/
git commit -m "feat(multiview): export, restore and watch with configuration"
```

---

### Task 8: Discovery, site and docs

**Files:**
- Modify: `src/Bmd/Devices/Discovery/DiscoveredDevice.cs`
- Test: `tests/Bmd.Tests/Devices/Discovery/DiscoveredDeviceTests.cs` (append)
- Create: `site/multiview.html`
- Modify: `site/index.html`, `CLAUDE.md`

- [ ] **Step 1: Write the failing discovery tests**

Append to `DiscoveredDeviceTests`:

```csharp
    [Theory]
    [InlineData("MultiView", "multiview")]
    [InlineData("multiview", "multiview")]
    [InlineData("MULTIVIEW", "multiview")]
    public void DeviceTypeFor_RecognisesTheMultiViewClass(string advertised, string expected)
    {
        // Verified against a real MultiView 4, which advertises class=MultiView.
        Assert.Equal(expected, DeviceClasses.DeviceTypeFor(advertised));
    }

    [Fact]
    public void DeviceTypeFor_StillRecognisesVideohub()
    {
        Assert.Equal("videohub", DeviceClasses.DeviceTypeFor("Videohub"));
    }

    [Fact]
    public void DeviceTypeFor_DoesNotGuessAtAtem()
    {
        // Observed on the network but unsupported; discovery must not offer to configure it.
        Assert.Null(DeviceClasses.DeviceTypeFor("AtemSwitcher"));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~DiscoveredDeviceTests`
Expected: FAIL — `DeviceTypeFor("MultiView")` returns null.

- [ ] **Step 3: Teach discovery the MultiView class**

In `src/Bmd/Devices/Discovery/DiscoveredDevice.cs`, replace the `KnownVideohubClasses` list and `DeviceTypeFor` with a table covering both device types:

```csharp
    /// <summary>mDNS TXT <c>class=</c> values mapped to the bmd device group that handles them.
    /// <para><c>Videohub</c> and <c>MultiView</c> are <b>confirmed against real hardware</b>
    /// (a Smart Videohub 40x40 and a MultiView 4). The remaining Videohub spellings are seed
    /// values based on observation, not a specification — Blackmagic does not publish this set.
    /// Run <c>bmd discover --all</c> against real devices to refine the table.</para></summary>
    static readonly KeyValuePair<string, string>[] ClassMap =
    [
        new("Videohub", "videohub"),
        new("SmartVideohub", "videohub"),
        new("VideoHub", "videohub"),
        new("MultiView", "multiview"),
    ];

    public static IReadOnlyList<string> KnownVideohubClasses { get; } =
        [.. ClassMap.Where(e => e.Value == "videohub").Select(e => e.Key)];

    /// <summary>Case-insensitive lookup of the bmd device group for a device class.
    /// Returns null for anything not in the table — never a guess.</summary>
    public static string? DeviceTypeFor(string deviceClass)
    {
        foreach (var (advertised, group) in ClassMap)
            if (string.Equals(advertised, deviceClass, StringComparison.OrdinalIgnoreCase))
                return group;
        return null;
    }
```

If `KnownVideohubClasses` has no remaining callers after this change, delete it rather than leaving dead surface.

- [ ] **Step 4: Verify `discover --add` writes the right key**

`DiscoverCommands.AddSelected` already writes `new ConfigKey(deviceType, "host")`, so a discovered MultiView now writes `multiview.host` with no further change. Confirm by reading that method; if it hardcodes `"videohub"` anywhere, fix it to use the resolved device type.

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~Discover`
Expected: PASS.

- [ ] **Step 5: Write the site page**

Create `site/multiview.html`, following `site/videohub.html`'s structure exactly — same `guide-header`, `back-link`, `page-nav`, numbered `<section>` headings, `pre.cmd` blocks and footer. Read that file first and match its conventions; do not invent new classes. Sections:

1. **Connecting** — `bmd discover --add`, or `bmd config set multiview.host <addr>`.
2. **Sources and views** — `multiview input list`, `multiview view list`, and the fact that on a MultiView 4 views 5 and 6 are the Solo and Audio inputs rather than windows.
3. **Routing** — `multiview view set 1 3`, destination first.
4. **Layout and format** — `multiview layout 2x2`, `multiview format 1080i5994`, and plainly: bmd does not validate these because Blackmagic does not document the block, so the device accepts or rejects the value and bmd reports what it said.
5. **Overlays and behaviour** — `multiview show borders|labels|audio-meters|tally on|off`, `multiview take-mode`, `multiview widescreen-sd`.
6. **Solo** — `multiview solo 3`, `multiview solo off`.
7. **Backups, export and restore** — snapshots include configuration; `--dry-run`.
8. **Watching** — `multiview watch`.

In `site/index.html`: add MultiView to the support list in the hero (it currently says HyperDeck and ATEM are not implemented — MultiView must move from unimplemented to supported), add a `bmd multiview` row or two to the command list in the "Using it" section, and add a link to `multiview.html` in the footer and the guide callout.

- [ ] **Step 6: Tick the roadmap**

In `CLAUDE.md`, append milestone 10 to the roadmap line and add MultiView to the device list, so the file no longer describes MultiView as a future device.

- [ ] **Step 7: Full verification and commit**

Run: `dotnet test`
Expected: PASS.

Run: `dotnet publish src/Bmd -c Release -r win-x64`
Expected: succeeds with **no `IL2xxx`/`IL3xxx` warnings**. If `vswhere.exe` is not found, add `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to PATH for the session — that is a known local toolchain quirk, not a code problem.

Run: `dotnet run --project src/Bmd -- multiview --help`
Expected: the full group listing.

```bash
git add src/Bmd/ tests/Bmd.Tests/ site/ CLAUDE.md
git commit -m "feat(multiview): discovery, guide page and roadmap"
```

---

## Self-review

**1. Spec coverage.**

| Spec requirement | Task |
|---|---|
| Retain unrecognised blocks so CONFIGURATION reaches the model | 1 |
| Typed read/write of CONFIGURATION; `Devices/MultiView/` | 2 |
| Extract the shared core; `Devices/Videohub/` stays the protocol home | 3 |
| `bmd multiview` group with view vocabulary and its own config section | 4, 5 |
| Never whitelist Layout/Output format; validate booleans | 2, 6 |
| Snapshots include optional configuration; absent restores as today | 7 |
| `bmd videohub` keeps working against a MultiView, unwarned | 3 (no videohub behavior changes), verified by the untouched suite in Task 3 Step 5 |
| Discovery learns the `MultiView` class | 8 |
| Site ships with the change | 8 |
| 1-based numbering throughout | 4, 5 (`OutOfRange`, `[Argument] int view`) |
| `--json` one document, camelCase, stable names | 4, 5, 6, 7 |
| Backups before every mutation, path reported | 5, 6, 7 |

**2. Placeholder scan.** No TBDs. Two steps deliberately delegate to existing code rather than restating it — Task 7 Step 5 (export/restore/watch mirror the Videohub implementations) and Task 8 Step 5 (the site page mirrors `videohub.html`). Both name the exact file to read and the exact differences required, because reproducing ~200 lines of verified export/verify/retry logic in this document would invite it drifting from the original.

**3. Type consistency.** `VideohubState.ExtraBlocks` (Task 1) is consumed in Tasks 4 and 7. `MultiViewConfiguration.FromLines`/`LinesFor`/`ProtocolNameFor`/`TryParseOnOff`/`BlockHeader` (Task 2) are consumed in Tasks 4, 6 and 7. `DeviceSession`'s four methods (Task 3) are consumed in Tasks 4, 5, 6 and 7. `MultiViewCommands.ReadConfiguration` (Task 4) is consumed in Task 7. `OutOfRange` (Task 5) is consumed in Task 6's `Solo`. Every result record is registered on `BmdJsonContext` in the same task that introduces it.

**One risk worth naming:** Task 3 is a pure refactor whose only proof is that 474 existing tests still pass. Its reviewer should check that the moved code is byte-identical apart from the section parameterisation, and that no existing test was edited to accommodate it.
