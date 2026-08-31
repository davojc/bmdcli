using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Bmd.Commands;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class AgentsCommandsTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), $"bmd-agents-{Guid.NewGuid():N}");
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public AgentsCommandsTests()
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

    [Fact]
    public void Agents_PrintsTheEmbeddedGuide()
    {
        // Reads it back through the same path the shipped command uses, so a build that fails to
        // embed the resource — or embeds it under a different name — fails here rather than in a
        // user's terminal.
        Assert.Equal(0, new AgentsCommands().Agents());

        var text = _stdout.ToString();
        Assert.StartsWith("# bmd for agents", text);
        Assert.True(text.Length > 4000, $"the guide looks truncated at {text.Length} characters");
    }

    [Fact]
    public void Agents_Write_SavesUtf8WithoutAByteOrderMark()
    {
        var path = Path.Combine(_directory, "AGENTS.md");
        Assert.Equal(0, new AgentsCommands().Agents(write: path));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'#', bytes[0]);   // no BOM: a stray one shows up in whatever reads this
        Assert.Contains("bmd atem program set", File.ReadAllText(path));
    }

    [Fact]
    public void Agents_Skill_InstallsAsASkillWithFrontmatter()
    {
        // Without the header this is a document an agent will not discover; with it, the agent
        // loads it only when a task actually involves Blackmagic hardware.
        var path = Path.Combine(_directory, "SKILL.md");
        Assert.Equal(0, new AgentsCommands().Agents(skill: true, write: path));

        var lines = File.ReadAllLines(path);
        var text = File.ReadAllText(path);
        Assert.Equal("---", lines[0]);
        Assert.Equal("name: bmd", lines[1]);
        Assert.StartsWith("description: Use when controlling Blackmagic", lines[2]);
        Assert.Equal("---", lines[3]);
        Assert.Equal("", lines[4]);              // blank line before the body
        Assert.Equal("# bmd for agents", lines[5]);
        Assert.Contains("bmd atem program set", text);
    }

    [Fact]
    public void Agents_Write_DoesNotAddSkillFrontmatter()
    {
        // A tool that reads a plain file wants the document, not a header it will render as text.
        var path = Path.Combine(_directory, "plain.md");
        Assert.Equal(0, new AgentsCommands().Agents(write: path));
        Assert.DoesNotContain("name: bmd", File.ReadAllText(path));
    }

    [Fact]
    public void Agents_Skill_DefaultsToTheConventionalSkillLocation()
    {
        // Never a main instruction file: CLAUDE.md and its equivalents are the project's own
        // instructions, loaded for every task and often hand-written. A tool manual belongs in a
        // skill that loads on demand, and must never overwrite those.
        // Separators are normalised before comparing: Path.Combine keeps whatever the caller
        // wrote, so "elsewhere/" stays forward-slashed even on Windows.
        static string Normal(string path) => path.Replace('\\', '/');

        Assert.Equal(".claude/skills/bmd/SKILL.md", Normal(AgentsCommands.SkillPath(null, skill: true)));
        Assert.Equal("elsewhere/SKILL.md", Normal(AgentsCommands.SkillPath("elsewhere/", skill: true)));
        Assert.Equal("custom/place.md", Normal(AgentsCommands.SkillPath("custom/place.md", skill: true)));
        Assert.Equal("AGENTS.md", Normal(AgentsCommands.SkillPath("AGENTS.md", skill: false)));
    }

    [Fact]
    public void Agents_SkillAndWriteCompose()
    {
        // --skill chooses the format, --write the destination, so the two combine rather than
        // conflict: a skill can be installed somewhere other than the default location.
        var path = Path.Combine(_directory, "custom", "SKILL.md");
        Assert.Equal(0, new AgentsCommands().Agents(skill: true, write: path));
        Assert.StartsWith("---", File.ReadAllText(path));
    }

    [Fact]
    public void Agents_Write_RefusesToClobberWithoutForce()
    {
        var path = Path.Combine(_directory, "CLAUDE.md");
        File.WriteAllText(path, "existing project instructions");

        Assert.Equal(1, new AgentsCommands().Agents(write: path));
        Assert.Contains("--force", _stderr.ToString());
        Assert.Equal("existing project instructions", File.ReadAllText(path));

        Assert.Equal(0, new AgentsCommands().Agents(write: path, force: true));
        Assert.StartsWith("# bmd for agents", File.ReadAllText(path));
    }

    [Fact]
    public void Agents_Write_CreatesMissingDirectories()
    {
        // `--write .cursor/rules/bmd.md` should work in a fresh checkout.
        var path = Path.Combine(_directory, "nested", "rules", "bmd.md");
        Assert.Equal(0, new AgentsCommands().Agents(write: path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Guide_OnlyNamesCommandsThatExist()
    {
        // The guide's whole design is to describe contracts rather than restate flags, so it
        // cannot go stale on flags. It CAN go stale on command names, and that is the failure a
        // reader would trust — so every `bmd ...` example is checked against the registrations in
        // Program.cs, the same way GroupHelp's table is.
        var registered = Regex.Matches(File.ReadAllText(ProgramCsPath()), @"app\.Add\(""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.True(registered.Length >= 40, $"expected many registrations, found {registered.Length}");

        var groups = registered
            .Where(path => path.Contains(' '))
            .Select(path => path[..path.IndexOf(' ')])
            .Distinct()
            .ToHashSet(StringComparer.Ordinal);

        var unknown = new List<string>();
        foreach (Match match in Regex.Matches(AgentsCommands.Read()!, @"(?m)^\s*bmd ([a-z0-9 -]+)"))
        {
            var invocation = match.Groups[1].Value;
            // Everything from the first flag onward is arguments, not a command path.
            var flag = invocation.IndexOf("--", StringComparison.Ordinal);
            if (flag >= 0) invocation = invocation[..flag];
            invocation = invocation.Trim();

            // `bmd --help` and `bmd atem --help` are help, not registrations.
            if (invocation.Length == 0 || groups.Contains(invocation)) continue;
            if (!registered.Any(path =>
                    invocation == path || invocation.StartsWith(path + " ", StringComparison.Ordinal)))
                unknown.Add(invocation);
        }

        Assert.Empty(unknown);
    }

    static string ProgramCsPath([CallerFilePath] string here = "")
    {
        var testDirectory = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "..", "src", "Bmd", "Program.cs"));
    }
}
