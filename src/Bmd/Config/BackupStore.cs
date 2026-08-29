using System.Text;
using Bmd.Devices.Videohub;

namespace Bmd.Config;

public sealed class ConfigValueException(string message) : Exception(message);

/// <summary>Stores pre-mutation device snapshots on disk, newest-first, pruned to a keep count.</summary>
public sealed class BackupStore
{
    const int DefaultKeep = 10;

    public bool AutoBackupEnabled { get; }
    public int Keep { get; }
    public string RootDirectory { get; }

    BackupStore(bool autoBackupEnabled, int keep, string rootDirectory)
    {
        AutoBackupEnabled = autoBackupEnabled;
        Keep = keep;
        RootDirectory = rootDirectory;
    }

    public static BackupStore FromConfig(ConfigStore config)
    {
        var auto = Get(config, "backup.auto") is not { } raw || !raw.Equals("false", StringComparison.OrdinalIgnoreCase);
        var keep = DefaultKeep;
        if (Get(config, "backup.keep") is { } keepText)
        {
            if (!int.TryParse(keepText, out keep) || keep < 1)
                throw new ConfigValueException($"config backup.keep must be a positive number, not '{keepText}'");
        }
        var root = Get(config, "backup.dir") ?? Path.Combine(ConfigPaths.StateDirectory, "backups");
        return new BackupStore(auto, keep, root);
    }

    static string? Get(ConfigStore config, string key)
    {
        ConfigKey.TryParse(key, out var parsed);
        return config.GetEffective(parsed);
    }

    /// <summary>Filesystem-safe directory name for a device: host + model, lowercased.</summary>
    public static string DeviceKey(string host, string modelName) =>
        $"{Sanitize(host)}_{Sanitize(modelName)}";

    static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var collapsed = builder.ToString();
        while (collapsed.Contains("--")) collapsed = collapsed.Replace("--", "-");
        return collapsed.Trim('-');
    }

    /// <summary>Writes a snapshot, verifies it reads back intact, prunes old ones. Returns the path.</summary>
    public string Write(string deviceKey, VideohubSnapshot snapshot)
    {
        var directory = Path.Combine(RootDirectory, deviceKey);
        Directory.CreateDirectory(directory);
        var path = UniquePath(directory, snapshot.ExportedAt);
        File.WriteAllText(path, snapshot.ToJson());

        var verification = VideohubSnapshot.FromJson(File.ReadAllText(path));
        if (verification.Outputs.Length != snapshot.Outputs.Length
            || verification.Inputs.Length != snapshot.Inputs.Length
            || verification.Outputs.Where((o, i) => o != snapshot.Outputs[i]).Any()
            || verification.Inputs.Where((input, i) => input != snapshot.Inputs[i]).Any())
        {
            throw new IOException($"backup written to {path} did not read back intact");
        }

        Prune(deviceKey);
        return path;
    }

    /// <summary>Existing backup file paths for a device, newest first.</summary>
    public IReadOnlyList<string> List(string deviceKey)
    {
        var directory = Path.Combine(RootDirectory, deviceKey);
        if (!Directory.Exists(directory)) return [];
        return Directory.GetFiles(directory, "*.json")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    void Prune(string deviceKey)
    {
        foreach (var path in List(deviceKey).Skip(Keep))
        {
            try { File.Delete(path); }
            catch (IOException) { /* a locked old backup is not worth failing the mutation over */ }
        }
    }

    static string UniquePath(string directory, DateTimeOffset stamp)
    {
        var basename = stamp.UtcDateTime.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(directory, $"{basename}.json");
        for (var suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(directory, $"{basename}-{suffix}.json");
        return path;
    }
}
