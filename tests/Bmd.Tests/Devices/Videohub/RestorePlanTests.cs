using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class RestorePlanTests
{
    static readonly DateTimeOffset Stamp = new(2026, 8, 29, 10, 12, 0, TimeSpan.Zero);

    static VideohubState State(string dump = Fixtures.Dump4x4) =>
        DumpParser.Parse(BlockReader.ReadBlocks(dump));

    static VideohubSnapshot Snapshot(string dump = Fixtures.Dump4x4) =>
        VideohubSnapshot.FromState(State(dump), Stamp);

    [Fact]
    public void Compute_IdenticalStateAndSnapshot_IsEmpty()
    {
        Assert.Empty(RestorePlan.Compute(Snapshot(), State()));
    }

    [Fact]
    public void Compute_ChangedRoute_ProducesOneRouteChange()
    {
        // device currently has output 1 ← input 1 (wire "0 0"); snapshot says input 4 (wire "0 3")
        var device = State(Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0"));
        var change = Assert.Single(RestorePlan.Compute(Snapshot(), device));
        Assert.Equal(RestoreChangeKind.Route, change.Kind);
        Assert.Equal(1, change.N);
        Assert.Equal(4, change.TargetInput);
        Assert.Equal("Cam 1", change.From);
        Assert.Equal("Cam 4", change.To);
        Assert.Equal("route 1: Cam 1 → Cam 4", change.Describe());
    }

    [Fact]
    public void Compute_ChangedInputLabel_ProducesOneLabelChange()
    {
        var device = State(Fixtures.Dump4x4.Replace("0 Cam 1", "0 Camera One"));
        var change = Assert.Single(RestorePlan.Compute(Snapshot(), device));
        Assert.Equal(RestoreChangeKind.InputLabel, change.Kind);
        Assert.Equal(1, change.N);
        Assert.Equal("Camera One", change.From);
        Assert.Equal("Cam 1", change.To);
        Assert.Equal("input 1 label: 'Camera One' → 'Cam 1'", change.Describe());
    }

    [Fact]
    public void Compute_ChangedOutputLabel_ProducesOneLabelChange()
    {
        var device = State(Fixtures.Dump4x4.Replace("3 Aux", "3 Auxiliary"));
        var change = Assert.Single(RestorePlan.Compute(Snapshot(), device));
        Assert.Equal(RestoreChangeKind.OutputLabel, change.Kind);
        Assert.Equal(4, change.N);
        Assert.Equal("output 4 label: 'Auxiliary' → 'Aux'", change.Describe());
    }

    [Fact]
    public void Compute_OrdersLabelsBeforeRoutes_AndAscendingWithinKind()
    {
        var device = State(Fixtures.Dump4x4
            .Replace("1 Cam 2", "1 Camera Two")
            .Replace("1 Preview", "1 Prev")
            .Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0"));
        var changes = RestorePlan.Compute(Snapshot(), device);
        Assert.Equal(3, changes.Count);
        Assert.Equal(RestoreChangeKind.InputLabel, changes[0].Kind);
        Assert.Equal(RestoreChangeKind.OutputLabel, changes[1].Kind);
        Assert.Equal(RestoreChangeKind.Route, changes[2].Kind);
    }

    [Fact]
    public void Compute_RouteToRenamedInput_UsesSnapshotsNewLabelForTo()
    {
        // Finding 4: when a restore both renames input 4 and routes an output to it, the route
        // line's To must name the input's NEW (snapshot) label, not its current device label —
        // that is what it will actually be called once the restore finishes applying labels too.
        var device = State(Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0"));
        var baseSnapshot = Snapshot();
        var renamedInputs = baseSnapshot.Inputs
            .Select(i => i.N == 4 ? i with { Label = "Cam 4 New" } : i).ToArray();
        var snapshot = baseSnapshot with { Inputs = renamedInputs };

        var changes = RestorePlan.Compute(snapshot, device);
        var routeChange = Assert.Single(changes, c => c.Kind == RestoreChangeKind.Route);
        Assert.Equal("Cam 1", routeChange.From);      // device's current label for the currently-routed input
        Assert.Equal("Cam 4 New", routeChange.To);    // snapshot's NEW label for the target input
        Assert.Equal("route 1: Cam 1 → Cam 4 New", routeChange.Describe());
    }

    [Fact]
    public void Compute_IsIdempotent_AfterApplyingEverything()
    {
        // simulate a converged device by computing against the snapshot's own source state
        var snapshot = Snapshot();
        Assert.Empty(RestorePlan.Compute(snapshot, State()));
        Assert.Empty(RestorePlan.Compute(snapshot, State()));
    }
}
