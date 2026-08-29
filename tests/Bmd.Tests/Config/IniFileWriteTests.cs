using Bmd.Config;

namespace Bmd.Tests.Config;

public class IniFileWriteTests
{
    [Fact]
    public void ToText_RoundTripsUnmodifiedContent()
    {
        var text = "# comment\n[videohub]\n\thost = 10.0.0.5\n\n[update]\n\tcheck = false\n";
        Assert.Equal(text, IniFile.Parse(text).ToText());
    }

    [Fact]
    public void Set_ReplacesExistingKeyInPlace_PreservingComments()
    {
        var ini = IniFile.Parse("# keep me\n[videohub]\n\thost = 10.0.0.5 # old\n\tport = 9990\n");
        ini.Set("videohub", "host", "10.0.0.9");
        Assert.Equal("# keep me\n[videohub]\n\thost = 10.0.0.9\n\tport = 9990\n", ini.ToText());
    }

    [Fact]
    public void Set_AppendsKeyToExistingSection()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n[update]\n\tcheck = false\n");
        ini.Set("videohub", "port", "9991");
        Assert.Equal("[videohub]\n\thost = 10.0.0.5\n\tport = 9991\n[update]\n\tcheck = false\n", ini.ToText());
    }

    [Fact]
    public void Set_AppendsNewSectionAtEnd()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n");
        ini.Set("update", "check", "false");
        Assert.Equal("[videohub]\n\thost = 10.0.0.5\n[update]\n\tcheck = false\n", ini.ToText());
    }

    [Fact]
    public void Set_OnEmptyFile_CreatesSection()
    {
        var ini = IniFile.Empty();
        ini.Set("videohub", "host", "10.0.0.5");
        Assert.Equal("[videohub]\n\thost = 10.0.0.5\n", ini.ToText());
    }

    [Fact]
    public void Set_QuotesValuesContainingCommentCharsOrEdgeWhitespace()
    {
        var ini = IniFile.Empty();
        ini.Set("videohub", "label", "Studio # A");
        Assert.Equal("[videohub]\n\tlabel = \"Studio # A\"\n", ini.ToText());
        Assert.Equal("Studio # A", ini.Get("videohub", "label"));
    }

    [Fact]
    public void Unset_RemovesKeyLine_ReturnsTrue()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n\tport = 9990\n");
        Assert.True(ini.Unset("videohub", "host"));
        Assert.Equal("[videohub]\n\tport = 9990\n", ini.ToText());
    }

    [Fact]
    public void Unset_ReturnsFalse_WhenMissing()
    {
        var ini = IniFile.Parse("[videohub]\n\thost = 10.0.0.5\n");
        Assert.False(ini.Unset("videohub", "port"));
        Assert.False(ini.Unset("update", "check"));
    }
}
