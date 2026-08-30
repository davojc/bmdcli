using System.Text;
using System.Text.RegularExpressions;
using Bmd.Devices.Atem;
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

        VideohubSnapshot verification;
        try
        {
            verification = VideohubSnapshot.FromJson(File.ReadAllText(path));
        }
        catch (SnapshotFormatException ex)
        {
            throw new IOException($"backup written to {path} did not read back intact: {ex.Message}", ex);
        }
        if (verification.Device != snapshot.Device
            || verification.ExportedAt != snapshot.ExportedAt
            || verification.Outputs.Length != snapshot.Outputs.Length
            || verification.Inputs.Length != snapshot.Inputs.Length
            || verification.Outputs.Where((o, i) => o != snapshot.Outputs[i]).Any()
            || verification.Inputs.Where((input, i) => input != snapshot.Inputs[i]).Any())
        {
            throw new IOException($"backup written to {path} did not read back intact");
        }

        Prune(deviceKey);
        return path;
    }

    /// <summary>Writes an ATEM snapshot, verifies it reads back intact, prunes old ones.
    ///
    /// A separate overload rather than a shared abstraction over both snapshot types: they have
    /// no fields in common beyond a device name and a timestamp, and the read-back check is the
    /// point of this method — a generic version could only compare the JSON text, which would
    /// pass on a snapshot that round-trips to something structurally different.</summary>
    public string Write(string deviceKey, AtemSnapshot snapshot)
    {
        var directory = Path.Combine(RootDirectory, deviceKey);
        Directory.CreateDirectory(directory);
        var path = UniquePath(directory, snapshot.ExportedAt);
        File.WriteAllText(path, snapshot.ToJson());

        AtemSnapshot verification;
        try
        {
            verification = AtemSnapshot.FromJson(File.ReadAllText(path));
        }
        catch (SnapshotFormatException ex)
        {
            throw new IOException($"backup written to {path} did not read back intact: {ex.Message}", ex);
        }
        if (verification.Device != snapshot.Device
            || verification.ExportedAt != snapshot.ExportedAt
            || verification.Sources.Length != snapshot.Sources.Length
            || verification.Auxes.Length != snapshot.Auxes.Length
            || verification.Sources.Where((s, i) => s != snapshot.Sources[i]).Any()
            || verification.Auxes.Where((a, i) => a != snapshot.Auxes[i]).Any())
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
        // Sort by parsed (stamp, suffix) rather than the raw filename: '-' (0x2D) sorts
        // before '.' (0x2E) ordinally, so a naive filename sort would put "<stamp>-2.json"
        // before "<stamp>.json" even though the bare name was written first (suffix 1).
        // Files that don't match the expected shape sort last instead of throwing.
        return Directory.GetFiles(directory, "*.json")
            .Select(path => (Path: path, Key: NameKey(path)))
            .OrderByDescending(entry => entry.Key.Matched)
            .ThenByDescending(entry => entry.Key.Stamp, StringComparer.Ordinal)
            .ThenByDescending(entry => entry.Key.Suffix)
            .Select(entry => entry.Path)
            .ToArray();
    }

    static readonly Regex NamePattern = new(@"^(\d{8}-\d{6})(?:-(\d+))?\.json$", RegexOptions.Compiled);

    static (bool Matched, string Stamp, int Suffix) NameKey(string path)
    {
        var match = NamePattern.Match(Path.GetFileName(path));
        if (!match.Success) return (false, "", 0);
        var suffix = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
        return (true, match.Groups[1].Value, suffix);
    }

    void Prune(string deviceKey)
    {
        foreach (var path in List(deviceKey).Skip(Keep))
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* a locked or read-only old backup is not worth failing the mutation over */ }
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
