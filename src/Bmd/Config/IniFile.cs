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

    public string ToText() => _lines.Count == 0 ? "" : string.Join('\n', _lines) + "\n";

    public void Set(string section, string key, string value)
    {
        var formatted = $"\t{key} = {FormatValue(value)}";
        var (sectionStart, sectionEnd) = FindSection(section);
        if (sectionStart < 0)
        {
            _lines.Add($"[{section}]");
            _lines.Add(formatted);
            return;
        }
        var keyLine = FindKeyLine(sectionStart, sectionEnd, key);
        if (keyLine >= 0) { _lines[keyLine] = formatted; return; }
        // insert after the last non-blank line of the section
        var insertAt = sectionEnd;
        while (insertAt > sectionStart + 1 && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
        _lines.Insert(insertAt, formatted);
    }

    public bool Unset(string section, string key)
    {
        var (sectionStart, sectionEnd) = FindSection(section);
        if (sectionStart < 0) return false;
        var keyLine = FindKeyLine(sectionStart, sectionEnd, key);
        if (keyLine < 0) return false;
        _lines.RemoveAt(keyLine);
        return true;
    }

    /// <returns>(header line index, exclusive end index) or (-1, -1)</returns>
    (int Start, int End) FindSection(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var t = _lines[i].Trim();
            if (t.Length > 1 && t[0] == '[' && t[^1] == ']' && SectionEquals(t[1..^1].Trim(), section))
            {
                var end = i + 1;
                while (end < _lines.Count && !_lines[end].TrimStart().StartsWith('[')) end++;
                return (i, end);
            }
        }
        return (-1, -1);
    }

    int FindKeyLine(int sectionStart, int sectionEnd, string key)
    {
        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            var t = _lines[i].Trim();
            if (t.Length == 0 || t[0] is '#' or ';') continue;
            var eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    static string FormatValue(string value) =>
        value.IndexOfAny(['#', ';']) >= 0 || value != value.Trim()
            ? $"\"{value}\""
            : value;
}
