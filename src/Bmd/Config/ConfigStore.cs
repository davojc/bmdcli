namespace Bmd.Config;

public sealed record ConfigEntry(string Key, string Value, string Origin, string? Context = null);

/// <summary>A configured device of one type: its context name (null for the unlabelled default),
/// its address, and whether it is the one commands currently use.</summary>
public sealed record DeviceContext(string? Name, string? Host, bool Active)
{
    /// <summary>What to show a user. The default context has no name of its own.</summary>
    public string DisplayName => Name ?? ConfigKey.DefaultContextName;
}

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
        _local.Get(key.IniSection, key.Name) ?? _global.Get(key.IniSection, key.Name);

    /// <summary>The context commands for <paramref name="section"/> currently run against, or null
    /// for the unlabelled default. Set by `bmd &lt;type&gt; context set`.</summary>
    public string? ActiveContext(string section) =>
        ConfigKey.Normalise(GetEffective(new ConfigKey(section, ContextKeyName)));

    /// <summary>The key holding which context is active. Lives in the unlabelled section, so a
    /// context can never carry its own idea of which context is active.</summary>
    public const string ContextKeyName = "context";

    /// <summary>Every configured context for a device type, newest-first by nothing in particular —
    /// the default (if it has a host) first, then named contexts in alphabetical order.
    ///
    /// A context exists when something has been written under it, which in practice means a host.
    /// There is no separate registry to drift out of step with the config file.</summary>
    public IReadOnlyList<DeviceContext> Contexts(string section)
    {
        var active = ActiveContext(section);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { _global, _local })
            foreach (var (iniSection, _, _) in file.Entries())
            {
                var (parsed, context) = ConfigKey.SplitIniSection(iniSection);
                if (context is not null && parsed.Equals(section, StringComparison.OrdinalIgnoreCase))
                    names.Add(context);
            }

        var result = new List<DeviceContext>();
        var defaultHost = GetEffective(new ConfigKey(section, "host"));
        if (defaultHost is not null || active is null)
            result.Add(new DeviceContext(null, defaultHost, active is null));
        foreach (var name in names)
            result.Add(new DeviceContext(
                name,
                GetEffective(new ConfigKey(section, "host", name)),
                string.Equals(name, active, StringComparison.OrdinalIgnoreCase)));
        return result;
    }

    public IReadOnlyList<ConfigEntry> ListEffective()
    {
        var result = new List<ConfigEntry>();
        var seen = new HashSet<(string, string?)>();
        // Identity is the key AND its context: `atem.host` in two contexts is two different
        // settings, and deduplicating on the key alone silently hides every contexted one.
        var localEntries = _localPath is null
            ? []
            : _local.Entries().Select(e => Entry(e.Section, e.Key, e.Value, _localPath)).ToList();
        var localKeys = new HashSet<(string, string?)>(localEntries.Select(e => (e.Key, e.Context)));

        // local wins, so global entries a local file shadows are skipped
        foreach (var (section, key, value) in _global.Entries())
        {
            var entry = Entry(section, key, value, _globalPath);
            var identity = (entry.Key, entry.Context);
            if (!localKeys.Contains(identity) && seen.Add(identity))
                result.Add(entry);
        }
        foreach (var entry in localEntries)
            if (seen.Add((entry.Key, entry.Context))) result.Add(entry);
        return result;
    }

    /// <summary>Builds a listing entry, splitting a context out of the INI section name so a
    /// contexted key is shown under its own name rather than as a section called `atem "gallery"`.
    /// The key stays `atem.host` in both cases: two contexts genuinely define the same key, and
    /// flattening them into distinct key names would misrepresent that.</summary>
    static ConfigEntry Entry(string iniSection, string key, string value, string origin)
    {
        var (section, context) = ConfigKey.SplitIniSection(iniSection);
        return new ConfigEntry(KeyOf(section, key), value, origin, context);
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
        file.Set(key.IniSection, key.Name, value);
        Save(file, path);
        return path;
    }

    /// <summary>Removes a key from one file. Returns false when that file does not exist or does
    /// not carry the key — removing from one scope never reaches into the other.</summary>
    public bool Unset(ConfigKey key, ConfigScope scope)
    {
        var (file, path) = scope == ConfigScope.Project ? (_local, _localPath) : (_global, _globalPath);
        if (path is null || !file.Unset(key.IniSection, key.Name)) return false;
        Save(file, path);
        return true;
    }

    static void Save(IniFile file, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, file.ToText());
    }
}
