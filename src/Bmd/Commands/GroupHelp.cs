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
/// of a filtered one. This class intercepts those invocation shapes before `app.Run(args)`
/// and prints a filtered listing built from a small static table of `(path, description)` pairs
/// that mirrors the registrations in Program.cs.
///
/// <para>Root help (`bmd`, `bmd --help`, `bmd -h`) is intercepted here too. ConsoleAppFramework
/// handled it, but listed all ~40 registrations flat with their full XML summaries — including
/// the multi-line ones, rendered with this source file's own indentation. The root listing shows
/// groups instead; a group's commands are one `--help` away.</para></summary>
internal static class GroupHelp
{
    internal readonly record struct Entry(string Path, string Description);

    internal static readonly Entry[] Commands =
    [
        new("discover", "Find Blackmagic devices on the local network via mDNS."),
        new("version", "Show the version and platform of this bmd binary."),
        new("agents", "Print the guide for driving bmd from an AI agent or a script."),
        new("update", "Download and install the newest release of bmd, replacing this binary in place."),
        new("config set", "Set a configuration value."),
        new("config get", "Print the effective value of a configuration key."),
        new("config unset", "Remove a configuration key."),
        new("config list", "List all effective configuration values."),
        new("videohub info", "Show device information (model, protocol version, input/output counts)."),
        new("videohub input list", "List inputs (1-based) with their labels."),
        new("videohub output list", "List outputs (1-based) with their labels and lock state."),
        new("videohub route list", "List the current routing (1-based): which input feeds each output."),
        new("videohub watch", "Stream device changes as they happen, including changes made by other controllers. Numbering is 1-based."),
        new("videohub export", "Export a verified snapshot of labels and routing (1-based). Locks are not captured."),
        new("videohub restore", "Apply a snapshot to the device, changing only what differs. Numbering is 1-based."),
        new("videohub route set", "Route an input to an output (both 1-based, matching the device's front panel)."),
        new("videohub input rename", "Rename an input (1-based)."),
        new("videohub output rename", "Rename an output (1-based)."),
        new("videohub output lock", "Take the lock on an output (1-based), preventing other controllers from routing it or taking it over without --force."),
        new("videohub context list", "List the configured Videohubs and which one commands use."),
        new("videohub context set", "Choose which Videohub commands act on. Omit the name to pick from a list."),
        new("videohub output unlock", "Release the lock on an output (1-based). Without --force, unlocking an output locked by another controller is left to the device to accept or refuse."),
        new("multiview info", "Show device information (model, protocol version, source and view counts, layout)."),
        new("multiview input list", "List sources (1-based) with their labels."),
        new("multiview view list", "List views (1-based) with label, source, and lock state."),
        new("multiview config", "Print the device's CONFIGURATION block exactly as reported."),
        new("multiview view set", "Put a source in a view: view then input (both 1-based, destination first)."),
        new("multiview input rename", "Rename a source (1-based)."),
        new("multiview view rename", "Rename a view (1-based)."),
        new("multiview view lock", "Take the lock on a view (1-based)."),
        new("multiview view unlock", "Release the lock on a view (1-based)."),
        new("multiview layout", "Set the window layout, e.g. 2x2. The device validates the value, not bmd."),
        new("multiview format", "Set the output video format, e.g. 1080i5994. The device validates the value."),
        new("multiview solo", "Show one source full-screen, or 'off' to leave solo mode."),
        new("multiview show", "Turn an on-screen overlay on or off: borders, labels, audio-meters, tally."),
        new("multiview take-mode", "Turn take mode on or off."),
        new("multiview widescreen-sd", "Turn widescreen SD on or off."),
        new("multiview watch", "Stream device changes as they happen."),
        new("multiview context list", "List the configured MultiViews and which one commands use."),
        new("multiview context set", "Choose which MultiView commands act on. Omit the name to pick from a list."),
        new("multiview export", "Export a verified snapshot of sources, views and configuration."),
        new("multiview restore", "Apply a snapshot, changing only what differs."),
        new("atem info", "Show switcher information (model, protocol version, topology)."),
        new("atem input list", "List inputs with their names and source ids. --all adds internal sources."),
        new("atem status", "Show what is on the program and preview buses."),
        new("atem aux list", "List auxiliary outputs (1-based) and the source feeding each."),
        new("atem input rename", "Rename an input on the switcher itself."),
        new("atem aux set", "Route a source to an auxiliary output."),
        new("atem context list", "List the configured ATEMs and which one commands use."),
        new("atem context set", "Choose which ATEM commands act on. Omit the name to pick from a list."),
        new("atem program set", "Put a source on the program bus. Cuts on air immediately."),
        new("atem preview set", "Put a source on the preview bus."),
    ];

    /// <summary>The command groups, each with the one-line description it gets in the root
    /// listing, in the order they are listed.</summary>
    static readonly Entry[] GroupDescriptions =
    [
        new("videohub", "Route and label a Videohub."),
        new("multiview", "Drive a MultiView's views, layout and overlays."),
        new("atem", "Name inputs and route auxes on an ATEM switcher."),
        new("config", "Read and write bmd's own settings."),
    ];

    static readonly string[] Groups = [.. GroupDescriptions.Select(g => g.Path)];

    /// <summary>Commands that belong to no group. The root listing is their only listing, so
    /// anything registered at the top level in Program.cs must appear here to stay discoverable.</summary>
    static readonly Entry[] Ungrouped = [.. Commands.Where(c => !c.Path.Contains(' '))];

    /// <summary>Attempts to handle `args` as root help (no arguments, `--help` or `-h`), or as
    /// `[group]` / `[group] --help`/`-h` for a known command group. Returns false (and writes
    /// nothing) for anything else — an exact command, an unknown group, `--version` — so those
    /// cases fall through to ConsoleAppFramework unchanged.</summary>
    internal static bool TryWrite(string[] args, TextWriter writer)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0] is "--help" or "-h"))
        {
            WriteRoot(writer);
            return true;
        }

        var group = args[0];
        if (!Groups.Contains(group)) return false;
        if (args.Length > 1 && args[1] is not ("--help" or "-h")) return false;
        if (args.Length > 2) return false;

        var entries = Commands.Where(c => c.Path.StartsWith($"{group} ", StringComparison.Ordinal)).ToArray();
        if (entries.Length == 0) return false;

        var width = entries.Max(e => e.Path.Length);
        writer.WriteLine($"Usage: bmd {group} [command] [-h|--help]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        foreach (var entry in entries)
            writer.WriteLine($"  {entry.Path.PadRight(width)}    {entry.Description}");
        writer.WriteLine();
        writer.WriteLine("Run `bmd [command] --help` for a command's arguments and options.");
        return true;
    }

    /// <summary>The root listing: groups and ungrouped commands, never the ~40 individual
    /// commands. ConsoleAppFramework's own root help printed every registration flat, each with
    /// its full XML summary, which made the one screen a new user sees the least useful one.</summary>
    static void WriteRoot(TextWriter writer)
    {
        var width = Math.Max(
            GroupDescriptions.Max(g => g.Path.Length),
            Ungrouped.Max(c => c.Path.Length));

        writer.WriteLine("bmd — control Blackmagic Design devices over the network.");
        writer.WriteLine();
        writer.WriteLine("Usage: bmd [group] [command] [options]");
        writer.WriteLine();
        writer.WriteLine("Groups:");
        foreach (var entry in GroupDescriptions)
            writer.WriteLine($"  {entry.Path.PadRight(width)}    {entry.Description}");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        foreach (var entry in Ungrouped)
            writer.WriteLine($"  {entry.Path.PadRight(width)}    {entry.Description}");
        writer.WriteLine();
        writer.WriteLine("Run `bmd <group> --help` to list a group's commands.");
        writer.WriteLine("Run `bmd --version` to show this binary's version.");
    }
}
