using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubSnapshotValidationTests
{
    static readonly DateTimeOffset Stamp = new(2026, 8, 29, 10, 12, 0, TimeSpan.Zero);

    static VideohubSnapshot Valid() =>
        VideohubSnapshot.FromState(DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4)), Stamp);

    /// <summary>Serializes a snapshot built from raw parts, bypassing FromState's guarantees.</summary>
    static string Json(SnapshotInput[] inputs, SnapshotOutput[] outputs, int videoInputs = 4, int videoOutputs = 4) =>
        new VideohubSnapshot("Blackmagic Smart Videohub", videoInputs, videoOutputs, Stamp, inputs, outputs).ToJson();

    static SnapshotInput[] Inputs(params int[] ns) => ns.Select(n => new SnapshotInput(n, $"In {n}")).ToArray();
    static SnapshotOutput[] Outputs(params (int N, int Input)[] entries) =>
        entries.Select(e => new SnapshotOutput(e.N, $"Out {e.N}", e.Input)).ToArray();

    [Fact]
    public void FromJson_AcceptsAValidSnapshot()
    {
        var parsed = VideohubSnapshot.FromJson(Valid().ToJson());
        Assert.Equal(4, parsed.Outputs.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void FromJson_InputNumberOutOfRange_Throws(int n)
    {
        var json = Json(Inputs(n, 2, 3, 4), Outputs((1, 1), (2, 2), (3, 3), (4, 4)));
        var ex = Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
        Assert.Contains("input", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(n.ToString(), ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void FromJson_OutputNumberOutOfRange_Throws(int n)
    {
        var json = Json(Inputs(1, 2, 3, 4), Outputs((n, 1), (2, 2), (3, 3), (4, 4)));
        var ex = Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
        Assert.Contains("output", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromJson_DuplicateInputNumbers_Throws()
    {
        var json = Json(Inputs(1, 1, 3, 4), Outputs((1, 1), (2, 2), (3, 3), (4, 4)));
        var ex = Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromJson_DuplicateOutputNumbers_Throws()
    {
        var json = Json(Inputs(1, 2, 3, 4), Outputs((2, 1), (2, 2), (3, 3), (4, 4)));
        var ex = Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void FromJson_RouteTargetOutOfRange_Throws(int input)
    {
        var json = Json(Inputs(1, 2, 3, 4), Outputs((1, input), (2, 2), (3, 3), (4, 4)));
        var ex = Assert.Throws<SnapshotFormatException>(() => VideohubSnapshot.FromJson(json));
        Assert.Contains("output 1", ex.Message);
        Assert.Contains(input.ToString(), ex.Message);
    }

    [Fact]
    public void IncompatibilityWith_MatchingDevice_IsEmpty()
    {
        var device = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4)).Device;
        Assert.Empty(Valid().IncompatibilityWith(device));
    }

    [Fact]
    public void IncompatibilityWith_DifferentModel_ReportsIt()
    {
        var device = new VideohubDeviceInfo("Other Hub", null, 4, 4, "2.8");
        var problems = Valid().IncompatibilityWith(device);
        Assert.Contains(problems, p => p.Contains("Other Hub") && p.Contains("Blackmagic Smart Videohub"));
    }

    [Fact]
    public void IncompatibilityWith_DifferentCounts_ReportsBoth()
    {
        var device = new VideohubDeviceInfo("Blackmagic Smart Videohub", null, 20, 20, "2.8");
        var problems = Valid().IncompatibilityWith(device);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("input"));
        Assert.Contains(problems, p => p.Contains("output"));
    }
}
