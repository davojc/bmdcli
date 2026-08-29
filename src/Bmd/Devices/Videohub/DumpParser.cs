namespace Bmd.Devices.Videohub;

public static class DumpParser
{
    public static readonly IReadOnlyList<string> RequiredHeaders =
        ["VIDEOHUB DEVICE", "INPUT LABELS", "OUTPUT LABELS", "VIDEO OUTPUT ROUTING", "VIDEO OUTPUT LOCKS"];

    public static VideohubState Parse(IReadOnlyList<ProtocolBlock> blocks)
    {
        var byHeader = new Dictionary<string, ProtocolBlock>(StringComparer.Ordinal);
        foreach (var block in blocks) byHeader.TryAdd(block.Header, block);
        foreach (var required in RequiredHeaders)
            if (!byHeader.ContainsKey(required))
                throw new VideohubProtocolException($"device dump is missing the {required} block");

        var device = ParseDevice(byHeader["VIDEOHUB DEVICE"],
            byHeader.TryGetValue("PROTOCOL PREAMBLE", out var preamble) ? preamble : null);
        var inputLabels = ParseIndexed(byHeader["INPUT LABELS"], device.VideoInputs);
        var outputLabels = ParseIndexed(byHeader["OUTPUT LABELS"], device.VideoOutputs);
        var routes = new int[device.VideoOutputs];
        foreach (var (index, value) in ParsePairs(byHeader["VIDEO OUTPUT ROUTING"], device.VideoOutputs))
        {
            var route = ParseInt(value, "VIDEO OUTPUT ROUTING");
            if (route < 0 || route >= device.VideoInputs)
                throw new VideohubProtocolException(
                    $"route for output {index} has invalid input {route} (valid range 0-{device.VideoInputs - 1})");
            routes[index] = route;
        }
        var locks = new LockState[device.VideoOutputs];
        foreach (var (index, value) in ParsePairs(byHeader["VIDEO OUTPUT LOCKS"], device.VideoOutputs))
            locks[index] = ParseLock(value);
        return new VideohubState(device, inputLabels, outputLabels, routes, locks);
    }

    /// <summary>Returns a new state with the update block's entries applied.
    /// Blocks that are not recognised return the state unchanged.</summary>
    public static VideohubState ApplyUpdate(VideohubState state, ProtocolBlock block)
    {
        var device = state.Device;
        switch (block.Header)
        {
            case "INPUT LABELS":
            {
                var labels = Enumerable.Range(1, device.VideoInputs).Select(state.GetInputLabel).ToArray();
                foreach (var (index, value) in ParsePairs(block, device.VideoInputs)) labels[index] = value;
                return state.WithInputLabels(labels);
            }
            case "OUTPUT LABELS":
            {
                var labels = Enumerable.Range(1, device.VideoOutputs).Select(state.GetOutputLabel).ToArray();
                foreach (var (index, value) in ParsePairs(block, device.VideoOutputs)) labels[index] = value;
                return state.WithOutputLabels(labels);
            }
            case "VIDEO OUTPUT ROUTING":
            {
                var routes = Enumerable.Range(1, device.VideoOutputs).Select(n => state.GetRoute(n) - 1).ToArray();
                foreach (var (index, value) in ParsePairs(block, device.VideoOutputs))
                {
                    var route = ParseInt(value, block.Header);
                    if (route < 0 || route >= device.VideoInputs)
                        throw new VideohubProtocolException(
                            $"route for output {index + 1} has invalid input {route} (valid range 0-{device.VideoInputs - 1})");
                    routes[index] = route;
                }
                return state.WithRoutes(routes);
            }
            case "VIDEO OUTPUT LOCKS":
            {
                var locks = Enumerable.Range(1, device.VideoOutputs).Select(state.GetLock).ToArray();
                foreach (var (index, value) in ParsePairs(block, device.VideoOutputs)) locks[index] = ParseLock(value);
                return state.WithLocks(locks);
            }
            default:
                return state;
        }
    }

    static LockState ParseLock(string value) => value switch
    {
        "U" => LockState.Unlocked,
        "O" => LockState.Owned,
        "L" => LockState.Locked,
        _ => throw new VideohubProtocolException($"unknown lock state '{value}'"),
    };

    static VideohubDeviceInfo ParseDevice(ProtocolBlock device, ProtocolBlock? preamble)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in device.Lines)
        {
            var colon = line.IndexOf(':');
            if (colon > 0) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var version = "";
        if (preamble is not null)
            foreach (var line in preamble.Lines)
                if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                    version = line["Version:".Length..].Trim();
        return new VideohubDeviceInfo(
            fields.GetValueOrDefault("Model name", "unknown"),
            fields.TryGetValue("Friendly name", out var friendly) && friendly.Length > 0 ? friendly : null,
            ParseInt(fields.GetValueOrDefault("Video inputs", "0"), "Video inputs"),
            ParseInt(fields.GetValueOrDefault("Video outputs", "0"), "Video outputs"),
            version);
    }

    static string[] ParseIndexed(ProtocolBlock block, int count)
    {
        var values = new string[count];
        Array.Fill(values, "");
        foreach (var (index, value) in ParsePairs(block, count)) values[index] = value;
        return values;
    }

    /// <summary>Parses "&lt;0-based index&gt; &lt;rest of line&gt;" entries, skipping out-of-range indices.</summary>
    static IEnumerable<(int Index, string Value)> ParsePairs(ProtocolBlock block, int count)
    {
        foreach (var line in block.Lines)
        {
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var index = ParseInt(line[..space], block.Header);
            if (index >= 0 && index < count) yield return (index, line[(space + 1)..]);
        }
    }

    static int ParseInt(string text, string context) =>
        int.TryParse(text, out var value)
            ? value
            : throw new VideohubProtocolException($"invalid number '{text}' in {context} block");
}
