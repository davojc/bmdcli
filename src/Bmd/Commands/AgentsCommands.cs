using System.Reflection;
using System.Text;

namespace Bmd.Commands;

/// <summary>`bmd agents` — prints the guide that tells an AI coding agent how to drive bmd.
///
/// The document is embedded in the binary rather than fetched, so it always describes the version
/// you are actually running, works with no network, and cannot drift from the commands it
/// documents. The same file ships as a release asset for anyone who has not installed bmd yet.
///
/// <para>Deliberately one document rather than one per vendor. What differs between Claude, Codex,
/// Gemini, Cursor and the rest is where the file goes and what header it needs, not the content.
/// Keeping N near-identical documents in step is how they end up disagreeing.</para>
///
/// <para><b>A skill is the right home for this, not a main instruction file.</b> `CLAUDE.md`,
/// `AGENTS.md` and `GEMINI.md` are a project's own instructions — how to work on <i>that</i>
/// repository — and they are loaded into context for every task. A manual for a tool the project
/// merely uses does not belong there: it would sit in context permanently and, worse, overwrite
/// instructions someone wrote by hand. <c>--skill</c> installs it where it is loaded only when a
/// task actually involves Blackmagic hardware.</para></summary>
public sealed class AgentsCommands
{
    /// <summary>Set by the csproj as an embedded resource. Named, rather than derived from the
    /// assembly name, so renaming the assembly cannot silently break the lookup.</summary>
    const string ResourceName = "bmd.agents.md";

    /// <summary>Print the guide to using bmd from an AI agent or a script: the contracts that hold across every command, the conventions that will surprise you, and worked examples. Use --skill to install it as an agent skill, which is where it belongs.</summary>
    /// <param name="skill">Install as an agent skill, at .claude/skills/bmd/SKILL.md unless --write says otherwise. Adds the frontmatter that makes an agent load it only when a task involves Blackmagic hardware.</param>
    /// <param name="write">Where to save it. Without --skill the plain guide is written, for a tool that reads a file directly rather than discovering skills.</param>
    /// <param name="force">Overwrite the file if it already exists.</param>
    public int Agents(bool skill = false, string? write = null, bool force = false)
    {
        var text = Read();
        if (text is null)
        {
            Console.Error.WriteLine("error: this build does not carry the agent guide");
            return 1;
        }

        // --skill chooses the format, --write the destination; either one implies a file. Given
        // neither, print, so the command still composes with a pipe or a redirect.
        if (!skill && write is null)
        {
            Console.Out.Write(text);
            return 0;
        }

        var path = SkillPath(write, skill);
        // The blank line is explicit rather than trusted to the raw-string literal: a frontmatter
        // block running straight into the first heading is valid markdown but not every parser
        // that reads the header is that forgiving.
        if (skill) text = Frontmatter + Environment.NewLine + text;
        try
        {
            if (File.Exists(path) && !force)
            {
                Console.Error.WriteLine($"error: {path} already exists (pass --force to overwrite)");
                return 1;
            }
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // UTF-8 with no BOM, explicitly: PowerShell's own redirection still defaults to other
            // encodings on some hosts, and a BOM at the top of a markdown file shows up as stray
            // characters in whatever reads it.
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine($"Wrote {path}");
            if (skill)
                Console.WriteLine("Your agent will load it when a task involves Blackmagic hardware.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Where the file goes. No <c>--write</c> means the conventional skill location; a
    /// directory means "put SKILL.md in here"; anything else is taken literally.</summary>
    internal static string SkillPath(string? given, bool skill)
    {
        if (given is null) return Path.Combine(".claude", "skills", "bmd", "SKILL.md");
        if (skill && (Directory.Exists(given) || given.EndsWith('/') || given.EndsWith('\\')))
            return Path.Combine(given, "SKILL.md");
        return given;
    }

    /// <summary>The header that makes this a skill rather than a document. The description is what
    /// an agent matches against when deciding whether the skill is relevant, so it names the
    /// hardware and the tasks rather than describing the document.</summary>
    internal const string Frontmatter =
        """
        ---
        name: bmd
        description: Use when controlling Blackmagic Design hardware over a network with the bmd CLI - Videohub routers, MultiView multiviewers, and ATEM switchers. Covers routing, labels, aux outputs, program and preview, device contexts, backups and JSON output.
        ---

        """;

    internal static string? Read()
    {
        using var stream = typeof(AgentsCommands).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
