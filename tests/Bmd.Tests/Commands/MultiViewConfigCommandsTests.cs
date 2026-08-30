using System.Text.Json;
using Bmd.Commands.MultiView;
using Bmd.Config;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class MultiViewConfigCommandsTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), $"bmd-mvc-{Guid.NewGuid():N}");
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public MultiViewConfigCommandsTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    MultiViewCommands Commands() =>
        new(() => ConfigStore.Load(Path.Combine(_directory, "config"), _directory));

    static string? Value(FakeVideohub device, string name) =>
        device.Configuration().FirstOrDefault(p => p.Key == name).Value;

    [Fact]
    public async Task Layout_SendsWhateverValueTheUserAsksFor()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Layout("3x1", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("3x1", Value(device, "Layout"));
    }

    [Fact]
    public async Task Layout_DoesNotWhitelistValues()
    {
        // The CONFIGURATION block is undocumented and valid layouts vary by model and firmware,
        // so bmd must not decide what is valid — the device does, by ACK or NAK.
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Layout("7x7", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("7x7", Value(device, "Layout"));
    }

    [Fact]
    public async Task Layout_ReportsADeviceRejectionAsOneErrorLine()
    {
        await using var device = FakeVideohub.StartRejecting(Fixtures.DumpMultiView4);

        var exit = await Commands().Layout("9x9", "127.0.0.1", device.Port);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    [Fact]
    public async Task Format_SetsTheOutputFormat()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        Assert.Equal(0, await Commands().Format("2160p25", "127.0.0.1", device.Port));
        Assert.Equal("2160p25", Value(device, "Output format"));
    }

    [Fact]
    public async Task Solo_WithASourceNumberEnablesSoloAndRoutesTheSoloInput()
    {
        // Solo is two things on the wire: the enable flag, and which source the Solo Input view
        // is fed from. One command does both so the user does not have to know that.
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Solo("3", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("true", Value(device, "Solo enabled"));
        Assert.Equal(2, device.Routes()[4]);   // view 5 (Solo Input) ← 0-based input 2
    }

    [Fact]
    public async Task Solo_Off_DisablesSoloAndLeavesRoutingAlone()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        var before = device.Routes()[4];

        var exit = await Commands().Solo("off", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("false", Value(device, "Solo enabled"));
        Assert.Equal(before, device.Routes()[4]);
    }

    [Fact]
    public async Task Solo_RejectsAnythingThatIsNeitherOffNorASourceNumber()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Solo("banana", "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("off", _stderr.ToString());
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task Solo_RejectsASourceNumberOutsideTheDevice()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Solo("9", "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("1 and 4", _stderr.ToString());
    }

    [Theory]
    [InlineData("borders", "Display border")]
    [InlineData("labels", "Display labels")]
    [InlineData("audio-meters", "Display audio meters")]
    [InlineData("tally", "Display SDI tally")]
    public async Task Show_TogglesEachDisplaySetting(string cliName, string protocolName)
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        Assert.Equal(0, await Commands().Show(cliName, "on", "127.0.0.1", device.Port));
        Assert.Equal("true", Value(device, protocolName));

        Assert.Equal(0, await Commands().Show(cliName, "off", "127.0.0.1", device.Port));
        Assert.Equal("false", Value(device, protocolName));
    }

    [Fact]
    public async Task Show_RejectsAnUnknownSettingWithExitTwoAndListsTheValidOnes()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Show("brightness", "on", "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("borders", _stderr.ToString());
        Assert.Contains("tally", _stderr.ToString());
    }

    [Fact]
    public async Task Show_RejectsAValueThatIsNotOnOrOff()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Show("labels", "maybe", "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("on", _stderr.ToString());
        Assert.Contains("off", _stderr.ToString());
    }

    [Fact]
    public async Task TakeMode_AndWidescreenSd_AreTheirOwnCommandsNotDisplaySettings()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        Assert.Equal(0, await Commands().TakeMode("off", "127.0.0.1", device.Port));
        Assert.Equal("false", Value(device, "Take Mode"));

        Assert.Equal(0, await Commands().WidescreenSd("off", "127.0.0.1", device.Port));
        Assert.Equal("false", Value(device, "Widescreen SD enabled"));
    }

    [Fact]
    public async Task ConfigWrites_Json_EmitOneDocumentWithTheBackupPath()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Layout("4x1", "127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("Layout", doc.RootElement.GetProperty("setting").GetString());
        Assert.Equal("4x1", doc.RootElement.GetProperty("value").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("backup").GetString()));
    }
}
