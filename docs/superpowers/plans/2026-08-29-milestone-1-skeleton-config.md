# bmd Milestone 1: Skeleton + Config Subsystem Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A buildable `bmd` executable with ConsoleAppFramework wiring and a fully working git-style layered config subsystem (`bmd config set/get/unset/list`), published and smoke-tested as a Native AOT binary.

**Architecture:** Single console project `src/Bmd` (AssemblyName `bmd`) with a `Config/` layer (INI parser, path resolution, layered store) that never references the CLI framework, and a thin `Commands/ConfigCommands.cs` on top. xUnit tests in `tests/Bmd.Tests` drive everything test-first.

**Tech Stack:** .NET 10, ConsoleAppFramework v5 (source generator, the only package reference in `src/Bmd`), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md`

## Global Constraints

- Target framework `net10.0`; `PublishAot=true` from the first commit — a failing AOT publish is a broken build.
- `src/Bmd` has exactly one PackageReference: ConsoleAppFramework (analyzer-only, `PrivateAssets=all`). No other runtime dependencies, no reflection, no `dynamic`.
- No environment variables for bmd configuration. (`XDG_CONFIG_HOME`/`APPDATA` are OS platform conventions for *locating* the config dir and are allowed.)
- Config precedence: local `.bmdconfig` (walk-up discovery from cwd) overrides global (`~/.config/bmd/config` on Unix, `%APPDATA%\bmd\config` on Windows).
- Errors: one clear message to stderr, no stack traces. Exit codes: 0 success, 1 operation failure, 2 invalid key format. (Framework-level argument parse failures use ConsoleAppFramework's default of 1; acceptable per spec intent.)
- Config files are written with LF line endings and tab-indented keys (git style).
- TDD: every behavior lands as failing test → implementation → passing test → commit.
- `Config/` must not reference ConsoleAppFramework or `Commands/`.

---

### Task 1: Solution skeleton with ConsoleAppFramework wiring

**Files:**
- Create: `Bmd.sln`, `src/Bmd/Bmd.csproj`, `src/Bmd/Program.cs`, `tests/Bmd.Tests/Bmd.Tests.csproj`, `tests/Bmd.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a building solution; `Program.cs` registers command classes via `app.Add<T>("name")` — later tasks add `ConfigCommands` there.

- [ ] **Step 1: Create solution and projects**

Run from repo root:

```powershell
dotnet new sln -n Bmd
dotnet new console -o src/Bmd -n Bmd
dotnet new xunit -o tests/Bmd.Tests -n Bmd.Tests
dotnet sln add src/Bmd tests/Bmd.Tests
dotnet add tests/Bmd.Tests reference src/Bmd
dotnet add src/Bmd package ConsoleAppFramework
```

- [ ] **Step 2: Replace `src/Bmd/Bmd.csproj` property group**

Keep the ConsoleAppFramework PackageReference the template `dotnet add package` created (it must have `PrivateAssets=all`; add that if missing). Properties:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <AssemblyName>bmd</AssemblyName>
  <RootNamespace>Bmd</RootNamespace>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <PublishAot>true</PublishAot>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

- [ ] **Step 3: Write `src/Bmd/Program.cs`**

```csharp
using ConsoleAppFramework;

var app = ConsoleApp.Create();
app.Run(args);
```

- [ ] **Step 4: Verify build, tests, and help output**

```powershell
dotnet build
dotnet test
dotnet run --project src/Bmd -- --help
```

Expected: build succeeds, the template test passes, help prints a usage header with no commands. Delete the template's `UnitTest1.cs` and add `tests/Bmd.Tests/SmokeTests.cs`:

```csharp
namespace Bmd.Tests;

public class SmokeTests
{
    [Fact]
    public void TestProjectReferencesMainProject() => Assert.True(true);
}
```

(Placeholder so the test project isn't empty; real tests arrive in Task 2 and it can be deleted then.)

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: solution skeleton with ConsoleAppFramework wiring"
```

---

### Task 2: IniFile — parsing and reading

**Files:**
- Create: `src/Bmd/Config/IniFile.cs`, `tests/Bmd.Tests/Config/IniFileParseTests.cs`
- Delete: `tests/Bmd.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `IniFile.Parse(string text) : IniFile` and `IniFile.Empty() : IniFile`
  - `Get(string section, string key) : string?` — case-insensitive section/key match; `[videohub]` only (a subsection header like `[videohub "studio"]` is a distinct section whose name is the raw inner text `videohub "studio"`)
  - `Entries() : IEnumerable<(string Section, string Key, string Value)>` in file order

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Config/IniFileParseTests.cs`:

```csharp
using Bmd.Config;

namespace Bmd.Tests.Config;

public class IniFileParseTests
{
    [Fact]
    public void Get_ReturnsValue_ForSimpleSectionAndKey()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n");
        Assert.Equal("10.0.0.5", ini.Get("videohub", "host"));
    }

    [Fact]
    public void Get_IsCaseInsensitive_ForSectionAndKey()
    {
        var ini = IniFile.Parse("[VideoHub]\n\tHost = 10.0.0.5\n");
        Assert.Equal("10.0.0.5", ini.Get("videohub", "host"));
    }

    [Fact]
    public void Get_ReturnsNull_WhenMissing()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n");
        Assert.Null(ini.Get("videohub", "port"));
        Assert.Null(ini.Get("update", "check"));
        Assert.Null(IniFile.Empty().Get("videohub", "host"));
    }

    [Fact]
    public void Parse_IgnoresCommentLines_AndInlineComments()
    {
        var text = "# global bmd config\n[videohub]\n\t; the studio hub\n\thost = 10.0.0.5 # main\n";
        var ini = IniFile.Parse(text);
        Assert.Equal("10.0.0.5", ini.Get("videohub", "host"));
    }

    [Fact]
    public void Parse_QuotedValue_PreservesSpacesAndCommentChars()
    {
        var ini = IniFile.Parse("[videohub]\n\tlabel = \"Studio # A\"\n");
        Assert.Equal("Studio # A", ini.Get("videohub", "label"));
    }

    [Fact]
    public void Parse_SubsectionHeader_IsADistinctSection()
    {
        var text = "[videohub]\n\thost = 10.0.0.5\n[videohub \"studio\"]\n\thost = 10.0.0.9\n";
        var ini = IniFile.Parse(text);
        Assert.Equal("10.0.0.5", ini.Get("videohub", "host"));
        Assert.Equal("10.0.0.9", ini.Get("videohub \"studio\"", "host"));
    }

    [Fact]
    public void Entries_ReturnsAllKeyValues_InFileOrder()
    {
        var text = "[videohub]\n\thost = 10.0.0.5\n\tport = 9990\n[update]\n\tcheck = false\n";
        var entries = IniFile.Parse(text).Entries().ToArray();
        Assert.Equal([("videohub", "host", "10.0.0.5"), ("videohub", "port", "9990"), ("update", "check", "false")], entries);
    }

    [Fact]
    public void Parse_ToleratesCrlfAndBlankLines()
    {
        var ini = IniFile.Parse("\r\n[videohub]\r\n\r\n\thost = 10.0.0.5\r\n");
        Assert.Equal("10.0.0.5", ini.Get("videohub", "host"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter IniFileParseTests`
Expected: compilation failure — `Bmd.Config.IniFile` does not exist.

- [ ] **Step 3: Implement `src/Bmd/Config/IniFile.cs`**

Line-preserving model: the file is a `List<string>` of lines; reads scan it, writes (Task 3) edit it in place so comments and layout survive.

```csharp
namespace Bmd.Config;

/// <summary>Git-style INI file. Line-preserving: unedited lines round-trip byte-for-byte (LF).</summary>
public sealed class IniFile
{
    readonly List<string> _lines;

    IniFile(List<string> lines) => _lines = lines;

    public static IniFile Empty() => new([]);

    public static IniFile Parse(string text)
    {
        var lines = text.Length == 0
            ? new List<string>()
            : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        return new IniFile(lines);
    }

    public string? Get(string section, string key)
    {
        foreach (var (s, k, v) in Entries())
            if (SectionEquals(s, section) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    public IEnumerable<(string Section, string Key, string Value)> Entries()
    {
        string? current = null;
        foreach (var line in _lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';') continue;
            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                current = trimmed[1..^1].Trim();
                continue;
            }
            var eq = trimmed.IndexOf('=');
            if (current is null || eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = ParseValue(trimmed[(eq + 1)..]);
            if (key.Length > 0) yield return (current, key, value);
        }
    }

    static string ParseValue(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith('"'))
        {
            var close = raw.IndexOf('"', 1);
            return close > 0 ? raw[1..close] : raw[1..];
        }
        var comment = raw.IndexOfAny(['#', ';']);
        if (comment >= 0) raw = raw[..comment];
        return raw.Trim();
    }

    static bool SectionEquals(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
```

Also delete `tests/Bmd.Tests/SmokeTests.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter IniFileParseTests`
Expected: all 8 tests PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: git-style INI parsing (IniFile.Parse/Get/Entries)"
```

---

### Task 3: IniFile — Set/Unset/ToText with layout preservation

**Files:**
- Modify: `src/Bmd/Config/IniFile.cs`
- Create: `tests/Bmd.Tests/Config/IniFileWriteTests.cs`

**Interfaces:**
- Consumes: Task 2's `IniFile`.
- Produces:
  - `Set(string section, string key, string value) : void` — replaces in place, appends to existing section, or appends a new section at end
  - `Unset(string section, string key) : bool` — true if a line was removed
  - `ToText() : string` — LF-joined, single trailing newline when non-empty; unedited lines byte-identical

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Config/IniFileWriteTests.cs`:

```csharp
using Bmd.Config;

namespace Bmd.Tests.Config;

public class IniFileWriteTests
{
    [Fact]
    public void ToText_RoundTripsUnmodifiedContent()
    {
        var text = "# comment\n[videohub]\n\thost = 10.0.0.5\n\n[update]\n\tcheck = false\n";
        Assert.Equal(text, IniFile.Parse(text).ToText());
    }

    [Fact]
    public void Set_ReplacesExistingKeyInPlace_PreservingComments()
    {
        var ini = IniFile.Parse("# keep me\n[videohub]\n\thost = 10.0.0.5 # old\n\tport = 9990\n");
        ini.Set("videohub", "host", "10.0.0.9");
        Assert.Equal("# keep me\n[videohub]\n\thost = 10.0.0.9\n\tport = 9990\n", ini.ToText());
    }

    [Fact]
    public void Set_AppendsKeyToExistingSection()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n[update]\n\tcheck = false\n");
        ini.Set("videohub", "port", "9991");
        Assert.Equal("[videohub]\n\thost = 10.0.0.5\n\tport = 9991\n[update]\n\tcheck = false\n", ini.ToText());
    }

    [Fact]
    public void Set_AppendsNewSectionAtEnd()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n");
        ini.Set("update", "check", "false");
        Assert.Equal("[videohub]\n\thost = 10.0.0.5\n[update]\n\tcheck = false\n", ini.ToText());
    }

    [Fact]
    public void Set_OnEmptyFile_CreatesSection()
    {
        var ini = IniFile.Empty();
        ini.Set("videohub", "host", "10.0.0.5");
        Assert.Equal("[videohub]\n\thost = 10.0.0.5\n", ini.ToText());
    }

    [Fact]
    public void Set_QuotesValuesContainingCommentCharsOrEdgeWhitespace()
    {
        var ini = IniFile.Empty();
        ini.Set("videohub", "label", "Studio # A");
        Assert.Equal("[videohub]\n\tlabel = \"Studio # A\"\n", ini.ToText());
        Assert.Equal("Studio # A", ini.Get("videohub", "label"));
    }

    [Fact]
    public void Unset_RemovesKeyLine_ReturnsTrue()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n\tport = 9990\n");
        Assert.True(ini.Unset("videohub", "host"));
        Assert.Equal("[videohub]\n\tport = 9990\n", ini.ToText());
    }

    [Fact]
    public void Unset_ReturnsFalse_WhenMissing()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n");
        Assert.False(ini.Unset("videohub", "port"));
        Assert.False(ini.Unset("update", "check"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter IniFileWriteTests`
Expected: compilation failure — `Set`, `Unset`, `ToText` not defined.

- [ ] **Step 3: Add write support to `IniFile`**

Add to `src/Bmd/Config/IniFile.cs`:

```csharp
public string ToText() => _lines.Count == 0 ? "" : string.Join('\n', _lines) + "\n";

public void Set(string section, string key, string value)
{
    var formatted = $"\t{key} = {FormatValue(value)}";
    var (sectionStart, sectionEnd) = FindSection(section);
    if (sectionStart < 0)
    {
        _lines.Add($"[{section}]");
        _lines.Add(formatted);
        return;
    }
    var keyLine = FindKeyLine(sectionStart, sectionEnd, key);
    if (keyLine >= 0) { _lines[keyLine] = formatted; return; }
    // insert after the last non-blank line of the section
    var insertAt = sectionEnd;
    while (insertAt > sectionStart + 1 && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
    _lines.Insert(insertAt, formatted);
}

public bool Unset(string section, string key)
{
    var (sectionStart, sectionEnd) = FindSection(section);
    if (sectionStart < 0) return false;
    var keyLine = FindKeyLine(sectionStart, sectionEnd, key);
    if (keyLine < 0) return false;
    _lines.RemoveAt(keyLine);
    return true;
}

/// <returns>(header line index, exclusive end index) or (-1, -1)</returns>
(int Start, int End) FindSection(string section)
{
    for (var i = 0; i < _lines.Count; i++)
    {
        var t = _lines[i].Trim();
        if (t.Length > 1 && t[0] == '[' && t[^1] == ']' && SectionEquals(t[1..^1].Trim(), section))
        {
            var end = i + 1;
            while (end < _lines.Count && !_lines[end].TrimStart().StartsWith('[')) end++;
            return (i, end);
        }
    }
    return (-1, -1);
}

int FindKeyLine(int sectionStart, int sectionEnd, string key)
{
    for (var i = sectionStart + 1; i < sectionEnd; i++)
    {
        var t = _lines[i].Trim();
        if (t.Length == 0 || t[0] is '#' or ';') continue;
        var eq = t.IndexOf('=');
        if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
    }
    return -1;
}

static string FormatValue(string value) =>
    value.IndexOfAny(['#', ';']) >= 0 || value != value.Trim()
        ? $"\"{value}\""
        : value;
```

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`
Expected: all IniFile tests PASS (16 total).

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: INI Set/Unset/ToText with comment and layout preservation"
```

---

### Task 4: ConfigPaths — global path and local walk-up discovery

**Files:**
- Create: `src/Bmd/Config/ConfigPaths.cs`, `tests/Bmd.Tests/Config/ConfigPathsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `ConfigPaths.GlobalConfigPath : string` — `%APPDATA%\bmd\config` on Windows; `$XDG_CONFIG_HOME/bmd/config` or `~/.config/bmd/config` elsewhere
  - `ConfigPaths.LocalFileName : const string` = `".bmdconfig"`
  - `ConfigPaths.FindLocalConfig(string startDirectory) : string?` — nearest `.bmdconfig` walking up from `startDirectory` to the filesystem root, or null

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Config/ConfigPathsTests.cs`:

```csharp
using Bmd.Config;

namespace Bmd.Tests.Config;

public class ConfigPathsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void FindLocalConfig_FindsFileInStartDirectory()
    {
        var file = Path.Combine(_root, ConfigPaths.LocalFileName);
        File.WriteAllText(file, "");
        Assert.Equal(file, ConfigPaths.FindLocalConfig(_root));
    }

    [Fact]
    public void FindLocalConfig_WalksUpToParent()
    {
        var file = Path.Combine(_root, ConfigPaths.LocalFileName);
        File.WriteAllText(file, "");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "a", "b")).FullName;
        Assert.Equal(file, ConfigPaths.FindLocalConfig(nested));
    }

    [Fact]
    public void FindLocalConfig_PrefersNearestFile()
    {
        File.WriteAllText(Path.Combine(_root, ConfigPaths.LocalFileName), "");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var nearest = Path.Combine(nested, ConfigPaths.LocalFileName);
        File.WriteAllText(nearest, "");
        Assert.Equal(nearest, ConfigPaths.FindLocalConfig(nested));
    }

    [Fact]
    public void FindLocalConfig_ReturnsNull_WhenAbsent()
    {
        Assert.Null(ConfigPaths.FindLocalConfig(_root));
    }

    [Fact]
    public void GlobalConfigPath_EndsWithBmdConfig()
    {
        var path = ConfigPaths.GlobalConfigPath;
        Assert.True(Path.IsPathRooted(path));
        Assert.Equal("config", Path.GetFileName(path));
        Assert.Equal("bmd", Path.GetFileName(Path.GetDirectoryName(path)!));
    }
}
```

(`FindLocalConfig_ReturnsNull_WhenAbsent` assumes no `.bmdconfig` exists in the temp root's ancestors — true for OS temp directories; the other tests don't depend on that.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ConfigPathsTests`
Expected: compilation failure — `ConfigPaths` does not exist.

- [ ] **Step 3: Implement `src/Bmd/Config/ConfigPaths.cs`**

```csharp
namespace Bmd.Config;

public static class ConfigPaths
{
    public const string LocalFileName = ".bmdconfig";

    public static string GlobalConfigPath
    {
        get
        {
            string baseDir;
            if (OperatingSystem.IsWindows())
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            else
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                baseDir = string.IsNullOrEmpty(xdg)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                    : xdg;
            }
            return Path.Combine(baseDir, "bmd", "config");
        }
    }

    public static string? FindLocalConfig(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, LocalFileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ConfigPathsTests`
Expected: all 5 tests PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: config path resolution (global path, .bmdconfig walk-up)"
```

---

### Task 5: ConfigStore — layered load, effective values, writes

**Files:**
- Create: `src/Bmd/Config/ConfigKey.cs`, `src/Bmd/Config/ConfigStore.cs`, `tests/Bmd.Tests/Config/ConfigKeyTests.cs`, `tests/Bmd.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: `IniFile` (Tasks 2-3), `ConfigPaths` (Task 4).
- Produces:
  - `ConfigKey.TryParse(string raw, out ConfigKey key) : bool`; `ConfigKey` is `readonly record struct ConfigKey(string Section, string Name)` splitting on the first `.`
  - `ConfigStore.Load(string globalPath, string startDirectory) : ConfigStore` and `ConfigStore.LoadDefault() : ConfigStore` (uses `ConfigPaths.GlobalConfigPath` + `Environment.CurrentDirectory`)
  - `GetEffective(ConfigKey key) : string?` — local wins over global
  - `ListEffective() : IReadOnlyList<ConfigEntry>` where `ConfigEntry` is `record ConfigEntry(string Key, string Value, string Origin)`; `Key` is `section.name` lowercased, `Origin` is the source file path; one entry per key (local shadows global); global entries first, then local-only, each group in file order
  - `Set(ConfigKey key, string value, bool global) : string` — writes and saves; returns the path written. Local writes go to the discovered `.bmdconfig`, or create one in `startDirectory` if none found. Global writes create the directory if needed.
  - `Unset(ConfigKey key, bool global) : bool` — false if the key wasn't present in that layer (no file rewrite in that case)

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Config/ConfigKeyTests.cs`:

```csharp
using Bmd.Config;

namespace Bmd.Tests.Config;

public class ConfigKeyTests
{
    [Theory]
    [InlineData("videohub.host", "videohub", "host")]
    [InlineData("update.check", "update", "check")]
    [InlineData("VideoHub.Host", "VideoHub", "Host")]
    public void TryParse_SplitsOnFirstDot(string raw, string section, string name)
    {
        Assert.True(ConfigKey.TryParse(raw, out var key));
        Assert.Equal(section, key.Section);
        Assert.Equal(name, key.Name);
    }

    [Theory]
    [InlineData("nodot")]
    [InlineData(".host")]
    [InlineData("videohub.")]
    [InlineData("")]
    public void TryParse_RejectsMalformedKeys(string raw)
    {
        Assert.False(ConfigKey.TryParse(raw, out _));
    }
}
```

`tests/Bmd.Tests/Config/ConfigStoreTests.cs`:

```csharp
using Bmd.Config;

namespace Bmd.Tests.Config;

public class ConfigStoreTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    string GlobalPath => Path.Combine(_root, "global", "bmd", "config");
    string WorkDir => Path.Combine(_root, "work");

    public ConfigStoreTests() => Directory.CreateDirectory(WorkDir);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    static ConfigKey Key(string raw)
    {
        Assert.True(ConfigKey.TryParse(raw, out var key));
        return key;
    }

    ConfigStore Load() => ConfigStore.Load(GlobalPath, WorkDir);

    [Fact]
    public void GetEffective_ReturnsNull_WhenNothingConfigured()
    {
        Assert.Null(Load().GetEffective(Key("videohub.host")));
    }

    [Fact]
    public void Set_Global_CreatesFileAndDirectories_GetEffectiveReadsIt()
    {
        var store = Load();
        var written = store.Set(Key("videohub.host"), "10.0.0.5", global: true);
        Assert.Equal(GlobalPath, written);
        Assert.Equal("10.0.0.5", Load().GetEffective(Key("videohub.host")));
    }

    [Fact]
    public void Set_Local_CreatesBmdconfigInStartDirectory()
    {
        var store = Load();
        var written = store.Set(Key("videohub.host"), "10.0.0.9", global: false);
        Assert.Equal(Path.Combine(WorkDir, ConfigPaths.LocalFileName), written);
        Assert.Equal("10.0.0.9", Load().GetEffective(Key("videohub.host")));
    }

    [Fact]
    public void Set_Local_WritesToDiscoveredFileInParent()
    {
        var parentConfig = Path.Combine(WorkDir, ConfigPaths.LocalFileName);
        File.WriteAllText(parentConfig, "[videohub]\n\tport = 9990\n");
        var nested = Directory.CreateDirectory(Path.Combine(WorkDir, "nested")).FullName;
        var store = ConfigStore.Load(GlobalPath, nested);
        var written = store.Set(Key("videohub.host"), "10.0.0.9", global: false);
        Assert.Equal(parentConfig, written);
        Assert.Contains("port = 9990", File.ReadAllText(parentConfig));
        Assert.Contains("host = 10.0.0.9", File.ReadAllText(parentConfig));
    }

    [Fact]
    public void GetEffective_LocalOverridesGlobal()
    {
        var store = Load();
        store.Set(Key("videohub.host"), "10.0.0.5", global: true);
        store.Set(Key("videohub.host"), "10.0.0.9", global: false);
        Assert.Equal("10.0.0.9", Load().GetEffective(Key("videohub.host")));
    }

    [Fact]
    public void ListEffective_ShadowsGlobalWithLocal_AndReportsOrigin()
    {
        var store = Load();
        store.Set(Key("videohub.host"), "10.0.0.5", global: true);
        store.Set(Key("update.check"), "false", global: true);
        store.Set(Key("videohub.host"), "10.0.0.9", global: false);

        var entries = ConfigStore.Load(GlobalPath, WorkDir).ListEffective();

        Assert.Equal(2, entries.Count);
        var host = Assert.Single(entries, e => e.Key == "videohub.host");
        Assert.Equal("10.0.0.9", host.Value);
        Assert.Equal(Path.Combine(WorkDir, ConfigPaths.LocalFileName), host.Origin);
        var check = Assert.Single(entries, e => e.Key == "update.check");
        Assert.Equal("false", check.Value);
        Assert.Equal(GlobalPath, check.Origin);
    }

    [Fact]
    public void Unset_RemovesFromChosenLayer()
    {
        var store = Load();
        store.Set(Key("videohub.host"), "10.0.0.5", global: true);
        Assert.True(Load().Unset(Key("videohub.host"), global: true));
        Assert.Null(Load().GetEffective(Key("videohub.host")));
    }

    [Fact]
    public void Unset_ReturnsFalse_WhenKeyAbsentInLayer()
    {
        var store = Load();
        store.Set(Key("videohub.host"), "10.0.0.5", global: true);
        Assert.False(Load().Unset(Key("videohub.host"), global: false));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "ConfigKeyTests|ConfigStoreTests"`
Expected: compilation failure — `ConfigKey`, `ConfigStore` do not exist.

- [ ] **Step 3: Implement `ConfigKey` and `ConfigStore`**

`src/Bmd/Config/ConfigKey.cs`:

```csharp
namespace Bmd.Config;

/// <summary>A config address like "videohub.host": section + key name, split on the first dot.</summary>
public readonly record struct ConfigKey(string Section, string Name)
{
    public static bool TryParse(string raw, out ConfigKey key)
    {
        key = default;
        var dot = raw.IndexOf('.');
        if (dot <= 0 || dot == raw.Length - 1) return false;
        key = new ConfigKey(raw[..dot], raw[(dot + 1)..]);
        return true;
    }

    public override string ToString() => $"{Section.ToLowerInvariant()}.{Name.ToLowerInvariant()}";
}
```

`src/Bmd/Config/ConfigStore.cs`:

```csharp
namespace Bmd.Config;

public sealed record ConfigEntry(string Key, string Value, string Origin);

/// <summary>Layered config: local .bmdconfig (walk-up from start directory) over global file.</summary>
public sealed class ConfigStore
{
    readonly string _globalPath;
    readonly string _startDirectory;
    readonly string? _localPath;
    readonly IniFile _global;
    readonly IniFile _local;

    ConfigStore(string globalPath, string startDirectory, string? localPath, IniFile global, IniFile local)
    {
        _globalPath = globalPath;
        _startDirectory = startDirectory;
        _localPath = localPath;
        _global = global;
        _local = local;
    }

    public static ConfigStore LoadDefault() =>
        Load(ConfigPaths.GlobalConfigPath, Environment.CurrentDirectory);

    public static ConfigStore Load(string globalPath, string startDirectory)
    {
        var localPath = ConfigPaths.FindLocalConfig(startDirectory);
        return new ConfigStore(
            globalPath,
            startDirectory,
            localPath,
            LoadFile(globalPath),
            localPath is null ? IniFile.Empty() : LoadFile(localPath));
    }

    static IniFile LoadFile(string path) =>
        File.Exists(path) ? IniFile.Parse(File.ReadAllText(path)) : IniFile.Empty();

    public string? GetEffective(ConfigKey key) =>
        _local.Get(key.Section, key.Name) ?? _global.Get(key.Section, key.Name);

    public IReadOnlyList<ConfigEntry> ListEffective()
    {
        var result = new List<ConfigEntry>();
        var seen = new HashSet<string>();
        // local wins, so collect local shadow keys first
        var localEntries = _localPath is null
            ? []
            : _local.Entries().Select(e => new ConfigEntry(KeyOf(e.Section, e.Key), e.Value, _localPath)).ToList();
        var localKeys = new HashSet<string>(localEntries.Select(e => e.Key));

        foreach (var (section, key, value) in _global.Entries())
        {
            var name = KeyOf(section, key);
            if (!localKeys.Contains(name) && seen.Add(name))
                result.Add(new ConfigEntry(name, value, _globalPath));
        }
        foreach (var entry in localEntries)
            if (seen.Add(entry.Key)) result.Add(entry);
        return result;
    }

    static string KeyOf(string section, string key) =>
        $"{section.ToLowerInvariant()}.{key.ToLowerInvariant()}";

    public string Set(ConfigKey key, string value, bool global)
    {
        var (file, path) = global
            ? (_global, _globalPath)
            : (_local, _localPath ?? Path.Combine(_startDirectory, ConfigPaths.LocalFileName));
        file.Set(key.Section, key.Name, value);
        Save(file, path);
        return path;
    }

    public bool Unset(ConfigKey key, bool global)
    {
        var (file, path) = global ? (_global, _globalPath) : (_local, _localPath);
        if (path is null || !file.Unset(key.Section, key.Name)) return false;
        Save(file, path);
        return true;
    }

    static void Save(IniFile file, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, file.ToText());
    }
}
```

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`
Expected: all tests PASS (30 total: 16 IniFile + 5 ConfigPaths + 6 ConfigKey theories counted as 7 rows + ConfigStore 9 — trust the run, the count just needs to be everything green).

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: layered ConfigStore with effective values, origins, and writes"
```

---

### Task 6: ConfigCommands + Program wiring

**Files:**
- Create: `src/Bmd/Commands/ConfigCommands.cs`, `tests/Bmd.Tests/Commands/ConfigCommandsTests.cs`
- Modify: `src/Bmd/Program.cs`

**Interfaces:**
- Consumes: `ConfigStore`, `ConfigKey`, `ConfigEntry` (Task 5).
- Produces: the `bmd config set|get|unset|list` command surface. `ConfigCommands` takes an optional `ConfigStore`-factory constructor for tests; ConsoleAppFramework uses the parameterless path via `LoadDefault()`.

- [ ] **Step 1: Write the failing tests**

Commands are tested directly (methods returning exit codes, console redirected), not through the framework — the framework's own parsing is not ours to test.

`tests/Bmd.Tests/Commands/ConfigCommandsTests.cs`:

```csharp
using Bmd.Commands;
using Bmd.Config;

namespace Bmd.Tests.Commands;

public class ConfigCommandsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");

    public ConfigCommandsTests()
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

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        Assert.Equal(0, Commands().Set("videohub.host", "10.0.0.5"));
        Assert.Equal(0, Commands().Get("videohub.host"));
        Assert.Equal("10.0.0.5", _stdout.ToString().Trim());
    }

    [Fact]
    public void Get_MissingKey_Exit1_MessageOnStderr()
    {
        Assert.Equal(1, Commands().Get("videohub.host"));
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("not set", _stderr.ToString());
    }

    [Fact]
    public void InvalidKeyFormat_Exit2()
    {
        Assert.Equal(2, Commands().Set("nodot", "x"));
        Assert.Equal(2, Commands().Get("nodot"));
        Assert.Equal(2, Commands().Unset("nodot"));
        Assert.Contains("section.key", _stderr.ToString());
    }

    [Fact]
    public void Unset_MissingKey_Exit1()
    {
        Assert.Equal(1, Commands().Unset("videohub.host"));
        Assert.Contains("not set", _stderr.ToString());
    }

    [Fact]
    public void List_PrintsEffectiveEntries()
    {
        Commands().Set("videohub.host", "10.0.0.5", global: true);
        Commands().Set("videohub.host", "10.0.0.9");
        Assert.Equal(0, Commands().List());
        Assert.Equal("videohub.host=10.0.0.9", _stdout.ToString().Trim());
    }

    [Fact]
    public void List_ShowOrigin_PrefixesFilePath()
    {
        Commands().Set("videohub.host", "10.0.0.9");
        Assert.Equal(0, Commands().List(showOrigin: true));
        var line = _stdout.ToString().Trim();
        Assert.Equal($"{Path.Combine(WorkDir, ConfigPaths.LocalFileName)}\tvideohub.host=10.0.0.9", line);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ConfigCommandsTests`
Expected: compilation failure — `ConfigCommands` does not exist.

- [ ] **Step 3: Implement `src/Bmd/Commands/ConfigCommands.cs` and wire into Program**

```csharp
using Bmd.Config;

namespace Bmd.Commands;

/// <summary>bmd config — git-style layered configuration.</summary>
public class ConfigCommands
{
    readonly Func<ConfigStore> _load;

    public ConfigCommands() : this(ConfigStore.LoadDefault) { }
    public ConfigCommands(Func<ConfigStore> load) => _load = load;

    /// <summary>Set a configuration value.</summary>
    /// <param name="key">Configuration key, e.g. videohub.host.</param>
    /// <param name="value">Value to assign.</param>
    /// <param name="global">-g, Write to the global config file instead of local .bmdconfig.</param>
    public int Set([Argument] string key, [Argument] string value, bool global = false)
    {
        if (!TryKey(key, out var k)) return 2;
        _load().Set(k, value, global);
        return 0;
    }

    /// <summary>Print the effective value of a configuration key.</summary>
    /// <param name="key">Configuration key, e.g. videohub.host.</param>
    public int Get([Argument] string key)
    {
        if (!TryKey(key, out var k)) return 2;
        var value = _load().GetEffective(k);
        if (value is null)
        {
            Console.Error.WriteLine($"error: '{k}' is not set");
            return 1;
        }
        Console.WriteLine(value);
        return 0;
    }

    /// <summary>Remove a configuration key.</summary>
    /// <param name="key">Configuration key, e.g. videohub.host.</param>
    /// <param name="global">-g, Remove from the global config file instead of local .bmdconfig.</param>
    public int Unset([Argument] string key, bool global = false)
    {
        if (!TryKey(key, out var k)) return 2;
        if (!_load().Unset(k, global))
        {
            Console.Error.WriteLine($"error: '{k}' is not set in the {(global ? "global" : "local")} config");
            return 1;
        }
        return 0;
    }

    /// <summary>List all effective configuration values.</summary>
    /// <param name="showOrigin">Prefix each value with the file it came from.</param>
    public int List(bool showOrigin = false)
    {
        foreach (var entry in _load().ListEffective())
            Console.WriteLine(showOrigin ? $"{entry.Origin}\t{entry.Key}={entry.Value}" : $"{entry.Key}={entry.Value}");
        return 0;
    }

    static bool TryKey(string raw, out ConfigKey key)
    {
        if (ConfigKey.TryParse(raw, out key)) return true;
        Console.Error.WriteLine($"error: key '{raw}' is not in section.key format (e.g. videohub.host)");
        return false;
    }
}
```

Note: `[Argument]` is `ConsoleAppFramework.ArgumentAttribute` — the source generator makes the namespace available globally; add `using ConsoleAppFramework;` if the build asks for it.

`src/Bmd/Program.cs` becomes:

```csharp
using Bmd.Commands;
using ConsoleAppFramework;

var app = ConsoleApp.Create();
app.Add<ConfigCommands>("config");
app.Run(args);
```

- [ ] **Step 4: Run all tests, then exercise the real CLI**

```powershell
dotnet test
dotnet run --project src/Bmd -- config set videohub.host 10.0.0.5
dotnet run --project src/Bmd -- config get videohub.host
dotnet run --project src/Bmd -- config list --show-origin
dotnet run --project src/Bmd -- config unset videohub.host
```

Expected: tests PASS; the CLI round-trip prints `10.0.0.5`, the list line shows the repo-root `.bmdconfig` path, unset succeeds. **Delete the `.bmdconfig` this created in the repo root afterwards** (`Remove-Item .bmdconfig`) — and add `.bmdconfig` to `.gitignore` so a developer's local test config never gets committed.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: bmd config set/get/unset/list commands"
```

---

### Task 7: Native AOT publish proven

**Files:**
- Modify: `src/Bmd/Bmd.csproj` (only if the publish surfaces warnings/errors)

**Interfaces:**
- Consumes: everything above.
- Produces: a published native `bmd.exe` that passes a config round-trip — the milestone's exit criterion.

- [ ] **Step 1: Publish for the host RID**

```powershell
dotnet publish src/Bmd -c Release -r win-x64
```

Expected: success, **zero AOT/trim analysis warnings** (IL2xxx/IL3xxx). Any such warning is a failure — fix the offending code rather than suppressing, and consult the spec's AOT constraint.

- [ ] **Step 2: Smoke-test the native binary**

```powershell
$exe = "src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe"
& $exe --help
& $exe config set videohub.host 10.0.0.5
& $exe config get videohub.host        # expect: 10.0.0.5
& $exe config list --show-origin
& $exe config unset videohub.host
& $exe config get videohub.host        # expect: exit code 1, "not set" on stderr
Remove-Item .bmdconfig -ErrorAction SilentlyContinue
```

Expected: help lists the `config` command; round-trip behaves exactly like `dotnet run` did; binary is a single native exe (a few MB).

- [ ] **Step 3: Record binary size and commit any csproj adjustments**

```powershell
(Get-Item src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe).Length / 1MB
git add -A; git commit -m "chore: prove Native AOT publish (milestone 1 complete)" --allow-empty
```

(`--allow-empty` because a clean publish may need no changes; the commit marks the milestone boundary either way.)

---

## Self-Review Notes

- **Spec coverage (milestone 1):** skeleton (Task 1), INI parser incl. subsection preservation and comments (Tasks 2-3), global/local paths + walk-up (Task 4), layering/precedence/origins/writes (Task 5), the four `bmd config` verbs with spec exit codes (Task 6), AOT proven (Task 7). Config precedence "flag > local > global" — the flag layer applies to device commands (milestone 2+); nothing to build here.
- **Deviation noted in Global Constraints:** framework-level argument parse failures exit 1 (ConsoleAppFramework default), not 2; our own invalid-key errors use 2. Revisit only if it bites.
- **Type consistency check:** `ConfigKey.TryParse`/`ConfigStore.Load(globalPath, startDirectory)`/`ConfigEntry(Key, Value, Origin)` names match across Tasks 5-6. `ConfigPaths.LocalFileName` used consistently.
