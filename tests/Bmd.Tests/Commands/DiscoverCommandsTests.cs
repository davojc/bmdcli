using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Bmd.Commands;
using Bmd.Config;
using Bmd.Devices.Discovery;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class DiscoverCommandsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");

    static readonly DiscoveredDevice Hub =
        new("Hub One", "Videohub", "videohub", IPAddress.Parse("192.168.1.10"), 9990);
    static readonly DiscoveredDevice Unsupported =
        new("Switcher", "AtemSwitcher", null, IPAddress.Parse("192.168.1.20"), 9910);

    public DiscoverCommandsTests()
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

    DiscoverCommands Commands(IReadOnlyList<DiscoveredDevice> devices) =>
        new((timeout, ct) => Task.FromResult(devices), () => ConfigStore.Load(GlobalPath, WorkDir));

    DiscoverCommands FailingCommands(Exception exception) =>
        new((timeout, ct) => throw exception, () => ConfigStore.Load(GlobalPath, WorkDir));

    [Fact]
    public async Task Discover_ListsOnlySupportedDevicesByDefault()
    {
        Assert.Equal(0, await Commands([Hub, Unsupported]).Discover());
        var text = _stdout.ToString();
        Assert.Contains("Hub One", text);
        Assert.Contains("videohub", text);
        Assert.DoesNotContain("Switcher", text);
        Assert.Equal("", _stderr.ToString());
    }

    [Fact]
    public async Task Discover_All_ListsUnsupportedOnesToo()
    {
        Assert.Equal(0, await Commands([Hub, Unsupported]).Discover(all: true));
        var text = _stdout.ToString();
        Assert.Contains("Hub One", text);
        Assert.Contains("Switcher", text);
        Assert.Contains("AtemSwitcher", text);   // the raw class, since it has no bmd device type
    }

    [Fact]
    public async Task Discover_Json_IsAnArrayWithCamelCaseFields()
    {
        Assert.Equal(0, await Commands([Hub]).Discover(json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(1, root.GetArrayLength());
        var entry = root[0];
        Assert.Equal("Hub One", entry.GetProperty("name").GetString());
        Assert.Equal("Videohub", entry.GetProperty("deviceClass").GetString());
        Assert.Equal("videohub", entry.GetProperty("deviceType").GetString());
        Assert.Equal("192.168.1.10", entry.GetProperty("address").GetString());
        Assert.Equal(9990, entry.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task Discover_NothingFound_Exit0_EmptyJsonArray()
    {
        Assert.Equal(0, await Commands([]).Discover(json: true));
        Assert.Equal("[]", _stdout.ToString().Trim());
        Assert.Equal("", _stderr.ToString());
    }

    [Fact]
    public async Task Discover_NothingFound_Human_NoteOnStderr_NothingOnStdout()
    {
        Assert.Equal(0, await Commands([]).Discover());
        Assert.Equal("", _stdout.ToString());
        var err = _stderr.ToString();
        Assert.Contains("mDNS", err);
        Assert.Contains("subnet", err);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Discover_NonPositiveTimeout_Exit2(int timeout)
    {
        Assert.Equal(2, await Commands([]).Discover(timeout: timeout));
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("positive", _stderr.ToString());
    }

    [Fact]
    public async Task Discover_SocketFailure_Exit1_CleanError()
    {
        Assert.Equal(1, await FailingCommands(new SocketException()).Discover());
        Assert.Equal("", _stdout.ToString());
        var err = _stderr.ToString();
        Assert.StartsWith("error:", err);
        Assert.DoesNotContain("   at ", err);
    }

    [Fact]
    public async Task Discover_All_SortsByNameRegardlessOfArrivalOrder()
    {
        // mDNS responses arrive in whatever order the network delivers them, not a stable one —
        // the command must impose its own order rather than leaking that non-determinism.
        Assert.Equal(0, await Commands([Unsupported, Hub]).Discover(all: true));
        var text = _stdout.ToString();
        Assert.True(text.IndexOf("Hub One", StringComparison.Ordinal) < text.IndexOf("Switcher", StringComparison.Ordinal));
    }
}
