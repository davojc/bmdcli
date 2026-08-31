using System.Text.Json;
using Bmd.Config;
using Bmd.Output;
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
    /// <param name="project">Write to a .bmdconfig in the current directory tree instead of your user config, pinning this setting to this directory. Without it, the value is saved for your user and applies wherever you run bmd.</param>
    /// <param name="context">Act on a named device context (a second device of the same type) rather than the default one. See `bmd videohub context list` and its equivalents.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public int Set([Argument] string key, [Argument] string value, bool project = false, string? context = null, bool json = false)
    {
        if (!TryKey(key, context, out var k)) return 2;
        if (!TryValue(value)) return 2;
        return RunGuarded(() =>
        {
            var file = _load().Set(k, value, project ? ConfigScope.Project : ConfigScope.User);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(new ConfigSetResult(k.ToString(), value, file), BmdJsonContext.Default.ConfigSetResult));
            return 0;
        });
    }

    /// <summary>Print the effective value of a configuration key.</summary>
    /// <param name="key">Configuration key, e.g. videohub.host.</param>
    /// <param name="context">Act on a named device context (a second device of the same type) rather than the default one. See `bmd videohub context list` and its equivalents.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public int Get([Argument] string key, string? context = null, bool json = false)
    {
        if (!TryKey(key, context, out var k)) return 2;
        return RunGuarded(() =>
        {
            var value = _load().GetEffective(k);
            if (value is null)
            {
                Console.Error.WriteLine($"error: '{k}' is not set");
                return 1;
            }
            if (json)
            {
                var origin = _load().ListEffective().FirstOrDefault(e => e.Key == k.ToString())?.Origin;
                Console.WriteLine(JsonSerializer.Serialize(new ConfigGetResult(k.ToString(), value, origin), BmdJsonContext.Default.ConfigGetResult));
            }
            else
            {
                Console.WriteLine(value);
            }
            return 0;
        });
    }

    /// <summary>Remove a configuration key.</summary>
    /// <param name="key">Configuration key, e.g. videohub.host.</param>
    /// <param name="project">Remove from the .bmdconfig in the current directory tree instead of your user config.</param>
    /// <param name="context">Act on a named device context (a second device of the same type) rather than the default one. See `bmd videohub context list` and its equivalents.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public int Unset([Argument] string key, bool project = false, string? context = null, bool json = false)
    {
        if (!TryKey(key, context, out var k)) return 2;
        return RunGuarded(() =>
        {
            if (!_load().Unset(k, project ? ConfigScope.Project : ConfigScope.User))
            {
                // Name the scope that was actually searched: a key set for the user but not in a
                // .bmdconfig should not read as "not set" when the user ran `unset --project`.
                Console.Error.WriteLine($"error: '{k}' is not set in the {(project ? "project" : "user")} config");
                return 1;
            }
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(new ConfigUnsetResult(k.ToString(), true), BmdJsonContext.Default.ConfigUnsetResult));
            return 0;
        });
    }

    /// <summary>List all effective configuration values.</summary>
    /// <param name="showOrigin">Prefix each value with the file it came from.</param>
    /// <param name="json">Emit the result as a JSON array on stdout.</param>
    public int List(bool showOrigin = false, bool json = false)
    {
        return RunGuarded(() =>
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(_load().ListEffective().ToArray(), BmdJsonContext.Default.ConfigEntryArray));
                return 0;
            }
            foreach (var entry in _load().ListEffective())
            {
                // A contexted key is shown with its context: two contexts legitimately define the
                // same key, and a flat listing would otherwise print the same line twice.
                var name = entry.Context is null ? entry.Key : $"{entry.Key} [{entry.Context}]";
                Console.WriteLine(showOrigin ? $"{entry.Origin}\t{name}={entry.Value}" : $"{name}={entry.Value}");
            }
            return 0;
        });
    }

    static bool TryKey(string raw, string? context, out ConfigKey key)
    {
        if (context is not null && !ConfigKey.IsValidContext(context))
        {
            Console.Error.WriteLine(
                $"error: '{context}' is not a usable context name " +
                $"('{ConfigKey.DefaultContextName}' is reserved for the unnamed one)");
            key = default;
            return false;
        }
        if (ConfigKey.TryParse(raw, context, out key)) return true;
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
