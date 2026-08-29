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
    /// <summary>Videohub's default TCP port — see <c>VideohubClient</c>. A discovered device
    /// advertising this port needs no <c>.port</c> entry written alongside its <c>.host</c>
    /// one, since it is already what the rest of bmd assumes when the key is absent.</summary>
    const int DefaultPort = 9990;

    readonly Func<TimeSpan, CancellationToken, Task<IReadOnlyList<DiscoveredDevice>>> _discover;
    readonly Func<ConfigStore> _loadConfig;

    // Real terminals answer Console.IsInputRedirected accurately, but the test runner always
    // redirects stdin, so a direct read here would make the non-interactive guard fire on every
    // --add test regardless of what it's meant to check. Going through an injectable seam lets
    // tests drive both sides of that guard deliberately instead of at the mercy of the runner.
    readonly Func<bool> _isInteractive;

    public DiscoverCommands() : this(MdnsClient.DiscoverAsync, ConfigStore.LoadDefault) { }

    public DiscoverCommands(
        Func<TimeSpan, CancellationToken, Task<IReadOnlyList<DiscoveredDevice>>> discover,
        Func<ConfigStore> loadConfig,
        Func<bool>? isInteractive = null)
    {
        _discover = discover;
        _loadConfig = loadConfig;
        _isInteractive = isInteractive ?? (() => !Console.IsInputRedirected);
    }

    /// <summary>Find Blackmagic devices on the local network via mDNS. Finding nothing is not an error — mDNS doesn't cross subnets, and some older devices predate mDNS entirely.</summary>
    /// <param name="timeout">How long to wait for responses, in seconds.</param>
    /// <param name="all">List every responding device, not just recognized types — shows each device's raw advertised class, useful for learning what a real device reports. Cannot be combined with --add.</param>
    /// <param name="add">Interactively pick one of the discovered, recognized devices and write its host (and port, if not the default) into config — the same effect as running `bmd config set`, without typing the address by hand.</param>
    /// <param name="global">Used with --add: write to the global config file instead of local .bmdconfig.</param>
    /// <param name="json">Emit the result as a JSON array on stdout (empty array when nothing is found); with --add, emit the written key(s) in the same shape as `bmd config set --json`.</param>
    /// <param name="ct">Cancelled by Ctrl+C.</param>
    public async Task<int> Discover(int? timeout = null, bool all = false, bool add = false, bool global = false, bool json = false, CancellationToken ct = default)
    {
        if (add && all)
        {
            Console.Error.WriteLine("error: --add cannot be combined with --all — you can only add a device bmd recognizes");
            return 2;
        }

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

        if (add)
            return AddSelected(shown, global, json);

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
            Console.Error.WriteLine(NoDevicesFoundMessage);
            return 0;
        }

        Table.Write(["NAME", "TYPE", "ADDRESS"],
            shown.Select(d => (IReadOnlyList<string>)[d.Name, TypeCell(d), AddressCell(d)]).ToArray());
        return 0;
    }

    const string NoDevicesFoundMessage =
        "No devices found. mDNS discovery does not cross subnets, and some older Blackmagic " +
        "devices predate mDNS support entirely — if yours isn't showing up, configure it directly " +
        "with `bmd config set videohub.host <address>`.";

    /// <summary>The interactive `--add` flow: list the supported candidates, ask which one to
    /// keep, and write its address (and non-default port) into config. `shown` is already
    /// filtered to devices with a recognized <see cref="DiscoveredDevice.DeviceType"/> — callers
    /// reject `--add --all` before reaching here, so every entry's <c>DeviceType</c> is non-null.</summary>
    int AddSelected(IReadOnlyList<DiscoveredDevice> shown, bool global, bool json)
    {
        if (shown.Count == 0)
        {
            // Nothing found means nothing to add regardless of whether stdin is interactive —
            // checked before the terminal guard so this case never demands a real terminal.
            Console.Error.WriteLine(NoDevicesFoundMessage);
            return 0;
        }

        if (!_isInteractive())
        {
            Console.Error.WriteLine("error: --add needs an interactive terminal; run bmd config set <type>.host <address> instead");
            return 2;
        }

        for (var i = 0; i < shown.Count; i++)
            Console.Error.WriteLine($"{i + 1}. {shown[i].Name} ({shown[i].DeviceType}) {AddressCell(shown[i])}");
        Console.Error.Write($"Select a device [1-{shown.Count}] (or q to cancel): ");

        var line = Console.In.ReadLine();
        if (string.IsNullOrWhiteSpace(line) || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Cancelled — nothing written.");
            return 0;
        }

        if (!int.TryParse(line.Trim(), out var choice) || choice < 1 || choice > shown.Count)
        {
            Console.Error.WriteLine($"error: selection must be a number from 1 to {shown.Count}");
            return 2;
        }

        var device = shown[choice - 1];
        var deviceType = device.DeviceType!;

        var toSet = new List<(ConfigKey Key, string Value)> { (new ConfigKey(deviceType, "host"), device.Address.ToString()) };
        if (device.Port != DefaultPort)
            toSet.Add((new ConfigKey(deviceType, "port"), device.Port.ToString()));

        try
        {
            var store = _loadConfig();
            foreach (var (key, value) in toSet)
            {
                var file = store.Set(key, value, global);
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(new ConfigSetResult(key.ToString(), value, file), BmdJsonContext.Default.ConfigSetResult));
                else
                    Console.WriteLine($"Set {key} = {value} in {file}");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mirrors ConfigCommands.RunGuarded: a locked file or unwritable directory becomes
            // one clear stderr line, not a stack trace.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
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
