namespace Bmd.Devices.Videohub;

public enum RestoreChangeKind { InputLabel, OutputLabel, Route }

/// <summary>One difference between a snapshot and a device, expressed in 1-based terms.
/// For routes, From/To are input labels and TargetInput is the 1-based input to route.</summary>
public sealed record RestoreChange(RestoreChangeKind Kind, int N, string From, string To, int TargetInput = 0)
{
    public string Describe() => Kind switch
    {
        RestoreChangeKind.InputLabel => $"input {N} label: '{From}' → '{To}'",
        RestoreChangeKind.OutputLabel => $"output {N} label: '{From}' → '{To}'",
        _ => $"route {N}: {From} → {To}",
    };
}

/// <summary>Computes the ordered changes that converge a device to a snapshot.
/// The snapshot must already be validated and compatible with the device.</summary>
public static class RestorePlan
{
    public static IReadOnlyList<RestoreChange> Compute(VideohubSnapshot snapshot, VideohubState state)
    {
        var changes = new List<RestoreChange>();

        foreach (var input in snapshot.Inputs.OrderBy(i => i.N))
        {
            var current = state.GetInputLabel(input.N);
            if (current != input.Label)
                changes.Add(new RestoreChange(RestoreChangeKind.InputLabel, input.N, current, input.Label));
        }

        foreach (var output in snapshot.Outputs.OrderBy(o => o.N))
        {
            var current = state.GetOutputLabel(output.N);
            if (current != output.Label)
                changes.Add(new RestoreChange(RestoreChangeKind.OutputLabel, output.N, current, output.Label));
        }

        // The route's To label comes from the SNAPSHOT's input labels, not the device's current
        // ones: restore always applies label differences too, so if this restore also renames
        // the target input, the snapshot's label is what it will actually be called once the
        // restore finishes — not whatever the device currently calls it (Finding 4).
        var snapshotInputLabels = snapshot.Inputs.ToDictionary(i => i.N, i => i.Label);
        foreach (var output in snapshot.Outputs.OrderBy(o => o.N))
        {
            var currentInput = state.GetRoute(output.N);
            if (currentInput == output.Input) continue;
            changes.Add(new RestoreChange(
                RestoreChangeKind.Route, output.N,
                state.GetInputLabel(currentInput), snapshotInputLabels[output.Input], output.Input));
        }

        return changes;
    }
}
