using System.Net.NetworkInformation;
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
        catch (Exception ex) when (ex is SocketException or NetworkInformationException)
        {
            // SocketException: every interface failed to send (MdnsClient.DiscoverAsync rethrows
            // the last such failure) — a total send failure, not "nothing answered". That
            // distinction matters: it means the machine itself couldn't even ask, so it is
            // reported as an error rather than folded into the ordinary "no devices found"
            // empty-result path below.
            // NetworkInformationException: NetworkInterface.GetAllNetworkInterfaces() can fail at
            // the OS level (it derives from Win32Exception, not SocketException, so it needs
            // naming explicitly here) — same treatment, one clean stderr line instead of an
            // unhandled crash with a stack trace.
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
            return AddSelected(devices.Count, shown, global, json);

        if (json)
        {
            var results = shown
                .Select(d => new DiscoveredDeviceResult(d.Name, d.DeviceClass, d.DeviceType, d.Address.ToString(), d.Port, d.TxtEntries))
                .ToArray();
            Console.WriteLine(JsonSerializer.Serialize(results, BmdJsonContext.Default.DiscoveredDeviceResultArray));
            return 0;
        }

        if (shown.Length == 0)
        {
            Console.Error.WriteLine(EmptyResultMessage(devices.Count));
            return 0;
        }

        // TXT entries are only shown under --all (the diagnostic listing); the default table
        // stays exactly as clean as it is today. Passing null (rather than an all-empty details
        // array) when !all also means the default path never touches TxtEntries at all.
        IReadOnlyList<IReadOnlyList<string>>? details = all
            ? shown.Select(d => (IReadOnlyList<string>)d.TxtEntries.Select(TxtLineForDisplay).ToArray()).ToArray()
            : null;

        Table.Write(["NAME", "TYPE", "ADDRESS"],
            shown.Select(d => (IReadOnlyList<string>)[d.Name, TypeCell(d), AddressCell(d)]).ToArray(),
            details);
        return 0;
    }

    const string NoDevicesFoundMessage =
        "No devices found. mDNS discovery does not cross subnets, and some older Blackmagic " +
        "devices predate mDNS support entirely — if yours isn't showing up, configure it directly " +
        "with `bmd config set videohub.host <address>`.";

    /// <summary>The stderr note for an empty result, shared by the plain listing and `--add`
    /// (which is why it lives here rather than being duplicated in <see cref="AddSelected"/>):
    /// plain <see cref="NoDevicesFoundMessage"/> when literally nothing answered, or a distinct
    /// note when <paramref name="devicesAnswered"/> devices answered but none was a recognized
    /// bmd device type — "No devices found" would be actively misleading in that case, since
    /// something is in fact on the network.</summary>
    static string EmptyResultMessage(int devicesAnswered) =>
        devicesAnswered == 0
            ? NoDevicesFoundMessage
            : $"{devicesAnswered} device{(devicesAnswered == 1 ? "" : "s")} answered but none is a type bmd recognizes — " +
              "run `bmd discover --all` to see them.";

    /// <summary>The interactive `--add` flow: list the supported candidates, ask which one to
    /// keep, and write its address (and non-default port) into config. `shown` is already
    /// filtered to devices with a recognized <see cref="DiscoveredDevice.DeviceType"/> — callers
    /// reject `--add --all` before reaching here, so every entry's <c>DeviceType</c> is non-null.
    /// <paramref name="devicesAnswered"/> is the unfiltered count (before that filtering), used
    /// only to pick the right <see cref="EmptyResultMessage"/> when nothing is left to add.</summary>
    int AddSelected(int devicesAnswered, IReadOnlyList<DiscoveredDevice> shown, bool global, bool json)
    {
        if (shown.Count == 0)
        {
            // Nothing found means nothing to add regardless of whether stdin is interactive —
            // checked before the terminal guard so this case never demands a real terminal.
            Console.Error.WriteLine(EmptyResultMessage(devicesAnswered));
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
            var results = new List<ConfigSetResult>(toSet.Count);
            foreach (var (key, value) in toSet)
            {
                var file = store.Set(key, value, global);
                if (json)
                    results.Add(new ConfigSetResult(key.ToString(), value, file));
                else
                    Console.WriteLine($"Set {key} = {value} in {file}");
            }
            // Every non-streaming command emits exactly one JSON document (see the spec's
            // "Agents and scripting" section) — one or two keys are written here (host, plus
            // port when it isn't the default), so the results are collected and emitted as a
            // single array rather than one document per key, which would produce JSON Lines
            // and silently break `| jq` for the two-key case.
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(results.ToArray(), BmdJsonContext.Default.ConfigSetResultArray));
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

    /// <summary>Indented four spaces beneath a device's table row under <c>--all</c>. TXT
    /// entries come straight off the network, so <see cref="SanitizeForDisplay"/> guards the
    /// terminal against a literal newline (which would otherwise fake an extra row and break the
    /// one-line-per-entry layout) or a raw control/escape byte (which could otherwise manipulate
    /// the terminal itself). This only affects what gets printed here — <c>DiscoveredDevice</c>
    /// and the JSON output both keep the entry exactly as advertised.</summary>
    static string TxtLineForDisplay(string entry) => "    " + SanitizeForDisplay(entry);

    /// <summary>Replaces every control character (a literal newline, carriage return, tab, ESC,
    /// or any other C0/C1 control code) with the Unicode replacement character, leaving
    /// everything else untouched. See <see cref="TxtLineForDisplay"/> for why.</summary>
    static string SanitizeForDisplay(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsControl(source[i]) ? '�' : source[i];
        });
}
