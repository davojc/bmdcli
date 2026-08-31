using Bmd.Output;

namespace Bmd.Tests.Output;

public class HelpTextTests
{
    static string[] Lines(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    [Fact]
    public void Reflow_WrapsALongDescriptionUnderItsOwnColumn()
    {
        // The bug this exists for: the generator emits one long line, the terminal breaks it at
        // its own edge with no hanging indent, and the continuation restarts at column zero —
        // which destroys the two-column layout exactly where the description is most useful.
        // Ordinary words, deliberately: a word longer than the column is a separate case with
        // its own test, and mixing the two would have this one pass or fail for the wrong reason.
        var help = "  --all                  " + string.Join(" ", Enumerable.Repeat("word", 24));

        var lines = Lines(HelpText.Reflow(help, 60));

        Assert.True(lines.Length > 1, "a description twice the width should have wrapped");
        Assert.StartsWith("  --all", lines[0]);
        Assert.All(lines, l => Assert.True(l.Length <= 60, $"line overflows: '{l}'"));

        // Every continuation begins under the description, not at the margin.
        var column = lines[0].IndexOf("word", StringComparison.Ordinal);
        Assert.All(lines.Skip(1), l => Assert.Equal(column, l.Length - l.TrimStart().Length));
    }

    [Fact]
    public void Reflow_LeavesAShortRowAlone()
    {
        const string help = "  --json    Emit the result as JSON on stdout.";
        Assert.Equal(help, HelpText.Reflow(help, 96).TrimEnd('\n'));
    }

    [Fact]
    public void Reflow_DropsTheNullDefaultButKeepsARealOne()
    {
        // "[Default: null]" says only that the author used a nullable type.
        var text = HelpText.Reflow("  --host <string?>    Device address. [Default: null]", 96);
        Assert.DoesNotContain("Default", text);

        var kept = HelpText.Reflow("  --timeout <int>    Seconds to wait. [Default: 5]", 96);
        Assert.Contains("[Default: 5]", kept);
    }

    [Fact]
    public void Reflow_NamesTheProgramInTheUsageLine()
    {
        // The generator writes the command path alone, so it reads as though `atem aux set` were
        // the thing you type at a prompt.
        Assert.StartsWith(
            "Usage: bmd atem aux set [arguments...]",
            HelpText.Reflow("Usage: atem aux set [arguments...]", 96));
    }

    [Fact]
    public void Reflow_DoesNotDoubleUpAUsageLineThatAlreadyNamesTheProgram()
    {
        const string help = "Usage: bmd [group] [command] [options]";
        Assert.Equal(help, HelpText.Reflow(help, 96).TrimEnd('\n'));
    }

    [Fact]
    public void Reflow_WrapsTheSummaryParagraph()
    {
        var summary = string.Join(" ", Enumerable.Repeat("word", 40));

        var lines = Lines(HelpText.Reflow(summary, 50));

        Assert.True(lines.Length > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 50, $"line overflows: '{l}'"));
        // A paragraph has no hanging indent: it is not a column.
        Assert.All(lines, l => Assert.False(l.StartsWith(' ')));
    }

    [Fact]
    public void Reflow_PreservesBlankLinesAndSectionHeadings()
    {
        const string help = "Options:\n\n  --json    Emit JSON.\n";
        var lines = Lines(HelpText.Reflow(help, 96));

        Assert.Equal("Options:", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.StartsWith("  --json", lines[2]);
    }

    [Fact]
    public void Reflow_PutsTheTextUnderneathWhenTheLabelIsTooWide()
    {
        // A label wide enough to squeeze the description into a ribbon reads worse in two
        // columns than in one.
        var help = "  --an-extremely-long-option-name-indeed <string>    " + string.Join(" ", Enumerable.Repeat("word", 20));

        var lines = Lines(HelpText.Reflow(help, 60));

        Assert.Equal("  --an-extremely-long-option-name-indeed <string>", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("      ", l));
        Assert.All(lines, l => Assert.True(l.Length <= 60, $"line overflows: '{l}'"));
    }

    [Fact]
    public void Reflow_LetsAnUnbreakableWordOverflowRatherThanBreakingIt()
    {
        // Breaking a path or a URL mid-word makes it uncopyable, which is worse than a ragged edge.
        var url = new string('u', 70);
        var text = HelpText.Reflow($"  --x    see {url}", 50);
        Assert.Contains(url, text);
    }

    [Fact]
    public void Reflow_LeavesEverythingAloneOnAVeryNarrowTerminal()
    {
        // Below the minimum, wrapping produces more damage than the overflow it prevents.
        var help = "  --all    " + new string('x', 100);
        Assert.Equal(help, HelpText.Reflow(help, 20));
    }

    [Fact]
    public void ConsoleWidth_IsUsableEvenWithNoConsoleAttached()
    {
        // The test host has no terminal, which is the same situation as a redirect or a pipe.
        var width = HelpText.ConsoleWidth();
        Assert.InRange(width, HelpText.MinimumWidth, 100);
    }

    [Fact]
    public void Reflow_HandlesRealGeneratedHelpEndToEnd()
    {
        const string generated = """
            Usage: atem input rename [arguments...] [options...] [-h|--help] [--version]

            Rename an input on the switcher itself, so the name matches in its multiviewer, its software control, and every other controller.

            Arguments:
              [0] <string>     Source to rename: its id or its current name, as shown by `bmd atem input list`.

            Options:
              --short <string?>    New short name, up to 4 characters — this is what the switcher shows on multiviewer labels. Omit to leave it unchanged. [Default: null]
              --json               Emit the result as JSON on stdout.
            """;

        var lines = Lines(HelpText.Reflow(generated, 88));

        Assert.All(lines, l => Assert.True(l.Length <= 88, $"line overflows at {l.Length}: '{l}'"));
        Assert.StartsWith("Usage: bmd atem input rename", lines[0]);
        Assert.DoesNotContain("[Default: null]", string.Join("\n", lines));
        Assert.Contains(lines, l => l.Trim().StartsWith("--short"));
        // The row wrapped rather than being truncated: the tail of that description survives.
        Assert.Contains("Omit to leave it unchanged.", string.Join(" ", lines));
    }
}
