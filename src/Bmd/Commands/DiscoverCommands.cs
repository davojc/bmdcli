using System.Net.Sockets;
using System.Text.Json;
using Bmd.Config;
using Bmd.Devices.Discovery;
using Bmd.Output;

namespace Bmd.Commands;

/// <summary>bmd discover — find Blackmagic devices announcing themselves via mDNS on the local
/// network.</summary>
public class DiscoverCommands
{
    readonly Func<TimeSpan, CancellationToken, Task<IReadOnlyList<DiscoveredDevice>>> _discover;

    // Unused by plain `discover` (list/--all/--json) — kept as a constructor parameter now so a
    // later `--add` flow (writing the chosen device into config, mirroring `bmd config set`) can
    // land without changing this seam's shape again.
    readonly Func<ConfigStore> _loadConfig;

    public DiscoverCommands() : this(MdnsClient.DiscoverAsync, ConfigStore.LoadDefault) { }

    public DiscoverCommands(
        Func<TimeSpan, CancellationToken, Task<IReadOnlyList<DiscoveredDevice>>> discover,
        Func<ConfigStore> loadConfig)
    {
        _discover = discover;
        _loadConfig = loadConfig;
    }

    /// <summary>Find Blackmagic devices on the local network via mDNS. Finding nothing is not an error — mDNS doesn't cross subnets, and some older devices predate mDNS entirely.</summary>
    /// <param name="timeout">How long to wait for responses, in seconds.</param>
    /// <param name="all">List every responding device, not just recognized types — shows each device's raw advertised class, useful for learning what a real device reports.</param>
    /// <param name="json">Emit the result as a JSON array on stdout (empty array when nothing is found).</param>
    /// <param name="ct">Cancelled by Ctrl+C.</param>
    public async Task<int> Discover(int? timeout = null, bool all = false, bool json = false, CancellationToken ct = default)
    {
        var resolvedTimeout = timeout ?? 3;
        if (resolvedTimeout <= 0)
        {
            Console.Error.WriteLine("error: timeout must be a positive number of seconds");
            return 2;
        }

        IReadOnlyList<DiscoveredDevice> devices;
        try
        {
            devices = await _discover(TimeSpan.FromSeconds(resolvedTimeout), ct);
        }
        catch (SocketException ex)
        {
            // Every interface failed to send (MdnsClient.DiscoverAsync rethrows the last such
            // failure) — a total send failure, not "nothing answered". That distinction matters:
            // it means the machine itself couldn't even ask, so it is reported as an error
            // rather than folded into the ordinary "no devices found" empty-result path below.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        var shown = (all ? devices : devices.Where(d => d.DeviceType is not null))
            // Response order depends on network timing, not anything meaningful — without an
            // explicit order, two runs against the same unchanged network could print devices in
            // a different order, which is confusing to read and unfriendly to script against.
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Address.ToString(), StringComparer.Ordinal)
            .ThenBy(d => d.Port)
            .ToArray();

        if (json)
        {
            var results = shown
                .Select(d => new DiscoveredDeviceResult(d.Name, d.DeviceClass, d.DeviceType, d.Address.ToString(), d.Port))
                .ToArray();
            Console.WriteLine(JsonSerializer.Serialize(results, BmdJsonContext.Default.DiscoveredDeviceResultArray));
            return 0;
        }

        if (shown.Length == 0)
        {
            Console.Error.WriteLine(
                "No devices found. mDNS discovery does not cross subnets, and some older Blackmagic " +
                "devices predate mDNS support entirely — if yours isn't showing up, configure it directly " +
                "with `bmd config set videohub.host <address>`.");
            return 0;
        }

        Table.Write(["NAME", "TYPE", "ADDRESS"],
            shown.Select(d => (IReadOnlyList<string>)[d.Name, TypeCell(d), AddressCell(d)]).ToArray());
        return 0;
    }

    /// <summary>The TYPE column: the recognized bmd device type when there is one, else the raw
    /// advertised class (blank when even that is missing, which a bare "unknown" placeholder
    /// stands in for so the column never renders an empty cell that looks like missing data).</summary>
    static string TypeCell(DiscoveredDevice device) =>
        device.DeviceType ?? (string.IsNullOrWhiteSpace(device.DeviceClass) ? "unknown" : device.DeviceClass);

    /// <summary>Host:port for the ADDRESS column. An IPv6 literal already contains colons, so it
    /// is bracketed the same way a URL would — "host:port" alone would be ambiguous for one.</summary>
    static string AddressCell(DiscoveredDevice device) =>
        device.Address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{device.Address}]:{device.Port}"
            : $"{device.Address}:{device.Port}";
}
