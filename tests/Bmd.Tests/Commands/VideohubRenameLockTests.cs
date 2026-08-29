using System.Text.Json;
using Bmd.Commands.Videohub;
using Bmd.Config;
using Bmd.Devices.Videohub;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubRenameLockTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");
    string BackupDir => Path.Combine(_root, "backups");

    public VideohubRenameLockTests()
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
    public async Task InputRename_ChangesLabel_ReportsBackup()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().InputRename(2, "Camera Two", host: "127.0.0.1", port: fake.Port));
        Assert.Equal("Camera Two", fake.InputLabels()[1]);
        var text = _stdout.ToString();
        Assert.Contains("Camera Two", text);
        Assert.Contains("Backup:", text);
    }

    [Fact]
    public async Task OutputRename_Json_ReportsPreviousAndNew()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().OutputRename(4, "Aux Feed", host: "127.0.0.1", port: fake.Port, json: true));
        Assert.Equal("Aux Feed", fake.OutputLabels()[3]);
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal("output", root.GetProperty("kind").GetString());
        Assert.Equal(4, root.GetProperty("n").GetInt32());
        Assert.Equal("Aux", root.GetProperty("previousLabel").GetString());
        Assert.Equal("Aux Feed", root.GetProperty("label").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("backup").GetString()));
        Assert.True(File.Exists(root.GetProperty("backup").GetString()));
    }

    [Fact]
    public async Task OutputLock_ThenUnlock_RoundTrips()
    {
        await using var fake = FakeVideohub.Start();

        Assert.Equal(0, await Commands().OutputLock(1, host: "127.0.0.1", port: fake.Port, json: true));
        Assert.Equal('O', fake.Locks()[0]);
        var lockRoot = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(1, lockRoot.GetProperty("output").GetInt32());
        Assert.Equal("owned", lockRoot.GetProperty("lock").GetString());
        Assert.Equal("unlocked", lockRoot.GetProperty("previousLock").GetString());

        _stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await Commands().OutputUnlock(1, host: "127.0.0.1", port: fake.Port, json: true));
        Assert.Equal('U', fake.Locks()[0]);
        var unlockRoot = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal("unlocked", unlockRoot.GetProperty("lock").GetString());
        Assert.Equal("owned", unlockRoot.GetProperty("previousLock").GetString());
    }

    [Fact]
    public async Task OutputUnlock_Force_Succeeds()
    {
        await using var fake = FakeVideohub.Start();
        await using (var other = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, TimeSpan.FromSeconds(5)))
            await other.LockOutputAsync(1);   // simulates a lock held by another controller
        Assert.Equal('O', fake.Locks()[0]);

        Assert.Equal(0, await Commands().OutputUnlock(1, force: true, host: "127.0.0.1", port: fake.Port));
        Assert.Equal('U', fake.Locks()[0]);
        Assert.Contains("Backup:", _stdout.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task OutOfRange_Exit2_DeviceUntouched(int n)
    {
        await using var fake = FakeVideohub.Start();
        var inputLabelsBefore = fake.InputLabels().ToArray();
        var outputLabelsBefore = fake.OutputLabels().ToArray();
        var locksBefore = fake.Locks().ToArray();

        Assert.Equal(2, await Commands().InputRename(n, "New Label", host: "127.0.0.1", port: fake.Port));
        Assert.Equal(2, await Commands().OutputRename(n, "New Label", host: "127.0.0.1", port: fake.Port));
        Assert.Equal(2, await Commands().OutputLock(n, host: "127.0.0.1", port: fake.Port));
        Assert.Equal(2, await Commands().OutputUnlock(n, host: "127.0.0.1", port: fake.Port));

        Assert.Equal(inputLabelsBefore, fake.InputLabels());
        Assert.Equal(outputLabelsBefore, fake.OutputLabels());
        Assert.Equal(locksBefore, fake.Locks());
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("between 1 and", _stderr.ToString());
    }

    [Theory]
    [InlineData("bad\nlabel")]
    [InlineData("bad\rlabel")]
    public async Task LabelWithNewline_Exit2(string label)
    {
        await using var fake = FakeVideohub.Start();

        Assert.Equal(2, await Commands().InputRename(1, label, host: "127.0.0.1", port: fake.Port));
        Assert.Equal(2, await Commands().OutputRename(1, label, host: "127.0.0.1", port: fake.Port));

        Assert.Equal("Cam 1", fake.InputLabels()[0]);
        Assert.Equal("Program", fake.OutputLabels()[0]);
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("error: label must not contain newlines", _stderr.ToString());
    }

    [Fact]
    public async Task Rejected_Exit1_CleanError()
    {
        await using var fake = FakeVideohub.StartRejecting();

        Assert.Equal(1, await Commands().InputRename(1, "New", host: "127.0.0.1", port: fake.Port));
        Assert.Equal("", _stdout.ToString());
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());

        _stderr.GetStringBuilder().Clear();
        Assert.Equal(1, await Commands().OutputLock(1, host: "127.0.0.1", port: fake.Port));
        Assert.Equal("", _stdout.ToString());
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }
}
