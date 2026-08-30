using System.Text.Json;
using Bmd.Commands.MultiView;
using Bmd.Config;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class MultiViewCommandsTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), $"bmd-mv-{Guid.NewGuid():N}");
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public MultiViewCommandsTests()
    {
        Directory.CreateDirectory(_directory);
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

    [Fact]
    public async Task Info_ReportsViewsAndTheCurrentLayout()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Info("127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        var text = _stdout.ToString();
        Assert.Contains("Blackmagic MultiView 4", text);
        Assert.Contains("AV Multiview", text);
        Assert.Contains("2x2", text);
        Assert.Contains("1080i5994", text);
        // Views, not outputs: the noun is the whole reason this group exists.
        Assert.Contains("Views:", text);
        Assert.DoesNotContain("Video outputs:", text);
    }

    [Fact]
    public async Task Info_Json_EmitsOneDocumentWithViewVocabulary()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Info("127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("Blackmagic MultiView 4", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("inputs").GetInt32());
        Assert.Equal(6, doc.RootElement.GetProperty("views").GetInt32());
        Assert.Equal("2x2", doc.RootElement.GetProperty("layout").GetString());
    }

    [Fact]
    public async Task ViewList_ShowsEachViewItsSourceAndLock()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().ViewList("127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        var text = _stdout.ToString();
        Assert.Contains("VIEW", text);
        Assert.DoesNotContain("OUT ", text);
        Assert.Contains("View 1", text);
        Assert.Contains("Solo Input", text);
        Assert.Contains("Confidence", text);
    }

    [Fact]
    public async Task ViewList_Json_UsesViewNotOutputAsTheFieldName()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        await Commands().ViewList("127.0.0.1", device.Port, json: true);

        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        var first = doc.RootElement[0];
        Assert.Equal(1, first.GetProperty("n").GetInt32());
        Assert.Equal("View 1", first.GetProperty("label").GetString());
        Assert.Equal("Stream", first.GetProperty("inputLabel").GetString());
        Assert.Equal(6, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task InputList_ShowsTheFourSources()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().InputList("127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Contains("Stream", _stdout.ToString());
        Assert.Contains("Confidence", _stdout.ToString());
    }

    [Fact]
    public async Task Config_PrintsEveryPropertyTheDeviceReported()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().Config("127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        var text = _stdout.ToString();
        Assert.Contains("Layout", text);
        Assert.Contains("2x2", text);
        Assert.Contains("Take Mode", text);
        Assert.Contains("Display SDI tally", text);
    }

    [Fact]
    public async Task Config_Json_EmitsOneDocumentOfNameValuePairs()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        await Commands().Config("127.0.0.1", device.Port, json: true);

        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal(9, doc.RootElement.GetArrayLength());
        Assert.Equal("Layout", doc.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("2x2", doc.RootElement[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Config_SaysSoWhenTheDeviceSendsNoConfigurationBlock()
    {
        // A plain Videohub answered on the multiview address: report it rather than print nothing.
        await using var device = FakeVideohub.Start(Fixtures.Dump4x4);

        var exit = await Commands().Config("127.0.0.1", device.Port);

        Assert.Equal(1, exit);
        Assert.Contains("no CONFIGURATION", _stderr.ToString());
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task Commands_ReadTheirHostFromTheMultiviewSection()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[multiview]\nhost = 127.0.0.1\nport = {device.Port}\n");

        var exit = await Commands().Info();

        Assert.Equal(0, exit);
        Assert.Contains("Blackmagic MultiView 4", _stdout.ToString());
    }

    [Fact]
    public async Task Commands_ErrorNamesMultiviewWhenNoHostIsConfigured()
    {
        var exit = await Commands().Info();

        Assert.Equal(1, exit);
        Assert.Contains("bmd config set multiview.host", _stderr.ToString());
    }
}
