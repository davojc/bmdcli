namespace Bmd.Config;

/// <summary>A config address like "videohub.host": section + key name, split on the first dot.</summary>
public readonly record struct ConfigKey(string Section, string Name)
{
    public static bool TryParse(string raw, out ConfigKey key)
    {
        key = default;
        var dot = raw.IndexOf('.');
        if (dot <= 0 || dot == raw.Length - 1) return false;
        key = new ConfigKey(raw[..dot], raw[(dot + 1)..]);
        return true;
    }

    public override string ToString() => $"{Section.ToLowerInvariant()}.{Name.ToLowerInvariant()}";
}
