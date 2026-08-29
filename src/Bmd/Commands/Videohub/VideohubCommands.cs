using System.Net.Sockets;
using System.Text.Json;
using Bmd.Config;
using Bmd.Devices.Videohub;
using Bmd.Output;

namespace Bmd.Commands.Videohub;

/// <summary>bmd videohub — control a Blackmagic Videohub over the network.</summary>
public class VideohubCommands
{
    readonly Func<ConfigStore> _loadConfig;

    public VideohubCommands() : this(ConfigStore.LoadDefault) { }
    public VideohubCommands(Func<ConfigStore> loadConfig) => _loadConfig = loadConfig;

    /// <summary>Show device information (model, protocol version, input/output counts).</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Info(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => WithClientAsync(host, port, timeout, client =>
        {
            var device = client.State.Device;
            if (json)
            {
                var result = new VideohubInfoResult(
                    device.ModelName, device.FriendlyName, device.ProtocolVersion,
                    device.VideoInputs, device.VideoOutputs);
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.VideohubInfoResult));
            }
            else
            {
                Console.WriteLine($"Model:          {device.ModelName}");
                if (device.FriendlyName is not null)
                    Console.WriteLine($"Friendly name:  {device.FriendlyName}");
                Console.WriteLine($"Protocol:       {device.ProtocolVersion}");
                Console.WriteLine($"Video inputs:   {device.VideoInputs}");
                Console.WriteLine($"Video outputs:  {device.VideoOutputs}");
            }
            return 0;
        });

    /// <summary>List inputs (1-based) with their labels.</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> InputList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => WithClientAsync(host, port, timeout, client =>
        {
            var device = client.State.Device;
            var entries = Enumerable.Range(1, device.VideoInputs)
                .Select(n => new VideohubInputEntry(n, client.State.GetInputLabel(n))).ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.VideohubInputEntryArray));
            else
                Table.Write(["N", "LABEL"], entries.Select(e => (IReadOnlyList<string>)[e.N.ToString(), e.Label]).ToArray());
            return 0;
        });

    /// <summary>List outputs (1-based) with label, routed input, and lock state.</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> OutputList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            var entries = Enumerable.Range(1, state.Device.VideoOutputs)
                .Select(n => new VideohubOutputEntry(
                    n, state.GetOutputLabel(n), state.GetRoute(n),
                    state.GetInputLabel(state.GetRoute(n)), LockWord(state.GetLock(n))))
                .ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.VideohubOutputEntryArray));
            else
                Table.Write(["N", "LABEL", "INPUT", "INPUT LABEL", "LOCK"],
                    entries.Select(e => (IReadOnlyList<string>)
                        [e.N.ToString(), e.Label, e.Input.ToString(), e.InputLabel, e.Lock]).ToArray());
            return 0;
        });

    /// <summary>List the current routing (1-based): which input feeds each output.</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> RouteList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            var entries = Enumerable.Range(1, state.Device.VideoOutputs)
                .Select(n => new VideohubRouteEntry(
                    n, state.GetOutputLabel(n), state.GetRoute(n), state.GetInputLabel(state.GetRoute(n))))
                .ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.VideohubRouteEntryArray));
            else
                Table.Write(["OUT", "OUTPUT LABEL", "IN", "INPUT LABEL"],
                    entries.Select(e => (IReadOnlyList<string>)
                        [e.Output.ToString(), e.OutputLabel, e.Input.ToString(), e.InputLabel]).ToArray());
            return 0;
        });

    static string LockWord(LockState lockState) => lockState switch
    {
        LockState.Owned => "owned",
        LockState.Locked => "locked",
        _ => "unlocked",
    };

    async Task<int> WithClientAsync(string? host, int? port, int? timeout, Func<VideohubClient, int> action)
    {
        try
        {
            var store = _loadConfig();
            var resolvedHost = host ?? GetConfig(store, "videohub.host");
            if (resolvedHost is null)
            {
                Console.Error.WriteLine("error: no host configured for videohub (run: bmd config set videohub.host <addr>)");
                return 1;
            }
            var resolvedPort = port ?? GetConfigInt(store, "videohub.port") ?? 9990;
            var resolvedTimeout = timeout ?? GetConfigInt(store, "videohub.timeout") ?? 5;
            if (resolvedTimeout <= 0)
            {
                Console.Error.WriteLine("error: timeout must be a positive number of seconds");
                return 2;
            }
            await using var client = await VideohubClient.ConnectAsync(
                resolvedHost, resolvedPort, TimeSpan.FromSeconds(resolvedTimeout));
            return action(client);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException
                                       or TimeoutException or VideohubProtocolException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (ConfigValueFormatException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static string? GetConfig(ConfigStore store, string key)
    {
        ConfigKey.TryParse(key, out var parsed);
        return store.GetEffective(parsed);
    }

    static int? GetConfigInt(ConfigStore store, string key)
    {
        var value = GetConfig(store, key);
        if (value is null) return null;
        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new ConfigValueFormatException($"config {key} is not a number: '{value}'");
    }

    sealed class ConfigValueFormatException(string message) : Exception(message);
}
