using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Bmd.Commands;

namespace Bmd.Tests.Commands;

public class GroupHelpTests
{
    static string Write(params string[] args)
    {
        var writer = new StringWriter();
        Assert.True(GroupHelp.TryWrite(args, writer));
        return writer.ToString();
    }

    // Each InlineData wraps its string[] payload in an explicit `new object[] { ... }`:
    // passing the string[] directly (`new[] { "videohub" }`) hits CS0182, because binding
    // it straight to InlineData's `params object[] data` needs an implicit array-covariance
    // conversion (string[] -> object[]), which C# disallows in attribute arguments.
    [Theory]
    [InlineData(new object[] { new[] { "videohub" } })]
    [InlineData(new object[] { new[] { "videohub", "--help" } })]
    [InlineData(new object[] { new[] { "videohub", "-h" } })]
    public void VideohubGroup_ListsOnlyVideohubCommands(string[] args)
    {
        var text = Write(args);
        Assert.Contains("videohub route set", text);
        Assert.Contains("videohub info", text);
        Assert.DoesNotContain("config get", text);
    }

    // Fix 8: eighteen multiview command registrations landed with zero group-help coverage —
    // a parallel theory to VideohubGroup_ListsOnlyVideohubCommands, above, so `bmd multiview`
    // and `bmd multiview --help` are exercised the same way.
    [Theory]
    [InlineData(new object[] { new[] { "multiview" } })]
    [InlineData(new object[] { new[] { "multiview", "--help" } })]
    [InlineData(new object[] { new[] { "multiview", "-h" } })]
    public void MultiViewGroup_ListsOnlyMultiViewCommands(string[] args)
    {
        var text = Write(args);
        Assert.Contains("multiview view set", text);
        Assert.Contains("multiview info", text);
        Assert.Contains("multiview solo", text);
        Assert.DoesNotContain("videohub", text);
        Assert.DoesNotContain("config get", text);
    }

    [Fact]
    public void ConfigGroup_ListsOnlyConfigCommands()
    {
        var text = Write("config", "--help");
        Assert.Contains("config set", text);
        Assert.DoesNotContain("videohub", text);
    }

    [Theory]
    [InlineData(new object[] { new[] { "videohub", "info" } })]          // an exact command: not group help
    [InlineData(new object[] { new[] { "videohub", "route", "set" } })]
    [InlineData(new object[] { new[] { "nonsense" } })]
    public void NonGroupInvocations_AreNotHandled(string[] args)
    {
        Assert.False(GroupHelp.TryWrite(args, new StringWriter()));
    }

    // Root help used to fall through to ConsoleAppFramework, which listed all 38 registrations
    // flat, each with its full XML summary — several of them multi-line, and rendered with the
    // C# source's own indentation. Reversing that is the point of these tests: the two cases
    // below (`--help` and no args at all) were previously asserted NOT to be handled.
    [Theory]
    [InlineData(new object[] { new string[0] })]
    [InlineData(new object[] { new[] { "--help" } })]
    [InlineData(new object[] { new[] { "-h" } })]
    public void RootHelp_ListsGroupsRatherThanEveryCommand(string[] args)
    {
        var text = Write(args);

        Assert.Contains("videohub", text);
        Assert.Contains("multiview", text);
        Assert.Contains("config", text);

        // Commands belonging to no group still have to appear here — this is their only listing.
        Assert.Contains("discover", text);
        Assert.Contains("version", text);
        Assert.Contains("update", text);

        // ...but a group's own commands do not. They live behind `bmd <group> --help`, which
        // the listing must say so nobody concludes the command simply doesn't exist.
        Assert.DoesNotContain("videohub route set", text);
        Assert.DoesNotContain("multiview view set", text);
        Assert.DoesNotContain("config get", text);
        Assert.Contains("bmd <group> --help", text);
    }

    [Fact]
    public void RootHelp_NamesTheBinaryAndSaysWhatItIs()
    {
        var text = Write("--help");
        Assert.StartsWith("bmd", text);
        Assert.Contains("Blackmagic", text);
    }

    [Fact]
    public void GroupHelp_UsageLineNamesTheBinary()
    {
        // Was "Usage: videohub [command] ...", which reads as if `videohub` were the program.
        Assert.Contains("Usage: bmd videohub", Write("videohub", "--help"));
    }

    [Theory]
    [InlineData(new object[] { new[] { "--version" } })]
    [InlineData(new object[] { new[] { "version" } })]
    public void VersionInvocations_StillFallThroughToTheFramework(string[] args)
    {
        // Intercepting root help must not swallow --version, and `version` is a real command.
        Assert.False(GroupHelp.TryWrite(args, new StringWriter()));
    }

    [Fact]
    public void NoCommandHelpTextLeaksXmlEntities()
    {
        // XML doc comments must escape `<`, and ConsoleAppFramework prints the summary without
        // unescaping it — so `&lt;output&gt;` reached the user's terminal verbatim. Uppercase
        // placeholders (OUTPUT INPUT) are the help-text convention anyway and cannot recur.
        // Scoped to Commands/ because entities in non-rendered docs elsewhere are harmless;
        // GroupHelp's own class summary is excluded for the same reason.
        var offenders =
            from file in Directory.EnumerateFiles(CommandsDirectory(), "*.cs", SearchOption.AllDirectories)
            where Path.GetFileName(file) != "GroupHelp.cs"
            from line in File.ReadAllLines(file).Select((text, index) => (text, number: index + 1))
            where line.text.Contains("&lt;") || line.text.Contains("&gt;")
            select $"{Path.GetFileName(file)}:{line.number}{line.text}";

        Assert.Empty(offenders);
    }

    static string CommandsDirectory([CallerFilePath] string here = "")
    {
        var testDirectory = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "..", "src", "Bmd", "Commands"));
    }

    [Fact]
    public void EveryRegisteredCommandAppearsInTheTable()
    {
        // guards against adding a command and forgetting the listing
        Assert.Contains(GroupHelp.Commands, c => c.Path == "videohub output unlock");
        Assert.All(GroupHelp.Commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
        Assert.Equal(GroupHelp.Commands.Length, GroupHelp.Commands.Select(c => c.Path).Distinct().Count());
    }

    // Fix 8: the fact above only ever spot-checked one Videohub path despite its name. This
    // reads every `app.Add("<path>", ...)` registration straight out of Program.cs (the single
    // source of truth for what's actually wired up) and asserts each one has a GroupHelp.Commands
    // entry — so forgetting the listing for ANY newly-registered command fails, not just the one
    // path that happened to be spot-checked.
    [Fact]
    public void EveryRegisteredCommandAppearsInTheTable_CheckedAgainstProgramCs()
    {
        var path = ProgramCsPath();
        Assert.True(File.Exists(path), $"expected to find Program.cs at '{path}'");

        var registered = Regex.Matches(File.ReadAllText(path), @"app\.Add\(""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        // Guards the guard: if the regex ever stops matching (e.g. Program.cs's registration
        // style changes), fail loudly here rather than silently passing on zero comparisons.
        Assert.True(registered.Length >= 30, $"expected many registrations, found {registered.Length}");

        foreach (var commandPath in registered)
            Assert.Contains(GroupHelp.Commands, c => c.Path == commandPath);
    }

    static string ProgramCsPath([CallerFilePath] string here = "")
    {
        var testDirectory = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "..", "src", "Bmd", "Program.cs"));
    }
}
