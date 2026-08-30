using System.Text.Json;
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubSnapshotTests
{
    static readonly DateTimeOffset Stamp = new(2026, 8, 29, 10, 12, 0, TimeSpan.Zero);

    static VideohubState State(string dump = Fixtures.Dump4x4) =>
        DumpParser.Parse(BlockReader.ReadBlocks(dump));

    static VideohubSnapshot Snapshot() => VideohubSnapshot.FromState(State(), Stamp);

    [Fact]
    public void FromState_CapturesOneBasedLabelsAndRoutes()
    {
        var snapshot = Snapshot();
        Assert.Equal("Blackmagic Smart Videohub", snapshot.Device);
        Assert.Equal(4, snapshot.VideoInputs);
        Assert.Equal(4, snapshot.VideoOutputs);
        Assert.Equal(Stamp, snapshot.ExportedAt);

        Assert.Equal(4, snapshot.Inputs.Length);
        Assert.Equal(1, snapshot.Inputs[0].N);
        Assert.Equal("Cam 1", snapshot.Inputs[0].Label);

        Assert.Equal(4, snapshot.Outputs.Length);
        Assert.Equal(1, snapshot.Outputs[0].N);
        Assert.Equal("Program", snapshot.Outputs[0].Label);
        Assert.Equal(4, snapshot.Outputs[0].Input); // wire "0 3" → 1-based input 4
        Assert.Equal(2, snapshot.Outputs[1].Input);
    }

    [Fact]
    public void ToJson_UsesCamelCase_AndEndsWithNewline()
    {
        var json = Snapshot().ToJson();
        Assert.EndsWith("\n", json);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("Blackmagic Smart Videohub", root.GetProperty("device").GetString());
        Assert.Equal(4, root.GetProperty("videoInputs").GetInt32());
        Assert.Equal(4, root.GetProperty("videoOutputs").GetInt32());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("exportedAt").GetString()));
        Assert.Equal(1, root.GetProperty("inputs")[0].GetProperty("n").GetInt32());
        Assert.Equal("Cam 1", root.GetProperty("inputs")[0].GetProperty("label").GetString());
        Assert.Equal(4, root.GetProperty("outputs")[0].GetProperty("input").GetInt32());
        Assert.False(root.EnumerateObject().Any(p => p.Name == "locks"), "locks are excluded by spec");
    }

    [Fact]
    public void FromJson_RoundTripsToJson()
    {
        var original = Snapshot();
        var parsed = VideohubSnapshot.FromJson(original.ToJson());
        Assert.Equal(original.Device, parsed.Device);
        Assert.Equal(original.VideoInputs, parsed.VideoInputs);
        Assert.Equal(original.VideoOutputs, parsed.VideoOutputs);
        Assert.Equal(original.ExportedAt, parsed.ExportedAt);
        Assert.Equal(original.Inputs.Select(i => (i.N, i.Label)), parsed.Inputs.Select(i => (i.N, i.Label)));
        Assert.Equal(original.Outputs.Select(o => (o.N, o.Label, o.Input)), parsed.Outputs.Select(o => (o.N, o.Label, o.Input)));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"device\":\"X\",\"videoInputs\":4}")]
    public void FromJson_Malformed_ThrowsSnapshotFormatException(string json)
    {
        Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
    }

    [Fact]
    public void DifferencesFrom_IdenticalState_IsEmpty()
    {
        Assert.Empty(Snapshot().DifferencesFrom(State()));
    }

    [Fact]
    public void DifferencesFrom_ChangedRoute_ReportsIt()
    {
        var changed = State(Fixtures.Dump4x4.Replace("0 3\n1 1", "0 0\n1 1"));
        var differences = Snapshot().DifferencesFrom(changed);
        var line = Assert.Single(differences);
        Assert.Contains("output 1", line);
    }

    [Fact]
    public void DifferencesFrom_ChangedLabel_ReportsIt()
    {
        var changed = State(Fixtures.Dump4x4.Replace("0 Cam 1", "0 Camera One"));
        var differences = Snapshot().DifferencesFrom(changed);
        var line = Assert.Single(differences);
        Assert.Contains("input 1", line);
    }

    [Fact]
    public void DifferencesFrom_DifferentDeviceSize_ReportsMismatch()
    {
        var smaller = Fixtures.Dump4x4
            .Replace("Video outputs: 4", "Video outputs: 3")
            .Replace("3 Aux\n", "")
            .Replace("3 U\n", "")
            .Replace("3 2\n", "");
        var differences = Snapshot().DifferencesFrom(State(smaller));
        Assert.Contains(differences, d => d.Contains("outputs"));
    }

    [Fact]
    public void FromState_OmitsConfigurationUnlessAsked()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.DumpMultiView4));
        var snapshot = VideohubSnapshot.FromState(state, DateTimeOffset.UnixEpoch);
        Assert.Null(snapshot.Configuration);
    }

    [Fact]
    public void FromState_CapturesConfigurationWhenAsked()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.DumpMultiView4));

        var snapshot = VideohubSnapshot.FromState(state, DateTimeOffset.UnixEpoch, includeConfiguration: true);

        Assert.NotNull(snapshot.Configuration);
        Assert.Equal("2x2", snapshot.Configuration!.Layout);
        Assert.Equal("1080i5994", snapshot.Configuration.OutputFormat);
        Assert.True(snapshot.Configuration.TakeMode);
        Assert.False(snapshot.Configuration.DisplayAudioMeters);
    }

    [Fact]
    public void FromState_LeavesConfigurationNullWhenTheDeviceSentNoBlock()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));
        var snapshot = VideohubSnapshot.FromState(state, DateTimeOffset.UnixEpoch, includeConfiguration: true);
        Assert.Null(snapshot.Configuration);
    }

    [Fact]
    public void Configuration_RoundTripsThroughJson()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.DumpMultiView4));
        var snapshot = VideohubSnapshot.FromState(state, DateTimeOffset.UnixEpoch, includeConfiguration: true);

        var restored = VideohubSnapshot.FromJson(snapshot.ToJson());

        Assert.Equal("2x2", restored.Configuration!.Layout);
        Assert.True(restored.Configuration.DisplayLabels);
    }

    [Fact]
    public void ASnapshotWrittenBeforeConfigurationExistedStillLoads()
    {
        // Every backup already on disk was written without this field; none of them may break.
        const string legacy = """
            {"device":"Blackmagic Smart Videohub 40 x 40","videoInputs":2,"videoOutputs":2,
             "exportedAt":"2026-08-29T14:35:12+00:00",
             "inputs":[{"n":1,"label":"A"},{"n":2,"label":"B"}],
             "outputs":[{"n":1,"label":"X","input":1},{"n":2,"label":"Y","input":2}]}
            """;

        var snapshot = VideohubSnapshot.FromJson(legacy);

        Assert.Null(snapshot.Configuration);
        Assert.Equal(2, snapshot.Outputs.Length);
    }
}
