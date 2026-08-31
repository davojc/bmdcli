using System.Net.Sockets;
using Bmd.Config;
using Bmd.Devices.Atem;

namespace Bmd.Commands.Atem;

/// <summary>Connect, back up, and error plumbing for the `bmd atem` group.
///
/// Deliberately not <see cref="DeviceSession"/>. That type is parameterised by config section
/// because a Videohub and a MultiView genuinely differ in nothing else — same protocol, same
/// port, same client. An ATEM shares none of that: binary UDP on 9910, a session handshake, and
/// its own snapshot type. Forcing one session over both transports would be an abstraction over
/// a similarity that does not exist.</summary>
internal sealed class AtemSession(Func<ConfigStore> loadConfig)
{
    public const string ConfigSection = "atem";

    string? _activeContext;

    /// <summary>Connects, runs the action, maps every expected failure to one stderr line.</summary>
    public Task<int> WithClientAsync(
        string? host, int? port, int? timeout, Func<AtemClient, Task<int>> action)
        => RunCatchingAsync(async () =>
        {
            var store = loadConfig();
            if (DeviceAddress.Resolve(
                    store, ConfigSection, host, port, timeout, AtemPacket.DefaultPort,
                    out var exit) is not { } target)
                return exit;
            _activeContext = target.Context;

            await using var client = await AtemClient.ConnectAsync(
                target.Host, target.Port, TimeSpan.FromSeconds(target.Timeout));
            return await action(client);
        });

    /// <summary>Connects and hands the action a thunk that writes the pre-change backup on demand.
    /// Call it immediately before the first mutation: a command that turns out to have nothing to
    /// do must not spend a backup slot. Idempotent, and null when backups are disabled.</summary>
    public Task<int> WithBackupAsync(
        string? host, int? port, int? timeout, bool noBackup,
        Func<AtemClient, Func<string?>, Task<int>> action)
        => WithClientAsync(host, port, timeout, client =>
        {
            string? written = null;
            var done = false;
            string? Backup()
            {
                if (done) return written;
                done = true;
                if (noBackup) return null;
                var store = BackupStore.FromConfig(loadConfig());
                if (!store.AutoBackupEnabled) return null;
                written = store.Write(
                    BackupStore.DeviceKey(client.Host, client.State.ProductName),
                    AtemSnapshot.FromState(client.State, DateTimeOffset.UtcNow));
                return written;
            }
            DeviceAddress.NoteContext(_activeContext, client.Host);
            return action(client, Backup);
        });

    internal static async Task<int> RunCatchingAsync(Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException
                                       or TimeoutException or AtemProtocolException
                                       or Bmd.Devices.Videohub.SnapshotFormatException
                                       or ConfigValueException or ConfigValueFormatException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

}
