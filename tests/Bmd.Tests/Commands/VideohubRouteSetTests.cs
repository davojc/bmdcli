using System.Text.Json;
using Bmd.Commands.Videohub;
using Bmd.Config;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubRouteSetTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");
    string BackupDir => Path.Combine(_root, "backups");

    public VideohubRouteSetTests()
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
    public async Task RouteSet_ChangesDevice_AndReportsBeforeAfter()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().RouteSet(3, 1, host: "127.0.0.1", port: fake.Port));
        Assert.Equal(0, fake.Routes()[2]);
        var text = _stdout.ToString();
        Assert.Contains("output 3", text);
        Assert.Contains("Cam 1", text);
        Assert.Contains("Backup:", text);
    }

    [Fact]
    public async Task RouteSet_Json_ReportsChangeAndBackup()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().RouteSet(3, 1, host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(3, root.GetProperty("output").GetInt32());
        Assert.Equal(1, root.GetProperty("input").GetInt32());
        Assert.Equal("Cam 1", root.GetProperty("inputLabel").GetString());
        Assert.Equal(1, root.GetProperty("previousInput").GetInt32());   // fixture: output 3 ← input 1
        Assert.False(string.IsNullOrEmpty(root.GetProperty("backup").GetString()));
        Assert.True(File.Exists(root.GetProperty("backup").GetString()));
    }

    [Fact]
    public async Task RouteSet_NoBackup_JsonBackupIsNull()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().RouteSet(2, 4, host: "127.0.0.1", port: fake.Port,
            noBackup: true, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("backup").ValueKind);
        Assert.Equal(3, fake.Routes()[1]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 5)]
    public async Task RouteSet_OutOfRange_Exit2_DeviceUntouched(int output, int input)
    {
        await using var fake = FakeVideohub.Start();
        var before = fake.Routes().ToArray();
        Assert.Equal(2, await Commands().RouteSet(output, input, host: "127.0.0.1", port: fake.Port));
        Assert.Equal(before, fake.Routes());
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("between 1 and", _stderr.ToString());
    }

    [Fact]
    public async Task RouteSet_Rejected_Exit1_CleanError()
    {
        await using var fake = FakeVideohub.StartRejecting();
        Assert.Equal(1, await Commands().RouteSet(1, 2, host: "127.0.0.1", port: fake.Port));
        Assert.Equal("", _stdout.ToString());
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }
}
