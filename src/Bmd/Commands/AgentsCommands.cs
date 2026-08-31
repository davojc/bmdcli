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
/// Gemini, Cursor and the rest is the <i>filename</i> each looks for, not the content — so
/// <c>--write</c> takes a path and the caller picks the convention. Keeping N near-identical
/// documents in step is how they end up disagreeing.</para></summary>
public sealed class AgentsCommands
{
    /// <summary>Set by the csproj as an embedded resource. Named, rather than derived from the
    /// assembly name, so renaming the assembly cannot silently break the lookup.</summary>
    const string ResourceName = "bmd.agents.md";

    /// <summary>Print the guide to using bmd from an AI agent or a script: the contracts that hold across every command, the conventions that will surprise you, and worked examples. Redirect it, or use --write, to save it where your tool looks for it.</summary>
    /// <param name="write">Save to this file instead of printing. Written as UTF-8 without a byte-order mark. Pass the flag with no path for AGENTS.md in the current directory.</param>
    /// <param name="force">Overwrite the file if it already exists.</param>
    public int Agents(string? write = null, bool force = false)
    {
        var text = Read();
        if (text is null)
        {
            Console.Error.WriteLine("error: this build does not carry the agent guide");
            return 1;
        }

        if (write is null)
        {
            Console.Out.Write(text);
            return 0;
        }

        // An empty --write means "the conventional name here". Anything else is taken literally,
        // so `--write CLAUDE.md` or `--write .cursor/rules/bmd.md` both work.
        var path = write.Length == 0 ? "AGENTS.md" : write;
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
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    internal static string? Read()
    {
        using var stream = typeof(AgentsCommands).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
