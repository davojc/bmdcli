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

    [Fact]
    public async Task Info_DegradesGracefullyWhenTheDeviceSendsNoConfigurationBlock()
    {
        // A plain Videohub answered on the multiview address: Info must still succeed,
        // just without the layout/output-format lines that come from CONFIGURATION.
        await using var device = FakeVideohub.Start(Fixtures.Dump4x4);

        var exit = await Commands().Info("127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        var text = _stdout.ToString();
        Assert.Contains("Model:", text);
        Assert.DoesNotContain("Layout:", text);
        Assert.DoesNotContain("Output format:", text);
    }

    [Fact]
    public async Task ViewSet_PutsASourceInAViewAndReportsTheChange()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewSet(1, 3, "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal(2, device.Routes()[0]);          // 1-based view 1 → 0-based input index 2
        var text = _stdout.ToString();
        Assert.Contains("View 1", text);
        Assert.Contains("Presenter", text);
    }

    [Fact]
    public async Task ViewSet_Json_ReportsTheViewAndItsBackupPath()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewSet(2, 4, "127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal(2, doc.RootElement.GetProperty("view").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("input").GetInt32());
        Assert.Equal("Confidence", doc.RootElement.GetProperty("inputLabel").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("backup").GetString()));
    }

    [Fact]
    public async Task ViewSet_RejectsAViewNumberOutsideTheDeviceWithExitTwo()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().ViewSet(7, 1, "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("view", _stderr.ToString());
        Assert.Contains("1", _stderr.ToString());
        Assert.Contains("6", _stderr.ToString());
    }

    [Fact]
    public async Task ViewSet_RejectsASourceNumberOutsideTheDeviceWithExitTwo()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);

        var exit = await Commands().ViewSet(1, 9, "127.0.0.1", device.Port);

        Assert.Equal(2, exit);
        Assert.Contains("input", _stderr.ToString());
    }

    [Fact]
    public async Task ViewRename_ChangesTheWindowLabel()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewRename(1, "Programme", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("Programme", device.OutputLabels()[0]);
    }

    [Fact]
    public async Task InputRename_ChangesTheSourceLabel()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().InputRename(2, "Desk Feed", "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("Desk Feed", device.InputLabels()[1]);
    }

    [Fact]
    public async Task ViewLock_ThenUnlock_RoundTrips()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        Assert.Equal(0, await Commands().ViewLock(3, "127.0.0.1", device.Port));
        Assert.Equal('O', device.Locks()[2]);

        Assert.Equal(0, await Commands().ViewUnlock(3, host: "127.0.0.1", port: device.Port));
        Assert.Equal('U', device.Locks()[2]);
    }

    [Fact]
    public async Task Mutations_ReportADeviceRejectionAsOneErrorLine()
    {
        await using var device = FakeVideohub.StartRejecting(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewSet(1, 2, "127.0.0.1", device.Port);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    [Fact]
    public async Task ViewSet_HumanMode_ReportsTheBackupLine()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewSet(1, 3, "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Matches(@"Backup: .+", _stdout.ToString());
        Assert.DoesNotContain("Backup: skipped", _stdout.ToString());
    }

    [Fact]
    public async Task ViewRename_Json_ReportsTheBackupPath()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewRename(1, "Programme", "127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("backup").GetString()));
    }

    [Fact]
    public async Task ViewLock_Json_ReportsTheBackupPath()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {_directory.Replace("\\", "/")}/backups\n");

        var exit = await Commands().ViewLock(3, "127.0.0.1", device.Port, json: true);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("backup").GetString()));
    }

    [Fact]
    public async Task Export_WritesConfigurationAlongsideLabelsAndRouting()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        var file = Path.Combine(_directory, "show.json");

        var exit = await Commands().Export(file, "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("2x2", doc.RootElement.GetProperty("configuration").GetProperty("layout").GetString());
        Assert.Equal(6, doc.RootElement.GetProperty("outputs").GetArrayLength());
    }

    [Fact]
    public async Task Export_DeviceConfigurationChangesEveryAttempt_Exit1_ListsDifferences()
    {
        // fake serves a different Layout on each connection: labels and routing never move, so
        // without configuration in the verify comparison this would report "verified" wrongly.
        await using var device = FakeVideohub.StartChangingConfiguration();
        var file = Path.Combine(_directory, "show.json");

        var exit = await Commands().Export(file, "127.0.0.1", device.Port);

        Assert.Equal(1, exit);
        Assert.Contains("configuration", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    [Fact]
    public async Task Restore_DryRun_ReportsTheConfigurationItWouldChangeAndChangesNothing()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        var file = Path.Combine(_directory, "show.json");
        Assert.Equal(0, await Commands().Export(file, "127.0.0.1", device.Port));

        var edited = File.ReadAllText(file).Replace("\"layout\": \"2x2\"", "\"layout\": \"4x1\"");
        File.WriteAllText(file, edited);
        _stdout.GetStringBuilder().Clear();

        var exit = await Commands().Restore(file, "127.0.0.1", device.Port, dryRun: true);

        Assert.Equal(0, exit);
        Assert.Contains("4x1", _stdout.ToString());
        Assert.Equal("2x2", device.Configuration().First(p => p.Key == "Layout").Value);
    }

    [Fact]
    public async Task Restore_AppliesConfigurationDifferencesOnly()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        var file = Path.Combine(_directory, "show.json");
        Assert.Equal(0, await Commands().Export(file, "127.0.0.1", device.Port));
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"layout\": \"2x2\"", "\"layout\": \"4x1\""));
        var before = device.ReceivedMutationCount();

        var exit = await Commands().Restore(file, "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("4x1", device.Configuration().First(p => p.Key == "Layout").Value);
        Assert.Equal("1080i5994", device.Configuration().First(p => p.Key == "Output format").Value);
        // The load-bearing assertion: only Layout genuinely differs, so exactly one CONFIGURATION
        // block is sent — not nine. A regression to whole-record diffing would re-send every
        // property (both this device's Configuration() values above would still read correctly,
        // since re-sending an unchanged value is a no-op on the wire) and only this count would catch it.
        Assert.Equal(1, device.ReceivedMutationCount() - before);
    }

    [Fact]
    public async Task Restore_LeavesConfigurationAloneWhenTheSnapshotHasNone()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        var file = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(file, """
            {"device":"Blackmagic MultiView 4","videoInputs":4,"videoOutputs":6,
             "exportedAt":"2026-08-29T14:35:12+00:00",
             "inputs":[{"n":1,"label":"Stream"},{"n":2,"label":"Screens"},
                       {"n":3,"label":"Presenter"},{"n":4,"label":"Confidence"}],
             "outputs":[{"n":1,"label":"View 1","input":1},{"n":2,"label":"View 2","input":2},
                        {"n":3,"label":"View 3","input":3},{"n":4,"label":"View 4","input":4},
                        {"n":5,"label":"Solo Input","input":3},{"n":6,"label":"Audio Input","input":1}]}
            """);

        var exit = await Commands().Restore(file, "127.0.0.1", device.Port);

        Assert.Equal(0, exit);
        Assert.Equal("2x2", device.Configuration().First(p => p.Key == "Layout").Value);
    }

    [Fact]
    public async Task Watch_StreamsAViewChangeAndStopsOnCancellation()
    {
        await using var device = FakeVideohub.Start(Fixtures.DumpMultiView4);
        using var cts = new CancellationTokenSource();

        var watching = Commands().Watch("127.0.0.1", device.Port, cancellationToken: cts.Token);
        await Task.Delay(150, CancellationToken.None);
        // Wire output index 0 is 1-based view 1 ("View 1"); PushRouteAsync takes wire (0-based)
        // indices, not the 1-based numbers the rest of this file uses everywhere else.
        await device.PushRouteAsync(0, 3);
        await Task.Delay(150, CancellationToken.None);
        await cts.CancelAsync();

        var exit = await watching.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("View 1", _stdout.ToString());
    }
}
