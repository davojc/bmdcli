using System.Text.Json;

namespace Bmd.Devices.Videohub;

public sealed class SnapshotFormatException(string message) : Exception(message);

public sealed record SnapshotInput(int N, string Label);
public sealed record SnapshotOutput(int N, string Label, int Input);

/// <summary>A point-in-time capture of a Videohub's labels and routing.
/// All numbering is 1-based. Locks are deliberately excluded (spec non-goal).</summary>
public sealed record VideohubSnapshot(
    string Device,
    int VideoInputs,
    int VideoOutputs,
    DateTimeOffset ExportedAt,
    SnapshotInput[] Inputs,
    SnapshotOutput[] Outputs)
{
    public static VideohubSnapshot FromState(VideohubState state, DateTimeOffset exportedAt)
    {
        var device = state.Device;
        var inputs = Enumerable.Range(1, device.VideoInputs)
            .Select(n => new SnapshotInput(n, state.GetInputLabel(n))).ToArray();
        var outputs = Enumerable.Range(1, device.VideoOutputs)
            .Select(n => new SnapshotOutput(n, state.GetOutputLabel(n), state.GetRoute(n))).ToArray();
        return new VideohubSnapshot(device.ModelName, device.VideoInputs, device.VideoOutputs, exportedAt, inputs, outputs);
    }

    public string ToJson() =>
        JsonSerializer.Serialize(this, SnapshotJsonContext.Default.VideohubSnapshot) + "\n";

    public static VideohubSnapshot FromJson(string json)
    {
        VideohubSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.VideohubSnapshot);
        }
        catch (JsonException ex)
        {
            throw new SnapshotFormatException($"snapshot is not valid JSON: {ex.Message}");
        }
        if (snapshot is null) throw new SnapshotFormatException("snapshot is empty");
        if (string.IsNullOrEmpty(snapshot.Device)) throw new SnapshotFormatException("snapshot is missing 'device'");
        if (snapshot.Inputs is null || snapshot.Outputs is null)
            throw new SnapshotFormatException("snapshot is missing 'inputs' or 'outputs'");
        if (snapshot.VideoInputs != snapshot.Inputs.Length)
            throw new SnapshotFormatException($"snapshot claims {snapshot.VideoInputs} inputs but lists {snapshot.Inputs.Length}");
        if (snapshot.VideoOutputs != snapshot.Outputs.Length)
            throw new SnapshotFormatException($"snapshot claims {snapshot.VideoOutputs} outputs but lists {snapshot.Outputs.Length}");
        ValidateEntries(snapshot);
        return snapshot;
    }

    static void ValidateEntries(VideohubSnapshot snapshot)
    {
        var seenInputs = new HashSet<int>();
        foreach (var input in snapshot.Inputs)
        {
            if (input.N < 1 || input.N > snapshot.VideoInputs)
                throw new SnapshotFormatException(
                    $"snapshot has input {input.N}, outside the valid range 1-{snapshot.VideoInputs}");
            if (!seenInputs.Add(input.N))
                throw new SnapshotFormatException($"snapshot has duplicate entries for input {input.N}");
        }

        var seenOutputs = new HashSet<int>();
        foreach (var output in snapshot.Outputs)
        {
            if (output.N < 1 || output.N > snapshot.VideoOutputs)
                throw new SnapshotFormatException(
                    $"snapshot has output {output.N}, outside the valid range 1-{snapshot.VideoOutputs}");
            if (!seenOutputs.Add(output.N))
                throw new SnapshotFormatException($"snapshot has duplicate entries for output {output.N}");
            if (output.Input < 1 || output.Input > snapshot.VideoInputs)
                throw new SnapshotFormatException(
                    $"snapshot routes output {output.N} to input {output.Input}, outside the valid range 1-{snapshot.VideoInputs}");
        }
    }

    /// <summary>Reasons this snapshot cannot be applied to the given device; empty when it can.</summary>
    public IReadOnlyList<string> IncompatibilityWith(VideohubDeviceInfo device)
    {
        var problems = new List<string>();
        if (!string.Equals(device.ModelName, Device, StringComparison.Ordinal))
            problems.Add($"snapshot is from '{Device}' but the device is '{device.ModelName}'");
        if (device.VideoInputs != VideoInputs)
            problems.Add($"snapshot has {VideoInputs} inputs but the device has {device.VideoInputs}");
        if (device.VideoOutputs != VideoOutputs)
            problems.Add($"snapshot has {VideoOutputs} outputs but the device has {device.VideoOutputs}");
        return problems;
    }

    /// <summary>Lines describing how the device differs from this snapshot; empty when identical.</summary>
    public IReadOnlyList<string> DifferencesFrom(VideohubState state)
    {
        var differences = new List<string>();
        var device = state.Device;
        if (device.VideoInputs != VideoInputs)
            differences.Add($"device has {device.VideoInputs} inputs, snapshot has {VideoInputs}");
        if (device.VideoOutputs != VideoOutputs)
            differences.Add($"device has {device.VideoOutputs} outputs, snapshot has {VideoOutputs}");
        if (differences.Count > 0) return differences; // sizes differ: per-entry comparison is meaningless

        foreach (var input in Inputs)
        {
            var actual = state.GetInputLabel(input.N);
            if (actual != input.Label)
                differences.Add($"input {input.N} label: device '{actual}', snapshot '{input.Label}'");
        }
        foreach (var output in Outputs)
        {
            var actualLabel = state.GetOutputLabel(output.N);
            if (actualLabel != output.Label)
                differences.Add($"output {output.N} label: device '{actualLabel}', snapshot '{output.Label}'");
            var actualRoute = state.GetRoute(output.N);
            if (actualRoute != output.Input)
                differences.Add($"output {output.N} route: device input {actualRoute}, snapshot input {output.Input}");
        }
        return differences;
    }
}
