using System.Text.Json;
using Bmd.Commands.MultiView;
using Bmd.Config;
using Bmd.Devices.Videohub;
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

    [Fact]
    public async Task Solo_AgainstADeviceWithNoConfigurationBlock_Exit1_DeviceUntouched()
    {
        // Fix 3: Solo used to route the Solo Input view before ever checking that the device
        // sent a CONFIGURATION block. Against a real Videohub (host misconfigured, typo,
        // swapped device) that would silently re-route a real router output before the later
        // CONFIGURATION send got rejected. The guard must fire before ANY mutation is sent.
        await using var device = FakeVideohub.Start(Fixtures.Dump4x4);
        var before = device.Routes().ToArray();
        var mutationsBefore = device.ReceivedMutationCount();

        var exit = await Commands().Solo("2", "127.0.0.1", device.Port);

        Assert.Equal(1, exit);
        Assert.Contains("probably a Videohub", _stderr.ToString());
        Assert.Equal(before, device.Routes());
        Assert.Equal(mutationsBefore, device.ReceivedMutationCount());
    }

    const string TinyMultiViewDump =
        "PROTOCOL PREAMBLE:\nVersion: 2.8\n\n" +
        "VIDEOHUB DEVICE:\n" +
        "Device present: true\n" +
        "Model name: Tiny MultiView\n" +
        "Video inputs: 1\n" +
        "Video outputs: 1\n\n" +
        "INPUT LABELS:\n0 Only Input\n\n" +
        "OUTPUT LABELS:\n0 Only Output\n\n" +
        "VIDEO OUTPUT LOCKS:\n0 U\n\n" +
        "VIDEO OUTPUT ROUTING:\n0 0\n\n" +
        "CONFIGURATION:\nLayout: 1x1\n\n" +
        "END PRELUDE:\n\n";

    [Fact]
    public async Task Solo_AgainstADeviceWithFewerThanTwoOutputs_Exit1_DeviceUntouched()
    {
        // Fix 3: soloView = VideoOutputs - 1 must never reach 0 (or negative) — that would throw
        // ArgumentOutOfRangeException past RunCatchingAsync's filter, the same class of bug as
        // the newline label (Fix 2). A device reporting a CONFIGURATION block but only one
        // output is contrived, but the guard must still hold rather than crash.
        await using var device = FakeVideohub.Start(TinyMultiViewDump);
        var before = device.Routes().ToArray();

        var exit = await Commands().Solo("1", "127.0.0.1", device.Port);

        Assert.Equal(1, exit);
        Assert.DoesNotContain("   at ", _stderr.ToString());
        Assert.Equal(before, device.Routes());
    }

    [Fact]
    public async Task Solo_Json_ReportsTheInputAndItsLabel()
    {
        // Fix 4: solo --json used to report only "Solo enabled": "true"/"value", losing the
        // input number and label the human output prints — an agent would need a follow-up
        // query to learn what was actually routed.
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Solo("3", "127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("input").GetInt32());
        Assert.Equal("Presenter", doc.RootElement.GetProperty("inputLabel").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("backup").GetString()));
    }

    [Fact]
    public async Task Solo_Off_Json_ReportsNullInput()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Solo("off", "127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("input").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("inputLabel").ValueKind);
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
    public async Task Layout_BackupCapturesThePreChangeConfiguration()
    {
        // Fix 1: the automatic pre-change backup must include CONFIGURATION — otherwise a
        // restore from it leaves layout (and every other mutated setting) untouched, and the
        // "mutations back up first" guarantee is hollow for exactly the commands whose
        // pre-change state IS the configuration.
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Layout("4x1", "127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        var backupPath = doc.RootElement.GetProperty("backup").GetString();
        Assert.False(string.IsNullOrWhiteSpace(backupPath));

        var backup = VideohubSnapshot.FromJson(File.ReadAllText(backupPath!));
        Assert.NotNull(backup.Configuration);
        // Pre-change: the device's Layout was "2x2" before this Layout("4x1") call — the backup
        // must have captured that, not the value the device now holds.
        Assert.Equal("2x2", backup.Configuration!.Layout);
        Assert.Equal("4x1", Value(device, "Layout"));
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
