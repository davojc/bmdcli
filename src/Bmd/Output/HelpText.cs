using System.Text;
using System.Text.RegularExpressions;

namespace Bmd.Output;

/// <summary>Re-wraps the help ConsoleAppFramework generates so it fits the terminal.
///
/// The generator emits each option's description as a single unwrapped line. A terminal then
/// soft-wraps it at its own edge with no hanging indent, so continuation text restarts at column
/// zero and the two-column layout collapses into a wall — worse the more useful the description
/// is, which is a bad thing to punish.
///
/// <para>This reflows that output rather than replacing it. Taking over help rendering outright
/// would mean maintaining a table of every command's parameters by hand, in parallel with the
/// ones the source generator already derives from the method signatures — a second source of
/// truth that would drift the first time somebody added a flag. Reformatting text the generator
/// produced keeps one source of truth and fixes every command at once, including commands that
/// do not exist yet.</para></summary>
public static partial class HelpText
{
    /// <summary>Used when the terminal will not say how wide it is — output redirected to a file
    /// or a pipe, or a host with no console attached.</summary>
    public const int FallbackWidth = 96;

    /// <summary>Below this, wrapping does more harm than the overflow it prevents, so the text is
    /// left alone and the terminal does whatever it was going to do.</summary>
    public const int MinimumWidth = 40;

    /// <summary>Beyond this, lines get too long to track back to the next one comfortably.</summary>
    const int MaximumWidth = 100;

    public static int ConsoleWidth()
    {
        try
        {
            var width = Console.WindowWidth;
            return width >= MinimumWidth ? Math.Min(width, MaximumWidth) : FallbackWidth;
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException)
        {
            // No console attached: piped, redirected, or running under a test host.
            return FallbackWidth;
        }
    }

    /// <summary>A help line laid out as two columns: some indent, a label, two or more spaces,
    /// then the description. That gap is what the generator uses to align the columns, and it is
    /// the only reliable way to tell a wrapped row from a prose paragraph.</summary>
    [GeneratedRegex(@"^(\s+)(\S.*?\S|\S)(\s{2,})(\S.*)$")]
    private static partial Regex TwoColumnRow();

    /// <summary>Every nullable option is annotated with this, which tells a reader nothing except
    /// that the author used a nullable type. Real defaults are left alone.</summary>
    [GeneratedRegex(@"\s*\[Default: null\]$")]
    private static partial Regex NullDefault();

    public static string Reflow(string help, int width)
    {
        if (width < MinimumWidth) return help;

        var output = new StringBuilder();
        // Split on \n and strip \r so a Windows-generated string reflows the same as a Unix one;
        // the writer puts native line endings back.
        foreach (var raw in help.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.Length == 0)
            {
                output.Append('\n');
                continue;
            }

            // The generator writes the command path but not the program it belongs to, so the
            // usage line reads as if `atem aux set` were the thing you type.
            if (line.StartsWith("Usage: ", StringComparison.Ordinal)
                && !line.StartsWith("Usage: bmd", StringComparison.Ordinal))
            {
                line = "Usage: bmd " + line["Usage: ".Length..];
            }

            var row = TwoColumnRow().Match(line);
            if (row.Success)
            {
                var indent = row.Groups[1].Value;
                var label = row.Groups[2].Value;
                var gap = row.Groups[3].Value;
                var description = NullDefault().Replace(row.Groups[4].Value, "");
                var column = indent.Length + label.Length + gap.Length;

                // A label so wide that the description would be squeezed into a ribbon: give up on
                // the second column and put the text underneath instead.
                if (column > width - 24)
                {
                    output.Append(indent).Append(label).Append('\n');
                    AppendWrapped(output, description, indent + "    ", width);
                    continue;
                }

                var first = indent + label + gap;
                AppendWrapped(output, description, new string(' ', column), width, first);
                continue;
            }

            // Prose: the command summary, which is one long line for the same reason.
            if (line.Length > width && !line.StartsWith(' '))
            {
                AppendWrapped(output, line, "", width);
                continue;
            }

            output.Append(line).Append('\n');
        }

        return output.ToString();
    }

    /// <summary>Writes <paramref name="text"/> wrapped to <paramref name="width"/>, indenting every
    /// line after the first to <paramref name="hangingIndent"/> — the hanging indent whose absence
    /// is the whole problem. <paramref name="firstPrefix"/> defaults to the hanging indent, so a
    /// paragraph and a two-column row share one implementation.</summary>
    static void AppendWrapped(
        StringBuilder output, string text, string hangingIndent, int width, string? firstPrefix = null)
    {
        var prefix = firstPrefix ?? hangingIndent;
        var available = Math.Max(width - hangingIndent.Length, 20);
        var line = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > available)
            {
                output.Append(prefix).Append(line).Append('\n');
                prefix = hangingIndent;
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            // A single word longer than the column (a long path, or a URL) is left to overflow:
            // breaking it would make it uncopyable, which is worse than a ragged edge.
            line.Append(word);
        }

        if (line.Length > 0) output.Append(prefix).Append(line).Append('\n');
    }
}
