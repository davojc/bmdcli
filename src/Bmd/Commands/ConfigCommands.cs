using Bmd.Config;
using ConsoleAppFramework;

namespace Bmd.Commands;

/// <summary>bmd config — git-style layered configuration.</summary>
public class ConfigCommands
{
    readonly Func<ConfigStore> _load;

    public ConfigCommands() : this(ConfigStore.LoadDefault) { }
    public ConfigCommands(Func<ConfigStore> load) => _load = load;

    /// <summary>Set a configuration value.</summary>
    /// <param name="key">Configuration key, e.g. videohub.host.</param>
    /// <param name="value">Value to assign.</param>
    /// <param name="global">-g, Write to the global config file instead of local .bmdconfig.</param>
    public int Set([Argument] string key, [Argument] string value, bool global = false)
    {
        if (!TryKey(key, out var k)) return 2;
        if (!TryValue(value)) return 2;
        return RunGuarded(() =>
        {
            _load().Set(k, value, global);
            return 0;
        });
    }

    /// <summary>Print the effective value of a configuration key.</summary>
    /// <param name="key">Configuration key, e.g. videohub.host.</param>
    public int Get([Argument] string key)
    {
        if (!TryKey(key, out var k)) return 2;
        return RunGuarded(() =>
        {
            var value = _load().GetEffective(k);
            if (value is null)
            {
                Console.Error.WriteLine($"error: '{k}' is not set");
                return 1;
            }
            Console.WriteLine(value);
            return 0;
        });
    }

    /// <summary>Remove a configuration key.</summary>
    /// <param name="key">Configuration key, e.g. videohub.host.</param>
    /// <param name="global">-g, Remove from the global config file instead of local .bmdconfig.</param>
    public int Unset([Argument] string key, bool global = false)
    {
        if (!TryKey(key, out var k)) return 2;
        return RunGuarded(() =>
        {
            if (!_load().Unset(k, global))
            {
                Console.Error.WriteLine($"error: '{k}' is not set in the {(global ? "global" : "local")} config");
                return 1;
            }
            return 0;
        });
    }

    /// <summary>List all effective configuration values.</summary>
    /// <param name="showOrigin">Prefix each value with the file it came from.</param>
    public int List(bool showOrigin = false)
    {
        return RunGuarded(() =>
        {
            foreach (var entry in _load().ListEffective())
                Console.WriteLine(showOrigin ? $"{entry.Origin}\t{entry.Key}={entry.Value}" : $"{entry.Key}={entry.Value}");
            return 0;
        });
    }

    static bool TryKey(string raw, out ConfigKey key)
    {
        if (ConfigKey.TryParse(raw, out key)) return true;
        Console.Error.WriteLine($"error: key '{raw}' must be in section.key format with no spaces or =[]#;\" characters (e.g. videohub.host)");
        return false;
    }

    static bool TryValue(string value)
    {
        if (value.IndexOfAny(['\n', '\r', '"']) < 0) return true;
        Console.Error.WriteLine("error: value must not contain quotes or newlines");
        return false;
    }

    /// <summary>Runs a store operation, converting IO failures (locked files, unwritable
    /// directories, permissions) into a single clear stderr message and exit code 1 instead
    /// of letting the exception propagate as a stack trace.</summary>
    static int RunGuarded(Func<int> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}
