using Bmd.Config;

namespace Bmd.Tests.Config;

public class ConfigKeyTests
{
    [Theory]
    [InlineData("videohub.host", "videohub", "host")]
    [InlineData("update.check", "update", "check")]
    [InlineData("VideoHub.Host", "VideoHub", "Host")]
    public void TryParse_SplitsOnFirstDot(string raw, string section, string name)
    {
        Assert.True(ConfigKey.TryParse(raw, out var key));
        Assert.Equal(section, key.Section);
        Assert.Equal(name, key.Name);
    }

    [Theory]
    [InlineData("nodot")]
    [InlineData(".host")]
    [InlineData("videohub.")]
    [InlineData("")]
    [InlineData("videohub.a=b")]
    [InlineData("video hub.host")]
    [InlineData("videohub.[x")]
    [InlineData("videohub.a\"b")]
    [InlineData("videohub.a]b")]
    [InlineData("videohub.a#b")]
    [InlineData("videohub.a;b")]
    public void TryParse_RejectsMalformedKeys(string raw)
    {
        Assert.False(ConfigKey.TryParse(raw, out _));
    }
}
