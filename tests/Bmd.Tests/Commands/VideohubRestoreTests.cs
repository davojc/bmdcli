using System.Text.Json;
using Bmd.Commands.Videohub;
using Bmd.Config;
using Bmd.Devices.Videohub;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubRestoreTests : IDisposable
{
    static readonly DateTimeOffset Stamp = new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);

    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");
    string BackupDir => Path.Combine(_root, "backups");

    public VideohubRestoreTests()
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

    /// <summary>Writes a snapshot file describing the fixture's ORIGINAL state.</summary>
    string SnapshotFile()
    {
        var snapshot = VideohubSnapshot.FromState(
            DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4)),
            new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(WorkDir, "snapshot.json");
        File.WriteAllText(path, snapshot.ToJson());
        return path;
    }

    /// <summary>Writes a snapshot claiming a 20x20 hub, incompatible with the 4x4 fixture.</summary>
    string IncompatibleSnapshotFile()
    {
        var snapshot = new VideohubSnapshot(
            "Blackmagic Smart Videohub", 20, 20, Stamp,
            Enumerable.Range(1, 20).Select(n => new SnapshotInput(n, $"In {n}")).ToArray(),
            Enumerable.Range(1, 20).Select(n => new SnapshotOutput(n, $"Out {n}", n)).ToArray());
        var path = Path.Combine(WorkDir, "incompatible.json");
        File.WriteAllText(path, snapshot.ToJson());
        return path;
    }

    /// <summary>Writes a snapshot that claims 4 inputs/outputs but routes output 1 to input 99 —
    /// invalid regardless of which device it is applied to.</summary>
    string InvalidIndicesSnapshotFile()
    {
        var snapshot = new VideohubSnapshot(
            "Blackmagic Smart Videohub", 4, 4, Stamp,
            [
                new SnapshotInput(1, "Cam 1"), new SnapshotInput(2, "Cam 2"),
                new SnapshotInput(3, "Cam 3"), new SnapshotInput(4, "Cam 4"),
            ],
            [
                new SnapshotOutput(1, "Program", 99), new SnapshotOutput(2, "Preview", 2),
                new SnapshotOutput(3, "Monitor", 3), new SnapshotOutput(4, "Aux", 4),
            ]);
        var path = Path.Combine(WorkDir, "invalid-indices.json");
        File.WriteAllText(path, snapshot.ToJson());
        return path;
    }

    [Fact]
    public async Task Restore_DeviceAlreadyMatches_ReportsNothingToDo_Exit0()
    {
        await using var fake = FakeVideohub.Start();
        var file = SnapshotFile();
        var before = fake.Routes();

        Assert.Equal(0, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port));

        Assert.Contains("nothing to do", _stdout.ToString());
        Assert.Equal(before, fake.Routes());
    }

    [Fact]
    public async Task Restore_AppliesRouteDifference_AndPrintsIt()
    {
        // fixture's output 1 is diverted to input 1 (wire "0 0"); the snapshot (from the
        // unmodified fixture) says output 1 should route to input 4 (wire "0 3").
        var dump = Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0");
        await using var fake = FakeVideohub.Start(dump);
        var file = SnapshotFile();

        Assert.Equal(0, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port));

        Assert.Equal(3, fake.Routes()[0]);
        var text = _stdout.ToString();
        Assert.Contains("route 1:", text);
        Assert.Contains("Backup:", text);
    }

    [Fact]
    public async Task Restore_AppliesLabelDifferences()
    {
        var dump = Fixtures.Dump4x4.Replace("0 Cam 1", "0 Camera One").Replace("3 Aux", "3 Auxiliary");
        await using var fake = FakeVideohub.Start(dump);
        var file = SnapshotFile();

        Assert.Equal(0, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port));

        Assert.Equal("Cam 1", fake.InputLabels()[0]);
        Assert.Equal("Aux", fake.OutputLabels()[3]);
    }

    [Fact]
    public async Task Restore_DryRun_ChangesNothing_AndSaysWould()
    {
        var dump = Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0");
        await using var fake = FakeVideohub.Start(dump);
        var file = SnapshotFile();
        var before = fake.Routes();

        Assert.Equal(0, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port, dryRun: true));

        Assert.Contains("would route 1:", _stdout.ToString());
        Assert.Equal(before, fake.Routes());
        Assert.False(Directory.Exists(BackupDir));
    }

    [Fact]
    public async Task Restore_Json_ReportsCountsAndDetails()
    {
        var dump = Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0");
        await using var fake = FakeVideohub.Start(dump);
        var file = SnapshotFile();

        Assert.Equal(0, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port, json: true));

        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(1, root.GetProperty("changes").GetInt32());
        Assert.Equal(1, root.GetProperty("applied").GetInt32());
        Assert.False(root.GetProperty("dryRun").GetBoolean());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("backup").GetString()));
        var details = root.GetProperty("details");
        Assert.Equal(1, details.GetArrayLength());
        Assert.Equal("route", details[0].GetProperty("kind").GetString());
        Assert.Equal(1, details[0].GetProperty("n").GetInt32());
        // The JSON report must reflect what actually reached the device, not just what the
        // command believes it sent — assert the mutation landed server-side too.
        Assert.Equal(3, fake.Routes()[0]);
    }

    [Fact]
    public async Task Restore_IncompatibleDevice_Exit2_DeviceUntouched()
    {
        await using var fake = FakeVideohub.Start();
        var file = IncompatibleSnapshotFile();
        var routesBefore = fake.Routes();
        var inputLabelsBefore = fake.InputLabels();
        var outputLabelsBefore = fake.OutputLabels();

        Assert.Equal(2, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port));

        Assert.Contains("20", _stderr.ToString());
        Assert.Equal("", _stdout.ToString());
        Assert.Equal(routesBefore, fake.Routes());
        Assert.Equal(inputLabelsBefore, fake.InputLabels());
        Assert.Equal(outputLabelsBefore, fake.OutputLabels());
    }

    [Fact]
    public async Task Restore_InvalidSnapshotIndices_Exit1_BeforeTouchingDevice()
    {
        await using var fake = FakeVideohub.Start();
        var file = InvalidIndicesSnapshotFile();
        var before = fake.Routes();

        Assert.Equal(1, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port));

        Assert.Contains("99", _stderr.ToString());
        Assert.Equal(before, fake.Routes());
    }

    [Fact]
    public async Task Restore_MissingFile_Exit1_CleanError()
    {
        var missing = Path.Combine(WorkDir, "does-not-exist.json");

        Assert.Equal(1, await Commands().Restore(missing, host: "127.0.0.1", port: 9990));

        Assert.Equal("", _stdout.ToString());
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    [Fact]
    public async Task Restore_Rejected_StopsAndReportsProgress()
    {
        var dump = Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0");
        await using var fake = FakeVideohub.StartRejecting(dump);
        var file = SnapshotFile();
        var before = fake.Routes();

        Assert.Equal(1, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port));

        Assert.Contains("error:", _stderr.ToString());
        Assert.Contains("rejected", _stderr.ToString());
        // nothing made it to the device, and nothing was printed as "applied"
        Assert.Equal(before, fake.Routes());
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task Restore_IsIdempotent_SecondRunIsNoOp()
    {
        var dump = Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0");
        await using var fake = FakeVideohub.Start(dump);
        var file = SnapshotFile();

        Assert.Equal(0, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port));
        Assert.Contains("route 1:", _stdout.ToString());

        _stdout.GetStringBuilder().Clear();

        Assert.Equal(0, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port));
        Assert.Contains("nothing to do", _stdout.ToString());
    }

    [Fact]
    public async Task Restore_ResumesAfterPartialFailure_ProgressAccumulatesRatherThanRepeats()
    {
        // Three changes required: an input label, an output label, and a route — computed in
        // that order by RestorePlan (labels before routes, ascending N within each kind).
        var dump = Fixtures.Dump4x4
            .Replace("0 Cam 1", "0 Camera One")
            .Replace("3 Aux", "3 Auxiliary")
            .Replace("VIDEO OUTPUT ROUTING:\n0 3", "VIDEO OUTPUT ROUTING:\n0 0");
        await using var fake = FakeVideohub.StartFailingAfter(1, dump);
        var file = SnapshotFile();

        // Run 1: the connection's allowance covers only the first computed change (the input
        // label rename). The second attempted change (the output label rename) is NAKed, which
        // is fatal to the loop, so the route change is never even attempted.
        Assert.Equal(1, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port));
        Assert.Contains("applied 1 of 3", _stderr.ToString());
        Assert.Equal("Cam 1", fake.InputLabels()[0]);       // landed
        Assert.Equal("Auxiliary", fake.OutputLabels()[3]);  // not yet — still wrong
        Assert.Equal(0, fake.Routes()[0]);                  // not yet — still wrong (wire 0 = input 1)

        _stdout.GetStringBuilder().Clear();
        _stderr.GetStringBuilder().Clear();

        // Run 2: a NEW connection to the SAME fake gets a fresh per-connection allowance.
        // RestorePlan recomputes against the now-partially-restored device, so only the two
        // REMAINING changes are considered — the already-fixed input label is not resent.
        Assert.Equal(1, await Commands().Restore(file, host: "127.0.0.1", port: fake.Port));
        Assert.Contains("applied 1 of 2", _stderr.ToString());
        Assert.Equal("Aux", fake.OutputLabels()[3]);        // landed this run
        Assert.Equal(0, fake.Routes()[0]);                  // still the last remaining change
    }
}
