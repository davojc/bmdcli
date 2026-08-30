using System.Net.Sockets;
using Bmd.Config;
using Bmd.Devices.Videohub;

namespace Bmd.Commands;

/// <summary>Everything a device command group needs around the edges of its own logic:
/// resolving the connection from flags then config, connecting, taking the pre-mutation backup,
/// and mapping every expected failure to one stderr line plus an exit code.
///
/// Parameterised by config section — `videohub`, `multiview` — because that is genuinely the
/// only thing that differed between the two groups. Both talk the same protocol to the same
/// port with the same client; only the key they read their address from changes.</summary>
internal sealed class DeviceSession(string configSection, Func<ConfigStore> loadConfig)
{
    readonly string _configSection = configSection;

    /// <summary>Synchronous-action convenience over <see cref="RunWithClientAsync"/>.</summary>
    public Task<int> WithClientAsync(string? host, int? port, int? timeout, Func<VideohubClient, int> action)
        => RunWithClientAsync(host, port, timeout, client => Task.FromResult(action(client)));

    /// <summary>Connects, backs up the pre-change state unless disabled, then runs the action.
    /// A failed backup aborts before the action runs.</summary>
    public Task<int> WithBackedUpClientAsync(
        string? host, int? port, int? timeout, bool noBackup,
        Func<VideohubClient, string?, Task<int>> action)
        => RunWithClientAsync(host, port, timeout, async client =>
        {
            string? backupPath = null;
            if (!noBackup)
            {
                var store = BackupStore.FromConfig(loadConfig());
                if (store.AutoBackupEnabled)
                {
                    // includeConfiguration: true so a MultiView mutation's backup captures its
                    // pre-change CONFIGURATION (layout, format, solo, the display toggles) — the
                    // very thing most MultiView mutations change. A no-op for a Videohub: FromState
                    // only populates Configuration when state.ExtraBlocks has a CONFIGURATION
                    // block, which a Videohub never sends (see VideohubSnapshot.FromState).
                    var snapshot = VideohubSnapshot.FromState(client.State, DateTimeOffset.UtcNow, includeConfiguration: true);
                    backupPath = store.Write(
                        BackupStore.DeviceKey(client.Host, client.State.Device.ModelName), snapshot);
                }
            }
            return await action(client, backupPath);
        });

    /// <summary>Connects and hands the action a thunk that writes the pre-change backup on demand.
    /// Call the thunk immediately before the first mutation: a command that turns out to have
    /// nothing to do — or turns out not to apply at all — must not spend a backup slot.
    /// The thunk is idempotent (a second call returns the same path without writing again) and
    /// returns null when backups are disabled. Unlike <see cref="WithBackedUpClientAsync"/>, which
    /// backs up eagerly because its five callers (route set, renames, lock, unlock) always intend
    /// to mutate, this seam defers the decision to the caller, who alone knows whether work is
    /// actually needed.</summary>
    public Task<int> WithDeferredBackupClientAsync(
        string? host, int? port, int? timeout, bool noBackup,
        Func<VideohubClient, Func<Task<string?>>, Task<int>> action)
        => RunWithClientAsync(host, port, timeout, client =>
        {
            string? written = null;
            var done = false;
            Task<string?> Backup()
            {
                if (done) return Task.FromResult(written);
                done = true;
                if (!noBackup)
                {
                    var store = BackupStore.FromConfig(loadConfig());
                    if (store.AutoBackupEnabled)
                    {
                        // Captured from client.State at the moment the thunk is first called —
                        // never mutated except by our own sends, so as long as every caller
                        // invokes this before its first mutation, this is always the pre-change
                        // state, regardless of how much later in the call the thunk fires.
                        // includeConfiguration: true — see WithBackedUpClientAsync for why (a
                        // no-op for a Videohub).
                        var snapshot = VideohubSnapshot.FromState(client.State, DateTimeOffset.UtcNow, includeConfiguration: true);
                        written = store.Write(
                            BackupStore.DeviceKey(client.Host, client.State.Device.ModelName), snapshot);
                    }
                }
                return Task.FromResult(written);
            }
            return action(client, Backup);
        });

    /// <summary>Resolves the connection from flags then config, connects, runs the action,
    /// and maps every expected failure to one stderr line plus an exit code.</summary>
    public Task<int> RunWithClientAsync(
        string? host, int? port, int? timeout, Func<VideohubClient, Task<int>> action)
        => RunCatchingAsync(async () =>
        {
            var store = loadConfig();
            var resolvedHost = host ?? GetConfig(store, $"{_configSection}.host");
            if (resolvedHost is null)
            {
                Console.Error.WriteLine(
                    $"error: no host configured for {_configSection} " +
                    $"(run: bmd config set {_configSection}.host <addr>)");
                return 1;
            }
            var resolvedPort = port ?? GetConfigInt(store, $"{_configSection}.port") ?? 9990;
            var resolvedTimeout = timeout ?? GetConfigInt(store, $"{_configSection}.timeout") ?? 5;
            if (resolvedTimeout <= 0)
            {
                Console.Error.WriteLine("error: timeout must be a positive number of seconds");
                return 2;
            }
            await using var client = await VideohubClient.ConnectAsync(
                resolvedHost, resolvedPort, TimeSpan.FromSeconds(resolvedTimeout));
            return await action(client);
        });

    /// <summary>Runs <paramref name="body"/>, mapping every expected failure to one stderr line
    /// plus exit code 1. The single filter shared by the real connect+action path and by the
    /// <c>ThrowingProbeAsync</c> test seam.</summary>
    internal static async Task<int> RunCatchingAsync(Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException
                                       or TimeoutException or VideohubProtocolException
                                       or VideohubCommandRejectedException
                                       or SnapshotFormatException or ConfigValueException
                                       or ConfigValueFormatException)
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
