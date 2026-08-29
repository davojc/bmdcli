using Bmd.Commands.Videohub;
using Bmd.Config;
using Bmd.Devices.Videohub;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubBackupTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");
    string BackupDir => Path.Combine(_root, "backups");

    public VideohubBackupTests()
    {
        Directory.CreateDirectory(WorkDir);
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        SetConfig("backup.dir", BackupDir);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Directory.Delete(_root, recursive: true);
    }

    void SetConfig(string key, string value)
    {
        Assert.True(ConfigKey.TryParse(key, out var parsed));
        ConfigStore.Load(GlobalPath, WorkDir).Set(parsed, value, global: false);
    }

    VideohubCommands Commands() => new(() => ConfigStore.Load(GlobalPath, WorkDir));

    [Fact]
    public async Task Backup_WrittenBeforeAction_AndPathHandedToIt()
    {
        await using var fake = FakeVideohub.Start();
        string? seen = null;
        Assert.Equal(0, await Commands().BackupProbeAsync("127.0.0.1", fake.Port, noBackup: false,
            (_, path) => { seen = path; return Task.FromResult(0); }));
        Assert.NotNull(seen);
        Assert.True(File.Exists(seen));
        var snapshot = VideohubSnapshot.FromJson(File.ReadAllText(seen!));
        Assert.Equal(4, snapshot.Outputs.Length);
        Assert.Equal(4, snapshot.Outputs[0].Input);   // pre-change state
    }

    [Fact]
    public async Task NoBackupFlag_SkipsBackup_PathIsNull()
    {
        await using var fake = FakeVideohub.Start();
        string? seen = "not null";
        Assert.Equal(0, await Commands().BackupProbeAsync("127.0.0.1", fake.Port, noBackup: true,
            (_, path) => { seen = path; return Task.FromResult(0); }));
        Assert.Null(seen);
        Assert.False(Directory.Exists(BackupDir));
    }

    [Fact]
    public async Task BackupAutoFalse_SkipsBackup()
    {
        SetConfig("backup.auto", "false");
        await using var fake = FakeVideohub.Start();
        string? seen = "not null";
        Assert.Equal(0, await Commands().BackupProbeAsync("127.0.0.1", fake.Port, noBackup: false,
            (_, path) => { seen = path; return Task.FromResult(0); }));
        Assert.Null(seen);
    }

    [Fact]
    public async Task BackupFailure_AbortsBeforeAction_Exit1()
    {
        // a FILE where the backup directory must be → Write cannot create the device directory
        Directory.CreateDirectory(Path.GetDirectoryName(BackupDir)!);
        File.WriteAllText(BackupDir, "not a directory");
        await using var fake = FakeVideohub.Start();
        var ran = false;
        Assert.Equal(1, await Commands().BackupProbeAsync("127.0.0.1", fake.Port, noBackup: false,
            (_, _) => { ran = true; return Task.FromResult(0); }));
        Assert.False(ran, "the action must not run when the backup fails");
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    [Fact]
    public async Task BackupsAreDeviceKeyed()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().BackupProbeAsync("127.0.0.1", fake.Port, noBackup: false,
            (_, _) => Task.FromResult(0)));
        var deviceDirectory = Assert.Single(Directory.GetDirectories(BackupDir));
        Assert.Contains("127-0-0-1", Path.GetFileName(deviceDirectory));
        Assert.Contains("videohub", Path.GetFileName(deviceDirectory));
    }
}
