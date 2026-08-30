using System.Text.Json;
using Bmd.Config;
using Bmd.Devices.MultiView;
using Bmd.Devices.Videohub;
using Bmd.Output;
using ConsoleAppFramework;

namespace Bmd.Commands.MultiView;

/// <summary>bmd multiview — control a Blackmagic MultiView over the network.
///
/// A MultiView speaks the same Videohub Ethernet Protocol as a router, so this group shares the
/// client and the session plumbing. What differs is vocabulary and one extra protocol block: the
/// six things the wire calls "video outputs" are four multiview windows plus a Solo and an Audio
/// input, so everything a user sees here says <b>view</b>.</summary>
public class MultiViewCommands
{
    readonly DeviceSession _session;

    public MultiViewCommands() : this(ConfigStore.LoadDefault) { }
    public MultiViewCommands(Func<ConfigStore> loadConfig) => _session = new DeviceSession("multiview", loadConfig);

    /// <summary>Reads the CONFIGURATION block out of a connected device's state, or
    /// <see cref="MultiViewConfiguration.Empty"/> when the device never sent one.</summary>
    internal static MultiViewConfiguration ReadConfiguration(VideohubState state) =>
        state.ExtraBlocks.TryGetValue(MultiViewConfiguration.BlockHeader, out var lines)
            ? MultiViewConfiguration.FromLines(lines)
            : MultiViewConfiguration.Empty;

    /// <summary>Show device information: model, protocol version, source and view counts, and the current layout and output format.</summary>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Info(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var device = client.State.Device;
            var config = ReadConfiguration(client.State);
            if (json)
            {
                var result = new MultiViewInfoResult(
                    device.ModelName, device.FriendlyName, device.ProtocolVersion,
                    device.VideoInputs, device.VideoOutputs, config.Layout, config.OutputFormat);
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewInfoResult));
            }
            else
            {
                Console.WriteLine($"Model:          {device.ModelName}");
                if (device.FriendlyName is not null)
                    Console.WriteLine($"Friendly name:  {device.FriendlyName}");
                Console.WriteLine($"Protocol:       {device.ProtocolVersion}");
                Console.WriteLine($"Inputs:         {device.VideoInputs}");
                Console.WriteLine($"Views:          {device.VideoOutputs}");
                if (config.Layout is not null) Console.WriteLine($"Layout:         {config.Layout}");
                if (config.OutputFormat is not null) Console.WriteLine($"Output format:  {config.OutputFormat}");
            }
            return 0;
        });

    /// <summary>List sources (1-based) with their labels.</summary>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> InputList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var device = client.State.Device;
            var entries = Enumerable.Range(1, device.VideoInputs)
                .Select(n => new MultiViewInputEntry(n, client.State.GetInputLabel(n))).ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.MultiViewInputEntryArray));
            else
                Table.Write(["N", "LABEL"], entries.Select(e => (IReadOnlyList<string>)[e.N.ToString(), e.Label]).ToArray());
            return 0;
        });

    /// <summary>List views (1-based) with label, the source feeding each, and lock state. On a MultiView 4 the last two entries are the Solo and Audio inputs rather than windows.</summary>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ViewList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            var entries = Enumerable.Range(1, state.Device.VideoOutputs)
                .Select(n => new MultiViewViewEntry(
                    n, state.GetOutputLabel(n), state.GetRoute(n),
                    state.GetInputLabel(state.GetRoute(n)), VideohubUpdate.Word(state.GetLock(n))))
                .ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.MultiViewViewEntryArray));
            else
                Table.Write(["VIEW", "LABEL", "IN", "SOURCE", "LOCK"],
                    entries.Select(e => (IReadOnlyList<string>)
                        [e.N.ToString(), e.Label, e.Input.ToString(), e.InputLabel, e.Lock]).ToArray());
            return 0;
        });

    /// <summary>Print the device's CONFIGURATION block: layout, output format, and the display and behaviour toggles, exactly as the device reports them. Properties bmd does not recognise are shown too — the block is undocumented and varies by model and firmware.</summary>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="json">Emit the result as a JSON array of name/value pairs on stdout.</param>
    public Task<int> Config(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var config = ReadConfiguration(client.State);
            if (config.Raw.Count == 0)
            {
                Console.Error.WriteLine(
                    $"error: {client.State.Device.ModelName} sent no CONFIGURATION block — " +
                    "it is probably a Videohub rather than a MultiView (try: bmd videohub info)");
                return 1;
            }
            if (json)
            {
                var entries = config.Raw.Select(p => new MultiViewConfigEntry(p.Key, p.Value)).ToArray();
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.MultiViewConfigEntryArray));
            }
            else
            {
                Table.Write(["SETTING", "VALUE"],
                    config.Raw.Select(p => (IReadOnlyList<string>)[p.Key, p.Value]).ToArray());
            }
            return 0;
        });

    /// <summary>Range check shared by every command taking a view or source number. Returns an
    /// error message, or null when the value is in range. 1-based throughout, matching the
    /// device's own front panel.</summary>
    static string? OutOfRange(string noun, int value, int count) =>
        value >= 1 && value <= count ? null : $"{noun} must be between 1 and {count}, not {value}";

    /// <summary>Put a source in a view. Argument order is destination first, then source — view
    /// then input, matching `videohub route set &lt;output&gt; &lt;input&gt;` and the device's own
    /// front-panel convention. Both numbers are 1-based. On a MultiView 4, views 5 and 6 are the
    /// Solo and Audio inputs rather than windows.</summary>
    /// <param name="view">Destination: which view to change (1-based).</param>
    /// <param name="input">Source: which input to show in it (1-based).</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ViewSet(
        [Argument] int view, [Argument] int input,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var state = client.State;
            if (OutOfRange("view", view, state.Device.VideoOutputs) is { } viewError)
            {
                Console.Error.WriteLine($"error: {viewError}");
                return 2;
            }
            if (OutOfRange("input", input, state.Device.VideoInputs) is { } inputError)
            {
                Console.Error.WriteLine($"error: {inputError}");
                return 2;
            }

            await client.SetRouteAsync(view, input);

            var result = new MultiViewRouteSetResult(
                view, state.GetOutputLabel(view), input, state.GetInputLabel(input), backup);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewRouteSetResult));
            else
            {
                Console.WriteLine($"view {view} ({result.ViewLabel}) ← input {input} ({result.InputLabel})");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });

    /// <summary>Rename a source (1-based) on the device itself, so its label matches on the front panel and in other controllers.</summary>
    /// <param name="input">Which source to rename (1-based).</param>
    /// <param name="label">The new label.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> InputRename(
        [Argument] int input, [Argument] string label,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => RenameAsync("input", input, label, host, port, timeout, noBackup, json);

    /// <summary>Rename a view (1-based) on the device itself.</summary>
    /// <param name="view">Which view to rename (1-based).</param>
    /// <param name="label">The new label.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ViewRename(
        [Argument] int view, [Argument] string label,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => RenameAsync("view", view, label, host, port, timeout, noBackup, json);

    Task<int> RenameAsync(
        string kind, int n, string label,
        string? host, int? port, int? timeout, bool noBackup, bool json)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var state = client.State;
            var isInput = kind == "input";
            var count = isInput ? state.Device.VideoInputs : state.Device.VideoOutputs;
            if (OutOfRange(kind, n, count) is { } error)
            {
                Console.Error.WriteLine($"error: {error}");
                return 2;
            }

            var old = isInput ? state.GetInputLabel(n) : state.GetOutputLabel(n);
            if (isInput) await client.RenameInputAsync(n, label);
            else await client.RenameOutputAsync(n, label);

            var result = new MultiViewRenameResult(kind, n, old, label, backup);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewRenameResult));
            else
            {
                Console.WriteLine($"{kind} {n}: {old} → {label}");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });

    /// <summary>Take the lock on a view (1-based), preventing other controllers from changing its source or taking it over without --force.</summary>
    /// <param name="view">Which view to lock (1-based).</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ViewLock(
        [Argument] int view,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => LockAsync(view, force: false, unlock: false, host, port, timeout, noBackup, json);

    /// <summary>Release the lock on a view (1-based). Without --force, releasing a lock held by another controller is left to the device to accept or refuse.</summary>
    /// <param name="view">Which view to unlock (1-based).</param>
    /// <param name="force">Clear a lock held by another controller.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> ViewUnlock(
        [Argument] int view, bool force = false,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => LockAsync(view, force, unlock: true, host, port, timeout, noBackup, json);

    Task<int> LockAsync(
        int view, bool force, bool unlock,
        string? host, int? port, int? timeout, bool noBackup, bool json)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var state = client.State;
            if (OutOfRange("view", view, state.Device.VideoOutputs) is { } error)
            {
                Console.Error.WriteLine($"error: {error}");
                return 2;
            }

            if (unlock) await client.UnlockOutputAsync(view, force);
            else await client.LockOutputAsync(view);

            var word = unlock ? "unlocked" : "locked";
            var result = new MultiViewLockResult(view, state.GetOutputLabel(view), word, backup);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewLockResult));
            else
            {
                Console.WriteLine($"view {view} ({result.ViewLabel}): {word}");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });

    /// <summary>The display settings `show` accepts, for both validation and the error message.</summary>
    static readonly string[] ShowSettings = ["borders", "labels", "audio-meters", "tally"];

    /// <summary>Set the multiview window layout, for example 2x2. bmd does not validate the
    /// value: the CONFIGURATION block is undocumented and valid layouts differ by model and
    /// firmware, so the device decides and any rejection is reported.</summary>
    /// <param name="value">The layout to set. bmd sends whatever is given — the device, not bmd, validates it. Observed on a MultiView 4: 2x2.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Layout(
        [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => SetPropertyAsync("Layout", value, host, port, timeout, noBackup, json);

    /// <summary>Set the multiview output video format, for example 1080i5994. As with layout,
    /// bmd sends the value and lets the device accept or reject it.</summary>
    /// <param name="value">The format to set. bmd sends whatever is given — the device, not bmd, validates it. Observed on a MultiView 4: 1080i5994.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Format(
        [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => SetPropertyAsync("Output format", value, host, port, timeout, noBackup, json);

    /// <summary>Show one source full-screen, or leave solo mode. Passing a source number both
    /// enables solo and points the Solo Input at that source; passing off disables solo and
    /// leaves routing untouched.</summary>
    /// <param name="value">A source number (1-based) to solo, or "off" to leave solo mode.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Solo(
        [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var state = client.State;
            var off = string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
            int? source = null;
            if (!off)
            {
                if (!int.TryParse(value, out var parsed))
                {
                    Console.Error.WriteLine($"error: solo takes a source number (1-based) or 'off', not '{value}'");
                    return 2;
                }
                if (OutOfRange("input", parsed, state.Device.VideoInputs) is { } error)
                {
                    Console.Error.WriteLine($"error: {error}");
                    return 2;
                }
                source = parsed;
            }

            // The Solo Input is the second-to-last "output" on a MultiView 4. Route it first so
            // that by the time solo is enabled the correct source is already showing.
            if (source is { } input)
            {
                var soloView = state.Device.VideoOutputs - 1;
                await client.SetRouteAsync(soloView, input);
            }
            await client.SendBlockAsync(
                MultiViewConfiguration.BlockHeader,
                MultiViewConfiguration.LinesFor("Solo enabled", off ? "false" : "true"),
                description: $"CONFIGURATION (Solo enabled: {(off ? "false" : "true")})");

            var result = new MultiViewConfigSetResult("Solo enabled", off ? "false" : "true", backup);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewConfigSetResult));
            else
            {
                Console.WriteLine(off
                    ? "solo: off"
                    : $"solo: input {source} ({state.GetInputLabel(source!.Value)})");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });

    /// <summary>Turn one of the multiview's on-screen overlays on or off: borders, labels, audio-meters, or tally.</summary>
    /// <param name="setting">Which overlay: borders, labels, audio-meters, or tally.</param>
    /// <param name="value">on or off.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Show(
        [Argument] string setting, [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
    {
        if (!ShowSettings.Contains(setting, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"error: unknown setting '{setting}' — expected one of: {string.Join(", ", ShowSettings)}");
            return Task.FromResult(2);
        }
        return SetFlagAsync(MultiViewConfiguration.ProtocolNameFor(setting)!, value, host, port, timeout, noBackup, json);
    }

    /// <summary>Turn take mode on or off. In take mode the device holds a requested change until it is confirmed, rather than cutting immediately.</summary>
    /// <param name="value">on or off.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> TakeMode(
        [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => SetFlagAsync("Take Mode", value, host, port, timeout, noBackup, json);

    /// <summary>Turn widescreen SD on or off. Only affects standard-definition sources.</summary>
    /// <param name="value">on or off.</param>
    /// <param name="host">Device address; defaults to config multiview.host.</param>
    /// <param name="port">Device TCP port; defaults to config multiview.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config multiview.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup. Not recommended.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> WidescreenSd(
        [Argument] string value,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => SetFlagAsync("Widescreen SD enabled", value, host, port, timeout, noBackup, json);

    /// <summary>A boolean CONFIGURATION property. bmd validates on/off here because true/false is
    /// unambiguous — unlike layout and format, where it deliberately does not.</summary>
    Task<int> SetFlagAsync(
        string protocolProperty, string value,
        string? host, int? port, int? timeout, bool noBackup, bool json)
    {
        if (!MultiViewConfiguration.TryParseOnOff(value, out var flag))
        {
            Console.Error.WriteLine($"error: expected 'on' or 'off', not '{value}'");
            return Task.FromResult(2);
        }
        return SetPropertyAsync(protocolProperty, flag ? "true" : "false", host, port, timeout, noBackup, json);
    }

    Task<int> SetPropertyAsync(
        string protocolProperty, string value,
        string? host, int? port, int? timeout, bool noBackup, bool json)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            await client.SendBlockAsync(
                MultiViewConfiguration.BlockHeader,
                MultiViewConfiguration.LinesFor(protocolProperty, value),
                description: $"CONFIGURATION ({protocolProperty}: {value})");

            var result = new MultiViewConfigSetResult(protocolProperty, value, backup);
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.MultiViewConfigSetResult));
            else
            {
                Console.WriteLine($"{protocolProperty}: {value}");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });
}
