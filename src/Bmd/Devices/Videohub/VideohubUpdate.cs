namespace Bmd.Devices.Videohub;

public enum VideohubUpdateKind { InputLabel, OutputLabel, Route, Lock }

/// <summary>One observed change on a device, in 1-based terms.</summary>
public sealed record VideohubUpdate(VideohubUpdateKind Kind, int N, string From, string To)
{
    public string Describe() => Kind switch
    {
        VideohubUpdateKind.InputLabel => $"input {N} label: '{From}' → '{To}'",
        VideohubUpdateKind.OutputLabel => $"output {N} label: '{From}' → '{To}'",
        VideohubUpdateKind.Route => $"route {N}: {From} → {To}",
        _ => $"output {N} lock: {From} → {To}",
    };

    /// <summary>Every difference between two states, ordered input labels, output labels,
    /// routes, then locks. Differing device sizes compare only the overlapping range.</summary>
    public static IReadOnlyList<VideohubUpdate> Diff(VideohubState before, VideohubState after)
    {
        var updates = new List<VideohubUpdate>();
        var inputs = Math.Min(before.Device.VideoInputs, after.Device.VideoInputs);
        var outputs = Math.Min(before.Device.VideoOutputs, after.Device.VideoOutputs);

        for (var n = 1; n <= inputs; n++)
            if (before.GetInputLabel(n) != after.GetInputLabel(n))
                updates.Add(new VideohubUpdate(
                    VideohubUpdateKind.InputLabel, n, before.GetInputLabel(n), after.GetInputLabel(n)));

        for (var n = 1; n <= outputs; n++)
            if (before.GetOutputLabel(n) != after.GetOutputLabel(n))
                updates.Add(new VideohubUpdate(
                    VideohubUpdateKind.OutputLabel, n, before.GetOutputLabel(n), after.GetOutputLabel(n)));

        for (var n = 1; n <= outputs; n++)
        {
            var wasRoutedTo = before.GetRoute(n);
            var isRoutedTo = after.GetRoute(n);
            if (wasRoutedTo == isRoutedTo) continue;
            updates.Add(new VideohubUpdate(
                VideohubUpdateKind.Route, n,
                LabelOf(before, wasRoutedTo, inputs), LabelOf(after, isRoutedTo, inputs)));
        }

        for (var n = 1; n <= outputs; n++)
            if (before.GetLock(n) != after.GetLock(n))
                updates.Add(new VideohubUpdate(
                    VideohubUpdateKind.Lock, n, Word(before.GetLock(n)), Word(after.GetLock(n))));

        return updates;
    }

    static string LabelOf(VideohubState state, int input, int knownInputs) =>
        input >= 1 && input <= knownInputs ? state.GetInputLabel(input) : $"input {input}";

    /// <summary>The lock words used across the CLI: unlocked, owned, locked.</summary>
    public static string Word(LockState lockState) => lockState switch
    {
        LockState.Owned => "owned",
        LockState.Locked => "locked",
        _ => "unlocked",
    };
}
