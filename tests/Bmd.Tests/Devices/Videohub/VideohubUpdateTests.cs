using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubUpdateTests
{
    static VideohubState State(string dump = Fixtures.Dump4x4) =>
        DumpParser.Parse(BlockReader.ReadBlocks(dump));

    [Fact]
    public void Diff_IdenticalStates_IsEmpty()
    {
        Assert.Empty(VideohubUpdate.Diff(State(), State()));
    }

    [Fact]
    public void Diff_RouteChange_IsReportedWithLabels()
    {
        var after = State(Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0"));
        var update = Assert.Single(VideohubUpdate.Diff(State(), after));
        Assert.Equal(VideohubUpdateKind.Route, update.Kind);
        Assert.Equal(1, update.N);
        Assert.Equal("Cam 4", update.From);
        Assert.Equal("Cam 1", update.To);
        Assert.Equal("route 1: Cam 4 → Cam 1", update.Describe());
    }

    [Fact]
    public void Diff_InputLabelChange_IsReported()
    {
        var after = State(Fixtures.Dump4x4.Replace("1 Cam 2", "1 Camera Two"));
        var update = Assert.Single(VideohubUpdate.Diff(State(), after));
        Assert.Equal(VideohubUpdateKind.InputLabel, update.Kind);
        Assert.Equal(2, update.N);
        Assert.Equal("input 2 label: 'Cam 2' → 'Camera Two'", update.Describe());
    }

    [Fact]
    public void Diff_OutputLabelChange_IsReported()
    {
        var after = State(Fixtures.Dump4x4.Replace("3 Aux", "3 Aux Feed"));
        var update = Assert.Single(VideohubUpdate.Diff(State(), after));
        Assert.Equal(VideohubUpdateKind.OutputLabel, update.Kind);
        Assert.Equal(4, update.N);
        Assert.Equal("output 4 label: 'Aux' → 'Aux Feed'", update.Describe());
    }

    [Fact]
    public void Diff_LockChange_IsReportedWithWords()
    {
        var after = State(Fixtures.Dump4x4.Replace("VIDEO OUTPUT LOCKS:\n0 U", "VIDEO OUTPUT LOCKS:\n0 O"));
        var update = Assert.Single(VideohubUpdate.Diff(State(), after));
        Assert.Equal(VideohubUpdateKind.Lock, update.Kind);
        Assert.Equal(1, update.N);
        Assert.Equal("output 1 lock: unlocked → owned", update.Describe());
    }

    [Fact]
    public void Diff_MultipleChanges_AreOrderedByKindThenNumber()
    {
        var after = State(Fixtures.Dump4x4
            .Replace("1 Cam 2", "1 Camera Two")
            .Replace("3 Aux", "3 Aux Feed")
            .Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0")
            .Replace("VIDEO OUTPUT LOCKS:\n0 U", "VIDEO OUTPUT LOCKS:\n0 O"));
        var updates = VideohubUpdate.Diff(State(), after);
        Assert.Equal(4, updates.Count);
        Assert.Equal(VideohubUpdateKind.InputLabel, updates[0].Kind);
        Assert.Equal(VideohubUpdateKind.OutputLabel, updates[1].Kind);
        Assert.Equal(VideohubUpdateKind.Route, updates[2].Kind);
        Assert.Equal(VideohubUpdateKind.Lock, updates[3].Kind);
    }

    [Fact]
    public void Diff_DifferentDeviceSizes_ComparesOverlapOnly_DoesNotThrow()
    {
        var smaller = Fixtures.Dump4x4
            .Replace("Video outputs: 4", "Video outputs: 3")
            .Replace("3 Aux\n", "").Replace("3 U\n", "").Replace("3 2\n", "");
        var updates = VideohubUpdate.Diff(State(), State(smaller));
        Assert.Empty(updates);   // the overlapping 1..3 are identical
    }
}
