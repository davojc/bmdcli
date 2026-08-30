using System.Text.Json;
using Bmd.Config;
using Bmd.Devices.MultiView;
using Bmd.Devices.Videohub;
using Bmd.Output;

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
}
