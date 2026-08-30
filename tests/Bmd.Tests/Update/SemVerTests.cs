using Bmd.Update;

namespace Bmd.Tests.Update;

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("V1.2.3", 1, 2, 3, "")]
    [InlineData("0.1.0", 0, 1, 0, "")]
    [InlineData("1.2.3-rc.1", 1, 2, 3, "rc.1")]
    [InlineData("v1.2.3-rc.1", 1, 2, 3, "rc.1")]
    [InlineData("0.0.0-dev", 0, 0, 0, "dev")]
    [InlineData("1.2.3+build.5", 1, 2, 3, "")]
    [InlineData("1.2.3-rc.1+build.5", 1, 2, 3, "rc.1")]
    [InlineData("  1.2.3  ", 1, 2, 3, "")]
    [InlineData("0.0.0", 0, 0, 0, "")]                // a bare "0" identifier is not a leading zero
    [InlineData("1.2.3-rc.0", 1, 2, 3, "rc.0")]
    public void TryParse_AcceptsValidVersions(string raw, int major, int minor, int patch, string pre)
    {
        Assert.True(SemVer.TryParse(raw, out var version));
        Assert.Equal(new SemVer(major, minor, patch, pre), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("abc")]
    [InlineData("1.-2.3")]
    [InlineData("1. 2.3")]
    [InlineData("+1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-rc..1")]
    [InlineData("1.2.3-rc$1")]
    [InlineData("99999999999.0.0")] // overflows int
    [InlineData("01.2.3")]          // semver 2.0.0: no leading zeroes in numeric identifiers
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-rc.01")]
    public void TryParse_RejectsInvalidVersions(string? raw)
    {
        Assert.False(SemVer.TryParse(raw, out _));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("1.2.3", "2.0.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.2.3-rc.1", "1.2.3")]      // a pre-release precedes its release
    [InlineData("1.2.3-rc.1", "1.2.3-rc.2")]
    [InlineData("1.2.3-rc.2", "1.2.3-rc.10")] // numeric identifiers compare numerically
    [InlineData("1.2.3-alpha", "1.2.3-beta")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1.1")] // a longer identifier set wins when equal so far
    [InlineData("1.2.3-1", "1.2.3-alpha")]     // numeric ranks below alphanumeric
    [InlineData("0.1.0-rc.1", "0.1.0")]
    [InlineData("0.0.0-dev", "0.1.0")]
    public void CompareTo_OrdersLowerBeforeHigher(string lower, string higher)
    {
        Assert.True(SemVer.TryParse(lower, out var a));
        Assert.True(SemVer.TryParse(higher, out var b));
        Assert.True(a.CompareTo(b) < 0, $"{lower} should sort before {higher}");
        Assert.True(b.CompareTo(a) > 0, $"{higher} should sort after {lower}");
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+a", "1.2.3+b")] // build metadata takes no part in precedence
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    public void CompareTo_TreatsEquivalentVersionsAsEqual(string left, string right)
    {
        Assert.True(SemVer.TryParse(left, out var a));
        Assert.True(SemVer.TryParse(right, out var b));
        Assert.Equal(0, a.CompareTo(b));
        Assert.Equal(a, b);
    }

    [Fact]
    public void IsPreRelease_IsTrueOnlyWithASuffix()
    {
        Assert.True(SemVer.TryParse("1.2.3-rc.1", out var pre));
        Assert.True(pre.IsPreRelease);
        Assert.True(SemVer.TryParse("1.2.3", out var release));
        Assert.False(release.IsPreRelease);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    [InlineData("1.2.3+meta", "1.2.3")]
    public void ToString_RoundTripsWithoutTheTagPrefixOrBuildMetadata(string raw, string expected)
    {
        Assert.True(SemVer.TryParse(raw, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Fact]
    public void DefaultValue_IsSafeToInspect()
    {
        // A failed TryParse (or any other path that produces default(SemVer)) leaves PreRelease
        // null — a plain record struct default, not one of TryParse's own "" values. Neither
        // member should throw just because the value was never actually parsed.
        var version = default(SemVer);
        Assert.False(version.IsPreRelease);
        Assert.Equal("0.0.0", version.ToString());
    }

    [Fact]
    public void CompareTo_TreatsDefaultValueAsZeroZeroZeroWithNoPreRelease()
    {
        // CompareTo must be as null-safe as IsPreRelease/ToString: default(SemVer) is "0.0.0"
        // with no pre-release, both against itself and against parsed values.
        Assert.Equal(0, default(SemVer).CompareTo(default));

        Assert.True(SemVer.TryParse("0.0.0", out var zero));
        Assert.Equal(0, default(SemVer).CompareTo(zero));
        Assert.Equal(0, zero.CompareTo(default));

        Assert.True(SemVer.TryParse("1.0.0", out var oneOhOh));
        Assert.True(default(SemVer).CompareTo(oneOhOh) < 0);
        Assert.True(oneOhOh.CompareTo(default) > 0);

        Assert.True(SemVer.TryParse("1.2.3-rc.1", out var pre));
        Assert.True(pre.CompareTo(default) > 0);
        Assert.True(default(SemVer).CompareTo(pre) < 0);
    }
}
