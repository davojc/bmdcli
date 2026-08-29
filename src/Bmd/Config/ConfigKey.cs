namespace Bmd.Config;

/// <summary>A config address like "videohub.host": section + key name, split on the first dot.</summary>
public readonly record struct ConfigKey(string Section, string Name)
{
    public static bool TryParse(string raw, out ConfigKey key)
    {
        key = default;
        var dot = raw.IndexOf('.');
        if (dot <= 0 || dot == raw.Length - 1) return false;
        var section = raw[..dot];
        var name = raw[(dot + 1)..];
        if (!IsValidPart(section) || !IsValidPart(name)) return false;
        key = new ConfigKey(section, name);
        return true;
    }

    // Section/name parts must not contain characters that would corrupt the INI
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

    public override string ToString() => $"{Section.ToLowerInvariant()}.{Name.ToLowerInvariant()}";
}
