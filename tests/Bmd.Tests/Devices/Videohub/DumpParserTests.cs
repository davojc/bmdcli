using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class DumpParserTests
{
    static VideohubState Parse() => DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));

    [Fact]
    public void Parse_DeviceInfo()
    {
        var device = Parse().Device;
        Assert.Equal("Blackmagic Smart Videohub", device.ModelName);
        Assert.Equal("Test Hub", device.FriendlyName);
        Assert.Equal(4, device.VideoInputs);
        Assert.Equal(4, device.VideoOutputs);
        Assert.Equal("2.8", device.ProtocolVersion);
    }

    [Fact]
    public void Labels_AreOneBased_AndPreserveSpaces()
    {
        var state = Parse();
        Assert.Equal("Cam 1", state.GetInputLabel(1));
        Assert.Equal("Cam 4", state.GetInputLabel(4));
        Assert.Equal("Program", state.GetOutputLabel(1));
        Assert.Equal("Aux", state.GetOutputLabel(4));
    }

    [Fact]
    public void Routes_AreOneBased()
    {
        var state = Parse();
        Assert.Equal(4, state.GetRoute(1)); // wire: 0 3
        Assert.Equal(2, state.GetRoute(2)); // wire: 1 1
        Assert.Equal(1, state.GetRoute(3)); // wire: 2 0
        Assert.Equal(3, state.GetRoute(4)); // wire: 3 2
    }

    [Fact]
    public void Locks_MapWireLetters()
    {
        var state = Parse();
        Assert.Equal(LockState.Unlocked, state.GetLock(1));
        Assert.Equal(LockState.Owned, state.GetLock(2));
        Assert.Equal(LockState.Locked, state.GetLock(3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void OutOfRange_Throws(int n)
    {
        var state = Parse();
        Assert.Throws<ArgumentOutOfRangeException>(() => state.GetInputLabel(n));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.GetRoute(n));
    }

    [Fact]
    public void Parse_MissingRequiredBlock_Throws()
    {
        var withoutRouting = BlockReader.ReadBlocks(Fixtures.Dump4x4)
            .Where(b => b.Header != "VIDEO OUTPUT ROUTING").ToArray();
        var ex = Assert.Throws<VideohubProtocolException>(() => DumpParser.Parse(withoutRouting));
        Assert.Contains("VIDEO OUTPUT ROUTING", ex.Message);
    }

    [Fact]
    public void Parse_MissingFriendlyName_IsNull()
    {
        var blocks = BlockReader.ReadBlocks(Fixtures.Dump4x4.Replace("Friendly name: Test Hub\n", ""));
        Assert.Null(DumpParser.Parse(blocks).Device.FriendlyName);
    }

    [Fact]
    public void Parse_RouteValueOutOfRange_Throws()
    {
        var dump = Fixtures.Dump4x4.Replace("0 3\n", "0 7\n");
        var blocks = BlockReader.ReadBlocks(dump);
        var ex = Assert.Throws<VideohubProtocolException>(() => DumpParser.Parse(blocks));
        Assert.Contains("7", ex.Message);
    }

    const string MultiViewConfigBlock =
        "CONFIGURATION:\n" +
        "Layout: 2x2\n" +
        "Output format: 1080i5994\n" +
        "Solo enabled: false\n" +
        "Take Mode: true\n\n";

    [Fact]
    public void Parse_KeepsUnrecognisedBlocks()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4 + MultiViewConfigBlock));

        Assert.True(state.ExtraBlocks.ContainsKey("CONFIGURATION"));
        Assert.Equal(
            ["Layout: 2x2", "Output format: 1080i5994", "Solo enabled: false", "Take Mode: true"],
            state.ExtraBlocks["CONFIGURATION"]);
    }

    [Fact]
    public void Parse_LeavesExtraBlocksEmptyForAPlainVideohub()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));
        Assert.Empty(state.ExtraBlocks);
    }

    [Fact]
    public void Parse_DoesNotTreatPreambleOrEndPreludeAsExtra()
    {
        // Both appear in every real dump and are already accounted for; neither is device-specific.
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4 + MultiViewConfigBlock));
        Assert.DoesNotContain("PROTOCOL PREAMBLE", state.ExtraBlocks.Keys);
        Assert.DoesNotContain("END PRELUDE", state.ExtraBlocks.Keys);
    }

    [Fact]
    public void ApplyUpdate_ReplacesAnExtraBlockWholesale()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4 + MultiViewConfigBlock));
        var update = BlockReader.ReadBlocks("CONFIGURATION:\nLayout: 3x1\n\n")[0];

        var updated = DumpParser.ApplyUpdate(state, update);

        // The device pushes the properties that changed, so the block is replaced, not merged.
        Assert.Equal(["Layout: 3x1"], updated.ExtraBlocks["CONFIGURATION"]);
        Assert.Equal(["Layout: 2x2", "Output format: 1080i5994", "Solo enabled: false", "Take Mode: true"],
            state.ExtraBlocks["CONFIGURATION"]); // original untouched
    }

    [Fact]
    public void ApplyUpdate_AddsAnExtraBlockThatWasNotInTheDump()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));
        var update = BlockReader.ReadBlocks("CONFIGURATION:\nLayout: 2x2\n\n")[0];

        var updated = DumpParser.ApplyUpdate(state, update);

        Assert.Equal(["Layout: 2x2"], updated.ExtraBlocks["CONFIGURATION"]);
    }

    [Theory]
    [InlineData("ACK")]
    [InlineData("NAK")]
    public void ApplyUpdate_NeverStoresAcknowledgements(string header)
    {
        // ACK/NAK are header-only control blocks handled by the client, not device state.
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));
        var updated = DumpParser.ApplyUpdate(state, new ProtocolBlock(header, []));
        Assert.Empty(updated.ExtraBlocks);
    }

    [Fact]
    public void ApplyUpdate_StillLeavesTypedStateAloneForAnExtraBlock()
    {
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));
        var updated = DumpParser.ApplyUpdate(state, BlockReader.ReadBlocks("CONFIGURATION:\nLayout: 2x2\n\n")[0]);

        Assert.Equal(state.GetRoute(1), updated.GetRoute(1));
        Assert.Equal(state.GetInputLabel(1), updated.GetInputLabel(1));
        Assert.Equal(state.GetLock(1), updated.GetLock(1));
    }
}
