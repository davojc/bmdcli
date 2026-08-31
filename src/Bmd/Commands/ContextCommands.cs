using System.Text.Json;
using Bmd.Config;
using Bmd.Output;
using ConsoleAppFramework;

namespace Bmd.Commands;

/// <summary>`bmd videohub context list` and `context set`, and the same pair for every device type — choosing which device of a type
/// commands act on, when you have more than one.
///
/// One instance per device type, constructed with that type's config section, because the two
/// commands differ in nothing else. A context is a git-style INI subsection, so
/// <c>[videohub "gallery"]</c> holds a second Videohub's settings alongside the first.
///
/// <para>`context set` <b>selects</b>; it does not define. A context comes into being when
/// something is written under it — <c>bmd config set videohub.host 10.0.0.5 --context gallery</c>,
/// or <c>bmd discover --add --context gallery</c> — so there is no registry to fall out of step
/// with the config file.</para></summary>
public sealed class ContextCommands
{
    readonly string _section;
    readonly Func<ConfigStore> _loadConfig;
    readonly Func<bool> _isInteractive;

    public ContextCommands(string section)
        : this(section, ConfigStore.LoadDefault) { }

    public ContextCommands(string section, Func<ConfigStore> loadConfig, Func<bool>? isInteractive = null)
    {
        _section = section;
        _loadConfig = loadConfig;
        _isInteractive = isInteractive ?? (() => !Console.IsInputRedirected);
    }

    /// <summary>List the configured devices of this type, marking the one commands currently use.</summary>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public int List(bool json = false)
    {
        var contexts = _loadConfig().Contexts(_section);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                contexts.Select(c => new ContextEntry(c.DisplayName, c.Host, c.Active)).ToArray(),
                BmdJsonContext.Default.ContextEntryArray));
            return 0;
        }

        if (contexts.Count == 0 || contexts.All(c => c.Host is null))
        {
            Console.WriteLine($"No {_section} device configured yet.");
            Console.WriteLine($"  bmd config set {_section}.host <address>");
            Console.WriteLine($"  bmd config set {_section}.host <address> --context <name>   (a second one)");
            return 0;
        }

        Table.Write(
            ["", "CONTEXT", "HOST"],
            [.. contexts.Select(c => (IReadOnlyList<string>)
                [c.Active ? "*" : "", c.DisplayName, c.Host ?? ""])]);
        return 0;
    }

    /// <summary>Choose which device of this type commands act on. Omit the name to pick from a list. The choice is remembered until you change it.</summary>
    /// <param name="context">Context to switch to, or `default` for the unlabelled one. Omit to choose interactively.</param>
    /// <param name="project">Write the choice to a .bmdconfig in the current directory tree instead of your user config, pinning just this directory to one device.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public int Set([Argument] string? context = null, bool project = false, bool json = false)
    {
        var store = _loadConfig();
        var contexts = store.Contexts(_section);

        if (context is null)
        {
            if (!_isInteractive())
            {
                Console.Error.WriteLine(
                    $"error: no context given and nothing to prompt with " +
                    $"(run: bmd {_section} context set <name>)");
                return 2;
            }
            if (Choose(contexts) is not { } chosen) return 1;
            context = chosen;
        }

        var normalised = ConfigKey.Normalise(context);
        if (normalised is not null && !ConfigKey.IsValidContext(normalised))
        {
            Console.Error.WriteLine($"error: '{context}' is not a usable context name");
            return 2;
        }

        // Refuse to select a context with no address. Every device command would then fail with a
        // less obvious message, and — worse — someone might read the failure as the device being
        // unreachable rather than unconfigured.
        var target = contexts.FirstOrDefault(c =>
            string.Equals(c.Name, normalised, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.Host is null)
        {
            Console.Error.WriteLine(
                $"error: no {_section} context named '{context}' with a host " +
                $"(run: bmd {_section} context list)");
            return 1;
        }

        var scope = project ? ConfigScope.Project : ConfigScope.User;
        var key = new ConfigKey(_section, ConfigStore.ContextKeyName);
        var path = normalised is null
            ? UnsetActive(store, key, scope)
            : store.Set(key, normalised, scope);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new ContextSetResult(_section, target.DisplayName, target.Host, path),
                BmdJsonContext.Default.ContextSetResult));
        }
        else
        {
            Console.WriteLine($"Now using {_section} context '{target.DisplayName}' ({target.Host})");
            if (path is not null) Console.WriteLine($"Saved to {path}");
        }
        return 0;
    }

    /// <summary>Selecting the default context means removing the active-context key rather than
    /// writing "default" into it — otherwise a config would carry a context name that deliberately
    /// does not exist as a section.</summary>
    static string? UnsetActive(ConfigStore store, ConfigKey key, ConfigScope scope)
    {
        store.Unset(key, scope);
        return null;
    }

    string? Choose(IReadOnlyList<DeviceContext> contexts)
    {
        var usable = contexts.Where(c => c.Host is not null).ToList();
        if (usable.Count == 0)
        {
            Console.Error.WriteLine(
                $"error: no {_section} device configured " +
                $"(run: bmd config set {_section}.host <address>)");
            return null;
        }

        Console.WriteLine($"Which {_section}?");
        for (var i = 0; i < usable.Count; i++)
        {
            var mark = usable[i].Active ? " (current)" : "";
            Console.WriteLine($"  {i + 1}) {usable[i].DisplayName}  {usable[i].Host}{mark}");
        }
        Console.Write($"Choose 1-{usable.Count}: ");

        var answer = Console.ReadLine();
        if (!int.TryParse(answer?.Trim(), out var pick) || pick < 1 || pick > usable.Count)
        {
            Console.Error.WriteLine("error: nothing chosen");
            return null;
        }
        return usable[pick - 1].DisplayName;
    }
}

public sealed record ContextEntry(string Context, string? Host, bool Active);

public sealed record ContextSetResult(string Device, string Context, string? Host, string? Path);
