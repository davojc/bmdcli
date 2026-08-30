# Milestone 9: Self-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `bmd update [--check]` — detect, download, checksum-verify and self-install the latest GitHub release — plus the passive once-per-24h "a new release is available" notice.

**Architecture:** A new `src/Bmd/Update/` namespace holds the whole feature, layered the same way `Devices/` is: pure functions at the bottom (semver precedence, `checksums.txt` parsing), then a disk cache, then an HTTP client for the GitHub Releases API, then a platform-specific installer, with a thin `Commands/UpdateCommands.cs` on top that resolves, formats and returns exit codes. The passive notice runs concurrently with the invoked command and prints to stderr after it.

**Tech Stack:** .NET 10, Native AOT, ConsoleAppFramework v5, System.Text.Json source generators, `System.IO.Compression` (zip) + `System.Formats.Tar` (tar.gz), `System.Security.Cryptography.SHA256`, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` — sections "Self-update", "Distribution and releases", "Agents and scripting", "Configuration".

## Global Constraints

Copied verbatim from `CLAUDE.md` and the spec. Every task's requirements implicitly include this section.

- **Native AOT always on.** `PublishAot=true`. No reflection-dependent libraries, no `dynamic`, no reflection-based JSON. A dependency that isn't AOT/trim-safe doesn't get added. **No new NuGet packages in this milestone** — everything used here is in the BCL.
- **JSON: System.Text.Json source generators only.** Every serialized/deserialized type is registered on a `JsonSerializerContext`. `JsonSerializer.Serialize(object)` / `Deserialize<T>(string)` overloads that take no context are forbidden.
- **Agent-first interface.** Every command supports `--json`: exactly **one** JSON document on stdout, camelCase, stable field names. `--json` changes representation, never behavior. Errors stay plain `error: …` on stderr regardless of `--json`.
- **Help text is the API.** Every command, argument and flag is documented via XML doc comments — ConsoleAppFramework generates help from them. Units, ranges and defaults belong in the text.
- **Errors:** one clear message to stderr, no stack traces. **Exit codes: 0 success, 1 operation failure, 2 usage/format error.**
- **No environment variables for configuration.** Config comes from the layered store only (`--flag` > local `.bmdconfig` > global > built-in default). `XDG_*` reads inside `ConfigPaths` are OS path discovery, not bmd configuration, and are the sole exception that already exists.
- **Layering:** `Update/` never references ConsoleAppFramework or `Commands/`. Command classes stay thin (resolve config → call client → format).
- **TDD.** Write the failing test, watch it fail, implement, watch it pass, commit. Pure functions are tested directly; anything touching the network is tested through an injected fake; the executable swap is exercised on a **dummy file, never the running test binary**.
- **The Pages site ships with the change.** Any commit that adds or changes a command updates the relevant `site/` page in the same change.
- **Mutations back up first** applies to *device* state and is not in scope here — `bmd update` mutates the local binary, and its safety property is the checksum-verify-then-atomic-swap-with-restore-on-failure sequence in Task 5.

## Non-negotiable behaviors from the spec

- `bmd update --check` queries the latest **non-pre-release** release of `davojc/bmdcli`, compares semver against the embedded version, reports, and **exits 0** whether or not an update exists.
- `bmd update` verifies SHA-256 against the release's `checksums.txt` **before touching anything**. Bad checksum → abort, **exit 1, nothing changed**.
- Unix swap: extract next to the current executable, set the execute bit, atomic rename over the current path. Windows swap: `bmd.exe` → `bmd.exe.old`, new binary moved into place, `.old` silently cleaned up on a later run. **Any failure restores the original.** No write permission → clear error telling the user to re-run elevated.
- Passive notice is suppressed when: `update.check = false`, stderr is not a TTY, `--json` was passed, or the command is `bmd update` / `bmd version` itself.
- Passive notice text, exactly:
  ```
  A new release of bmd is available: 1.2.0 → 1.4.1
  Run `bmd update` to upgrade.
  ```

## Recorded design rulings

These resolve points where the spec is silent or where a literal reading needed a decision. They are binding for this plan.

1. **The passive check's fake is an `HttpMessageHandler`, not a socket server.** The spec says tests run "against a local fake releases HTTP server". A fake `HttpMessageHandler` injected into `HttpClient` is that fake server at the `HttpClient` boundary: it exercises URL construction, headers, status handling, JSON parsing and streaming to disk, without binding a port. `HttpListener` needs a fixed port (no port-0 support) and URL ACLs on Windows, which makes it flaky in CI for zero added coverage. Cost if wrong: real socket/TLS behavior is unverified by unit tests — accepted, since the release pipeline's own end-to-end download was already validated by hand for v0.1.0.

2. **The passive check joins its background fetch for at most 500 ms at exit.** The spec says the check "never delays the command" and runs as "a background task". A pure fire-and-forget task is killed when `Main` returns, so the cache would never be written and the feature would never fire. The fetch therefore starts *before* the command and runs concurrently with it — the command's own work is never blocked — and at exit the process waits up to 500 ms for it. This happens at most once per 24 hours, and only when stderr is a TTY and `--json` was not passed. **Task 6 updates the spec to say this.** Cost if wrong: a worst case of 500 ms added to one invocation per day.

3. **`ConfigPaths.CacheDirectory` on Windows is `%LOCALAPPDATA%\bmd`, the same directory `StateDirectory` returns.** That is what the spec specifies, and Windows has no separate cache convention. The two do not collide: backups live in the `backups\` subdirectory, the update cache is the file `update-check.json`. Task 2 documents the deliberate overlap in a code comment.

4. **A build with no RID (`unknown`, i.e. a plain `dotnet build`) refuses to self-install** with exit 1 and a message pointing at the releases page. It cannot know which asset to fetch. `--check` still works, because comparing versions needs no asset.

---

## File structure

**Created:**

| File | Responsibility |
|---|---|
| `src/Bmd/Update/SemVer.cs` | Parse and order semantic versions. Pure. |
| `src/Bmd/Update/Checksums.cs` | Parse `sha256sum`-format text; hash a local file. Pure. |
| `src/Bmd/Update/UpdateCheckCache.cs` | Read/write the 24h passive-check cache file. |
| `src/Bmd/Update/ReleaseInfo.cs` | GitHub release model + asset naming. Includes `UpdateException`. |
| `src/Bmd/Update/UpdateJsonContext.cs` | Source-gen JSON context for the GitHub DTOs and the cache file. |
| `src/Bmd/Update/ReleaseClient.cs` | HTTP against the GitHub Releases API; download to file. |
| `src/Bmd/Update/UpdateInstaller.cs` | Extract the archive; platform-specific executable swap. |
| `src/Bmd/Update/UpdateNotice.cs` | Passive-notice eligibility + text formatting. Pure. |
| `src/Bmd/Update/UpdateNoticeRunner.cs` | Starts the concurrent check; writes the notice at exit. |
| `src/Bmd/Commands/UpdateCommands.cs` | The `bmd update` command. |
| `src/Bmd/Commands/UpdateResults.cs` | `--json` result records. |
| `tests/Bmd.Tests/Update/*` | Tests for the above. |
| `tests/Bmd.Tests/Commands/UpdateCommandsTests.cs` | Command-level tests. |

**Modified:** `src/Bmd/Config/ConfigPaths.cs` (add `CacheDirectory`), `src/Bmd/Output/BmdJsonContext.cs` (two result records), `src/Bmd/Program.cs` (register `update`, wire the notice), `src/Bmd/Commands/GroupHelp.cs` (table entry), `site/index.html`, `docs/superpowers/specs/2026-08-29-bmd-cli-design.md`, `CLAUDE.md`.

---

### Task 1: Semantic versioning and checksum parsing

Two pure, dependency-free building blocks. Everything later in the milestone compares versions with one and verifies downloads with the other.

**Files:**
- Create: `src/Bmd/Update/SemVer.cs`
- Create: `src/Bmd/Update/Checksums.cs`
- Test: `tests/Bmd.Tests/Update/SemVerTests.cs`
- Test: `tests/Bmd.Tests/Update/ChecksumsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `Bmd.Update.SemVer` — `readonly record struct SemVer(int Major, int Minor, int Patch, string PreRelease) : IComparable<SemVer>`, with `bool IsPreRelease`, `static bool TryParse(string? raw, out SemVer version)`, `int CompareTo(SemVer other)`, `override string ToString()`.
  - `Bmd.Update.Checksums` — `static bool TryFind(string checksumsText, string assetName, out string sha256Hex)` and `static string OfFile(string path)` (lowercase hex).

- [ ] **Step 1: Write the failing SemVer tests**

Create `tests/Bmd.Tests/Update/SemVerTests.cs`:

```csharp
using Bmd.Update;

namespace Bmd.Tests.Update;

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("V1.2.3", 1, 2, 3, "")]
    [InlineData("0.1.0", 0, 1, 0, "")]
    [InlineData("1.2.3-rc.1", 1, 2, 3, "rc.1")]
    [InlineData("v1.2.3-rc.1", 1, 2, 3, "rc.1")]
    [InlineData("0.0.0-dev", 0, 0, 0, "dev")]
    [InlineData("1.2.3+build.5", 1, 2, 3, "")]
    [InlineData("1.2.3-rc.1+build.5", 1, 2, 3, "rc.1")]
    [InlineData("  1.2.3  ", 1, 2, 3, "")]
    public void TryParse_AcceptsValidVersions(string raw, int major, int minor, int patch, string pre)
    {
        Assert.True(SemVer.TryParse(raw, out var version));
        Assert.Equal(new SemVer(major, minor, patch, pre), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("abc")]
    [InlineData("1.-2.3")]
    [InlineData("1. 2.3")]
    [InlineData("+1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-rc..1")]
    [InlineData("1.2.3-rc$1")]
    [InlineData("99999999999.0.0")] // overflows int
    public void TryParse_RejectsInvalidVersions(string? raw)
    {
        Assert.False(SemVer.TryParse(raw, out _));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("1.2.3", "2.0.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.2.3-rc.1", "1.2.3")]      // a pre-release precedes its release
    [InlineData("1.2.3-rc.1", "1.2.3-rc.2")]
    [InlineData("1.2.3-rc.2", "1.2.3-rc.10")] // numeric identifiers compare numerically
    [InlineData("1.2.3-alpha", "1.2.3-beta")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1.1")] // a longer identifier set wins when equal so far
    [InlineData("1.2.3-1", "1.2.3-alpha")]     // numeric ranks below alphanumeric
    [InlineData("0.1.0-rc.1", "0.1.0")]
    [InlineData("0.0.0-dev", "0.1.0")]
    public void CompareTo_OrdersLowerBeforeHigher(string lower, string higher)
    {
        Assert.True(SemVer.TryParse(lower, out var a));
        Assert.True(SemVer.TryParse(higher, out var b));
        Assert.True(a.CompareTo(b) < 0, $"{lower} should sort before {higher}");
        Assert.True(b.CompareTo(a) > 0, $"{higher} should sort after {lower}");
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+a", "1.2.3+b")] // build metadata takes no part in precedence
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    public void CompareTo_TreatsEquivalentVersionsAsEqual(string left, string right)
    {
        Assert.True(SemVer.TryParse(left, out var a));
        Assert.True(SemVer.TryParse(right, out var b));
        Assert.Equal(0, a.CompareTo(b));
        Assert.Equal(a, b);
    }

    [Fact]
    public void IsPreRelease_IsTrueOnlyWithASuffix()
    {
        Assert.True(SemVer.TryParse("1.2.3-rc.1", out var pre));
        Assert.True(pre.IsPreRelease);
        Assert.True(SemVer.TryParse("1.2.3", out var release));
        Assert.False(release.IsPreRelease);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    [InlineData("1.2.3+meta", "1.2.3")]
    public void ToString_RoundTripsWithoutTheTagPrefixOrBuildMetadata(string raw, string expected)
    {
        Assert.True(SemVer.TryParse(raw, out var version));
        Assert.Equal(expected, version.ToString());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~SemVerTests`
Expected: FAIL — compile error, `SemVer` does not exist.

- [ ] **Step 3: Implement SemVer**

Create `src/Bmd/Update/SemVer.cs`:

```csharp
namespace Bmd.Update;

/// <summary>A semantic version, parsed from a release tag (<c>v1.2.0</c>) or from the
/// build-stamped version string (<c>1.2.0</c>, or <c>0.0.0-dev</c> for a local build).
/// Ordering follows the semver 2.0.0 precedence rules, which is what makes
/// <c>0.1.0-rc.1 &lt; 0.1.0</c> come out right — the pre-release tags this project publishes
/// must not read as newer than the release they precede.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string PreRelease)
    : IComparable<SemVer>
{
    public bool IsPreRelease => PreRelease.Length > 0;

    /// <summary>Parses a version, tolerating a leading <c>v</c>/<c>V</c> (git tags carry one)
    /// and surrounding whitespace. Build metadata (<c>+…</c>) is discarded: semver says it takes
    /// no part in precedence, so keeping it would make two equal versions compare unequal.</summary>
    public static bool TryParse(string? raw, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        if (text[0] is 'v' or 'V') text = text[1..];

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        var pre = "";
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            pre = text[(dash + 1)..];
            text = text[..dash];
            if (pre.Length == 0) return false;
            foreach (var part in pre.Split('.'))
            {
                if (part.Length == 0) return false;
                foreach (var c in part)
                    if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        if (!TryNumber(parts[0], out var major)) return false;
        if (!TryNumber(parts[1], out var minor)) return false;
        if (!TryNumber(parts[2], out var patch)) return false;

        version = new SemVer(major, minor, patch, pre);
        return true;
    }

    /// <summary>Digits only, then <see cref="int.TryParse(string?, out int)"/>. The digit check
    /// rejects the signs and whitespace TryParse would otherwise accept; TryParse then rejects
    /// anything too large for an int.</summary>
    static bool TryNumber(string part, out int value)
    {
        value = 0;
        if (part.Length == 0) return false;
        foreach (var c in part)
            if (!char.IsAsciiDigit(c)) return false;
        return int.TryParse(part, out value);
    }

    public int CompareTo(SemVer other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);
        if (PreRelease == other.PreRelease) return 0;
        // "1.2.3" is newer than "1.2.3-rc.1": a version without a pre-release suffix wins.
        if (PreRelease.Length == 0) return 1;
        if (other.PreRelease.Length == 0) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>Semver pre-release precedence: compare dot-separated identifiers left to right;
    /// all-numeric identifiers compare numerically and rank below alphanumeric ones; if every
    /// shared identifier is equal, the longer set wins.</summary>
    static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = IsNumeric(a[i]);
            var bNumeric = IsNumeric(b[i]);
            int result;
            if (aNumeric && bNumeric)
            {
                // Compared as digit strings rather than parsed ints: a pre-release identifier
                // has no length limit, so parsing could overflow. With leading zeros stripped,
                // "longer number is larger, else ordinal" is exactly numeric ordering.
                var x = a[i].TrimStart('0');
                var y = b[i].TrimStart('0');
                result = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            }
            else if (aNumeric) result = -1;
            else if (bNumeric) result = 1;
            else result = string.CompareOrdinal(a[i], b[i]);

            if (result != 0) return Math.Sign(result);
        }
        return a.Length.CompareTo(b.Length);
    }

    static bool IsNumeric(string part)
    {
        foreach (var c in part)
            if (!char.IsAsciiDigit(c)) return false;
        return part.Length > 0;
    }

    public override string ToString() =>
        PreRelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
```

- [ ] **Step 4: Run the SemVer tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~SemVerTests`
Expected: PASS, all tests green.

- [ ] **Step 5: Write the failing Checksums tests**

Create `tests/Bmd.Tests/Update/ChecksumsTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Bmd.Update;

namespace Bmd.Tests.Update;

public class ChecksumsTests
{
    // The exact shape `sha256sum * > checksums.txt` produces in .github/workflows/release.yml:
    // a 64-character lowercase digest, two spaces, the bare archive name.
    const string RealisticFile =
        "3b1f2c4d5e6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e  bmd-linux-x64.tar.gz\n" +
        "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90  bmd-win-x64.zip\n";

    [Fact]
    public void TryFind_ReturnsTheDigestForTheNamedAsset()
    {
        Assert.True(Checksums.TryFind(RealisticFile, "bmd-win-x64.zip", out var digest));
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", digest);
    }

    [Fact]
    public void TryFind_IsNotFooledByAPrefixOfAnotherName()
    {
        Assert.False(Checksums.TryFind(RealisticFile, "bmd-win-x64", out _));
        Assert.False(Checksums.TryFind(RealisticFile, "bmd-osx-arm64.tar.gz", out _));
    }

    [Fact]
    public void TryFind_AcceptsTheBinaryModeStarAndCrLfLineEndings()
    {
        const string text =
            "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90 *bmd-win-x64.zip\r\n";
        Assert.True(Checksums.TryFind(text, "bmd-win-x64.zip", out var digest));
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", digest);
    }

    [Fact]
    public void TryFind_NormalizesUppercaseDigestsToLowercase()
    {
        const string text =
            "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90  bmd-win-x64.zip\n";
        Assert.True(Checksums.TryFind(text, "bmd-win-x64.zip", out var digest));
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", digest);
    }

    [Fact]
    public void TryFind_SkipsBlankAndMalformedLinesRatherThanFailing()
    {
        const string text =
            "\n" +
            "# a comment some future release job might add\n" +
            "not-a-digest  bmd-win-x64.zip\n" +
            "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90  bmd-win-x64.zip\n";
        Assert.True(Checksums.TryFind(text, "bmd-win-x64.zip", out var digest));
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", digest);
    }

    [Fact]
    public void TryFind_ReturnsFalseForAnAssetThatIsNotListed()
    {
        Assert.False(Checksums.TryFind(RealisticFile, "bmd-osx-x64.tar.gz", out var digest));
        Assert.Equal("", digest);
    }

    [Fact]
    public void TryFind_ReturnsFalseForEmptyText()
    {
        Assert.False(Checksums.TryFind("", "bmd-win-x64.zip", out _));
    }

    [Fact]
    public void OfFile_MatchesAKnownSha256()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bmd-checksum-{Guid.NewGuid():N}.bin");
        var content = "the quick brown fox"u8.ToArray();
        File.WriteAllBytes(path, content);
        try
        {
            var expected = Convert.ToHexStringLower(SHA256.HashData(content));
            Assert.Equal(expected, Checksums.OfFile(path));
            Assert.Equal(64, Checksums.OfFile(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OfFile_DiffersWhenAsingleByteChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-checksum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var a = Path.Combine(directory, "a.bin");
            var b = Path.Combine(directory, "b.bin");
            File.WriteAllBytes(a, [1, 2, 3, 4]);
            File.WriteAllBytes(b, [1, 2, 3, 5]);
            Assert.NotEqual(Checksums.OfFile(a), Checksums.OfFile(b));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~ChecksumsTests`
Expected: FAIL — compile error, `Checksums` does not exist.

- [ ] **Step 7: Implement Checksums**

Create `src/Bmd/Update/Checksums.cs`:

```csharp
using System.Security.Cryptography;

namespace Bmd.Update;

/// <summary>Reads the <c>checksums.txt</c> published with every release and hashes a
/// downloaded file so the two can be compared before anything is installed.</summary>
public static class Checksums
{
    /// <summary>Finds the SHA-256 recorded for <paramref name="assetName"/>, returned as
    /// lowercase hex. Lines are the <c>sha256sum</c> format the release job writes: a
    /// 64-character hex digest, whitespace, then the file name — optionally prefixed with
    /// <c>*</c>, which coreutils emits in binary mode. Lines that don't match that shape are
    /// skipped rather than treated as an error, so a header or comment added to the file in a
    /// future release cannot break updating for already-shipped binaries. First match wins.</summary>
    public static bool TryFind(string checksumsText, string assetName, out string sha256Hex)
    {
        sha256Hex = "";
        foreach (var rawLine in checksumsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var space = line.IndexOfAny([' ', '\t']);
            if (space <= 0) continue;

            var digest = line[..space];
            if (!IsSha256Hex(digest)) continue;

            var name = line[(space + 1)..].TrimStart(' ', '\t');
            if (name.StartsWith('*')) name = name[1..];
            if (!name.Equals(assetName, StringComparison.Ordinal)) continue;

            sha256Hex = digest.ToLowerInvariant();
            return true;
        }
        return false;
    }

    static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>Lowercase hex SHA-256 of a file's contents, streamed rather than buffered so a
    /// multi-megabyte archive never has to be held in memory.</summary>
    public static string OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
```

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: PASS — every pre-existing test still green, plus the new ones.

- [ ] **Step 9: Commit**

```bash
git add src/Bmd/Update/SemVer.cs src/Bmd/Update/Checksums.cs tests/Bmd.Tests/Update/
git commit -m "feat(update): semver precedence and checksums.txt parsing"
```

---

### Task 2: Cache directory and the 24-hour update-check cache

The passive notice must not hit the network more than once a day, so the last result is cached in the OS cache directory. Nothing in this task talks to the network.

**Files:**
- Modify: `src/Bmd/Config/ConfigPaths.cs` (add `CacheDirectory` after `StateDirectory`)
- Create: `src/Bmd/Update/UpdateCheckCache.cs`
- Create: `src/Bmd/Update/UpdateJsonContext.cs`
- Test: `tests/Bmd.Tests/Update/UpdateCheckCacheTests.cs`
- Test: `tests/Bmd.Tests/Config/ConfigPathsTests.cs` (add cases)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `Bmd.Config.ConfigPaths.CacheDirectory` — `static string { get; }`.
  - `Bmd.Update.UpdateCheckEntry` — `sealed record UpdateCheckEntry(string LatestVersion, DateTimeOffset CheckedAt)`.
  - `Bmd.Update.UpdateCheckCache` — `sealed class` with `UpdateCheckCache(string filePath)`, `static UpdateCheckCache Default()`, `string FilePath { get; }`, `UpdateCheckEntry? Read()`, `void Write(UpdateCheckEntry entry)`, `static bool IsStale(UpdateCheckEntry? entry, DateTimeOffset now)`.
  - `Bmd.Update.UpdateJsonContext` — source-gen context; in this task it registers `UpdateCheckEntry`. Task 3 adds the GitHub DTOs to the same context.

- [ ] **Step 1: Write the failing ConfigPaths test**

Append to `tests/Bmd.Tests/Config/ConfigPathsTests.cs` (inside the existing test class — keep the file's existing namespace, class name and usings):

```csharp
    [Fact]
    public void CacheDirectory_EndsInBmdAndIsRooted()
    {
        var path = ConfigPaths.CacheDirectory;
        Assert.True(Path.IsPathRooted(path));
        Assert.Equal("bmd", Path.GetFileName(path));
    }

    [Fact]
    public void CacheDirectory_IsDistinctFromTheGlobalConfigFile()
    {
        // Cache is disposable; config is not. They must never be the same location.
        Assert.NotEqual(
            Path.GetFullPath(ConfigPaths.GlobalConfigPath),
            Path.GetFullPath(ConfigPaths.CacheDirectory));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~ConfigPathsTests`
Expected: FAIL — compile error, `ConfigPaths.CacheDirectory` does not exist.

- [ ] **Step 3: Add CacheDirectory**

In `src/Bmd/Config/ConfigPaths.cs`, add this property immediately after the `StateDirectory` property:

```csharp
    /// <summary>OS cache directory for data bmd can lose without consequence — currently just
    /// the once-a-day update check result. Distinct in meaning from config (settings the user
    /// owns) and state (backups, which must survive).
    ///
    /// On Windows this resolves to the same <c>%LOCALAPPDATA%\bmd</c> as
    /// <see cref="StateDirectory"/>, deliberately: Windows has no separate cache convention, and
    /// the spec names this exact path. The two do not collide — backups live in the
    /// <c>backups\</c> subdirectory, the update check is the file <c>update-check.json</c>.</summary>
    public static string CacheDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bmd");
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            return string.IsNullOrEmpty(xdg)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "bmd")
                : Path.Combine(xdg, "bmd");
        }
    }
```

- [ ] **Step 4: Write the failing UpdateCheckCache tests**

Create `tests/Bmd.Tests/Update/UpdateCheckCacheTests.cs`:

```csharp
using Bmd.Update;

namespace Bmd.Tests.Update;

public class UpdateCheckCacheTests : IDisposable
{
    readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bmd-cache-{Guid.NewGuid():N}");

    string CacheFile => Path.Combine(_directory, "update-check.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Read_ReturnsNullWhenTheFileDoesNotExist()
    {
        Assert.Null(new UpdateCheckCache(CacheFile).Read());
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var cache = new UpdateCheckCache(CacheFile);
        var checkedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        cache.Write(new UpdateCheckEntry("1.4.1", checkedAt));

        var entry = cache.Read();
        Assert.NotNull(entry);
        Assert.Equal("1.4.1", entry!.LatestVersion);
        Assert.Equal(checkedAt, entry.CheckedAt);
    }

    [Fact]
    public void Write_CreatesTheDirectoryIfItIsMissing()
    {
        Assert.False(Directory.Exists(_directory));
        new UpdateCheckCache(CacheFile).Write(new UpdateCheckEntry("1.0.0", DateTimeOffset.UtcNow));
        Assert.True(File.Exists(CacheFile));
    }

    [Fact]
    public void Read_ReturnsNullForACorruptFileRatherThanThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CacheFile, "this is not json {{{");
        Assert.Null(new UpdateCheckCache(CacheFile).Read());
    }

    [Fact]
    public void Read_ReturnsNullWhenTheJsonIsWellFormedButHasNoVersion()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CacheFile, """{"checkedAt":"2026-08-30T12:00:00+00:00"}""");
        Assert.Null(new UpdateCheckCache(CacheFile).Read());
    }

    [Fact]
    public void Write_SwallowsIoFailuresBecauseAPassiveCheckMustNeverBreakACommand()
    {
        // A path whose parent is an existing *file* cannot be created as a directory.
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "");
        var cache = new UpdateCheckCache(Path.Combine(blocker, "update-check.json"));

        cache.Write(new UpdateCheckEntry("1.0.0", DateTimeOffset.UtcNow)); // must not throw
        Assert.Null(cache.Read());
    }

    [Fact]
    public void IsStale_IsTrueWhenThereIsNoEntry()
    {
        Assert.True(UpdateCheckCache.IsStale(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsStale_IsFalseWithinTwentyFourHours()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var entry = new UpdateCheckEntry("1.0.0", now.AddHours(-23));
        Assert.False(UpdateCheckCache.IsStale(entry, now));
    }

    [Fact]
    public void IsStale_IsTrueAtExactlyTwentyFourHours()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var entry = new UpdateCheckEntry("1.0.0", now.AddHours(-24));
        Assert.True(UpdateCheckCache.IsStale(entry, now));
    }

    [Fact]
    public void IsStale_IsTrueForAnEntryStampedInTheFuture()
    {
        // Clock skew (or a hand-edited file) must not pin the cache as fresh indefinitely.
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var entry = new UpdateCheckEntry("1.0.0", now.AddHours(1));
        Assert.True(UpdateCheckCache.IsStale(entry, now));
    }

    [Fact]
    public void Default_PointsIntoTheOsCacheDirectory()
    {
        var path = UpdateCheckCache.Default().FilePath;
        Assert.Equal("update-check.json", Path.GetFileName(path));
        Assert.StartsWith(Bmd.Config.ConfigPaths.CacheDirectory, path, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 5: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~UpdateCheckCacheTests`
Expected: FAIL — compile error, `UpdateCheckCache` does not exist.

- [ ] **Step 6: Implement the JSON context**

Create `src/Bmd/Update/UpdateJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Bmd.Update;

/// <summary>Source-generated JSON for everything the update feature reads or writes: the
/// GitHub Releases API responses and bmd's own update-check cache file. Separate from
/// <c>BmdJsonContext</c>, which is strictly the shape of <c>--json</c> command output — these
/// types are internal plumbing and are not part of the CLI's published contract.
///
/// Reflection-based serialization is forbidden project-wide (Native AOT), so every type
/// crossing this boundary is registered here.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateCheckEntry))]
public partial class UpdateJsonContext : JsonSerializerContext;
```

- [ ] **Step 7: Implement UpdateCheckCache**

Create `src/Bmd/Update/UpdateCheckCache.cs`:

```csharp
using System.Text.Json;
using Bmd.Config;

namespace Bmd.Update;

/// <summary>The result of the last passive update check: the newest release version seen, and
/// when it was seen.</summary>
public sealed record UpdateCheckEntry(string LatestVersion, DateTimeOffset CheckedAt);

/// <summary>The once-a-day passive check's memory, stored in the OS cache directory.
///
/// Every operation here is best-effort by design: a passive check exists to be helpful, so a
/// missing, corrupt, or unwritable cache file must degrade to "no notice this run" and never
/// surface an error or fail the command the user actually asked for.</summary>
public sealed class UpdateCheckCache(string filePath)
{
    /// <summary>How long a cached result is trusted before the next check is allowed.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public string FilePath { get; } = filePath;

    public static UpdateCheckCache Default() =>
        new(Path.Combine(ConfigPaths.CacheDirectory, "update-check.json"));

    /// <summary>The cached entry, or null when there is none, it cannot be read, or it does not
    /// carry a version.</summary>
    public UpdateCheckEntry? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var entry = JsonSerializer.Deserialize(
                File.ReadAllText(FilePath), UpdateJsonContext.Default.UpdateCheckEntry);
            return string.IsNullOrWhiteSpace(entry?.LatestVersion) ? null : entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Write(UpdateCheckEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(entry, UpdateJsonContext.Default.UpdateCheckEntry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do and nothing worth saying: the next run simply checks again.
        }
    }

    /// <summary>Whether a fresh check is due. A missing entry is stale, and so is one stamped in
    /// the future — clock skew or a hand-edited file must not pin the cache as fresh forever.</summary>
    public static bool IsStale(UpdateCheckEntry? entry, DateTimeOffset now) =>
        entry is null || entry.CheckedAt > now || now - entry.CheckedAt >= MaxAge;
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter "FullyQualifiedName~UpdateCheckCacheTests|FullyQualifiedName~ConfigPathsTests"`
Expected: PASS.

- [ ] **Step 9: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add src/Bmd/Config/ConfigPaths.cs src/Bmd/Update/ tests/Bmd.Tests/
git commit -m "feat(update): OS cache directory and the 24h update-check cache"
```

---

### Task 3: GitHub release client

Fetches the latest non-pre-release from the public GitHub Releases API, and downloads assets. This is the only file in the milestone that touches the network.

**Files:**
- Create: `src/Bmd/Update/ReleaseInfo.cs`
- Create: `src/Bmd/Update/ReleaseClient.cs`
- Modify: `src/Bmd/Update/UpdateJsonContext.cs` (register the GitHub DTOs)
- Test: `tests/Bmd.Tests/Update/FakeHttpHandler.cs`
- Test: `tests/Bmd.Tests/Update/ReleaseClientTests.cs`

**Interfaces:**
- Consumes: `Bmd.Update.UpdateJsonContext` (Task 2).
- Produces:
  - `Bmd.Update.UpdateException : Exception` — `UpdateException(string message)`; carries a user-ready message.
  - `Bmd.Update.ReleaseAsset` — `sealed record ReleaseAsset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("browser_download_url")] string DownloadUrl)`.
  - `Bmd.Update.ReleaseInfo` — `sealed record ReleaseInfo(string TagName, bool PreRelease, ReleaseAsset[] Assets)` with `ReleaseAsset? FindAsset(string name)` and `static string ArchiveName(string runtimeIdentifier)`.
  - `Bmd.Update.ReleaseClient` — `sealed class`; `const string Repository = "davojc/bmdcli"`, `const string DefaultApiBase = "https://api.github.com"`, `const string ReleasesPageUrl = "https://github.com/davojc/bmdcli/releases/latest"`; `static HttpClient CreateHttpClient(string userAgentVersion)`; `ReleaseClient(HttpClient http, string apiBase = DefaultApiBase)`; `Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken ct)`; `Task<string> GetTextAsync(string url, CancellationToken ct)`; `Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct)`.

- [ ] **Step 1: Write the fake HTTP handler**

Create `tests/Bmd.Tests/Update/FakeHttpHandler.cs`. This is the "local fake releases server" the spec calls for, standing in at the `HttpClient` boundary — see ruling 1 in the plan header.

```csharp
using System.Net;

namespace Bmd.Tests.Update;

/// <summary>An in-process stand-in for the GitHub releases host. Routes are matched on the
/// request's absolute URI; anything unrouted answers 404, which is what a real server does and
/// keeps a test that mistypes a URL honest.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    /// <summary>Every absolute URI this handler has been asked for, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Headers of the most recent request, for asserting the User-Agent GitHub requires.</summary>
    public HttpRequestHeaders? LastRequestHeaders { get; private set; }

    public FakeHttpHandler Text(string url, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = () => new HttpResponseMessage(status) { Content = new StringContent(body) };
        return this;
    }

    public FakeHttpHandler Bytes(string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = () => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        return this;
    }

    public FakeHttpHandler Throws(string url, Exception exception)
    {
        _routes[url] = () => throw exception;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);
        LastRequestHeaders = request.Headers;
        if (!_routes.TryGetValue(url, out var respond))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"no route for {url}")
            });
        return Task.FromResult(respond());
    }
}
```

Add `using System.Net.Http.Headers;` at the top of that file for `HttpRequestHeaders`.

- [ ] **Step 2: Write the failing ReleaseClient tests**

Create `tests/Bmd.Tests/Update/ReleaseClientTests.cs`:

```csharp
using System.Net;
using Bmd.Update;

namespace Bmd.Tests.Update;

public class ReleaseClientTests
{
    const string ApiBase = "https://api.example.test";
    const string LatestUrl = "https://api.example.test/repos/davojc/bmdcli/releases/latest";

    const string LatestJson = """
        {
          "tag_name": "v0.2.0",
          "prerelease": false,
          "assets": [
            { "name": "bmd-win-x64.zip",
              "browser_download_url": "https://downloads.example.test/bmd-win-x64.zip" },
            { "name": "bmd-linux-x64.tar.gz",
              "browser_download_url": "https://downloads.example.test/bmd-linux-x64.tar.gz" },
            { "name": "checksums.txt",
              "browser_download_url": "https://downloads.example.test/checksums.txt" }
          ]
        }
        """;

    static ReleaseClient ClientFor(FakeHttpHandler handler) =>
        new(new HttpClient(handler), ApiBase);

    [Fact]
    public async Task GetLatestReleaseAsync_ParsesTagAssetsAndPreReleaseFlag()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, LatestJson);

        var release = await ClientFor(handler).GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        Assert.Equal("v0.2.0", release.TagName);
        Assert.False(release.PreRelease);
        Assert.Equal(3, release.Assets.Length);
        Assert.Equal(LatestUrl, Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task GetLatestReleaseAsync_SendsAUserAgentBecauseGitHubRejectsRequestsWithout()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, LatestJson);
        var http = ReleaseClient.CreateHttpClient("0.1.0");
        // Swap in the fake transport while keeping the headers CreateHttpClient configured.
        var client = new ReleaseClient(new HttpClient(handler)
        {
            DefaultRequestHeaders = { { "User-Agent", $"bmd/0.1.0" } }
        }, ApiBase);
        http.Dispose();

        await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequestHeaders);
        Assert.Contains(handler.LastRequestHeaders!.UserAgent,
            product => product.Product?.Name == "bmd");
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsAReadableUpdateExceptionOnHttpFailure()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, "rate limited", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => ClientFor(handler).GetLatestReleaseAsync(TestContext.Current.CancellationToken));

        Assert.Contains("403", ex.Message);
        Assert.DoesNotContain("Exception", ex.Message);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsUpdateExceptionOnMalformedJson()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, "{ this is not json");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => ClientFor(handler).GetLatestReleaseAsync(TestContext.Current.CancellationToken));

        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsUpdateExceptionWhenTheNetworkIsUnreachable()
    {
        var handler = new FakeHttpHandler().Throws(LatestUrl, new HttpRequestException("no such host"));

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => ClientFor(handler).GetLatestReleaseAsync(TestContext.Current.CancellationToken));

        Assert.Contains("no such host", ex.Message);
    }

    [Fact]
    public async Task GetTextAsync_ReturnsTheBody()
    {
        const string url = "https://downloads.example.test/checksums.txt";
        var handler = new FakeHttpHandler().Text(url, "abc  bmd-win-x64.zip\n");

        var text = await ClientFor(handler).GetTextAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal("abc  bmd-win-x64.zip\n", text);
    }

    [Fact]
    public async Task DownloadToFileAsync_WritesTheBytesToDisk()
    {
        const string url = "https://downloads.example.test/bmd-win-x64.zip";
        var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 };
        var handler = new FakeHttpHandler().Bytes(url, payload);
        var destination = Path.Combine(Path.GetTempPath(), $"bmd-dl-{Guid.NewGuid():N}.zip");

        try
        {
            await ClientFor(handler).DownloadToFileAsync(url, destination, TestContext.Current.CancellationToken);
            Assert.Equal(payload, File.ReadAllBytes(destination));
        }
        finally { if (File.Exists(destination)) File.Delete(destination); }
    }

    [Fact]
    public async Task DownloadToFileAsync_LeavesNoPartialFileWhenTheServerErrors()
    {
        const string url = "https://downloads.example.test/bmd-win-x64.zip";
        var handler = new FakeHttpHandler().Text(url, "gone", HttpStatusCode.NotFound);
        var destination = Path.Combine(Path.GetTempPath(), $"bmd-dl-{Guid.NewGuid():N}.zip");

        try
        {
            await Assert.ThrowsAsync<UpdateException>(
                () => ClientFor(handler).DownloadToFileAsync(url, destination, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(destination));
        }
        finally { if (File.Exists(destination)) File.Delete(destination); }
    }

    [Theory]
    [InlineData("win-x64", "bmd-win-x64.zip")]
    [InlineData("linux-x64", "bmd-linux-x64.tar.gz")]
    [InlineData("linux-arm64", "bmd-linux-arm64.tar.gz")]
    [InlineData("osx-x64", "bmd-osx-x64.tar.gz")]
    [InlineData("osx-arm64", "bmd-osx-arm64.tar.gz")]
    public void ArchiveName_MatchesTheNamesTheReleaseWorkflowPublishes(string rid, string expected)
    {
        Assert.Equal(expected, ReleaseInfo.ArchiveName(rid));
    }

    [Fact]
    public void FindAsset_MatchesByExactNameAndReturnsNullOtherwise()
    {
        var release = new ReleaseInfo("v0.2.0", false,
        [
            new ReleaseAsset("bmd-win-x64.zip", "https://downloads.example.test/bmd-win-x64.zip"),
        ]);

        Assert.NotNull(release.FindAsset("bmd-win-x64.zip"));
        Assert.Null(release.FindAsset("bmd-osx-arm64.tar.gz"));
    }
}
```

Note on `TestContext.Current.CancellationToken`: if the installed xUnit version does not expose it, pass `CancellationToken.None` instead — the assertions are unaffected either way. Verify which applies before writing the file by checking how other async tests in `tests/Bmd.Tests/Devices/Videohub/VideohubClientTests.cs` obtain a token, and match that file's convention exactly.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~ReleaseClientTests`
Expected: FAIL — compile error, `ReleaseClient` does not exist.

- [ ] **Step 4: Implement the release model**

Create `src/Bmd/Update/ReleaseInfo.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Bmd.Update;

/// <summary>An update failed for a reason the user can act on. Carries a message that is already
/// fit to print after "error: " — no stack trace, no exception type names.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>One downloadable file attached to a GitHub release. Property names are pinned with
/// explicit attributes rather than left to a naming policy: this is an external API's contract,
/// not ours, and <c>browser_download_url</c> is not any camelCase policy's output.</summary>
public sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl);

/// <summary>A GitHub release, as much of it as updating needs.</summary>
public sealed record ReleaseInfo(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("prerelease")] bool PreRelease,
    [property: JsonPropertyName("assets")] ReleaseAsset[] Assets)
{
    public ReleaseAsset? FindAsset(string name) =>
        Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.Ordinal));

    /// <summary>The archive name the release workflow publishes for a runtime identifier.
    /// Keyed off the RID string rather than the running OS so it stays a pure function —
    /// and so a win-x64 binary asks for the zip even when something exotic hosts it.
    /// Mirrors the `archive:` entries in .github/workflows/release.yml; the Unix RIDs use
    /// tar.gz because zip does not reliably preserve the executable bit.</summary>
    public static string ArchiveName(string runtimeIdentifier) =>
        runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal)
            ? $"bmd-{runtimeIdentifier}.zip"
            : $"bmd-{runtimeIdentifier}.tar.gz";

    /// <summary>The checksums file every release carries, listing the SHA-256 of each archive.</summary>
    public const string ChecksumsAssetName = "checksums.txt";
}
```

- [ ] **Step 5: Register the DTOs on the JSON context**

In `src/Bmd/Update/UpdateJsonContext.cs`, add above the existing `UpdateCheckEntry` registration:

```csharp
[JsonSerializable(typeof(ReleaseInfo))]
[JsonSerializable(typeof(ReleaseAsset))]
```

- [ ] **Step 6: Implement ReleaseClient**

Create `src/Bmd/Update/ReleaseClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bmd.Update;

/// <summary>Talks to the public GitHub Releases API for <see cref="Repository"/> and downloads
/// release assets. No authentication: the repository is public, and asking a user for a token to
/// install an update would be absurd. Every failure mode is translated into an
/// <see cref="UpdateException"/> whose message is ready to print — the command layer above never
/// has to know what an <c>HttpRequestException</c> is.</summary>
public sealed class ReleaseClient(HttpClient http, string apiBase = ReleaseClient.DefaultApiBase)
{
    public const string Repository = "davojc/bmdcli";
    public const string DefaultApiBase = "https://api.github.com";
    public const string ReleasesPageUrl = "https://github.com/davojc/bmdcli/releases/latest";

    readonly string _apiBase = apiBase.TrimEnd('/');

    /// <summary>An HttpClient configured the way the GitHub API expects: a User-Agent (GitHub
    /// answers 403 without one), the versioned API media type, and a timeout short enough that a
    /// black-holed connection cannot hang the CLI indefinitely.</summary>
    public static HttpClient CreateHttpClient(string userAgentVersion)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("bmd", userAgentVersion));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>The newest non-pre-release release. The <c>/releases/latest</c> endpoint excludes
    /// pre-releases and drafts on GitHub's side, which is exactly the spec's rule — so a user
    /// running v0.1.0-rc.1 is offered v0.1.0, and nobody is ever offered an rc.</summary>
    public async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken ct)
    {
        var url = $"{_apiBase}/repos/{Repository}/releases/latest";
        var body = await GetTextAsync(url, ct);
        try
        {
            return JsonSerializer.Deserialize(body, UpdateJsonContext.Default.ReleaseInfo)
                   ?? throw new UpdateException("the GitHub releases API returned an empty response");
        }
        catch (JsonException ex)
        {
            throw new UpdateException($"the GitHub releases API response could not be read: {ex.Message}");
        }
    }

    public async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Streams a URL to a file. On any failure the partial file is removed, so a caller
    /// can never mistake a truncated download for a complete one — the checksum would catch it
    /// anyway, but leaving debris behind after a failed update is its own small bug.</summary>
    public async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(url, ct);
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using (var destination = File.Create(destinationPath))
                await source.CopyToAsync(destination, ct);
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"could not reach {url}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException($"timed out contacting {url}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new UpdateException($"{url} returned HTTP {status}");
        }
        return response;
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~ReleaseClientTests`
Expected: PASS.

- [ ] **Step 8: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add src/Bmd/Update/ tests/Bmd.Tests/Update/
git commit -m "feat(update): GitHub releases API client with asset download"
```

---

### Task 4: `bmd update --check`

The read-only half of the command: compare the embedded version against the latest release and report. No download, no install. Exit 0 either way.

**Files:**
- Create: `src/Bmd/Commands/UpdateCommands.cs`
- Create: `src/Bmd/Commands/UpdateResults.cs`
- Modify: `src/Bmd/Output/BmdJsonContext.cs`
- Modify: `src/Bmd/Program.cs`
- Modify: `src/Bmd/Commands/GroupHelp.cs`
- Test: `tests/Bmd.Tests/Commands/UpdateCommandsTests.cs`

**Interfaces:**
- Consumes: `SemVer` (Task 1); `ReleaseClient`, `ReleaseInfo`, `UpdateException` (Task 3).
- Produces:
  - `Bmd.Commands.UpdateCheckResult` — `sealed record UpdateCheckResult(string CurrentVersion, string LatestVersion, bool UpdateAvailable)`.
  - `Bmd.Commands.UpdateResult` — `sealed record UpdateResult(string CurrentVersion, string LatestVersion, bool Updated, string? Path)`.
  - `Bmd.Commands.UpdateCommands` — `public UpdateCommands()`, `public UpdateCommands(ReleaseClient client, string currentVersion, string runtimeIdentifier, string? executablePath)`, and `public async Task<int> Update(bool check = false, bool json = false, CancellationToken ct = default)`. Task 5 fills in the install branch of the same method.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bmd.Tests/Commands/UpdateCommandsTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Bmd.Commands;
using Bmd.Tests.Update;
using Bmd.Update;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class UpdateCommandsTests : IDisposable
{
    const string ApiBase = "https://api.example.test";
    const string LatestUrl = "https://api.example.test/repos/davojc/bmdcli/releases/latest";

    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public UpdateCommandsTests()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    static string ReleaseJson(string tag) => $$"""
        {
          "tag_name": "{{tag}}",
          "prerelease": false,
          "assets": [
            { "name": "bmd-win-x64.zip",
              "browser_download_url": "https://downloads.example.test/bmd-win-x64.zip" },
            { "name": "checksums.txt",
              "browser_download_url": "https://downloads.example.test/checksums.txt" }
          ]
        }
        """;

    static UpdateCommands CommandFor(FakeHttpHandler handler, string currentVersion,
        string rid = "win-x64", string? exePath = null) =>
        new(new ReleaseClient(new HttpClient(handler), ApiBase), currentVersion, rid,
            exePath ?? Path.Combine(Path.GetTempPath(), "bmd.exe"));

    [Fact]
    public async Task Check_ReportsUpToDateAndExitsZero()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.2.0").Update(check: true);

        Assert.Equal(0, exit);
        Assert.Contains("latest version", _stdout.ToString());
        Assert.DoesNotContain("bmd update", _stdout.ToString());
    }

    [Fact]
    public async Task Check_ReportsAnAvailableUpdateAndStillExitsZero()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.1.0").Update(check: true);

        Assert.Equal(0, exit);
        var text = _stdout.ToString();
        Assert.Contains("0.1.0", text);
        Assert.Contains("0.2.0", text);
        Assert.Contains("bmd update", text);
    }

    [Fact]
    public async Task Check_TreatsAPreReleaseBuildAsOlderThanItsRelease()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.1.0"));

        var exit = await CommandFor(handler, "0.1.0-rc.1").Update(check: true);

        Assert.Equal(0, exit);
        Assert.Contains("0.1.0-rc.1", _stdout.ToString());
    }

    [Fact]
    public async Task Check_NeverDownloadsAnything()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        await CommandFor(handler, "0.1.0").Update(check: true);

        Assert.Equal(LatestUrl, Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task Check_Json_EmitsExactlyOneDocumentWithStableFields()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.1.0").Update(check: true, json: true);

        Assert.Equal(0, exit);
        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("0.1.0", doc.RootElement.GetProperty("currentVersion").GetString());
        Assert.Equal("0.2.0", doc.RootElement.GetProperty("latestVersion").GetString());
        Assert.True(doc.RootElement.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task Check_Json_ReportsFalseWhenUpToDate()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        await CommandFor(handler, "0.2.0").Update(check: true, json: true);

        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        Assert.False(doc.RootElement.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task Check_NetworkFailureIsOneStderrLineAndExitOne()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, "nope", HttpStatusCode.ServiceUnavailable);

        var exit = await CommandFor(handler, "0.1.0").Update(check: true);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString()); // no stack trace
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task Check_ErrorsStayPlainTextEvenWithJson()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, "nope", HttpStatusCode.ServiceUnavailable);

        var exit = await CommandFor(handler, "0.1.0").Update(check: true, json: true);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task Check_UnparsableReleaseTagIsAnError()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("not-a-version"));

        var exit = await CommandFor(handler, "0.1.0").Update(check: true);

        Assert.Equal(1, exit);
        Assert.Contains("not-a-version", _stderr.ToString());
    }

    [Fact]
    public async Task Check_WorksEvenForABuildWithNoRuntimeIdentifier()
    {
        // --check compares versions; it never needs to know which asset to fetch.
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.1.0", rid: "unknown").Update(check: true);

        Assert.Equal(0, exit);
        Assert.Contains("0.2.0", _stdout.ToString());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~UpdateCommandsTests`
Expected: FAIL — compile error, `UpdateCommands` does not exist.

- [ ] **Step 3: Add the result records**

Create `src/Bmd/Commands/UpdateResults.cs`:

```csharp
namespace Bmd.Commands;

/// <summary>The `bmd update --check` result: the version of this binary, the newest published
/// release, and whether the latter is newer than the former.</summary>
public sealed record UpdateCheckResult(string CurrentVersion, string LatestVersion, bool UpdateAvailable);

/// <summary>The `bmd update` result. <c>Updated</c> is false when the binary was already current,
/// in which case <c>Path</c> is null and nothing on disk was touched.</summary>
public sealed record UpdateResult(string CurrentVersion, string LatestVersion, bool Updated, string? Path);
```

- [ ] **Step 4: Register them on the JSON context**

In `src/Bmd/Output/BmdJsonContext.cs`, add next to the existing `VersionResult` registration:

```csharp
[JsonSerializable(typeof(UpdateCheckResult))]
[JsonSerializable(typeof(UpdateResult))]
```

- [ ] **Step 5: Implement the command, check branch only**

Create `src/Bmd/Commands/UpdateCommands.cs`:

```csharp
using System.Text.Json;
using Bmd.Output;
using Bmd.Update;

namespace Bmd.Commands;

/// <summary>bmd update — check for, and install, a newer release of bmd itself.</summary>
public class UpdateCommands
{
    readonly ReleaseClient _client;
    readonly string _currentVersion;
    readonly string _runtimeIdentifier;
    readonly string? _executablePath;

    public UpdateCommands()
        : this(new ReleaseClient(ReleaseClient.CreateHttpClient(BuildInfo.Version)),
               BuildInfo.Version, BuildInfo.RuntimeIdentifier, Environment.ProcessPath)
    {
    }

    /// <summary>Test seam: the release source, the identity this binary claims, and the file that
    /// would be replaced. Tests pass a dummy executable path — the swap must never be pointed at
    /// the running test host.</summary>
    public UpdateCommands(ReleaseClient client, string currentVersion, string runtimeIdentifier,
        string? executablePath)
    {
        _client = client;
        _currentVersion = currentVersion;
        _runtimeIdentifier = runtimeIdentifier;
        _executablePath = executablePath;
    }

    /// <summary>Download and install the newest release of bmd, replacing this binary in place. The download is verified against the release's SHA-256 checksums before anything is replaced; a mismatch aborts with nothing changed. Pre-release versions are never offered.</summary>
    /// <param name="check">Report whether a newer release exists and exit without downloading or changing anything.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    /// <param name="ct">Cancelled by Ctrl+C.</param>
    public async Task<int> Update(bool check = false, bool json = false, CancellationToken ct = default)
    {
        try
        {
            var release = await _client.GetLatestReleaseAsync(ct);

            if (!SemVer.TryParse(release.TagName, out var latest))
                throw new UpdateException(
                    $"the latest release is tagged '{release.TagName}', which is not a version bmd can compare");
            if (!SemVer.TryParse(_currentVersion, out var current))
                throw new UpdateException(
                    $"this binary reports version '{_currentVersion}', which is not a version bmd can compare");

            var updateAvailable = latest.CompareTo(current) > 0;

            if (check)
            {
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(
                        new UpdateCheckResult(current.ToString(), latest.ToString(), updateAvailable),
                        BmdJsonContext.Default.UpdateCheckResult));
                else if (updateAvailable)
                    Console.WriteLine($"A new release of bmd is available: {current} → {latest}{Environment.NewLine}Run `bmd update` to upgrade.");
                else
                    Console.WriteLine($"bmd {current} is the latest version.");
                return 0;
            }

            // The install path lands in Task 5.
            if (!updateAvailable)
            {
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(
                        new UpdateResult(current.ToString(), latest.ToString(), false, null),
                        BmdJsonContext.Default.UpdateResult));
                else
                    Console.WriteLine($"bmd {current} is already the latest version.");
                return 0;
            }

            throw new UpdateException("installing updates is not implemented yet");
        }
        catch (UpdateException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("error: cancelled");
            return 1;
        }
    }
}
```

- [ ] **Step 6: Register the command**

In `src/Bmd/Program.cs`, add after the `version` registration block:

```csharp
var update = new UpdateCommands();
app.Add("update", update.Update);
```

In `src/Bmd/Commands/GroupHelp.cs`, add to the `Commands` array immediately after the `version` entry:

```csharp
        new("update", "Download and install the newest release of bmd, replacing this binary in place."),
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~UpdateCommandsTests`
Expected: PASS.

- [ ] **Step 8: Verify the command is wired and its help text renders**

Run: `dotnet run --project src/Bmd -- update --help`
Expected: help output naming `--check` and `--json` with the documented descriptions, exit 0.

Run: `dotnet run --project src/Bmd -- update --check`
Expected: real network call against GitHub. This build reports version `0.0.0-dev`, so expect the "A new release of bmd is available: 0.0.0-dev → 0.1.0" two-liner and exit 0. If the machine is offline, expect a single `error:` line and exit 1 — either outcome proves the wiring.

- [ ] **Step 9: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add src/Bmd/Commands/ src/Bmd/Program.cs src/Bmd/Output/BmdJsonContext.cs tests/Bmd.Tests/Commands/UpdateCommandsTests.cs
git commit -m "feat(update): bmd update --check against the GitHub releases API"
```

---

### Task 5: Download, verify and install

The install half: fetch the asset for this RID, verify SHA-256 against the release's `checksums.txt` before touching anything, then swap the executable with a restore-on-failure guarantee.

**Files:**
- Create: `src/Bmd/Update/UpdateInstaller.cs`
- Modify: `src/Bmd/Commands/UpdateCommands.cs` (replace the "not implemented yet" throw)
- Test: `tests/Bmd.Tests/Update/UpdateInstallerTests.cs`
- Test: `tests/Bmd.Tests/Commands/UpdateCommandsTests.cs` (add install cases)

**Interfaces:**
- Consumes: `Checksums` (Task 1); `ReleaseClient`, `ReleaseInfo`, `UpdateException` (Task 3); `UpdateCommands` (Task 4).
- Produces: `Bmd.Update.UpdateInstaller` — `static string ExecutableName { get; }`, `static string ExtractExecutable(string archivePath, string assetName, string destinationDirectory)`, `static void Replace(string newExecutablePath, string currentExecutablePath)`, `static void CleanUpPreviousUpdate(string currentExecutablePath)`.

- [ ] **Step 1: Write the failing installer tests**

Create `tests/Bmd.Tests/Update/UpdateInstallerTests.cs`:

```csharp
using System.Formats.Tar;
using System.IO.Compression;
using Bmd.Update;

namespace Bmd.Tests.Update;

public class UpdateInstallerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"bmd-install-{Guid.NewGuid():N}");

    public UpdateInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Path_(params string[] parts) => Path.Combine([_root, .. parts]);

    /// <summary>Builds a zip holding one entry, the way the release workflow's
    /// Compress-Archive step does.</summary>
    string MakeZip(string entryName, byte[] content)
    {
        var archive = Path_("bmd-win-x64.zip");
        using var stream = File.Create(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry(entryName).Open();
        entry.Write(content);
        return archive;
    }

    /// <summary>Builds a gzipped tar holding one entry, the way the release workflow's
    /// `tar czf` step does.</summary>
    string MakeTarGz(string entryName, byte[] content)
    {
        var archive = Path_("bmd-linux-x64.tar.gz");
        var staging = Path_("staging");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, entryName), content);
        using (var output = File.Create(archive))
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            TarFile.CreateFromDirectory(staging, gzip, includeBaseDirectory: false);
        Directory.Delete(staging, recursive: true);
        return archive;
    }

    [Fact]
    public void ExtractExecutable_PullsTheBinaryOutOfAZip()
    {
        var payload = "new bmd"u8.ToArray();
        var archive = MakeZip("bmd.exe", payload);
        var destination = Path_("extracted-zip");

        var extracted = UpdateInstaller.ExtractExecutable(archive, "bmd-win-x64.zip", destination);

        Assert.True(File.Exists(extracted));
        Assert.Equal(payload, File.ReadAllBytes(extracted));
    }

    [Fact]
    public void ExtractExecutable_PullsTheBinaryOutOfATarGz()
    {
        var payload = "new bmd"u8.ToArray();
        var archive = MakeTarGz("bmd", payload);
        var destination = Path_("extracted-tar");

        var extracted = UpdateInstaller.ExtractExecutable(archive, "bmd-linux-x64.tar.gz", destination);

        Assert.True(File.Exists(extracted));
        Assert.Equal(payload, File.ReadAllBytes(extracted));
    }

    [Fact]
    public void ExtractExecutable_ThrowsWhenTheArchiveDoesNotContainABmdBinary()
    {
        var archive = MakeZip("readme.txt", "not a binary"u8.ToArray());
        var destination = Path_("extracted-wrong");

        var ex = Assert.Throws<UpdateException>(
            () => UpdateInstaller.ExtractExecutable(archive, "bmd-win-x64.zip", destination));

        Assert.Contains("bmd", ex.Message);
    }

    [Fact]
    public void ExtractExecutable_ThrowsForAnUnrecognizedArchiveExtension()
    {
        var archive = Path_("bmd-win-x64.rar");
        File.WriteAllBytes(archive, [1, 2, 3]);

        Assert.Throws<UpdateException>(
            () => UpdateInstaller.ExtractExecutable(archive, "bmd-win-x64.rar", Path_("out")));
    }

    [Fact]
    public void ExtractExecutable_ThrowsForACorruptArchiveRatherThanLeakingTheRawException()
    {
        var archive = Path_("bmd-win-x64.zip");
        File.WriteAllBytes(archive, "definitely not a zip"u8.ToArray());

        Assert.Throws<UpdateException>(
            () => UpdateInstaller.ExtractExecutable(archive, "bmd-win-x64.zip", Path_("out")));
    }

    [Fact]
    public void Replace_PutsTheNewBinaryAtTheCurrentPath()
    {
        // A dummy file stands in for the running executable — never the test host itself.
        var current = Path_("bmd-current.bin");
        var replacement = Path_("bmd-new.bin");
        File.WriteAllText(current, "old");
        File.WriteAllText(replacement, "new");

        UpdateInstaller.Replace(replacement, current);

        Assert.Equal("new", File.ReadAllText(current));
        Assert.False(File.Exists(replacement));
    }

    [Fact]
    public void Replace_LeavesTheOriginalIntactWhenTheReplacementIsMissing()
    {
        var current = Path_("bmd-current.bin");
        File.WriteAllText(current, "old");

        Assert.ThrowsAny<Exception>(
            () => UpdateInstaller.Replace(Path_("does-not-exist.bin"), current));

        Assert.True(File.Exists(current));
        Assert.Equal("old", File.ReadAllText(current));
    }

    [Fact]
    public void Replace_OnWindowsLeavesTheOldBinaryBesideItForLaterCleanup()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix renames over the original; no .old file
        var current = Path_("bmd-current.exe");
        var replacement = Path_("bmd-new.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(replacement, "new");

        UpdateInstaller.Replace(replacement, current);

        Assert.Equal("new", File.ReadAllText(current));
        Assert.True(File.Exists(current + ".old"));
        Assert.Equal("old", File.ReadAllText(current + ".old"));
    }

    [Fact]
    public void Replace_OnUnixSetsTheExecuteBit()
    {
        if (OperatingSystem.IsWindows()) return;
        var current = Path_("bmd-current");
        var replacement = Path_("bmd-new");
        File.WriteAllText(current, "old");
        File.WriteAllText(replacement, "new");
        File.SetUnixFileMode(replacement, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        UpdateInstaller.Replace(replacement, current);

        Assert.True(File.GetUnixFileMode(current).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void CleanUpPreviousUpdate_RemovesAStaleOldFile()
    {
        var current = Path_("bmd-current.exe");
        File.WriteAllText(current, "current");
        File.WriteAllText(current + ".old", "stale");

        UpdateInstaller.CleanUpPreviousUpdate(current);

        Assert.False(File.Exists(current + ".old"));
        Assert.True(File.Exists(current));
    }

    [Fact]
    public void CleanUpPreviousUpdate_IsSilentWhenThereIsNothingToClean()
    {
        var current = Path_("bmd-current.exe");
        File.WriteAllText(current, "current");

        UpdateInstaller.CleanUpPreviousUpdate(current); // must not throw

        Assert.True(File.Exists(current));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~UpdateInstallerTests`
Expected: FAIL — compile error, `UpdateInstaller` does not exist.

- [ ] **Step 3: Implement UpdateInstaller**

Create `src/Bmd/Update/UpdateInstaller.cs`:

```csharp
using System.Formats.Tar;
using System.IO.Compression;

namespace Bmd.Update;

/// <summary>Unpacks a downloaded release archive and puts the new binary where the running one
/// lives. Every operation here is deliberately reversible up to the final rename: the caller has
/// already verified the archive's checksum, and this class must still leave a working bmd behind
/// if the filesystem refuses partway through.</summary>
public static class UpdateInstaller
{
    /// <summary>What the executable is called inside the release archive — the release workflow
    /// packages the published binary under its own name, `bmd.exe` on Windows and `bmd`
    /// elsewhere.</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "bmd.exe" : "bmd";

    /// <summary>Suffix the outgoing binary is renamed to on Windows, where a running image cannot
    /// be overwritten but can be renamed.</summary>
    public const string OldSuffix = ".old";

    /// <summary>Extracts <paramref name="archivePath"/> into <paramref name="destinationDirectory"/>
    /// and returns the path of the bmd executable inside it. The archive format is chosen from
    /// <paramref name="assetName"/>'s extension rather than sniffed, because the release workflow
    /// controls both and a surprise is a bug worth failing on.
    ///
    /// Both BCL extractors reject entries that would escape the destination directory, so a
    /// tampered archive cannot write outside <paramref name="destinationDirectory"/> — and the
    /// checksum has already been verified before this is ever called.</summary>
    public static string ExtractExecutable(string archivePath, string assetName, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            if (assetName.EndsWith(".zip", StringComparison.Ordinal))
            {
                ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
            }
            else if (assetName.EndsWith(".tar.gz", StringComparison.Ordinal))
            {
                using var input = File.OpenRead(archivePath);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, destinationDirectory, overwriteFiles: true);
            }
            else
            {
                throw new UpdateException($"release asset '{assetName}' is not an archive bmd knows how to open");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"could not unpack {assetName}: {ex.Message}");
        }

        var executable = Path.Combine(destinationDirectory, ExecutableName);
        if (!File.Exists(executable))
            throw new UpdateException($"release asset '{assetName}' does not contain a '{ExecutableName}' executable");
        return executable;
    }

    /// <summary>Moves <paramref name="newExecutablePath"/> onto <paramref name="currentExecutablePath"/>.
    ///
    /// On Unix a rename over a running executable is atomic and safe — the running process keeps
    /// the old inode open. On Windows the running image is locked against overwrite but not
    /// against rename, so the outgoing binary is moved aside to `<name>.old` first (the approach
    /// gh and rustup take) and cleaned up by a later run. If the second move fails, the `.old`
    /// file is moved back, so a failed update always leaves a working bmd in place.
    ///
    /// Both paths must be on the same volume for the rename to be atomic, which is why callers
    /// extract into a directory beside the current executable rather than into the system temp
    /// directory.</summary>
    public static void Replace(string newExecutablePath, string currentExecutablePath)
    {
        if (!File.Exists(newExecutablePath))
            throw new UpdateException($"the extracted executable is missing from {newExecutablePath}");

        if (!OperatingSystem.IsWindows())
        {
            // Owner rwx, group and world r-x: the same shape a package manager would install.
            File.SetUnixFileMode(newExecutablePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Move(newExecutablePath, currentExecutablePath, overwrite: true);
            return;
        }

        var old = currentExecutablePath + OldSuffix;
        TryDelete(old);
        if (File.Exists(old))
            throw new UpdateException(
                $"{old} is left over from an earlier update and could not be removed — close any running bmd and try again");

        var movedAside = File.Exists(currentExecutablePath);
        if (movedAside) Move(currentExecutablePath, old, overwrite: false);
        try
        {
            Move(newExecutablePath, currentExecutablePath, overwrite: false);
        }
        catch
        {
            if (movedAside) TryRestore(old, currentExecutablePath);
            throw;
        }
    }

    /// <summary>Deletes the `<name>.old` left behind by a Windows update once the process holding
    /// it has exited. Best effort and silent: it is 5 MB of tidiness, never a reason to fail
    /// anything.</summary>
    public static void CleanUpPreviousUpdate(string currentExecutablePath) =>
        TryDelete(currentExecutablePath + OldSuffix);

    static void Move(string source, string destination, bool overwrite)
    {
        try
        {
            File.Move(source, destination, overwrite);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UpdateException(
                $"cannot write to {Path.GetDirectoryName(destination)}: {ex.Message} — " +
                "re-run bmd update from an elevated prompt, or reinstall bmd somewhere you can write");
        }
        catch (IOException ex)
        {
            throw new UpdateException($"could not install the new binary at {destination}: {ex.Message}");
        }
    }

    static void TryRestore(string old, string currentExecutablePath)
    {
        try { File.Move(old, currentExecutablePath, overwrite: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original is still on disk under its .old name; say so rather than pretend.
            throw new UpdateException(
                $"the update failed and the previous binary could not be moved back — it is at {old}");
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 4: Run the installer tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~UpdateInstallerTests`
Expected: PASS.

- [ ] **Step 5: Write the failing command-level install tests**

Append these to the existing `UpdateCommandsTests` class in `tests/Bmd.Tests/Commands/UpdateCommandsTests.cs`. Add `using System.IO.Compression;` and `using System.Security.Cryptography;` to the file's usings.

```csharp
    const string ChecksumsUrl = "https://downloads.example.test/checksums.txt";
    const string AssetUrl = "https://downloads.example.test/bmd-win-x64.zip";

    /// <summary>A one-entry zip holding the given bytes, matching what the release workflow
    /// publishes for win-x64.</summary>
    static byte[] ZipContaining(string entryName, byte[] content)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry(entryName).Open())
            entry.Write(content);
        return memory.ToArray();
    }

    static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task Update_VerifiesTheChecksumThenReplacesTheBinary()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "old binary");

        var archive = ZipContaining(UpdateInstaller.ExecutableName, "new binary"u8.ToArray());
        var handler = new FakeHttpHandler()
            .Text(LatestUrl, ReleaseJson("v0.2.0"))
            .Text(ChecksumsUrl, $"{Sha256Of(archive)}  bmd-win-x64.zip\n")
            .Bytes(AssetUrl, archive);

        try
        {
            var exit = await CommandFor(handler, "0.1.0", exePath: current).Update();

            Assert.Equal(0, exit);
            Assert.Equal("new binary", File.ReadAllText(current));
            Assert.Contains("0.2.0", _stdout.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Update_AbortsWithNothingChangedWhenTheChecksumDoesNotMatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "old binary");

        var archive = ZipContaining(UpdateInstaller.ExecutableName, "new binary"u8.ToArray());
        var handler = new FakeHttpHandler()
            .Text(LatestUrl, ReleaseJson("v0.2.0"))
            .Text(ChecksumsUrl, $"{new string('a', 64)}  bmd-win-x64.zip\n")
            .Bytes(AssetUrl, archive);

        try
        {
            var exit = await CommandFor(handler, "0.1.0", exePath: current).Update();

            Assert.Equal(1, exit);
            Assert.Contains("checksum", _stderr.ToString());
            Assert.Equal("old binary", File.ReadAllText(current)); // untouched
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Update_FailsWhenTheReleaseHasNoChecksumsForTheAsset()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "old binary");

        var archive = ZipContaining(UpdateInstaller.ExecutableName, "new binary"u8.ToArray());
        var handler = new FakeHttpHandler()
            .Text(LatestUrl, ReleaseJson("v0.2.0"))
            .Text(ChecksumsUrl, "deadbeef  something-else.tar.gz\n")
            .Bytes(AssetUrl, archive);

        try
        {
            var exit = await CommandFor(handler, "0.1.0", exePath: current).Update();

            Assert.Equal(1, exit);
            Assert.Equal("old binary", File.ReadAllText(current));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Update_FailsWhenTheReleaseHasNoAssetForThisPlatform()
    {
        const string json = """
            {
              "tag_name": "v0.2.0",
              "prerelease": false,
              "assets": [
                { "name": "checksums.txt",
                  "browser_download_url": "https://downloads.example.test/checksums.txt" }
              ]
            }
            """;
        var handler = new FakeHttpHandler().Text(LatestUrl, json);

        var exit = await CommandFor(handler, "0.1.0").Update();

        Assert.Equal(1, exit);
        Assert.Contains("bmd-win-x64.zip", _stderr.ToString());
    }

    [Fact]
    public async Task Update_RefusesToInstallWhenTheBuildHasNoRuntimeIdentifier()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.1.0", rid: "unknown").Update();

        Assert.Equal(1, exit);
        Assert.Contains("github.com/davojc/bmdcli/releases", _stderr.ToString());
    }

    [Fact]
    public async Task Update_UpToDate_DownloadsNothingAndExitsZero()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.2.0").Update();

        Assert.Equal(0, exit);
        Assert.Equal(LatestUrl, Assert.Single(handler.Requests));
        Assert.Contains("already the latest", _stdout.ToString());
    }

    [Fact]
    public async Task Update_Json_EmitsExactlyOneDocumentAfterInstalling()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "old binary");

        var archive = ZipContaining(UpdateInstaller.ExecutableName, "new binary"u8.ToArray());
        var handler = new FakeHttpHandler()
            .Text(LatestUrl, ReleaseJson("v0.2.0"))
            .Text(ChecksumsUrl, $"{Sha256Of(archive)}  bmd-win-x64.zip\n")
            .Bytes(AssetUrl, archive);

        try
        {
            var exit = await CommandFor(handler, "0.1.0", exePath: current).Update(json: true);

            Assert.Equal(0, exit);
            var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
            using var doc = JsonDocument.Parse(lines[0]);
            Assert.True(doc.RootElement.GetProperty("updated").GetBoolean());
            Assert.Equal("0.2.0", doc.RootElement.GetProperty("latestVersion").GetString());
            Assert.Equal(current, doc.RootElement.GetProperty("path").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Update_LeavesNoTemporaryFilesBesideTheExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "old binary");

        var archive = ZipContaining(UpdateInstaller.ExecutableName, "new binary"u8.ToArray());
        var handler = new FakeHttpHandler()
            .Text(LatestUrl, ReleaseJson("v0.2.0"))
            .Text(ChecksumsUrl, $"{Sha256Of(archive)}  bmd-win-x64.zip\n")
            .Bytes(AssetUrl, archive);

        try
        {
            await CommandFor(handler, "0.1.0", exePath: current).Update();

            // Only the installed binary should remain — no staging directory, no downloaded
            // archive. A leftover `.old` is expected on Windows and is cleaned by a later run.
            var leftovers = Directory.GetFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .Where(name => name != UpdateInstaller.ExecutableName
                            && name != UpdateInstaller.ExecutableName + UpdateInstaller.OldSuffix)
                .ToArray();
            Assert.Empty(leftovers);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
```

Note: these tests build a `.zip` and assert against `UpdateInstaller.ExecutableName`, so they exercise the real code path on Windows. On Linux/macOS `ExecutableName` is `bmd` and `ReleaseInfo.ArchiveName("win-x64")` still yields `.zip`, so the archive shape and the expected entry name stay consistent — the tests pass on every platform.

- [ ] **Step 6: Run to verify the new tests fail**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~UpdateCommandsTests`
Expected: FAIL — the install cases hit "installing updates is not implemented yet".

- [ ] **Step 7: Implement the install branch**

In `src/Bmd/Commands/UpdateCommands.cs`, replace the line

```csharp
            throw new UpdateException("installing updates is not implemented yet");
```

with a call to a new private method, and add the method to the class:

```csharp
            return await InstallAsync(release, current, latest, json, ct);
```

```csharp
    /// <summary>Downloads the asset for this platform, verifies it against the release's
    /// checksums.txt, and swaps it into place. The order matters and is fixed by the spec:
    /// nothing on disk beside the running binary is touched until the checksum matches.</summary>
    async Task<int> InstallAsync(ReleaseInfo release, SemVer current, SemVer latest, bool json,
        CancellationToken ct)
    {
        if (_executablePath is null)
            throw new UpdateException(
                "bmd could not determine its own location, so it cannot replace itself — " +
                $"download the new version from {ReleaseClient.ReleasesPageUrl}");

        if (_runtimeIdentifier is "unknown" or "")
            throw new UpdateException(
                "this build was not published for a specific platform, so bmd update cannot pick a " +
                $"release asset — download the new version from {ReleaseClient.ReleasesPageUrl}");

        var assetName = ReleaseInfo.ArchiveName(_runtimeIdentifier);
        var asset = release.FindAsset(assetName)
            ?? throw new UpdateException($"release {latest} has no asset named {assetName} for this platform");
        var checksumsAsset = release.FindAsset(ReleaseInfo.ChecksumsAssetName)
            ?? throw new UpdateException($"release {latest} has no {ReleaseInfo.ChecksumsAssetName} to verify the download against");

        // A leftover .old from a previous Windows update, now that nothing holds it open.
        UpdateInstaller.CleanUpPreviousUpdate(_executablePath);

        // Staged beside the current executable, not in the system temp directory: the final move
        // must be a same-volume rename to be atomic, and a cross-device move would not be.
        var installDirectory = Path.GetDirectoryName(_executablePath)
            ?? throw new UpdateException($"bmd could not determine the directory of {_executablePath}");
        var staging = Path.Combine(installDirectory, $".bmd-update-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(
                $"cannot write to {installDirectory}: {ex.Message} — " +
                "re-run bmd update from an elevated prompt, or reinstall bmd somewhere you can write");
        }

        try
        {
            Progress(json, $"Downloading bmd {latest} ({assetName})...");
            var archivePath = Path.Combine(staging, assetName);
            await _client.DownloadToFileAsync(asset.DownloadUrl, archivePath, ct);

            var checksumsText = await _client.GetTextAsync(checksumsAsset.DownloadUrl, ct);
            if (!Checksums.TryFind(checksumsText, assetName, out var expected))
                throw new UpdateException(
                    $"{ReleaseInfo.ChecksumsAssetName} for release {latest} lists no checksum for {assetName} — nothing was changed");

            var actual = Checksums.OfFile(archivePath);
            if (!actual.Equals(expected, StringComparison.Ordinal))
                throw new UpdateException(
                    $"checksum mismatch for {assetName}: expected {expected}, got {actual} — nothing was changed");
            Progress(json, "Verified SHA-256 checksum.");

            var extracted = UpdateInstaller.ExtractExecutable(archivePath, assetName, Path.Combine(staging, "unpacked"));
            UpdateInstaller.Replace(extracted, _executablePath);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(
                new UpdateResult(current.ToString(), latest.ToString(), true, _executablePath),
                BmdJsonContext.Default.UpdateResult));
        else
            Console.WriteLine($"Updated bmd {current} → {latest} at {_executablePath}");
        return 0;
    }

    /// <summary>Progress goes to stderr, never stdout: with --json, stdout must carry exactly one
    /// document, and progress is not a result. Suppressed entirely under --json so a machine
    /// reader's stderr stays clean.</summary>
    static void Progress(bool json, string message)
    {
        if (!json) Console.Error.WriteLine(message);
    }

    static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
```

Add `using Bmd.Update;` if it is not already present (it is, from Task 4).

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter "FullyQualifiedName~UpdateCommandsTests|FullyQualifiedName~UpdateInstallerTests"`
Expected: PASS.

- [ ] **Step 9: Run the full suite and prove Native AOT still publishes**

Run: `dotnet test`
Expected: PASS.

Run: `dotnet publish src/Bmd -c Release -r win-x64`
Expected: build succeeds. **Check the output for `IL2xxx`/`IL3xxx` trim or AOT warnings** — `System.IO.Compression`, `System.Formats.Tar` and `HttpClient` are all AOT-safe, so there should be none. If any appear, they are a defect in this task, not something to note and move past.

- [ ] **Step 10: Commit**

```bash
git add src/Bmd/Update/UpdateInstaller.cs src/Bmd/Commands/UpdateCommands.cs tests/Bmd.Tests/
git commit -m "feat(update): download, checksum-verify and self-install a new release"
```

---

### Task 6: Passive update notice, config key, docs and site

The gh-style two-line nudge after ordinary commands, plus the documentation that makes the feature real: `update.check`, the site's upgrade note, the spec's revised background-check wording, and the roadmap tick.

**Files:**
- Create: `src/Bmd/Update/UpdateNotice.cs`
- Create: `src/Bmd/Update/UpdateNoticeRunner.cs`
- Modify: `src/Bmd/Program.cs`
- Modify: `site/index.html`
- Modify: `docs/superpowers/specs/2026-08-29-bmd-cli-design.md`
- Modify: `CLAUDE.md`
- Test: `tests/Bmd.Tests/Update/UpdateNoticeTests.cs`

**Interfaces:**
- Consumes: `SemVer` (Task 1); `UpdateCheckCache`, `UpdateCheckEntry` (Task 2); `ReleaseClient` (Task 3); `ConfigStore`, `ConfigKey` (existing).
- Produces:
  - `Bmd.Update.UpdateNotice` — `static bool IsEligible(string[] args, ConfigStore config, bool errorIsRedirected)`, `static string? Format(string currentVersion, string? latestVersion)`.
  - `Bmd.Update.UpdateNoticeRunner` — `static UpdateNoticeRunner Start(string[] args)`, `void WriteIfAny(TextWriter writer)`, plus the internal testable `Start` overload described below.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bmd.Tests/Update/UpdateNoticeTests.cs`:

```csharp
using Bmd.Config;
using Bmd.Update;

namespace Bmd.Tests.Update;

public class UpdateNoticeTests : IDisposable
{
    readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bmd-notice-{Guid.NewGuid():N}");

    public UpdateNoticeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A config store rooted in this test's own temp directory, so nothing here can read
    /// or write the developer's real config.</summary>
    ConfigStore ConfigWith(string? updateCheck)
    {
        var globalPath = Path.Combine(_directory, "config");
        if (updateCheck is not null)
            File.WriteAllText(globalPath, $"[update]\ncheck = {updateCheck}\n");
        return ConfigStore.Load(globalPath, _directory);
    }

    [Fact]
    public void IsEligible_IsTrueForAnOrdinaryInteractiveCommand()
    {
        Assert.True(UpdateNotice.IsEligible(
            ["videohub", "route", "list"], ConfigWith(null), errorIsRedirected: false));
    }

    [Fact]
    public void IsEligible_IsFalseWhenStderrIsNotATty()
    {
        Assert.False(UpdateNotice.IsEligible(
            ["videohub", "route", "list"], ConfigWith(null), errorIsRedirected: true));
    }

    [Fact]
    public void IsEligible_IsFalseWhenJsonWasRequested()
    {
        Assert.False(UpdateNotice.IsEligible(
            ["videohub", "route", "list", "--json"], ConfigWith(null), errorIsRedirected: false));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("version")]
    public void IsEligible_IsFalseForTheUpdateAndVersionCommandsThemselves(string command)
    {
        Assert.False(UpdateNotice.IsEligible([command], ConfigWith(null), errorIsRedirected: false));
    }

    [Fact]
    public void IsEligible_IsFalseWhenUpdateCheckIsDisabledInConfig()
    {
        Assert.False(UpdateNotice.IsEligible(
            ["videohub", "info"], ConfigWith("false"), errorIsRedirected: false));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    public void IsEligible_IsTrueForAnythingThatIsNotFalse(string value)
    {
        Assert.True(UpdateNotice.IsEligible(
            ["videohub", "info"], ConfigWith(value), errorIsRedirected: false));
    }

    [Fact]
    public void IsEligible_IgnoresCaseWhenReadingFalse()
    {
        Assert.False(UpdateNotice.IsEligible(
            ["videohub", "info"], ConfigWith("False"), errorIsRedirected: false));
    }

    [Fact]
    public void Format_ProducesTheExactTwoLineNoticeFromTheSpec()
    {
        var text = UpdateNotice.Format("1.2.0", "1.4.1");
        Assert.Equal(
            "A new release of bmd is available: 1.2.0 → 1.4.1" + Environment.NewLine +
            "Run `bmd update` to upgrade.",
            text);
    }

    [Fact]
    public void Format_ReturnsNullWhenTheCachedVersionIsNotNewer()
    {
        Assert.Null(UpdateNotice.Format("1.4.1", "1.4.1"));
        Assert.Null(UpdateNotice.Format("1.4.1", "1.2.0"));
    }

    [Fact]
    public void Format_ReturnsNullWhenThereIsNoCachedVersion()
    {
        Assert.Null(UpdateNotice.Format("1.2.0", null));
    }

    [Fact]
    public void Format_ReturnsNullRatherThanThrowingOnUnparsableVersions()
    {
        Assert.Null(UpdateNotice.Format("not-a-version", "1.4.1"));
        Assert.Null(UpdateNotice.Format("1.2.0", "not-a-version"));
    }

    [Fact]
    public void Format_TreatsAPreReleaseAsOlderThanItsRelease()
    {
        Assert.NotNull(UpdateNotice.Format("0.1.0-rc.1", "0.1.0"));
    }

    [Fact]
    public void Runner_WritesTheNoticeFromAFreshCacheWithoutFetching()
    {
        var cache = new UpdateCheckCache(Path.Combine(_directory, "update-check.json"));
        var now = DateTimeOffset.UtcNow;
        cache.Write(new UpdateCheckEntry("1.4.1", now.AddHours(-1)));
        var fetched = false;

        var runner = UpdateNoticeRunner.Start(
            ["videohub", "info"], ConfigWith(null), cache, "1.2.0", errorIsRedirected: false,
            _ => { fetched = true; return Task.FromResult<string?>("9.9.9"); }, now);

        var writer = new StringWriter();
        runner.WriteIfAny(writer);

        Assert.False(fetched); // cache is fresh; no network
        Assert.Contains("1.2.0 → 1.4.1", writer.ToString());
    }

    [Fact]
    public void Runner_FetchesAndCachesWhenTheCacheIsStale()
    {
        var cache = new UpdateCheckCache(Path.Combine(_directory, "update-check.json"));
        var now = DateTimeOffset.UtcNow;

        var runner = UpdateNoticeRunner.Start(
            ["videohub", "info"], ConfigWith(null), cache, "1.2.0", errorIsRedirected: false,
            _ => Task.FromResult<string?>("1.4.1"), now);

        var writer = new StringWriter();
        runner.WriteIfAny(writer);

        Assert.Contains("1.2.0 → 1.4.1", writer.ToString());
        var entry = cache.Read();
        Assert.NotNull(entry);
        Assert.Equal("1.4.1", entry!.LatestVersion);
    }

    [Fact]
    public void Runner_IsSilentWhenTheFetchFails()
    {
        var cache = new UpdateCheckCache(Path.Combine(_directory, "update-check.json"));

        var runner = UpdateNoticeRunner.Start(
            ["videohub", "info"], ConfigWith(null), cache, "1.2.0", errorIsRedirected: false,
            _ => Task.FromException<string?>(new UpdateException("network down")),
            DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        runner.WriteIfAny(writer); // must not throw

        Assert.Equal("", writer.ToString());
        Assert.Null(cache.Read()); // a failed check writes nothing
    }

    [Fact]
    public void Runner_DoesNothingAtAllWhenIneligible()
    {
        var cache = new UpdateCheckCache(Path.Combine(_directory, "update-check.json"));
        cache.Write(new UpdateCheckEntry("9.9.9", DateTimeOffset.UtcNow));
        var fetched = false;

        var runner = UpdateNoticeRunner.Start(
            ["videohub", "info", "--json"], ConfigWith(null), cache, "1.2.0", errorIsRedirected: false,
            _ => { fetched = true; return Task.FromResult<string?>("9.9.9"); }, DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        runner.WriteIfAny(writer);

        Assert.False(fetched);
        Assert.Equal("", writer.ToString());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~UpdateNoticeTests`
Expected: FAIL — compile error, `UpdateNotice` does not exist.

- [ ] **Step 3: Implement UpdateNotice**

Create `src/Bmd/Update/UpdateNotice.cs`:

```csharp
using Bmd.Config;

namespace Bmd.Update;

/// <summary>The decision and the wording behind the passive "a new release is available" notice.
/// Both halves are pure so the rules can be tested without a network, a terminal, or a clock.</summary>
public static class UpdateNotice
{
    /// <summary>The config key that turns the passive check off entirely.</summary>
    public const string ConfigKeyName = "update.check";

    /// <summary>Whether this invocation may run a passive check and print a notice. The four
    /// suppression rules come straight from the spec: the notice is a courtesy for a human
    /// watching a terminal, so it stays out of pipes, out of machine-readable output, and out of
    /// the two commands that already report versions themselves.</summary>
    public static bool IsEligible(string[] args, ConfigStore config, bool errorIsRedirected)
    {
        if (errorIsRedirected) return false;
        if (args.Contains("--json", StringComparer.Ordinal)) return false;
        if (args.Length > 0 && args[0] is "update" or "version") return false;

        if (!ConfigKey.TryParse(ConfigKeyName, out var key)) return false;
        var configured = config.GetEffective(key);
        return configured is null || !configured.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The two-line notice, or null when there is nothing worth saying — no cached
    /// version, a version that is not newer, or either version being unparsable. Unparsable is
    /// silence rather than an error: a passive check must never turn into a complaint about the
    /// command the user actually ran.</summary>
    public static string? Format(string currentVersion, string? latestVersion)
    {
        if (latestVersion is null) return null;
        if (!SemVer.TryParse(currentVersion, out var current)) return null;
        if (!SemVer.TryParse(latestVersion, out var latest)) return null;
        if (latest.CompareTo(current) <= 0) return null;

        return $"A new release of bmd is available: {current} → {latest}{Environment.NewLine}" +
               "Run `bmd update` to upgrade.";
    }
}
```

- [ ] **Step 4: Implement UpdateNoticeRunner**

Create `src/Bmd/Update/UpdateNoticeRunner.cs`:

```csharp
using Bmd.Config;

namespace Bmd.Update;

/// <summary>Runs the passive update check alongside the command the user actually asked for, and
/// prints the notice afterwards.
///
/// The check starts before the command and runs concurrently with it, so the command's own work
/// is never blocked. At exit the process gives the fetch a short bounded window
/// (<see cref="JoinWindow"/>) to finish — without it, a background task on a thread-pool thread
/// is simply killed when Main returns and the cache would never be written, which would make the
/// whole feature dead code. That wait is incurred at most once per 24 hours, and only when
/// stderr is a terminal and --json was not passed.</summary>
public sealed class UpdateNoticeRunner
{
    /// <summary>How long the process will wait at exit for an in-flight check.</summary>
    public static readonly TimeSpan JoinWindow = TimeSpan.FromMilliseconds(500);

    readonly string _currentVersion;
    readonly string? _cachedLatest;
    readonly Task<string?>? _fetch;

    UpdateNoticeRunner(string currentVersion, string? cachedLatest, Task<string?>? fetch)
    {
        _currentVersion = currentVersion;
        _cachedLatest = cachedLatest;
        _fetch = fetch;
    }

    /// <summary>A runner that will never fetch and never print.</summary>
    static UpdateNoticeRunner Inert() => new("", null, null);

    /// <summary>The production entry point: reads the real config, the real cache, and the real
    /// releases API. Any failure setting up — an unreadable config file, a missing home
    /// directory — degrades to doing nothing, because a passive check must never be able to break
    /// a command.</summary>
    public static UpdateNoticeRunner Start(string[] args)
    {
        try
        {
            return Start(args, ConfigStore.LoadDefault(), UpdateCheckCache.Default(),
                BuildInfo.Version, Console.IsErrorRedirected, FetchLatestAsync, DateTimeOffset.UtcNow);
        }
        catch
        {
            return Inert();
        }
    }

    static async Task<string?> FetchLatestAsync(CancellationToken ct)
    {
        using var http = ReleaseClient.CreateHttpClient(BuildInfo.Version);
        var release = await new ReleaseClient(http).GetLatestReleaseAsync(ct);
        return SemVer.TryParse(release.TagName, out var version) ? version.ToString() : null;
    }

    /// <summary>Test seam: every input the decision depends on, injected.</summary>
    internal static UpdateNoticeRunner Start(
        string[] args, ConfigStore config, UpdateCheckCache cache, string currentVersion,
        bool errorIsRedirected, Func<CancellationToken, Task<string?>> fetchLatest, DateTimeOffset now)
    {
        if (!UpdateNotice.IsEligible(args, config, errorIsRedirected)) return Inert();

        var entry = cache.Read();
        if (!UpdateCheckCache.IsStale(entry, now))
            return new UpdateNoticeRunner(currentVersion, entry?.LatestVersion, null);

        var fetch = Task.Run(async () =>
        {
            var latest = await fetchLatest(CancellationToken.None);
            if (latest is not null) cache.Write(new UpdateCheckEntry(latest, now));
            return latest;
        });
        return new UpdateNoticeRunner(currentVersion, entry?.LatestVersion, fetch);
    }

    /// <summary>Writes the notice, if there is one. Never throws: a failed passive check is
    /// silent by design, and this runs after the command has already produced its output.</summary>
    public void WriteIfAny(TextWriter writer)
    {
        var latest = _cachedLatest;
        if (_fetch is not null)
        {
            try
            {
                if (_fetch.Wait(JoinWindow)) latest = _fetch.Result ?? latest;
            }
            catch
            {
                // Network down, rate limited, DNS failure — none of it is the user's problem here.
            }
        }

        if (UpdateNotice.Format(_currentVersion, latest) is { } text)
            writer.WriteLine(text);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Bmd.Tests --filter FullyQualifiedName~UpdateNoticeTests`
Expected: PASS.

- [ ] **Step 6: Wire the notice into Program.cs**

In `src/Bmd/Program.cs`, change the tail of the file from:

```csharp
if (GroupHelp.TryWrite(args, Console.Out)) return 0;

app.Run(args);
return Environment.ExitCode;
```

to:

```csharp
if (GroupHelp.TryWrite(args, Console.Out)) return 0;

// The passive update check (see the spec's "Self-update") starts here so it overlaps the
// command's own work, and prints at most a two-line stderr notice once the command is done.
// It suppresses itself for --json, non-TTY stderr, `update`/`version`, and update.check = false.
var notice = UpdateNoticeRunner.Start(args);

app.Run(args);

notice.WriteIfAny(Console.Error);
return Environment.ExitCode;
```

Add `using Bmd.Update;` to the top of `Program.cs`.

- [ ] **Step 7: Verify the notice behaves in a real terminal**

Run: `dotnet run --project src/Bmd -- version`
Expected: version line only — `version` is suppressed, no notice.

Run: `dotnet run --project src/Bmd -- config list`
Expected: the config listing, then the two-line notice on stderr (this build is `0.0.0-dev`, which is older than the published 0.1.0). If the machine is offline, expect no notice and no error.

Run: `dotnet run --project src/Bmd -- config list --json`
Expected: one JSON document on stdout and **no notice**.

Run: `dotnet run --project src/Bmd -- config list 2>&1 | cat` (Bash) — expected: no notice, because stderr is redirected.

- [ ] **Step 8: Document `update.check` on the site and add the upgrade note**

In `site/index.html`:

1. In the quick-start section, after the first `bmd videohub route list` example, add the upgrade note the spec calls for:

```html
<p>
  Once bmd is installed, it keeps itself current: run <code>bmd update</code> to
  install the newest release, or <code>bmd update --check</code> to see whether one
  exists without changing anything. Downloads are verified against the release's
  SHA-256 checksums before anything is replaced.
</p>
<p>
  bmd also checks for a new release at most once a day and prints a one-line
  reminder when one appears. To turn that off:
  <code>bmd config set update.check false</code>.
</p>
```

Match the surrounding markup's existing class names and heading structure — read the file first and follow its conventions rather than inventing new ones.

2. Anywhere the page lists commands, add `bmd update` alongside `bmd version`.

- [ ] **Step 9: Update the spec's background-check wording**

In `docs/superpowers/specs/2026-08-29-bmd-cli-design.md`, in the "Passive check (gh-style)" paragraph, replace:

> during any normal command, at most once per 24 hours, a background task fetches the latest version and caches the result in the OS cache dir

with:

> during any normal command, at most once per 24 hours, a check runs concurrently with the command and caches the result in the OS cache dir

and replace:

> It never delays the command; network failures are silent.

with:

> The command's own work is never blocked by it; at exit the process waits at most 500 ms for an in-flight check before printing, so a fast command still gets a result rather than killing the task on the way out. Network failures are silent.

This records ruling 2 from the plan header, per CLAUDE.md's instruction to update the spec when a design decision changes.

- [ ] **Step 10: Tick the roadmap**

In `CLAUDE.md`, in the Roadmap section, mark milestone 9 complete by appending ` ✅` to `9 self-update`, matching how milestones 1 and 2 are marked.

In `docs/superpowers/specs/2026-08-29-bmd-cli-design.md`, no milestone-list change is needed — it does not carry completion marks.

- [ ] **Step 11: Run the full suite and the AOT publish**

Run: `dotnet test`
Expected: PASS, every test in the suite.

Run: `dotnet publish src/Bmd -c Release -r win-x64`
Expected: succeeds with no `IL2xxx`/`IL3xxx` warnings.

- [ ] **Step 12: Commit**

```bash
git add src/Bmd/Update/ src/Bmd/Program.cs site/index.html CLAUDE.md docs/superpowers/specs/2026-08-29-bmd-cli-design.md tests/Bmd.Tests/Update/UpdateNoticeTests.cs
git commit -m "feat(update): passive once-a-day update notice, update.check config, site docs"
```

---

## Self-review

**1. Spec coverage.** Every clause of the spec's "Self-update" section maps to a task:

| Spec requirement | Task |
|---|---|
| `bmd update --check` queries latest non-pre-release, compares semver, reports, exits 0 | 4 (`/releases/latest` excludes pre-releases server-side) |
| Downloads the asset matching the embedded RID | 5 (`ReleaseInfo.ArchiveName`) |
| Verifies SHA-256 against `checksums.txt` before touching anything; bad checksum → exit 1, nothing changed | 1 (`Checksums`) + 5 (order of operations, and a test asserting the old binary is intact) |
| Unix: extract next to the executable, chmod +x, atomic rename | 5 (`UpdateInstaller.Replace`, staging beside the executable) |
| Windows: `bmd.exe` → `bmd.exe.old`, new moved into place, `.old` cleaned on a later run | 5 (`Replace` + `CleanUpPreviousUpdate`, called at the start of an install) |
| Any failure restores the original | 5 (`TryRestore`) |
| No write permission → clear "re-run elevated" error | 5 (`Move`'s `UnauthorizedAccessException` branch, and the staging-directory creation branch) |
| Passive check at most once per 24h, cached in the OS cache dir | 2 + 6 |
| Notice text, exactly two lines, on stderr after the command | 6 (`UpdateNotice.Format`, asserted verbatim; written after `app.Run`) |
| Suppressed by `update.check = false` / non-TTY / `--json` / `update`/`version` | 6 (`IsEligible`, one test per rule) |
| `--json` on every command, one document, camelCase, stable names | 4 + 5 |
| Site documents any command change | 6 |

**2. Placeholder scan.** No "TBD", no "add error handling", no "similar to Task N". Every code step carries the code. The one instruction that defers to the implementer's judgment — matching `site/index.html`'s existing markup conventions in Task 6 Step 8 — is deliberate: the file exists and its conventions are readable, and inventing new class names would be worse than following them.

**3. Type consistency.** Checked across tasks: `SemVer.TryParse`/`CompareTo`/`ToString` (Task 1) are used unchanged in Tasks 4 and 6. `Checksums.TryFind`/`OfFile` (Task 1) are used in Task 5. `UpdateCheckCache.Read`/`Write`/`IsStale`/`Default()` and `UpdateCheckEntry(LatestVersion, CheckedAt)` (Task 2) are used in Task 6. `ReleaseClient.GetLatestReleaseAsync`/`GetTextAsync`/`DownloadToFileAsync`/`CreateHttpClient`/`ReleasesPageUrl` and `ReleaseInfo.FindAsset`/`ArchiveName`/`ChecksumsAssetName` (Task 3) are used in Tasks 4, 5 and 6. `UpdateInstaller.ExecutableName`/`OldSuffix`/`ExtractExecutable`/`Replace`/`CleanUpPreviousUpdate` (Task 5) are used in Task 5's command code and its tests. `UpdateCheckResult`/`UpdateResult` (Task 4) are registered on `BmdJsonContext` in Task 4 and serialized in Tasks 4 and 5. `UpdateException` is defined once, in Task 3's `ReleaseInfo.cs`, and thrown from Tasks 3, 5.

One deliberate cross-task dependency to watch: **Task 4 leaves `Update()` throwing `UpdateException("installing updates is not implemented yet")` on the install path, and Task 5 replaces that exact line.** Task 4's tests never reach it — every one of them either passes `check: true` or is already up to date.
