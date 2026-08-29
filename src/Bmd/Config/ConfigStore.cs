namespace Bmd.Config;

public sealed record ConfigEntry(string Key, string Value, string Origin);

/// <summary>Layered config: local .bmdconfig (walk-up from start directory) over global file.</summary>
public sealed class ConfigStore
{
    readonly string _globalPath;
    readonly string _startDirectory;
    readonly string? _localPath;
    readonly IniFile _global;
    readonly IniFile _local;

    ConfigStore(string globalPath, string startDirectory, string? localPath, IniFile global, IniFile local)
    {
        _globalPath = globalPath;
        _startDirectory = startDirectory;
        _localPath = localPath;
        _global = global;
        _local = local;
    }

    public static ConfigStore LoadDefault() =>
        Load(ConfigPaths.GlobalConfigPath, Environment.CurrentDirectory);

    public static ConfigStore Load(string globalPath, string startDirectory)
    {
        var localPath = ConfigPaths.FindLocalConfig(startDirectory);
        return new ConfigStore(
            globalPath,
            startDirectory,
            localPath,
            LoadFile(globalPath),
            localPath is null ? IniFile.Empty() : LoadFile(localPath));
    }

    static IniFile LoadFile(string path) =>
        File.Exists(path) ? IniFile.Parse(File.ReadAllText(path)) : IniFile.Empty();

    public string? GetEffective(ConfigKey key) =>
        _local.Get(key.Section, key.Name) ?? _global.Get(key.Section, key.Name);

    public IReadOnlyList<ConfigEntry> ListEffective()
    {
        var result = new List<ConfigEntry>();
        var seen = new HashSet<string>();
        // local wins, so collect local shadow keys first
        var localEntries = _localPath is null
            ? []
            : _local.Entries().Select(e => new ConfigEntry(KeyOf(e.Section, e.Key), e.Value, _localPath)).ToList();
        var localKeys = new HashSet<string>(localEntries.Select(e => e.Key));

        foreach (var (section, key, value) in _global.Entries())
        {
            var name = KeyOf(section, key);
            if (!localKeys.Contains(name) && seen.Add(name))
                result.Add(new ConfigEntry(name, value, _globalPath));
        }
        foreach (var entry in localEntries)
            if (seen.Add(entry.Key)) result.Add(entry);
        return result;
    }

    static string KeyOf(string section, string key) =>
        $"{section.ToLowerInvariant()}.{key.ToLowerInvariant()}";

    public string Set(ConfigKey key, string value, bool global)
    {
        var (file, path) = global
            ? (_global, _globalPath)
            : (_local, _localPath ?? Path.Combine(_startDirectory, ConfigPaths.LocalFileName));
        file.Set(key.Section, key.Name, value);
        Save(file, path);
        return path;
    }

    public bool Unset(ConfigKey key, bool global)
    {
        var (file, path) = global ? (_global, _globalPath) : (_local, _localPath);
        if (path is null || !file.Unset(key.Section, key.Name)) return false;
        Save(file, path);
        return true;
    }

    static void Save(IniFile file, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, file.ToText());
    }
}
