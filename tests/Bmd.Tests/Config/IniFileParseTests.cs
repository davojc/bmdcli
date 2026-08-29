using Bmd.Config;

namespace Bmd.Tests.Config;

public class IniFileParseTests
{
    [Fact]
    public void Get_ReturnsValue_ForSimpleSectionAndKey()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n");
        Assert.Equal("10.0.0.5", ini.Get("videohub", "host"));
    }

    [Fact]
    public void Get_IsCaseInsensitive_ForSectionAndKey()
    {
        var ini = IniFile.Parse("[VideoHub]\n\tHost = 10.0.0.5\n");
        Assert.Equal("10.0.0.5", ini.Get("videohub", "host"));
    }

    [Fact]
    public void Get_ReturnsNull_WhenMissing()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n");
        Assert.Null(ini.Get("videohub", "port"));
        Assert.Null(ini.Get("update", "check"));
        Assert.Null(IniFile.Empty().Get("videohub", "host"));
    }

    [Fact]
    public void Parse_IgnoresCommentLines_AndInlineComments()
    {
        var text = "# global bmd config\n[videohub]\n\t; the studio hub\n\thost = 10.0.0.5 # main\n";
        var ini = IniFile.Parse(text);
        Assert.Equal("10.0.0.5", ini.Get("videohub", "host"));
    }

    [Fact]
    public void Parse_QuotedValue_PreservesSpacesAndCommentChars()
    {
        var ini = IniFile.Parse("[videohub]\n\tlabel = \"Studio # A\"\n");
        Assert.Equal("Studio # A", ini.Get("videohub", "label"));
    }

    [Fact]
    public void Parse_SubsectionHeader_IsADistinctSection()
    {
        var text = "[videohub]\n\thost = 10.0.0.5\n[videohub \"studio\"]\n\thost = 10.0.0.9\n";
        var ini = IniFile.Parse(text);
        Assert.Equal("10.0.0.5", ini.Get("videohub", "host"));
        Assert.Equal("10.0.0.9", ini.Get("videohub \"studio\"", "host"));
    }

    [Fact]
    public void Entries_ReturnsAllKeyValues_InFileOrder()
    {
        var text = "[videohub]\n\thost = 10.0.0.5\n\tport = 9990\n[update]\n\tcheck = false\n";
        var entries = IniFile.Parse(text).Entries().ToArray();
        Assert.Equal([("videohub", "host", "10.0.0.5"), ("videohub", "port", "9990"), ("update", "check", "false")], entries);
    }

    [Fact]
    public void Parse_ToleratesCrlfAndBlankLines()
    {
        var ini = IniFile.Parse("\r\n[videohub]\r\n\r\n\thost = 10.0.0.5\r\n");
        Assert.Equal("10.0.0.5", ini.Get("videohub", "host"));
    }
}
