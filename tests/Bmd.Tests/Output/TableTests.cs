using Bmd.Output;

namespace Bmd.Tests.Output;

[Collection("console")]
public class TableTests : IDisposable
{
    readonly StringWriter _stdout = new();
    readonly TextWriter _origOut = Console.Out;

    public TableTests() => Console.SetOut(_stdout);
    public void Dispose() => Console.SetOut(_origOut);

    [Fact]
    public void Write_AlignsColumnsToWidestCell()
    {
        Table.Write(["N", "LABEL"], [["1", "Cam 1"], ["10", "X"]]);
        var lines = _stdout.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal("N   LABEL", lines[0]);
        Assert.Equal("1   Cam 1", lines[1]);
        Assert.Equal("10  X", lines[2]);
    }

    [Fact]
    public void Write_NoRows_PrintsHeaderOnly()
    {
        Table.Write(["A"], []);
        Assert.Equal("A", _stdout.ToString().TrimEnd());
    }
}
