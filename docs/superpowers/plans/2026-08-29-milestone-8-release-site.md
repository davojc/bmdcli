# bmd Milestone 8: Release Pipeline + Website Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pushing a semver tag builds `bmd` for five platforms and publishes a GitHub Release with checksums; a GitHub Pages site gives anyone a one-click download and the documentation to use it.

**Architecture:** Version and RID are stamped into the binary at build time by an MSBuild-generated source file (no reflection); a tag-triggered Actions matrix builds each RID on its own OS (Native AOT cannot cross-compile), archives it, and a final job assembles checksums and the Release; a hand-written static site in `site/` is deployed by a second workflow and links to GitHub's stable "latest release" asset URLs so it never goes stale.

**Tech Stack:** .NET 10, ConsoleAppFramework v5, GitHub Actions, plain HTML/CSS. No new package references, no site build toolchain.

**Spec:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` ("Distribution & releases", "Website (GitHub Pages)")

## Global Constraints

- `PublishAot=true` stays green: no reflection, no `dynamic`, JSON only via the source-generated contexts. No new package references.
- `Devices/` and `Config/` never reference ConsoleAppFramework or `Commands/`.
- Errors: one `error: ...` line on stderr, never a stack trace. Exit codes 0 / 1 / 2.
- Help is the API: full XML doc comments in plain prose (ConsoleAppFramework renders `<paramref>`/`<see>` literally, and multi-line `///` summaries render with embedded newlines).
- **The site is self-contained**: no CDNs, no web fonts, no external images, no analytics. System font stack, hand-written CSS. It must render correctly with no network beyond the page itself.
- **Download links must never name a version.** Use GitHub's stable latest-asset URLs so the page cannot go stale.
- TDD for code; the site and workflows are verified by inspection plus the checks each task specifies.

**Honest limitation, stated up front:** a tag-triggered workflow cannot be fully proven without pushing a tag, and Native AOT for four of the five RIDs cannot be built on this machine. Tasks 2 and 6 verify what is verifiable locally (YAML validity, the publish command shape, archive/checksum logic) and the milestone ends by *recommending* the user push a pre-release tag (e.g. `v0.1.0-rc.1`) as the real end-to-end test. **Do not push any tag or create any release — that is the user's call.**

---

### Task 1: `bmd version` with build-stamped version and RID

**Files:**
- Modify: `src/Bmd/Bmd.csproj`
- Create: `src/Bmd/Commands/VersionCommands.cs`, `tests/Bmd.Tests/Commands/VersionCommandsTests.cs`
- Modify: `src/Bmd/Program.cs`, `src/Bmd/Commands/GroupHelp.cs`, `src/Bmd/Output/BmdJsonContext.cs`

**Interfaces:**
- Produces:
  - An MSBuild target that generates `BuildInfo.g.cs` into the intermediate output and compiles it, exposing `internal static class BuildInfo { public const string Version = "…"; public const string RuntimeIdentifier = "…"; }`. Values come from `$(Version)` and `$(RuntimeIdentifier)`; when either is absent, use `"0.0.0-dev"` and `"unknown"` respectively. **No reflection** — this is why we generate a constant rather than reading assembly attributes.
  - `sealed record VersionResult(string Version, string RuntimeIdentifier)` registered in `BmdJsonContext`
  - `class VersionCommands` with `int Version(bool json = false)` registered as `version`, and added to `GroupHelp.Commands`
- Behavior: human output is one line, `bmd 1.2.0 (win-x64)`; `--json` emits `{"version":"1.2.0","runtimeIdentifier":"win-x64"}`. Exit 0 always.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Commands/VersionCommandsTests.cs` — console-capture harness as in the other command tests, `[Collection("console")]`:

```csharp
    [Fact] public void Version_Human_PrintsVersionAndRid()
        // asserts stdout matches the shape "bmd <something> (<something>)" — do NOT assert
        // a specific version, which changes per build; assert non-empty, no placeholder braces,
        // and that it starts with "bmd "

    [Fact] public void Version_Json_HasVersionAndRuntimeIdentifier()
        // parses stdout as ONE JSON document; both properties present and non-empty

    [Fact] public void Version_AlwaysExitsZero()
```

Write them out fully in the established style.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VersionCommandsTests`
Expected: compilation failure — `VersionCommands` does not exist.

- [ ] **Step 3: Implement**

In `src/Bmd/Bmd.csproj`, add a target that runs before compilation:

```xml
  <Target Name="GenerateBuildInfo" BeforeTargets="BeforeCompile">
    <PropertyGroup>
      <_BmdVersion Condition="'$(Version)' == '' or '$(Version)' == '1.0.0'">0.0.0-dev</_BmdVersion>
      <_BmdVersion Condition="'$(_BmdVersion)' == ''">$(Version)</_BmdVersion>
      <_BmdRid Condition="'$(RuntimeIdentifier)' == ''">unknown</_BmdRid>
      <_BmdRid Condition="'$(_BmdRid)' == ''">$(RuntimeIdentifier)</_BmdRid>
      <_BuildInfoFile>$(IntermediateOutputPath)BuildInfo.g.cs</_BuildInfoFile>
    </PropertyGroup>
    <ItemGroup>
      <_BuildInfoLines Include="// Generated by the GenerateBuildInfo target. Do not edit." />
      <_BuildInfoLines Include="namespace Bmd%3B" />
      <_BuildInfoLines Include="internal static class BuildInfo" />
      <_BuildInfoLines Include="{" />
      <_BuildInfoLines Include="    public const string Version = &quot;$(_BmdVersion)&quot;%3B" />
      <_BuildInfoLines Include="    public const string RuntimeIdentifier = &quot;$(_BmdRid)&quot;%3B" />
      <_BuildInfoLines Include="}" />
    </ItemGroup>
    <WriteLinesToFile File="$(_BuildInfoFile)" Lines="@(_BuildInfoLines)" Overwrite="true" WriteOnlyWhenDifferent="true" />
    <ItemGroup>
      <Compile Include="$(_BuildInfoFile)" />
    </ItemGroup>
  </Target>
```

(`%3B` is an escaped semicolon — MSBuild treats a bare `;` as an item separator. Verify the generated file compiles; if the escaping misbehaves, adapt and document.)

Note the `'$(Version)' == '1.0.0'` guard: that is the SDK's implicit default, so a plain `dotnet build` reports `0.0.0-dev` rather than pretending to be 1.0.0. A real release always passes `-p:Version=`.

`src/Bmd/Commands/VersionCommands.cs`:

```csharp
using System.Text.Json;
using Bmd.Output;

namespace Bmd.Commands;

/// <summary>bmd version — what this binary is.</summary>
public class VersionCommands
{
    /// <summary>Show the version and platform of this bmd binary.</summary>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public int Version(bool json = false)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(
                new VersionResult(BuildInfo.Version, BuildInfo.RuntimeIdentifier),
                BmdJsonContext.Default.VersionResult));
        else
            Console.WriteLine($"bmd {BuildInfo.Version} ({BuildInfo.RuntimeIdentifier})");
        return 0;
    }
}
```

Register in `Program.cs` and add to `GroupHelp.Commands`. Note ConsoleAppFramework already handles a `--version` flag; `bmd version` as a command is additive and is what the spec's update checks will call.

- [ ] **Step 4: Run all tests, then check the real CLI**

```powershell
dotnet test
dotnet run --project src/Bmd -- version           # expect: bmd 0.0.0-dev (unknown)
dotnet run --project src/Bmd -- version --json
dotnet publish src/Bmd -c Release -r win-x64 -p:Version=1.2.3
& src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe version   # expect: bmd 1.2.3 (win-x64)
```

The last check is the one that matters: it proves the tag's version and the RID both reach the binary.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: bmd version reporting the build-stamped version and platform"
```

---

### Task 2: Tag-triggered release workflow

**Files:**
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Produces a workflow triggered on `push` of tags matching `v*`, which:
  1. **build matrix** — five entries, each building on the OS Native AOT requires:
     | RID | runs-on | archive |
     |---|---|---|
     | `win-x64` | `windows-latest` | `bmd-win-x64.zip` |
     | `linux-x64` | `ubuntu-latest` | `bmd-linux-x64.tar.gz` |
     | `linux-arm64` | `ubuntu-24.04-arm` | `bmd-linux-arm64.tar.gz` |
     | `osx-x64` | `macos-latest` | `bmd-osx-x64.tar.gz` |
     | `osx-arm64` | `macos-latest` | `bmd-osx-arm64.tar.gz` |
  2. derives the version from the tag (`v1.2.3` → `1.2.3`) and passes `-p:Version=`
  3. publishes with `dotnet publish src/Bmd -c Release -r <rid> -p:Version=<version>`
  4. archives **only the executable** (never the `.pdb`), preserving the executable bit on Unix
  5. uploads each archive as a build artifact
  6. **release job** (needs the matrix) — downloads all artifacts, writes `checksums.txt` with SHA-256 of every archive, and creates the GitHub Release with all archives plus `checksums.txt`, marking it a **pre-release when the tag contains a hyphen** (`v1.2.3-rc.1`)

Requirements:
- `permissions: contents: write` on the release job only.
- Pin actions to major versions (`actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/upload-artifact@v4`, `actions/download-artifact@v4`, `softprops/action-gh-release@v2` or `gh release create` via the CLI — either is fine; if using `gh`, note it is preinstalled on GitHub runners).
- `dotnet-version: 10.0.x` in setup-dotnet.
- Linux AOT needs `clang` and `zlib1g-dev`; add an apt step on the Linux legs (the ubuntu images usually have clang, but do not rely on it — install explicitly and say so).
- The release body should link to the site and list the archives; keep it short.
- Concurrency: cancel-in-progress false (a release must not be half-cancelled).

- [ ] **Step 1: Write the workflow**

Author `.github/workflows/release.yml` to the specification above. Prefer clarity over cleverness — this file is read far more often than it runs.

- [ ] **Step 2: Validate what can be validated locally**

Nothing here can be end-to-end tested without pushing a tag. Do all of:

```powershell
# 1. YAML parses (PowerShell 5.1 has no YAML cmdlet; use dotnet or python if available,
#    otherwise state clearly that syntax was verified by careful reading only)
python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml')); print('YAML OK')"
```

```powershell
# 2. The publish command shape actually works for the one RID buildable here
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/Bmd -c Release -r win-x64 -p:Version=9.9.9
& src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe version    # expect: bmd 9.9.9 (win-x64)
```

```powershell
# 3. The archive step's logic, exercised locally on the win-x64 output
Compress-Archive -Path src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe -DestinationPath bmd-win-x64.zip -Force
Get-FileHash bmd-win-x64.zip -Algorithm SHA256
Remove-Item bmd-win-x64.zip
```

Report exactly which parts are proven and which rest on inspection. **Do not push a tag.**

- [ ] **Step 3: Commit**

```powershell
git add -A; git commit -m "ci: tag-triggered release workflow building all five platforms"
```

---

### Task 3: The site — index page

**Files:**
- Create: `site/index.html`, `site/style.css`

**Design direction** (follow it; do not import a framework):
- Terminal-adjacent but not a gimmick: system font stack for prose (`-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif`), a monospace stack for commands and the demo block. No web fonts.
- Respect `prefers-color-scheme` for light and dark. Both must be legible; check contrast.
- Responsive down to a phone: single column, no horizontal scrolling, tappable download buttons.
- Self-contained: no CDN, no external images, no analytics, no JS beyond the small OS-detection snippet described below.

**Content, in order:**
1. **Header** — `bmd`, one-sentence description ("Command-line control for Blackmagic Videohub routers"), and the current state (Videohub supported; HyperDeck and ATEM not yet).
2. **Demo block** — a realistic short terminal session, e.g. `bmd discover`, `bmd videohub route list`, `bmd videohub route set 3 1`, showing plausible output. Use the real output shapes from the CLI, not invented ones.
3. **Download** — five links using GitHub's stable latest-asset URLs:
   `https://github.com/davojc/bmdcli/releases/latest/download/bmd-<rid>.zip` (win-x64) and `…/bmd-<rid>.tar.gz` (the four Unix RIDs). A small inline script detects the visitor's platform from `navigator.userAgent`/`navigator.platform` and visually promotes the matching one; **with JS disabled every link must still be present and usable** — the script only adds emphasis, it never creates the links. Also link `checksums.txt` and say how to verify.
4. **Quick start** — configure a device (`bmd discover --add`, or `bmd config set videohub.host <address>`), then `bmd videohub route list`. Mention that `bmd <command> --help` documents everything.
5. **Link to the Videohub guide** (Task 4).
6. **Footer** — link to the GitHub repo, the licence, and a note that this is an independent project not affiliated with Blackmagic Design.

- [ ] **Step 1: Write the files**

- [ ] **Step 2: Verify**

- Open `site/index.html` in a browser and confirm it renders correctly in both colour schemes (force via the browser's emulation) and at a narrow viewport (~380 px) with no horizontal scroll.
- Confirm every download link's URL is exactly the stable `releases/latest/download/...` form with no version in it.
- Disable JavaScript and confirm all five downloads remain visible and clickable.
- Confirm no request leaves the page: check the network panel shows only the document and `style.css`.
- Note in your report that the download links will 404 until the first release exists — that is expected and is why Task 2 comes first.

- [ ] **Step 3: Commit**

```powershell
git add -A; git commit -m "docs: site landing page with platform downloads and quick start"
```

---

### Task 4: The site — Videohub guide and the docs backlog

**Files:**
- Create: `site/videohub.html`
- Modify: `site/index.html` (link it), `site/style.css` (only if the guide needs anything new)

**Content:** a practical guide covering every shipped command, written for someone who has never used the tool. Sections:
1. **Connecting** — `bmd discover`, `bmd discover --add`, or `bmd config set videohub.host`. Explain the config layering briefly (local `.bmdconfig` overrides global; `--host` overrides both) and that `bmd config list --show-origin` shows where a value came from.
2. **Looking at the hub** — `info`, `input list`, `output list`, `route list`, and that everything is 1-based to match the front panel.
3. **Changing routes** — `route set <output> <input>`, renames, lock/unlock/`--force`.
4. **Backups** — every change snapshots first, where backups live, `backup.auto`/`backup.keep`/`backup.dir`, `--no-backup`, and that a mutation aborts if its backup fails.
5. **The event workflow** — `export` before, `restore --dry-run` to preview, `restore` to put it back; restore applies only differences and is safe to re-run after a failure.
6. **Watching** — `watch`, including that it shows changes made by other controllers and the front panel.
7. **Scripting and agents** — `--json` on every command, exit codes 0/1/2, that `watch --json` emits one object per line while everything else emits one document.
8. **Known limitations** — absorb the backlog verbatim in spirit:
   - mDNS discovery does not cross subnets, and older Videohubs predate mDNS — use `bmd config set videohub.host` instead. (Replies *can* arrive from another subnet, but queries do not cross routers.)
   - Windows may prompt for firewall access on the first `discover`.
   - `discover --add` writes the device's numeric address, so a DHCP lease change means running it again — a static address or reservation is recommended.
   - `bmd videohub watch --json | head -1` leaves the watch process running; press Ctrl+C.
   - Discovery has so far been verified on Windows only.
9. **Troubleshooting** — what each exit code means, what `error: no host configured` means and how to fix it, what a device rejection (`NAK`) means, and that a locked output will refuse a route change until unlocked.

Write it as prose with command blocks, not a wall of tables. Every command shown must be one that actually exists — cross-check against `bmd --help` output rather than memory.

- [ ] **Step 1: Write the file**
- [ ] **Step 2: Verify** — renders in both colour schemes and narrow viewport; every command mentioned exists (check against the real `--help`); the index links to it and it links back.
- [ ] **Step 3: Commit**

```powershell
git add -A; git commit -m "docs: Videohub guide covering every command, limitations, and troubleshooting"
```

---

### Task 5: Pages deploy workflow

**Files:**
- Create: `.github/workflows/pages.yml`

**Interfaces:** a workflow that deploys `site/` to GitHub Pages on push to `main` when `site/**` (or the workflow itself) changes, plus `workflow_dispatch` for manual runs.

Requirements:
- `permissions: contents: read, pages: write, id-token: write`.
- `concurrency: group: pages, cancel-in-progress: false`.
- Uses `actions/configure-pages@v5`, `actions/upload-pages-artifact@v3` with `path: site`, and `actions/deploy-pages@v4` in a `deploy` job with the `github-pages` environment.
- No build step — the site is plain files.

- [ ] **Step 1: Write the workflow**
- [ ] **Step 2: Validate** — YAML parses (same approach as Task 2); confirm the `path` matches the site directory exactly. State plainly that deployment cannot be proven until the repo's Pages source is switched to "GitHub Actions", which is a manual setting only the repo owner can change.
- [ ] **Step 3: Commit**

```powershell
git add -A; git commit -m "ci: deploy the site to GitHub Pages on changes to site/"
```

---

### Task 6: Milestone proof

**Files:** none expected

- [ ] **Step 1: Full suite + publish with a version**

```powershell
dotnet test
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/Bmd -c Release -r win-x64 -p:Version=8.0.0
```

Zero IL2xxx/IL3xxx warnings.

- [ ] **Step 2: Native smokes**

```powershell
$exe = "src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe"
& $exe version                 # expect: bmd 8.0.0 (win-x64)
& $exe version --json
& $exe --help                  # version listed
Test-Path src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.pdb   # true — but the workflow must not ship it
```

- [ ] **Step 3: Cross-check the site against reality**

For every command shown anywhere in `site/`, confirm it exists in `& $exe --help`. Report any mismatch as a defect and fix the page. This is the check that keeps the docs honest.

- [ ] **Step 4: Record size, verify no stray state, commit**

```powershell
Test-Path .bmdconfig
(Get-Item src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe).Length / 1MB
git status --short
git add -A; git commit -m "chore: prove milestone 8 (release pipeline and site)" --allow-empty
```

---

## Self-Review Notes

- **Spec coverage:** tag-triggered five-RID matrix with checksums and pre-release handling (Task 2); `bmd version` reporting the stamped version and RID (Task 1); Pages site with stable latest-asset downloads, OS detection that degrades gracefully, quick start, and the per-device guide (Tasks 3-4); deploy workflow (Task 5). The maintenance rule — site updates ship with command changes — is already in CLAUDE.md.
- **Version stamping deliberately avoids reflection.** Reading `AssemblyInformationalVersion` at runtime is the usual approach, but this project forbids reflection for AOT reasons; an MSBuild-generated constant is exact, trivially AOT-safe, and testable.
- **Two things this milestone genuinely cannot prove**, both stated in the tasks rather than papered over: the release workflow needs a pushed tag, and Pages deployment needs the repo setting flipped to "GitHub Actions". The end of this milestone should surface both to the user as their decisions, along with a recommendation to validate with a `-rc.1` pre-release tag (which the workflow marks as a pre-release and which future update checks will ignore).
- **Not addressed (carried forward):** the milestone-4 deferral on `SendBlockAsync`'s unbounded writes; the milestone-7 deferrals on a pooled-record cap and dotted-instance-name truncation.
