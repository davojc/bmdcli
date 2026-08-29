namespace Bmd.Commands;

/// <summary>Filtered listings for `bmd &lt;group&gt;` and `bmd &lt;group&gt; --help`.
///
/// Commands are registered in Program.cs as flat delegate paths (`app.Add("videohub info", …)`)
/// rather than via ConsoleAppFramework's class-based `Add&lt;T&gt;`, because our command classes
/// (e.g. ConfigCommands) have two constructors — a parameterless one and a ConsoleAppFramework
/// requires exactly one constructor and treats any non-primitive constructor parameter as a
/// DI-resolved service. Flat registration sidesteps that, but it also means the framework never
/// creates a node for the group itself ("videohub", "config"): a bare `bmd videohub` or
/// `bmd videohub --help` has no matching command and no group-specific help text to fall back
/// to, so ConsoleAppFramework's generated router falls through to the full root listing instead
/// of a filtered one. This class intercepts those two invocation shapes before `app.Run(args)`
/// and prints a filtered listing built from a small static table of `(path, description)` pairs
/// that mirrors the registrations in Program.cs.</summary>
internal static class GroupHelp
{
    internal readonly record struct Entry(string Path, string Description);

    internal static readonly Entry[] Commands =
    [
        new("discover", "Find Blackmagic devices on the local network via mDNS."),
        new("version", "Show the version and platform of this bmd binary."),
        new("config set", "Set a configuration value."),
        new("config get", "Print the effective value of a configuration key."),
        new("config unset", "Remove a configuration key."),
        new("config list", "List all effective configuration values."),
        new("videohub info", "Show device information (model, protocol version, input/output counts)."),
        new("videohub input list", "List inputs (1-based) with their labels."),
        new("videohub output list", "List outputs (1-based) with label, routed input, and lock state."),
        new("videohub route list", "List the current routing (1-based): which input feeds each output."),
        new("videohub watch", "Stream device changes as they happen, including changes made by other controllers. Numbering is 1-based."),
        new("videohub export", "Export a verified snapshot of labels and routing (1-based). Locks are not captured."),
        new("videohub restore", "Apply a snapshot to the device, changing only what differs. Numbering is 1-based."),
        new("videohub route set", "Route an input to an output (both 1-based, matching the device's front panel)."),
        new("videohub input rename", "Rename an input (1-based)."),
        new("videohub output rename", "Rename an output (1-based)."),
        new("videohub output lock", "Take the lock on an output (1-based), preventing other controllers from routing it or taking it over without --force."),
        new("videohub output unlock", "Release the lock on an output (1-based). Without --force, unlocking an output locked by another controller is left to the device to accept or refuse."),
    ];

    static readonly string[] Groups = ["config", "videohub"];

    /// <summary>Attempts to handle `args` as `&lt;group&gt;` or `&lt;group&gt; --help`/`-h` for a
    /// known command group. Returns false (and writes nothing) for anything else — an exact
    /// command, an unknown group, root `--help`, or a bare/empty/unrecognized invocation — so
    /// those cases fall through to ConsoleAppFramework unchanged.</summary>
    internal static bool TryWrite(string[] args, TextWriter writer)
    {
        if (args.Length == 0) return false;
        var group = args[0];
        if (!Groups.Contains(group)) return false;
        if (args.Length > 1 && args[1] is not ("--help" or "-h")) return false;
        if (args.Length > 2) return false;

        var entries = Commands.Where(c => c.Path.StartsWith($"{group} ", StringComparison.Ordinal)).ToArray();
        if (entries.Length == 0) return false;

        var width = entries.Max(e => e.Path.Length);
        writer.WriteLine($"Usage: {group} [command] [-h|--help] [--version]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        foreach (var entry in entries)
            writer.WriteLine($"  {entry.Path.PadRight(width)}    {entry.Description}");
        return true;
    }
}
