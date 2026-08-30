namespace Bmd.Config;

public sealed record ConfigEntry(string Key, string Value, string Origin);

/// <summary>Which file a write targets.
///
/// Deliberately an enum rather than a bool. This used to be <c>bool global</c> defaulting to
/// false, meaning writes landed in a <c>.bmdconfig</c> in whatever directory you happened to be
/// standing in — so following the tool's own "run: bmd config set videohub.host &lt;addr&gt;" hint
/// from two different directories produced two different config files and a device that seemed to
/// forget its address. The default is now <see cref="User"/>. Flipping a bool's meaning is exactly
/// how a call site gets silently missed, and the failure mode here is writing to the wrong file,
/// so the compiler is made to check every one.</summary>
public enum ConfigScope
{
    /// <summary>The per-user config file — the default, and the right home for a device address,
    /// which is a property of your network rather than your working directory.</summary>
    User,

    /// <summary>The nearest <c>.bmdconfig</c> found by walking up from the current directory, or a
    /// new one there if none exists. For pinning a directory to a particular device.</summary>
    Project,
}

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

    /// <summary>Writes a value and returns the file it landed in. A <see cref="ConfigScope.Project"/>
    /// write reuses the <c>.bmdconfig</c> found by walk-up when there is one, and otherwise creates
    /// one in the current directory.</summary>
    public string Set(ConfigKey key, string value, ConfigScope scope)
    {
        var (file, path) = scope == ConfigScope.Project
            ? (_local, _localPath ?? Path.Combine(_startDirectory, ConfigPaths.LocalFileName))
            : (_global, _globalPath);
        file.Set(key.Section, key.Name, value);
        Save(file, path);
        return path;
    }

    /// <summary>Removes a key from one file. Returns false when that file does not exist or does
    /// not carry the key — removing from one scope never reaches into the other.</summary>
    public bool Unset(ConfigKey key, ConfigScope scope)
    {
        var (file, path) = scope == ConfigScope.Project ? (_local, _localPath) : (_global, _globalPath);
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
