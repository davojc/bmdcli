using System.Text.Json;
using Bmd.Commands.Atem;
using Bmd.Config;
using Bmd.Devices.Atem;
using Bmd.Devices.Videohub;
using Bmd.Output;
using ConsoleAppFramework;

namespace Bmd.Commands;

/// <summary>bmd diagram — one page showing every device bmd can reach and what is routed where.
///
/// <para>Top level rather than under a device group, because the question is about the rig rather
/// than about one box: a venue has a router, a multiviewer and a switcher, and the useful artefact
/// is the one that has all of them on it. Each device contributes the same shape — sources on the
/// left, destinations on the right, the paths currently joining them — which is why a Videohub's
/// outputs, a MultiView's windows and an ATEM's auxiliaries can share a page at all.</para>
///
/// <para>What it does not show, and says so on the page: how the devices are cabled to each other.
/// A device reports its own crosspoints and nothing about what is plugged into its inputs, so any
/// line drawn between two boxes would be a guess dressed up as a measurement.</para></summary>
public sealed class DiagramCommands
{
    readonly Func<ConfigStore> _loadConfig;

    public DiagramCommands() : this(ConfigStore.LoadDefault) { }

    public DiagramCommands(Func<ConfigStore> loadConfig) => _loadConfig = loadConfig;

    /// <summary>Write a self-contained HTML page showing every configured device and what is routed to what. Hovering a name traces the signal.</summary>
    /// <param name="file">Destination file; omit to write the page to stdout.</param>
    /// <param name="timeout">Connection timeout per device in seconds; defaults to each device's configured timeout, else 10.</param>
    /// <param name="json">Emit a summary object as JSON on stdout; requires a file.</param>
    public async Task<int> Draw([Argument] string? file = null, int? timeout = null, bool json = false)
    {
        if (json && file is null)
        {
            Console.Error.WriteLine(
                "error: --json requires a file argument (the page itself goes to stdout without it)");
            return 2;
        }

        var targets = Targets(_loadConfig());
        if (targets.Count == 0)
        {
            Console.Error.WriteLine(
                "error: no devices configured (run: bmd discover --add, or bmd config set videohub.host <addr>)");
            return 1;
        }

        var devices = new List<DiagramDevice>(targets.Count);
        // Indexed so every node id on the page is unique. Three ATEMs would otherwise each call
        // their first source `atem-src-1`; the script scopes its lookups per device so that
        // happens to work, but an id that is only unique by accident is one refactor from a bug.
        for (var i = 0; i < targets.Count; i++) devices.Add(await GatherAsync(targets[i], timeout, i));

        var page = Diagram.Render(devices, DateTimeOffset.UtcNow);
        var reached = devices.Count(d => d.Error is null);

        if (file is null)
        {
            Console.Write(page);
            return 0;
        }

        try
        {
            File.WriteAllText(file, page);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new DiagramResult(devices.Count, reached, devices.Count - reached, file),
                BmdJsonContext.Default.DiagramResult));
        }
        else
        {
            Console.WriteLine($"Drew {reached} of {devices.Count} device(s) → {file}");
            foreach (var device in devices.Where(d => d.Error is not null))
                Console.Error.WriteLine($"note: {device.Title} — {device.Error}");
        }

        // Unreachable devices are reported but do not fail the command: a page with a gap marked
        // in it is the useful artefact, and a rig where one box is off is an ordinary Tuesday.
        return 0;
    }

    /// <summary>Every configured device, across every context, in a stable order. A device type
    /// with no host configured contributes nothing rather than an error — most rigs are not all
    /// three.</summary>
    internal static IReadOnlyList<(string Section, string? Context, string Host)> Targets(ConfigStore store)
    {
        var targets = new List<(string, string?, string)>();
        foreach (var section in (string[])["videohub", "multiview", "atem"])
            foreach (var context in store.Contexts(section))
                if (context.Host is { Length: > 0 } host)
                    targets.Add((section, context.Name, host));
        return targets;
    }

    async Task<DiagramDevice> GatherAsync(
        (string Section, string? Context, string Host) target, int? timeout, int index)
    {
        var store = _loadConfig();
        var key = new ConfigKey(target.Section, "timeout", target.Context);
        // Ten rather than the five a single command uses. This is a batch: nobody is watching one
        // connection, and a device left out of the page costs more than a few seconds of patience.
        // A real 1 M/E Production Studio 4K intermittently needed more than five to finish its dump.
        const int Patient = 10;
        var seconds = timeout
            ?? (int.TryParse(store.GetEffective(key), out var configured) ? configured : Patient);
        var window = TimeSpan.FromSeconds(seconds > 0 ? seconds : Patient);

        var port = int.TryParse(
            store.GetEffective(new ConfigKey(target.Section, "port", target.Context)), out var p)
            ? p
            : target.Section == "atem" ? AtemPacket.DefaultPort : 9990;

        // ASCII: this string reaches stderr as well as the page, and a console that cannot encode
        // a middle dot prints a replacement character where a separator should be.
        var title = target.Context is null
            ? target.Section
            : $"{target.Section} ({target.Context})";

        try
        {
            return target.Section switch
            {
                "atem" => await AtemDeviceAsync($"d{index}", title, target.Host, port, window),
                "multiview" => await VideohubStyleDeviceAsync($"d{index}", title, target.Host, port, window, views: true),
                _ => await VideohubStyleDeviceAsync($"d{index}", title, target.Host, port, window, views: false),
            };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException
                                       or System.Net.Sockets.SocketException
                                       or VideohubProtocolException or AtemProtocolException)
        {
            return new DiagramDevice(title, target.Section, target.Section, target.Host,
                [], [], [], [], $"could not reach it: {ex.Message}");
        }
    }

    static async Task<DiagramDevice> VideohubStyleDeviceAsync(
        string prefix, string title, string host, int port, TimeSpan window, bool views)
    {
        await using var client = await VideohubClient.ConnectAsync(host, port, window);
        var state = client.State;
        var device = state.Device;
        var id = $"{prefix}-{(views ? "mv" : "vh")}";
        var outWord = views ? "VIEW" : "OUT";

        // Only sources that actually reach something: a 40-way hub with 22 idle inputs draws a
        // column half of which goes nowhere, which crowds out the half that matters.
        var used = new SortedSet<int>();
        var links = new List<DiagramLink>();
        for (var output = 1; output <= device.VideoOutputs; output++)
        {
            var input = state.GetRoute(output);
            used.Add(input);
            links.Add(new DiagramLink($"{id}-in-{input}", $"{id}-out-{output}"));
        }

        var idle = device.VideoInputs - used.Count;
        return new DiagramDevice(
            Title: string.IsNullOrWhiteSpace(device.FriendlyName) ? device.ModelName : device.FriendlyName!,
            Subtitle: $"{title} · {device.ModelName}",
            Kind: views ? "multiview" : "videohub",
            Host: host,
            Sources: [.. used.Select(n => new DiagramNode($"{id}-in-{n}", $"IN {n}", Label(state.GetInputLabel(n), n)))],
            Destinations: [.. Enumerable.Range(1, device.VideoOutputs)
                .Select(n => new DiagramNode($"{id}-out-{n}", $"{outWord} {n}", Label(state.GetOutputLabel(n), n)))],
            Links: links,
            Facts: [$"{device.VideoInputs} inputs", $"{device.VideoOutputs} {(views ? "views" : "outputs")}",
                    $"{used.Count} sources in use", $"{idle} idle"]);
    }

    static async Task<DiagramDevice> AtemDeviceAsync(
        string prefix, string title, string host, int port, TimeSpan window)
    {
        await using var client = await AtemClient.ConnectAsync(host, port, window);
        var state = client.State;
        var id = $"{prefix}-atem";

        // A switcher's destinations are its buses, not a numbered row of outputs: what is on air,
        // what is queued, and where each auxiliary is pointed.
        var destinations = new List<DiagramNode>
        {
            new($"{id}-program", "PROGRAM", state.NameOf(state.ProgramSource)),
            new($"{id}-preview", "PREVIEW", state.NameOf(state.PreviewSource)),
        };
        var links = new List<DiagramLink>
        {
            new($"{id}-src-{state.ProgramSource}", $"{id}-program"),
            new($"{id}-src-{state.PreviewSource}", $"{id}-preview"),
        };
        foreach (var aux in state.Auxes)
        {
            destinations.Add(new DiagramNode($"{id}-aux-{aux.Index}", $"AUX {aux.Index + 1}", state.NameOf(aux.Source)));
            links.Add(new DiagramLink($"{id}-src-{aux.Source}", $"{id}-aux-{aux.Index}"));
        }

        var referenced = links.Select(l => l.From).ToHashSet();
        var sources = state.Sources
            .Where(s => s.IsExternalInput || referenced.Contains($"{id}-src-{s.Id}"))
            .Select(s => new DiagramNode($"{id}-src-{s.Id}", $"SRC {s.Id}", state.NameOf(s.Id)))
            .ToList();

        return new DiagramDevice(
            Title: state.ProductName,
            Subtitle: $"{title} · protocol {state.ProtocolVersion}",
            Kind: "atem",
            Host: host,
            Sources: sources,
            Destinations: destinations,
            Links: links,
            Facts: [$"{state.Topology.MixEffects} M/E", $"{state.Topology.Sources} sources",
                    $"{state.Inputs.Count} inputs", $"{state.Topology.Auxiliaries} aux"]);
    }

    static string Label(string label, int number) =>
        string.IsNullOrWhiteSpace(label) ? $"#{number}" : label;
}

/// <summary>`bmd diagram --json`: what was drawn, so a script can tell whether a device was missed
/// without parsing the HTML it just wrote.</summary>
public sealed record DiagramResult(int Devices, int Reached, int Unreachable, string File);
