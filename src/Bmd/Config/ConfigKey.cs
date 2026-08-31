namespace Bmd.Config;

/// <summary>A config address like "videohub.host": section + key name, split on the first dot,
/// optionally scoped to a named context.
///
/// A context is stored as a git-style INI subsection — <c>[videohub "gallery"]</c> — so one config
/// file can hold several devices of the same type. <see cref="IniSection"/> is the only place that
/// spelling is constructed; nothing else should build a section name by hand.</summary>
public readonly record struct ConfigKey(string Section, string Name, string? Context = null)
{
    /// <summary>The context name that means "no context": the unlabelled section, which is what
    /// every config written before contexts existed uses and what a bare command still reads.
    /// Reserved, so a real context can never be called this and shadow the default.</summary>
    public const string DefaultContextName = "default";

    public static bool TryParse(string raw, out ConfigKey key) => TryParse(raw, null, out key);

    public static bool TryParse(string raw, string? context, out ConfigKey key)
    {
        key = default;
        var dot = raw.IndexOf('.');
        if (dot <= 0 || dot == raw.Length - 1) return false;
        var section = raw[..dot];
        var name = raw[(dot + 1)..];
        if (!IsValidPart(section) || !IsValidPart(name)) return false;
        if (context is not null && !IsValidContext(context)) return false;
        key = new ConfigKey(section, name, Normalise(context));
        return true;
    }

    /// <summary>Null for the default context, so callers never have to special-case the string.</summary>
    public static string? Normalise(string? context) =>
        context is null || context.Equals(DefaultContextName, StringComparison.OrdinalIgnoreCase)
            ? null
            : context;

    /// <summary>A context name must survive an INI round trip and must not be the reserved default.</summary>
    public static bool IsValidContext(string context) =>
        IsValidPart(context) && !context.Equals(DefaultContextName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The INI section this key lives in: <c>videohub</c>, or <c>videohub "gallery"</c>.</summary>
    public string IniSection => Context is null ? Section : $"{Section} \"{Context}\"";

    /// <summary>Splits an INI section name back into its type and context. The inverse of
    /// <see cref="IniSection"/>, for reading a file whose sections we did not construct.</summary>
    public static (string Section, string? Context) SplitIniSection(string iniSection)
    {
        var quote = iniSection.IndexOf('"');
        if (quote <= 0 || iniSection[^1] != '"') return (iniSection, null);
        return (iniSection[..quote].Trim(), iniSection[(quote + 1)..^1]);
    }

    // Section/name/context parts must not contain characters that would corrupt the INI
    // syntax on write or be mis-parsed on read (section/key delimiters, quoting,
    // comment markers, or whitespace/control characters).
    static bool IsValidPart(string part)
    {
        if (part.Length == 0) return false;
        foreach (var c in part)
        {
            if (c is '=' or '[' or ']' or '#' or ';' or '"') return false;
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        }
        return true;
    }

    public override string ToString() => Context is null
        ? $"{Section.ToLowerInvariant()}.{Name.ToLowerInvariant()}"
        : $"{Section.ToLowerInvariant()}.{Name.ToLowerInvariant()} [{Context}]";
}
