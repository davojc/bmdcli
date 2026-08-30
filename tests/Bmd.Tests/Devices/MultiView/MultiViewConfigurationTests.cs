using Bmd.Devices.MultiView;

namespace Bmd.Tests.Devices.MultiView;

public class MultiViewConfigurationTests
{
    static readonly string[] RealBlock =
    [
        "Layout: 2x2",
        "Output format: 1080i5994",
        "Solo enabled: false",
        "Widescreen SD enabled: true",
        "Display border: true",
        "Display labels: true",
        "Display audio meters: false",
        "Display SDI tally: false",
        "Take Mode: true",
    ];

    [Fact]
    public void FromLines_ReadsEveryPropertyTheRealDeviceReports()
    {
        var config = MultiViewConfiguration.FromLines(RealBlock);

        Assert.Equal("2x2", config.Layout);
        Assert.Equal("1080i5994", config.OutputFormat);
        Assert.False(config.SoloEnabled);
        Assert.True(config.WidescreenSdEnabled);
        Assert.True(config.DisplayBorder);
        Assert.True(config.DisplayLabels);
        Assert.False(config.DisplayAudioMeters);
        Assert.False(config.DisplaySdiTally);
        Assert.True(config.TakeMode);
    }

    [Fact]
    public void FromLines_KeepsEveryPropertyVerbatimInRawIncludingUnknownOnes()
    {
        // The block is undocumented and varies by model; `bmd multiview config` shows everything
        // the device sent, not only the properties this type happens to know about.
        var config = MultiViewConfiguration.FromLines([.. RealBlock, "Some Future Setting: 42"]);

        Assert.Equal(10, config.Raw.Count);
        Assert.Contains(config.Raw, p => p.Key == "Some Future Setting" && p.Value == "42");
        Assert.Equal("Layout", config.Raw[0].Key); // order preserved as received
    }

    [Fact]
    public void FromLines_TreatsAnAbsentPropertyAsUnknownRatherThanFalse()
    {
        var config = MultiViewConfiguration.FromLines(["Layout: 2x2"]);

        Assert.Equal("2x2", config.Layout);
        Assert.Null(config.SoloEnabled);
        Assert.Null(config.TakeMode);
        Assert.Null(config.OutputFormat);
    }

    [Fact]
    public void FromLines_MatchesPropertyNamesCaseInsensitively()
    {
        // The real device sends "Take Mode" with a capital M while every other property is
        // sentence case; matching exactly would silently drop it.
        var config = MultiViewConfiguration.FromLines(["take mode: true", "LAYOUT: 4x1"]);

        Assert.True(config.TakeMode);
        Assert.Equal("4x1", config.Layout);
    }

    [Fact]
    public void FromLines_IgnoresLinesWithNoColon()
    {
        var config = MultiViewConfiguration.FromLines(["Layout: 2x2", "nonsense", ""]);

        Assert.Equal("2x2", config.Layout);
        Assert.Single(config.Raw);
    }

    [Fact]
    public void FromLines_KeepsAValueContainingAColon()
    {
        var config = MultiViewConfiguration.FromLines(["Output format: 1080i59:94"]);
        Assert.Equal("1080i59:94", config.OutputFormat);
    }

    [Fact]
    public void Empty_HasNothingSet()
    {
        Assert.Null(MultiViewConfiguration.Empty.Layout);
        Assert.Empty(MultiViewConfiguration.Empty.Raw);
    }

    [Fact]
    public void LinesFor_ProducesASinglePropertyBlockBody()
    {
        Assert.Equal(["Layout: 3x1"], MultiViewConfiguration.LinesFor("Layout", "3x1"));
    }

    [Theory]
    [InlineData("borders", "Display border")]
    [InlineData("labels", "Display labels")]
    [InlineData("audio-meters", "Display audio meters")]
    [InlineData("tally", "Display SDI tally")]
    [InlineData("take-mode", "Take Mode")]
    [InlineData("widescreen-sd", "Widescreen SD enabled")]
    [InlineData("solo", "Solo enabled")]
    [InlineData("layout", "Layout")]
    [InlineData("format", "Output format")]
    public void ProtocolNameFor_MapsCliNamesToProtocolProperties(string cli, string expected)
    {
        Assert.Equal(expected, MultiViewConfiguration.ProtocolNameFor(cli));
    }

    [Fact]
    public void ProtocolNameFor_ReturnsNullForAnUnknownName()
    {
        Assert.Null(MultiViewConfiguration.ProtocolNameFor("brightness"));
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData("true", true)]
    [InlineData("off", false)]
    [InlineData("false", false)]
    public void TryParseOnOff_AcceptsTheDocumentedSpellings(string text, bool expected)
    {
        Assert.True(MultiViewConfiguration.TryParseOnOff(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("maybe")]
    public void TryParseOnOff_RejectsAnythingElse(string text)
    {
        Assert.False(MultiViewConfiguration.TryParseOnOff(text, out _));
    }
}
