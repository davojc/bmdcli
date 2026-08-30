using Bmd.Update;

namespace Bmd.Tests.Update;

public class UpdateCheckCacheTests : IDisposable
{
    readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bmd-cache-{Guid.NewGuid():N}");

    string CacheFile => Path.Combine(_directory, "update-check.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Read_ReturnsNullWhenTheFileDoesNotExist()
    {
        Assert.Null(new UpdateCheckCache(CacheFile).Read());
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var cache = new UpdateCheckCache(CacheFile);
        var checkedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        cache.Write(new UpdateCheckEntry("1.4.1", checkedAt));

        var entry = cache.Read();
        Assert.NotNull(entry);
        Assert.Equal("1.4.1", entry!.LatestVersion);
        Assert.Equal(checkedAt, entry.CheckedAt);
    }

    [Fact]
    public void Write_CreatesTheDirectoryIfItIsMissing()
    {
        Assert.False(Directory.Exists(_directory));
        new UpdateCheckCache(CacheFile).Write(new UpdateCheckEntry("1.0.0", DateTimeOffset.UtcNow));
        Assert.True(File.Exists(CacheFile));
    }

    [Fact]
    public void Read_ReturnsNullForACorruptFileRatherThanThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CacheFile, "this is not json {{{");
        Assert.Null(new UpdateCheckCache(CacheFile).Read());
    }

    [Fact]
    public void Read_ReturnsNullWhenTheJsonIsWellFormedButHasNoVersion()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CacheFile, """{"checkedAt":"2026-08-30T12:00:00+00:00"}""");
        Assert.Null(new UpdateCheckCache(CacheFile).Read());
    }

    [Fact]
    public void Write_SwallowsIoFailuresBecauseAPassiveCheckMustNeverBreakACommand()
    {
        // A path whose parent is an existing *file* cannot be created as a directory.
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "");
        var cache = new UpdateCheckCache(Path.Combine(blocker, "update-check.json"));

        cache.Write(new UpdateCheckEntry("1.0.0", DateTimeOffset.UtcNow)); // must not throw
        Assert.Null(cache.Read());
    }

    [Fact]
    public void IsStale_IsTrueWhenThereIsNoEntry()
    {
        Assert.True(UpdateCheckCache.IsStale(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsStale_IsFalseWithinTwentyFourHours()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var entry = new UpdateCheckEntry("1.0.0", now.AddHours(-23));
        Assert.False(UpdateCheckCache.IsStale(entry, now));
    }

    [Fact]
    public void IsStale_IsTrueAtExactlyTwentyFourHours()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var entry = new UpdateCheckEntry("1.0.0", now.AddHours(-24));
        Assert.True(UpdateCheckCache.IsStale(entry, now));
    }

    [Fact]
    public void IsStale_IsTrueForAnEntryStampedInTheFuture()
    {
        // Clock skew (or a hand-edited file) must not pin the cache as fresh indefinitely.
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var entry = new UpdateCheckEntry("1.0.0", now.AddHours(1));
        Assert.True(UpdateCheckCache.IsStale(entry, now));
    }

    [Fact]
    public void Default_PointsIntoTheOsCacheDirectory()
    {
        var path = UpdateCheckCache.Default().FilePath;
        Assert.Equal("update-check.json", Path.GetFileName(path));
        Assert.StartsWith(Bmd.Config.ConfigPaths.CacheDirectory, path, StringComparison.Ordinal);
    }
}
