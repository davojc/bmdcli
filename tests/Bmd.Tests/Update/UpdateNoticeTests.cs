using Bmd.Config;
using Bmd.Update;

namespace Bmd.Tests.Update;

public class UpdateNoticeTests : IDisposable
{
    readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bmd-notice-{Guid.NewGuid():N}");

    public UpdateNoticeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A config store rooted in this test's own temp directory, so nothing here can read
    /// or write the developer's real config.</summary>
    ConfigStore ConfigWith(string? updateCheck)
    {
        var globalPath = Path.Combine(_directory, "config");
        if (updateCheck is not null)
            File.WriteAllText(globalPath, $"[update]\ncheck = {updateCheck}\n");
        return ConfigStore.Load(globalPath, _directory);
    }

    [Fact]
    public void IsEligible_IsTrueForAnOrdinaryInteractiveCommand()
    {
        Assert.True(UpdateNotice.IsEligible(
            ["videohub", "route", "list"], () => ConfigWith(null), errorIsRedirected: false));
    }

    [Fact]
    public void IsEligible_IsFalseWhenStderrIsNotATty()
    {
        Assert.False(UpdateNotice.IsEligible(
            ["videohub", "route", "list"], () => ConfigWith(null), errorIsRedirected: true));
    }

    [Fact]
    public void IsEligible_IsFalseWhenJsonWasRequested()
    {
        Assert.False(UpdateNotice.IsEligible(
            ["videohub", "route", "list", "--json"], () => ConfigWith(null), errorIsRedirected: false));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("version")]
    public void IsEligible_IsFalseForTheUpdateAndVersionCommandsThemselves(string command)
    {
        Assert.False(UpdateNotice.IsEligible([command], () => ConfigWith(null), errorIsRedirected: false));
    }

    [Fact]
    public void IsEligible_IsFalseWhenUpdateCheckIsDisabledInConfig()
    {
        Assert.False(UpdateNotice.IsEligible(
            ["videohub", "info"], () => ConfigWith("false"), errorIsRedirected: false));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    public void IsEligible_IsTrueForAnythingThatIsNotFalse(string value)
    {
        Assert.True(UpdateNotice.IsEligible(
            ["videohub", "info"], () => ConfigWith(value), errorIsRedirected: false));
    }

    [Fact]
    public void IsEligible_IgnoresCaseWhenReadingFalse()
    {
        Assert.False(UpdateNotice.IsEligible(
            ["videohub", "info"], () => ConfigWith("False"), errorIsRedirected: false));
    }

    [Fact]
    public void Format_ProducesTheExactTwoLineNoticeFromTheSpec()
    {
        var text = UpdateNotice.Format("1.2.0", "1.4.1");
        Assert.Equal(
            "A new release of bmd is available: 1.2.0 → 1.4.1" + Environment.NewLine +
            "Run `bmd update` to upgrade.",
            text);
    }

    [Fact]
    public void Format_ReturnsNullWhenTheCachedVersionIsNotNewer()
    {
        Assert.Null(UpdateNotice.Format("1.4.1", "1.4.1"));
        Assert.Null(UpdateNotice.Format("1.4.1", "1.2.0"));
    }

    [Fact]
    public void Format_ReturnsNullWhenThereIsNoCachedVersion()
    {
        Assert.Null(UpdateNotice.Format("1.2.0", null));
    }

    [Fact]
    public void Format_ReturnsNullRatherThanThrowingOnUnparsableVersions()
    {
        Assert.Null(UpdateNotice.Format("not-a-version", "1.4.1"));
        Assert.Null(UpdateNotice.Format("1.2.0", "not-a-version"));
    }

    [Fact]
    public void Format_TreatsAPreReleaseAsOlderThanItsRelease()
    {
        Assert.NotNull(UpdateNotice.Format("0.1.0-rc.1", "0.1.0"));
    }

    [Fact]
    public void Runner_WritesTheNoticeFromAFreshCacheWithoutFetching()
    {
        var cache = new UpdateCheckCache(Path.Combine(_directory, "update-check.json"));
        var now = DateTimeOffset.UtcNow;
        cache.Write(new UpdateCheckEntry("1.4.1", now.AddHours(-1)));
        var fetched = false;

        var runner = UpdateNoticeRunner.Start(
            ["videohub", "info"], () => ConfigWith(null), cache, "1.2.0", errorIsRedirected: false,
            _ => { fetched = true; return Task.FromResult<string?>("9.9.9"); }, now);

        var writer = new StringWriter();
        runner.WriteIfAny(writer);

        Assert.False(fetched); // cache is fresh; no network
        Assert.Contains("1.2.0 → 1.4.1", writer.ToString());
    }

    [Fact]
    public void Runner_FetchesAndCachesWhenTheCacheIsStale()
    {
        var cache = new UpdateCheckCache(Path.Combine(_directory, "update-check.json"));
        var now = DateTimeOffset.UtcNow;

        var runner = UpdateNoticeRunner.Start(
            ["videohub", "info"], () => ConfigWith(null), cache, "1.2.0", errorIsRedirected: false,
            _ => Task.FromResult<string?>("1.4.1"), now);

        var writer = new StringWriter();
        runner.WriteIfAny(writer);

        Assert.Contains("1.2.0 → 1.4.1", writer.ToString());
        var entry = cache.Read();
        Assert.NotNull(entry);
        Assert.Equal("1.4.1", entry!.LatestVersion);
    }

    [Fact]
    public void Runner_IsSilentWhenTheFetchFails()
    {
        var cache = new UpdateCheckCache(Path.Combine(_directory, "update-check.json"));

        var runner = UpdateNoticeRunner.Start(
            ["videohub", "info"], () => ConfigWith(null), cache, "1.2.0", errorIsRedirected: false,
            _ => Task.FromException<string?>(new UpdateException("network down")),
            DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        runner.WriteIfAny(writer); // must not throw

        Assert.Equal("", writer.ToString());
        Assert.Null(cache.Read()); // a failed check writes nothing
    }

    /// <summary>A writer standing in for a dead stderr handle — e.g. the console detached from a
    /// parent process. Every member throws, matching the doc comment's "never throws" promise
    /// for the actual write, not just the fetch.</summary>
    sealed class FailingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("the handle is invalid");
    }

    [Fact]
    public void Runner_DoesNotThrowWhenTheWriterItselfFails()
    {
        var cache = new UpdateCheckCache(Path.Combine(_directory, "update-check.json"));
        cache.Write(new UpdateCheckEntry("1.4.1", DateTimeOffset.UtcNow.AddHours(-1)));

        var runner = UpdateNoticeRunner.Start(
            ["videohub", "info"], () => ConfigWith(null), cache, "1.2.0", errorIsRedirected: false,
            _ => Task.FromResult<string?>("9.9.9"), DateTimeOffset.UtcNow);

        var exception = Record.Exception(() => runner.WriteIfAny(new FailingWriter()));

        Assert.Null(exception);
    }

    [Fact]
    public void IsEligible_NeverLoadsTheConfigWhenACheaperCheckAlreadyMakesItIneligible()
    {
        var loaded = false;
        ConfigStore Load()
        {
            loaded = true;
            return ConfigWith(null);
        }

        Assert.False(UpdateNotice.IsEligible(
            ["videohub", "info", "--json"], Load, errorIsRedirected: false));
        Assert.False(loaded);

        Assert.False(UpdateNotice.IsEligible(
            ["videohub", "info"], Load, errorIsRedirected: true));
        Assert.False(loaded);

        Assert.False(UpdateNotice.IsEligible(["update"], Load, errorIsRedirected: false));
        Assert.False(loaded);
    }

    [Fact]
    public void IsEligible_LoadsTheConfigOnlyOnceTheCheapChecksHavePassed()
    {
        var loaded = false;
        ConfigStore Load()
        {
            loaded = true;
            return ConfigWith(null);
        }

        Assert.True(UpdateNotice.IsEligible(["videohub", "info"], Load, errorIsRedirected: false));

        Assert.True(loaded);
    }

    [Fact]
    public void Runner_DoesNothingAtAllWhenIneligible()
    {
        var cache = new UpdateCheckCache(Path.Combine(_directory, "update-check.json"));
        cache.Write(new UpdateCheckEntry("9.9.9", DateTimeOffset.UtcNow));
        var fetched = false;

        var runner = UpdateNoticeRunner.Start(
            ["videohub", "info", "--json"], () => ConfigWith(null), cache, "1.2.0", errorIsRedirected: false,
            _ => { fetched = true; return Task.FromResult<string?>("9.9.9"); }, DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        runner.WriteIfAny(writer);

        Assert.False(fetched);
        Assert.Equal("", writer.ToString());
    }
}
