using System.Text.Json;
using Bmd.Config;
using Bmd.Devices.Atem;
using Bmd.Output;
using ConsoleAppFramework;

namespace Bmd.Commands.Atem;

/// <summary>bmd atem — control a Blackmagic ATEM switcher over the network.
///
/// <para><b>Numbering.</b> Unlike the Videohub and MultiView groups, sources here are named by
/// the switcher's own source id rather than renumbered 1-based: an ATEM's ids are not a dense
/// range. Inputs happen to be 1-8, but colour bars are 1000, media player 1 is 3010, and the
/// program output is 10010 — renumbering those would invent a scheme the device's own front
/// panel, its software control, and every other controller disagree with. `input list` prints
/// the ids to use. Auxiliary outputs <i>are</i> presented 1-based, because there the device
/// counts from 1 on its own labels and only the wire is 0-based.</para></summary>
public sealed class AtemCommands
{
    readonly AtemSession _session;

    readonly Func<bool> _isInteractive;

    public AtemCommands() : this(ConfigStore.LoadDefault) { }

    public AtemCommands(Func<ConfigStore> loadConfig, Func<bool>? isInteractive = null)
    {
        _session = new AtemSession(loadConfig);
        _isInteractive = isInteractive ?? (() => !Console.IsInputRedirected);
    }

    /// <summary>Resolves a source given either its id or its name.
    ///
    /// An ATEM's source ids are the device's own and are not a dense range — inputs are 1-8 but
    /// colour bars are 1000 and media player 1 is 3010 — so requiring an id means looking one up
    /// before every command. Accepting the name that `input list` already prints removes that
    /// step, for a person and for a script alike. Ids still win: a source literally named "4"
    /// (the captured 1 M/E has several) must not shadow source 4.</summary>
    static bool TryResolveSource(AtemState state, string value, out int id, out string error)
    {
        error = "";
        if (int.TryParse(value, out id))
        {
            if (state.FindSource(id) is not null) return true;
            error = $"this switcher has no source {id} (run: bmd atem input list --all)";
            return false;
        }

        var matches = state.Sources
            .Where(source =>
                source.LongName.Equals(value, StringComparison.OrdinalIgnoreCase)
                || source.ShortName.Equals(value, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            id = matches[0].Id;
            return true;
        }
        error = matches.Count == 0
            ? $"this switcher has no source called '{value}' (run: bmd atem input list --all)"
            : $"'{value}' matches {matches.Count} sources ({string.Join(", ", matches.Select(m => m.Id))}) " +
              "— use the id instead";
        return false;
    }

    /// <summary>Show switcher information: model, protocol version, and what the device reports it has.</summary>
    /// <param name="host">Device address; defaults to config atem.host.</param>
    /// <param name="port">Device UDP port; defaults to config atem.port, else 9910.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config atem.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Info(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            var topology = state.Topology;
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new AtemInfoResult(state.ProductName, state.ProtocolVersion, topology.MixEffects,
                        topology.Sources, topology.DownstreamKeyers, topology.Auxiliaries,
                        topology.MediaPlayers, state.VideoMode),
                    BmdJsonContext.Default.AtemInfoResult));
            }
            else
            {
                Console.WriteLine($"Model:            {state.ProductName}");
                Console.WriteLine($"Protocol:         {state.ProtocolVersion}");
                Console.WriteLine($"Mix effects:      {topology.MixEffects}");
                Console.WriteLine($"Sources:          {topology.Sources}");
                Console.WriteLine($"Downstream keyers:{topology.DownstreamKeyers,3}");
                Console.WriteLine($"Auxiliaries:      {topology.Auxiliaries}");
                Console.WriteLine($"Media players:    {topology.MediaPlayers}");
                // Raw on purpose: the mode-number to format-name table is not published, and a
                // confidently wrong "1080i5994" is worse than the number the device reported.
                Console.WriteLine($"Video mode:       {state.VideoMode} (raw device value)");
            }
            return Task.FromResult(0);
        });

    /// <summary>List the switcher's inputs with their names and ids.</summary>
    /// <param name="all">Include internal sources too: colour bars, colour generators, media players, key and DSK masks, clean feeds, auxiliaries, and each mix effect's program and preview outputs.</param>
    /// <param name="host">Device address; defaults to config atem.host.</param>
    /// <param name="port">Device UDP port; defaults to config atem.port, else 9910.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config atem.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> InputList(
        bool all = false, string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var sources = all ? client.State.Sources : client.State.Inputs;
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    sources.Select(s => new AtemSourceEntry(s.Id, s.LongName, s.ShortName, s.IsExternalInput)).ToArray(),
                    BmdJsonContext.Default.AtemSourceEntryArray));
            }
            else
            {
                Table.Write(
                    ["ID", "NAME", "SHORT"],
                    [.. sources.Select(s => (IReadOnlyList<string>)[s.Id.ToString(), s.LongName, s.ShortName])]);
            }
            return Task.FromResult(0);
        });

    /// <summary>Show what is on the program and preview buses.</summary>
    /// <param name="host">Device address; defaults to config atem.host.</param>
    /// <param name="port">Device UDP port; defaults to config atem.port, else 9910.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config atem.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Status(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new AtemStatusResult(state.ProgramSource, state.NameOf(state.ProgramSource),
                        state.PreviewSource, state.NameOf(state.PreviewSource)),
                    BmdJsonContext.Default.AtemStatusResult));
            }
            else
            {
                Console.WriteLine($"Program:  {state.ProgramSource,-6} {state.NameOf(state.ProgramSource)}");
                Console.WriteLine($"Preview:  {state.PreviewSource,-6} {state.NameOf(state.PreviewSource)}");
            }
            return Task.FromResult(0);
        });

    /// <summary>Rename an input on the switcher itself, so the name matches in its multiviewer, its software control, and every other controller.</summary>
    /// <param name="input">Source to rename: its id or its current name, as shown by `bmd atem input list`.</param>
    /// <param name="name">New long name, up to 20 characters. Omit to change only the short name.</param>
    /// <param name="short">New short name, up to 4 characters — this is what the switcher shows on multiviewer labels. Omit to leave it unchanged.</param>
    /// <param name="host">Device address; defaults to config atem.host.</param>
    /// <param name="port">Device UDP port; defaults to config atem.port, else 9910.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config atem.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> InputRename(
        [Argument] string input, [Argument] string? name = null, string? @short = null,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
    {
        if (name is null && @short is null)
        {
            Console.Error.WriteLine("error: give a new name, a --short name, or both");
            return Task.FromResult(2);
        }
        if (name is { Length: > AtemChanges.MaxLongNameLength })
        {
            Console.Error.WriteLine(
                $"error: name must be {AtemChanges.MaxLongNameLength} characters or fewer, not {name.Length}");
            return Task.FromResult(2);
        }
        if (@short is { Length: > AtemChanges.MaxShortNameLength })
        {
            Console.Error.WriteLine(
                $"error: --short must be {AtemChanges.MaxShortNameLength} characters or fewer, not {@short.Length}");
            return Task.FromResult(2);
        }

        return _session.WithBackupAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            if (!TryResolveSource(client.State, input, out var id, out var lookupError))
            {
                Console.Error.WriteLine($"error: {lookupError}");
                return 1;
            }

            bool Applied(AtemState state)
            {
                var current = state.FindSource(id);
                return current is not null
                    && (name is null || current.LongName == name)
                    && (@short is null || current.ShortName == @short);
            }

            if (Applied(client.State))
            {
                Console.WriteLine($"No change: source {id} is already named that.");
                return 0;
            }

            var backupPath = backup();
            await client.SendCommandAsync(
                "CInL", AtemChanges.SetInputName(id, name, @short), Applied,
                TimeSpan.FromSeconds(timeout ?? 5));

            var renamed = client.State.FindSource(id)!;
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new AtemRenameResult(id, renamed.LongName, renamed.ShortName, backupPath),
                    BmdJsonContext.Default.AtemRenameResult));
            }
            else
            {
                Console.WriteLine($"Renamed source {id} to '{renamed.LongName}' ({renamed.ShortName})");
                Console.WriteLine($"Backup: {backupPath ?? "skipped"}");
            }
            return 0;
        });
    }

    /// <summary>List the auxiliary outputs and the source feeding each (1-based).</summary>
    /// <param name="host">Device address; defaults to config atem.host.</param>
    /// <param name="port">Device UDP port; defaults to config atem.port, else 9910.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config atem.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> AuxList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            var auxes = state.Auxes;
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    auxes.Select(a => new AtemAuxEntry(a.Index + 1, a.Source, state.NameOf(a.Source))).ToArray(),
                    BmdJsonContext.Default.AtemAuxEntryArray));
            }
            else if (auxes.Count == 0)
            {
                Console.WriteLine("This switcher has no auxiliary outputs.");
            }
            else
            {
                Table.Write(
                    ["AUX", "SOURCE", "NAME"],
                    [.. auxes.Select(a => (IReadOnlyList<string>)
                        [(a.Index + 1).ToString(), a.Source.ToString(), state.NameOf(a.Source)])]);
            }
            return Task.FromResult(0);
        });

    /// <summary>Route a source to an auxiliary output.</summary>
    /// <param name="aux">Which auxiliary output to change (1-based, matching the switcher's own labels).</param>
    /// <param name="source">Source to route to it: its id or its name, as shown by `bmd atem input list --all`.</param>
    /// <param name="host">Device address; defaults to config atem.host.</param>
    /// <param name="port">Device UDP port; defaults to config atem.port, else 9910.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config atem.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> AuxSet(
        [Argument] int aux, [Argument] string source,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => _session.WithBackupAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var state = client.State;
            var count = state.Topology.Auxiliaries;
            if (aux < 1 || aux > count)
            {
                Console.Error.WriteLine(count == 0
                    ? "error: this switcher has no auxiliary outputs"
                    : $"error: aux must be between 1 and {count}, not {aux}");
                return 1;
            }
            if (!TryResolveSource(state, source, out var sourceId, out var lookupError))
            {
                Console.Error.WriteLine($"error: {lookupError}");
                return 1;
            }

            var index = aux - 1;
            bool Applied(AtemState s) => s.AuxById.TryGetValue(index, out var a) && a.Source == sourceId;

            if (Applied(state))
            {
                Console.WriteLine($"No change: aux {aux} already shows {state.NameOf(sourceId)}.");
                return 0;
            }

            var backupPath = backup();
            await client.SendCommandAsync(
                "CAuS", AtemChanges.SetAuxSource(index, sourceId), Applied,
                TimeSpan.FromSeconds(timeout ?? 5));

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new AtemAuxSetResult(aux, sourceId, state.NameOf(sourceId), backupPath),
                    BmdJsonContext.Default.AtemAuxSetResult));
            }
            else
            {
                Console.WriteLine($"Aux {aux} now shows {sourceId} ({state.NameOf(sourceId)})");
                Console.WriteLine($"Backup: {backupPath ?? "skipped"}");
            }
            return 0;
        });

    /// <summary>Put a source on the program bus. This cuts to it on air immediately, so it asks first unless --force is given.</summary>
    /// <param name="source">Source to put on air: its id or its name, as shown by `bmd atem input list`.</param>
    /// <param name="force">Skip the confirmation. Required when not running interactively, so a script cuts to air only where someone wrote that they meant to.</param>
    /// <param name="mixEffect">Which mix effect to change (1-based). Defaults to 1.</param>
    /// <param name="host">Device address; defaults to config atem.host.</param>
    /// <param name="port">Device UDP port; defaults to config atem.port, else 9910.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config atem.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ProgramSet(
        [Argument] string source, int mixEffect = 1, bool force = false,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => SetBusAsync("program", "CPgI", source, mixEffect, host, port, timeout, noBackup, json,
            (state, id) => state.ProgramSource == id, AtemChanges.SetProgramSource, force);

    /// <summary>Put a source on the preview bus. Nothing changes on air.</summary>
    /// <param name="source">Source to preview: its id or its name, as shown by `bmd atem input list`.</param>
    /// <param name="mixEffect">Which mix effect to change (1-based). Defaults to 1.</param>
    /// <param name="host">Device address; defaults to config atem.host.</param>
    /// <param name="port">Device UDP port; defaults to config atem.port, else 9910.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config atem.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> PreviewSet(
        [Argument] string source, int mixEffect = 1,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => SetBusAsync("preview", "CPvI", source, mixEffect, host, port, timeout, noBackup, json,
            (state, id) => state.PreviewSource == id, AtemChanges.SetPreviewSource, force: true);

    /// <summary>Shared body of `program set` and `preview set`: the two differ only in the command
    /// they send, the state field they watch, and the word they print.</summary>
    Task<int> SetBusAsync(
        string bus, string command, string source, int mixEffect,
        string? host, int? port, int? timeout, bool noBackup, bool json,
        Func<AtemState, int, bool> isApplied, Func<int, int, byte[]> payload, bool force)
        => _session.WithBackupAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var state = client.State;
            if (mixEffect < 1 || mixEffect > state.Topology.MixEffects)
            {
                Console.Error.WriteLine(
                    $"error: --mix-effect must be between 1 and {state.Topology.MixEffects}, not {mixEffect}");
                return 1;
            }
            if (!TryResolveSource(state, source, out var sourceId, out var lookupError))
            {
                Console.Error.WriteLine($"error: {lookupError}");
                return 1;
            }

            bool Applied(AtemState s) => isApplied(s, sourceId);
            if (Applied(state))
            {
                Console.WriteLine($"No change: {state.NameOf(sourceId)} is already on {bus}.");
                return 0;
            }

            // A program cut is live the instant it lands, and contexts make it possible to be
            // pointed at a switcher you had forgotten about. Nothing else in bmd is both
            // instantaneous and visible to an audience, so this one command asks.
            if (!force && Confirm(client, state, sourceId) is { } refusal) return refusal;

            var backupPath = backup();
            await client.SendCommandAsync(
                command, payload(mixEffect - 1, sourceId), Applied, TimeSpan.FromSeconds(timeout ?? 5));

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new AtemBusSetResult(bus, sourceId, state.NameOf(sourceId), backupPath),
                    BmdJsonContext.Default.AtemBusSetResult));
            }
            else
            {
                Console.WriteLine($"{char.ToUpperInvariant(bus[0])}{bus[1..]}: {sourceId} ({state.NameOf(sourceId)})");
                Console.WriteLine($"Backup: {backupPath ?? "skipped"}");
            }
            return 0;
        });

    /// <summary>Asks before cutting to air. Returns null to proceed, or the exit code to stop with.
    ///
    /// Without a terminal there is nobody to ask, so this refuses rather than prompting into the
    /// void or quietly proceeding: a scheduled job cuts to air only where someone wrote --force
    /// and meant it. Exit 2, because the fix is to change the command rather than retry it.</summary>
    int? Confirm(AtemClient client, AtemState state, int sourceId)
    {
        if (!_isInteractive())
        {
            Console.Error.WriteLine(
                $"error: refusing to cut to air without a terminal to confirm at " +
                $"(re-run with --force if that is what you mean)");
            return 2;
        }

        Console.WriteLine(
            $"About to cut {state.ProductName} at {client.Host} to " +
            $"{sourceId} ({state.NameOf(sourceId)}). This goes on air immediately.");
        Console.Write("Type y to continue: ");
        if (Console.ReadLine()?.Trim() is "y" or "Y") return null;

        Console.Error.WriteLine("error: cancelled");
        return 1;
    }
}
