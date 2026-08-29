namespace Bmd.Config;

/// <summary>Git-style INI file. Line-preserving: unedited lines round-trip byte-for-byte (LF).</summary>
public sealed class IniFile
{
    readonly List<string> _lines;

    IniFile(List<string> lines) => _lines = lines;

    public static IniFile Empty() => new([]);

    public static IniFile Parse(string text)
    {
        var lines = text.Length == 0
            ? new List<string>()
            : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        return new IniFile(lines);
    }

    public string? Get(string section, string key)
    {
        foreach (var (s, k, v) in Entries())
            if (SectionEquals(s, section) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    public IEnumerable<(string Section, string Key, string Value)> Entries()
    {
        string? current = null;
        foreach (var line in _lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';') continue;
            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                current = trimmed[1..^1].Trim();
                continue;
            }
            var eq = trimmed.IndexOf('=');
            if (current is null || eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = ParseValue(trimmed[(eq + 1)..]);
            if (key.Length > 0) yield return (current, key, value);
        }
    }

    static string ParseValue(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith('"'))
        {
            var close = raw.IndexOf('"', 1);
            return close > 0 ? raw[1..close] : raw[1..];
        }
        var comment = raw.IndexOfAny(['#', ';']);
        if (comment >= 0) raw = raw[..comment];
        return raw.Trim();
    }

    static bool SectionEquals(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
