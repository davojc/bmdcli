using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class ProtocolBlockTests
{
    [Fact]
    public void ReadBlocks_SplitsOnBlankLines_AndStripsHeaderColon()
    {
        var text = "INPUT LABELS:\n0 Cam 1\n1 Cam 2\n\nVIDEO OUTPUT ROUTING:\n0 1\n\n";
        var blocks = BlockReader.ReadBlocks(text);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("INPUT LABELS", blocks[0].Header);
        Assert.Equal(["0 Cam 1", "1 Cam 2"], blocks[0].Lines);
        Assert.Equal("VIDEO OUTPUT ROUTING", blocks[1].Header);
        Assert.Equal(["0 1"], blocks[1].Lines);
    }

    [Fact]
    public void ReadBlocks_AckNak_AreHeaderOnlyBlocks()
    {
        var blocks = BlockReader.ReadBlocks("ACK\n\nNAK\n\n");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("ACK", blocks[0].Header);
        Assert.Empty(blocks[0].Lines);
        Assert.Equal("NAK", blocks[1].Header);
    }

    [Fact]
    public void ReadBlocks_ToleratesCrlf()
    {
        var blocks = BlockReader.ReadBlocks("VIDEOHUB DEVICE:\r\nModel name: X\r\n\r\n");
        var block = Assert.Single(blocks);
        Assert.Equal("VIDEOHUB DEVICE", block.Header);
        Assert.Equal(["Model name: X"], block.Lines);
    }

    [Fact]
    public void ReadBlocks_IgnoresLeadingBlankLines_AndUnterminatedTrailingBlock()
    {
        var blocks = BlockReader.ReadBlocks("\n\nEND PRELUDE:\n\nPARTIAL:\nno blank after");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("END PRELUDE", blocks[0].Header);
        Assert.Equal("PARTIAL", blocks[1].Header); // trailing block completes at end-of-text
    }

    [Fact]
    public void Accumulator_EmitsBlockOnBlankLine_NullOtherwise()
    {
        var acc = new BlockAccumulator();
        Assert.Null(acc.Add("INPUT LABELS:"));
        Assert.Null(acc.Add("0 Cam 1"));
        var block = acc.Add("");
        Assert.NotNull(block);
        Assert.Equal("INPUT LABELS", block!.Header);
        Assert.Equal(["0 Cam 1"], block.Lines);
        Assert.Null(acc.Add("")); // stray extra blank line between blocks is ignored
    }
}
