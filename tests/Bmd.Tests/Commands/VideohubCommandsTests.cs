using System.Text.Json;
using Bmd.Commands.Videohub;
using Bmd.Config;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubCommandsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");

    public VideohubCommandsTests()
    {
        Directory.CreateDirectory(WorkDir);
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Directory.Delete(_root, recursive: true);
    }

    VideohubCommands Commands() => new(() => ConfigStore.Load(GlobalPath, WorkDir));

    [Fact]
    public async Task Info_Json_ReportsDeviceFields()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().Info(host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal("Blackmagic Smart Videohub", root.GetProperty("modelName").GetString());
        Assert.Equal("Test Hub", root.GetProperty("friendlyName").GetString());
        Assert.Equal("2.8", root.GetProperty("protocolVersion").GetString());
        Assert.Equal(4, root.GetProperty("videoInputs").GetInt32());
        Assert.Equal(4, root.GetProperty("videoOutputs").GetInt32());
    }

    [Fact]
    public async Task Info_Human_PrintsReadableFields()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().Info(host: "127.0.0.1", port: fake.Port));
        var text = _stdout.ToString();
        Assert.Contains("Blackmagic Smart Videohub", text);
        Assert.Contains("4", text);
    }

    [Fact]
    public async Task Info_HostFromConfig()
    {
        await using var fake = FakeVideohub.Start();
        var commands = Commands();
        var store = ConfigStore.Load(GlobalPath, WorkDir);
        Assert.True(ConfigKey.TryParse("videohub.host", out var hostKey));
        Assert.True(ConfigKey.TryParse("videohub.port", out var portKey));
        store.Set(hostKey, "127.0.0.1", global: false);
        store.Set(portKey, fake.Port.ToString(), global: false);
        Assert.Equal(0, await commands.Info(json: true));
    }

    [Fact]
    public async Task Info_NoHostAnywhere_Exit1_WithHint()
    {
        Assert.Equal(1, await Commands().Info());
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("no host configured", _stderr.ToString());
        Assert.Contains("bmd config set videohub.host", _stderr.ToString());
    }

    [Fact]
    public async Task Info_ConnectionRefused_Exit1_CleanError()
    {
        Assert.Equal(1, await Commands().Info(host: "127.0.0.1", port: 1, timeout: 2));
        Assert.Equal("", _stdout.ToString());
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    [Fact]
    public async Task Info_InvalidPortInConfig_Exit1()
    {
        var store = ConfigStore.Load(GlobalPath, WorkDir);
        Assert.True(ConfigKey.TryParse("videohub.host", out var hostKey));
        Assert.True(ConfigKey.TryParse("videohub.port", out var portKey));
        store.Set(hostKey, "127.0.0.1", global: false);
        store.Set(portKey, "not-a-number", global: false);
        Assert.Equal(1, await Commands().Info());
        Assert.Contains("videohub.port", _stderr.ToString());
    }

    [Fact]
    public async Task InputList_Json_OneBasedEntries()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().InputList(host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(4, root.GetArrayLength());
        var first = root[0];
        Assert.Equal(1, first.GetProperty("n").GetInt32());
        Assert.Equal("Cam 1", first.GetProperty("label").GetString());
    }

    [Fact]
    public async Task OutputList_Json_IncludesRouteAndLock()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().OutputList(host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        var first = root[0]; // output 1: wire route 0←3, lock U
        Assert.Equal(1, first.GetProperty("n").GetInt32());
        Assert.Equal("Program", first.GetProperty("label").GetString());
        Assert.Equal(4, first.GetProperty("input").GetInt32());
        Assert.Equal("Cam 4", first.GetProperty("inputLabel").GetString());
        Assert.Equal("unlocked", first.GetProperty("lock").GetString());
        Assert.Equal("owned", root[1].GetProperty("lock").GetString());
        Assert.Equal("locked", root[2].GetProperty("lock").GetString());
    }

    [Fact]
    public async Task RouteList_Json_OneBasedBothSides()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().RouteList(host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        var first = root[0];
        Assert.Equal(1, first.GetProperty("output").GetInt32());
        Assert.Equal("Program", first.GetProperty("outputLabel").GetString());
        Assert.Equal(4, first.GetProperty("input").GetInt32());
        Assert.Equal("Cam 4", first.GetProperty("inputLabel").GetString());
    }

    [Fact]
    public async Task RouteList_Human_IsTable()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().RouteList(host: "127.0.0.1", port: fake.Port));
        var text = _stdout.ToString();
        Assert.Contains("OUT", text);
        Assert.Contains("Program", text);
        Assert.Contains("Cam 4", text);
    }
}
