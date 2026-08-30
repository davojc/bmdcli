using Bmd.Config;
using Bmd.Devices.Videohub;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Config;

public class BackupStoreTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");
    string BackupDir => Path.Combine(_root, "backups");

    public BackupStoreTests() => Directory.CreateDirectory(WorkDir);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    ConfigStore Config() => ConfigStore.Load(GlobalPath, WorkDir);

    void SetConfig(string key, string value)
    {
        Assert.True(ConfigKey.TryParse(key, out var parsed));
        Config().Set(parsed, value, ConfigScope.Project);
    }

    BackupStore Store()
    {
        SetConfig("backup.dir", BackupDir);
        return BackupStore.FromConfig(Config());
    }

    static VideohubSnapshot Snapshot(DateTimeOffset stamp) =>
        VideohubSnapshot.FromState(
            DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4)), stamp);

    static VideohubSnapshot Snapshot() => Snapshot(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Defaults_AutoOnKeepTen()
    {
        var store = BackupStore.FromConfig(Config());
        Assert.True(store.AutoBackupEnabled);
        Assert.Equal(10, store.Keep);
        Assert.EndsWith("backups", store.RootDirectory.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Config_TurnsAutoOff_AndSetsKeep()
    {
        SetConfig("backup.auto", "false");
        SetConfig("backup.keep", "3");
        var store = BackupStore.FromConfig(Config());
        Assert.False(store.AutoBackupEnabled);
        Assert.Equal(3, store.Keep);
    }

    [Fact]
    public void Config_InvalidKeep_Throws()
    {
        SetConfig("backup.keep", "many");
        var ex = Assert.Throws<ConfigValueException>(() => BackupStore.FromConfig(Config()));
        Assert.Contains("backup.keep", ex.Message);
    }

    [Fact]
    public void Write_CreatesFileUnderDeviceDirectory_AndRoundTrips()
    {
        var path = Store().Write("hub-a", Snapshot());
        Assert.True(File.Exists(path));
        Assert.Equal(Path.Combine(BackupDir, "hub-a"), Path.GetDirectoryName(path));
        var reread = VideohubSnapshot.FromJson(File.ReadAllText(path));
        Assert.Equal(4, reread.VideoOutputs);
        Assert.Equal(4, reread.Outputs[0].Input);
    }

    [Fact]
    public void Write_TwiceInSameSecond_DoesNotOverwrite()
    {
        var store = Store();
        var stamp = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        var first = store.Write("hub-a", Snapshot(stamp));
        var second = store.Write("hub-a", Snapshot(stamp));
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void Write_PrunesOldestBeyondKeep()
    {
        SetConfig("backup.keep", "2");
        SetConfig("backup.dir", BackupDir);
        var store = BackupStore.FromConfig(Config());
        for (var minute = 0; minute < 4; minute++)
            store.Write("hub-a", Snapshot(new DateTimeOffset(2026, 8, 29, 10, minute, 0, TimeSpan.Zero)));

        var remaining = store.List("hub-a");
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, path => Assert.DoesNotContain("100000", Path.GetFileNameWithoutExtension(path)));
    }

    [Fact]
    public void Write_SucceedsEvenWhenPruneCannotDeleteReadOnlyFile()
    {
        SetConfig("backup.keep", "1");
        SetConfig("backup.dir", BackupDir);
        var store = BackupStore.FromConfig(Config());

        var first = store.Write("hub-a", Snapshot(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero)));
        File.SetAttributes(first, FileAttributes.ReadOnly);
        try
        {
            var second = store.Write("hub-a", Snapshot(new DateTimeOffset(2026, 8, 29, 10, 1, 0, TimeSpan.Zero)));
            Assert.True(File.Exists(second));
        }
        finally
        {
            File.SetAttributes(first, FileAttributes.Normal);
        }
    }

    [Fact]
    public void List_NewestFirst_EmptyWhenNone()
    {
        var store = Store();
        Assert.Empty(store.List("hub-a"));
        store.Write("hub-a", Snapshot(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero)));
        store.Write("hub-a", Snapshot(new DateTimeOffset(2026, 8, 29, 11, 0, 0, TimeSpan.Zero)));
        var listed = store.List("hub-a");
        Assert.Equal(2, listed.Count);
        Assert.Contains("110000", Path.GetFileNameWithoutExtension(listed[0]));
    }

    [Fact]
    public void List_SameSecondBurst_OrdersNewestFirstPastSuffixNine()
    {
        SetConfig("backup.keep", "20");
        var store = Store();
        var stamp = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 11; i++) store.Write("hub-a", Snapshot(stamp));

        var listed = store.List("hub-a");
        Assert.Equal(11, listed.Count);
        // bare name is the first (oldest) write of the burst; -11 is the last (newest).
        Assert.EndsWith("20260829-100000-11.json", listed[0]);
        Assert.EndsWith("20260829-100000.json", listed[^1]);
    }

    [Fact]
    public void Prune_SameSecondBurst_KeepsNewestNotOldest()
    {
        SetConfig("backup.keep", "2");
        SetConfig("backup.dir", BackupDir);
        var store = BackupStore.FromConfig(Config());
        var stamp = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        store.Write("hub-a", Snapshot(stamp));
        store.Write("hub-a", Snapshot(stamp));
        store.Write("hub-a", Snapshot(stamp));

        var remaining = store.List("hub-a").Select(Path.GetFileName).ToArray();
        Assert.Equal(2, remaining.Length);
        Assert.Contains("20260829-100000-3.json", remaining);
        Assert.Contains("20260829-100000-2.json", remaining);
        Assert.DoesNotContain("20260829-100000.json", remaining);
    }

    [Fact]
    public void Write_SeparateDevices_DoNotShareDirectories()
    {
        var store = Store();
        store.Write("hub-a", Snapshot());
        store.Write("hub-b", Snapshot());
        Assert.Single(store.List("hub-a"));
        Assert.Single(store.List("hub-b"));
    }

    [Theory]
    [InlineData("10.0.0.5", "Blackmagic Smart Videohub", "10-0-0-5_blackmagic-smart-videohub")]
    [InlineData("hub.local", "Smart Videohub 20 x 20", "hub-local_smart-videohub-20-x-20")]
    public void DeviceKey_IsFilesystemSafe(string host, string model, string expected)
    {
        Assert.Equal(expected, BackupStore.DeviceKey(host, model));
    }
}
