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
