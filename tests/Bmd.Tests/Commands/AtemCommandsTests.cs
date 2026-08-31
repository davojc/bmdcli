using System.Text.Json;
using Bmd.Commands.Atem;
using Bmd.Config;
using Bmd.Tests.Devices.Atem;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class AtemCommandsTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), $"bmd-atem-{Guid.NewGuid():N}");
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public AtemCommandsTests()
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

    AtemCommands Commands(bool interactive = false) =>
        new(() => ConfigStore.Load(Path.Combine(_directory, "config"), _directory), () => interactive);

    /// <summary>Backups land inside the test's own directory rather than the user's real state
    /// directory. Without this a mutation test writes a backup into the machine running it.</summary>
    void UseLocalBackups() =>
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[backup]\ndir = {Path.Combine(_directory, "backups").Replace("\\", "/")}\n");

    // ---- read path --------------------------------------------------------------------

    [Fact]
    public async Task Info_ReportsTheModelAndTopology()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().Info("127.0.0.1", fake.Port));

        var text = _stdout.ToString();
        Assert.Contains("ATEM Television Studio HD", text);
        Assert.Contains("2.30", text);
        Assert.Contains("Auxiliaries:      1", text);
    }

    [Fact]
    public async Task Info_Json_EmitsExactlyOneDocument()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().Info("127.0.0.1", fake.Port, json: true));

        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("ATEM Television Studio HD", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(24, doc.RootElement.GetProperty("sources").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("auxiliaries").GetInt32());
    }

    [Fact]
    public async Task InputList_ShowsOnlyRealInputsByDefault()
    {
        // 24 sources arrive; 8 are physical. Listing all 24 buries the ones an operator named.
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().InputList(host: "127.0.0.1", port: fake.Port));

        var text = _stdout.ToString();
        Assert.Contains("Presenter", text);
        Assert.Contains("Lyrics", text);
        Assert.DoesNotContain("Color Bars", text);
        Assert.DoesNotContain("Live On Screens", text);
    }

    [Fact]
    public async Task InputList_All_AlsoShowsInternalSources()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().InputList(all: true, host: "127.0.0.1", port: fake.Port));

        var text = _stdout.ToString();
        Assert.Contains("Presenter", text);
        Assert.Contains("Color Bars", text);
        Assert.Contains("Live On Screens", text);
    }

    [Fact]
    public async Task InputList_ShowsAnUnnamedInputAsBlankRatherThanInventingAName()
    {
        // Inputs 2 and 3 are genuinely unnamed on the captured switcher.
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().InputList(host: "127.0.0.1", port: fake.Port, json: true));

        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        var second = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == 2);
        Assert.Equal("", second.GetProperty("name").GetString());
        Assert.True(second.GetProperty("external").GetBoolean());
    }

    [Fact]
    public async Task Status_NamesWhatIsOnProgramAndPreview()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().Status("127.0.0.1", fake.Port, json: true));

        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        Assert.Equal(6, doc.RootElement.GetProperty("programSource").GetInt32());
        Assert.Equal("Lyrics", doc.RootElement.GetProperty("programName").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("previewSource").GetInt32());
        Assert.Equal("Presenter", doc.RootElement.GetProperty("previewName").GetString());
    }

    [Fact]
    public async Task AuxList_IsOneBasedAndNamesTheSource()
    {
        // The wire is 0-based; the switcher's own labels count from 1, so bmd does too.
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().AuxList("127.0.0.1", fake.Port, json: true));

        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        var aux = Assert.Single(doc.RootElement.EnumerateArray().ToArray());
        Assert.Equal(1, aux.GetProperty("aux").GetInt32());
        Assert.Equal(6, aux.GetProperty("source").GetInt32());
        Assert.Equal("Lyrics", aux.GetProperty("sourceName").GetString());
    }

    // ---- write path -------------------------------------------------------------------

    [Fact]
    public async Task InputRename_NamesAnInputAndBacksUpFirst()
    {
        UseLocalBackups();
        await using var fake = FakeAtem.Start();

        Assert.Equal(0, await Commands().InputRename(
            "2", "Camera Two", @short: "CAM2", host: "127.0.0.1", port: fake.Port));

        var text = _stdout.ToString();
        Assert.Contains("Camera Two", text);
        Assert.Contains("CAM2", text);
        Assert.Contains("Backup: ", text);
        Assert.DoesNotContain("Backup: skipped", text);
        Assert.Equal("CInL", Assert.Single(fake.Commands).Name);
    }

    [Fact]
    public async Task InputRename_BackupCapturesThePreChangeNames()
    {
        UseLocalBackups();
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().InputRename(
            "2", "Camera Two", host: "127.0.0.1", port: fake.Port, json: true));

        var backup = JsonDocument.Parse(_stdout.ToString().Trim())
            .RootElement.GetProperty("backup").GetString();
        Assert.NotNull(backup);

        // The point of a backup: it holds what the input was called *before*, so the change
        // is reversible. Input 2 was unnamed.
        using var snapshot = JsonDocument.Parse(File.ReadAllText(backup));
        var source = snapshot.RootElement.GetProperty("sources").EnumerateArray()
            .Single(s => s.GetProperty("id").GetInt32() == 2);
        Assert.Equal("", source.GetProperty("name").GetString());
    }

    [Fact]
    public async Task InputRename_NoBackup_SkipsIt()
    {
        UseLocalBackups();
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().InputRename(
            "2", "Camera Two", host: "127.0.0.1", port: fake.Port, noBackup: true));

        Assert.Contains("Backup: skipped", _stdout.ToString());
    }

    [Fact]
    public async Task InputRename_DoesNothingWhenTheNameAlreadyMatches()
    {
        // A no-op must not write to a production switcher, and must not spend a backup slot.
        UseLocalBackups();
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().InputRename(
            "1", "Presenter", host: "127.0.0.1", port: fake.Port));

        Assert.Contains("No change", _stdout.ToString());
        Assert.Empty(fake.Commands);
        Assert.False(Directory.Exists(Path.Combine(_directory, "backups")));
    }

    [Theory]
    [InlineData("This name is far too long to fit", null)]
    [InlineData(null, "TOOLONG")]
    public async Task InputRename_RejectsAnOverlongNameBeforeConnecting(string? name, string? shortName)
    {
        // Exit 2, not 1: this is a usage error, and it is caught without touching the device.
        Assert.Equal(2, await Commands().InputRename("1", name, @short: shortName, host: "127.0.0.1"));
        Assert.Contains("characters or fewer", _stderr.ToString());
    }

    [Fact]
    public async Task InputRename_RequiresAName()
    {
        Assert.Equal(2, await Commands().InputRename("1", host: "127.0.0.1"));
        Assert.Contains("give a new name", _stderr.ToString());
    }

    [Fact]
    public async Task InputRename_RejectsASourceTheSwitcherDoesNotHave()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(1, await Commands().InputRename("99", "Nope", host: "127.0.0.1", port: fake.Port));
        Assert.Contains("no source 99", _stderr.ToString());
    }

    [Fact]
    public async Task AuxSet_RoutesASourceToAnAux()
    {
        UseLocalBackups();
        await using var fake = FakeAtem.Start();

        Assert.Equal(0, await Commands().AuxSet(1, "1", host: "127.0.0.1", port: fake.Port, json: true));

        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        Assert.Equal(1, doc.RootElement.GetProperty("aux").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("source").GetInt32());
        Assert.Equal("Presenter", doc.RootElement.GetProperty("sourceName").GetString());
        Assert.Equal("CAuS", Assert.Single(fake.Commands).Name);
    }

    [Fact]
    public async Task AuxSet_SendsTheZeroBasedIndexForAOneBasedAux()
    {
        // The one conversion in this group. Sending aux 1 as index 1 would route the wrong
        // output on a switcher with more than one, and silently do nothing on this one.
        UseLocalBackups();
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().AuxSet(1, "1", host: "127.0.0.1", port: fake.Port));

        var payload = Assert.Single(fake.Commands).Payload.ToArray();
        Assert.Equal(0, payload[1]);
    }

    [Fact]
    public async Task AuxSet_RejectsAnAuxTheSwitcherDoesNotHave()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(1, await Commands().AuxSet(2, "1", host: "127.0.0.1", port: fake.Port));
        Assert.Contains("aux must be between 1 and 1", _stderr.ToString());
    }

    [Fact]
    public async Task AuxSet_DoesNothingWhenTheAuxAlreadyShowsThatSource()
    {
        UseLocalBackups();
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().AuxSet(1, "6", host: "127.0.0.1", port: fake.Port));

        Assert.Contains("No change", _stdout.ToString());
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public async Task ProgramSet_CutsToASource()
    {
        UseLocalBackups();
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().ProgramSet(
            "1", force: true, host: "127.0.0.1", port: fake.Port, json: true));

        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        Assert.Equal("program", doc.RootElement.GetProperty("bus").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("source").GetInt32());
        Assert.Equal("CPgI", Assert.Single(fake.Commands).Name);
    }

    [Fact]
    public async Task PreviewSet_UsesItsOwnCommand()
    {
        UseLocalBackups();
        await using var fake = FakeAtem.Start();
        Assert.Equal(0, await Commands().PreviewSet("4", host: "127.0.0.1", port: fake.Port));

        Assert.Equal("CPvI", Assert.Single(fake.Commands).Name);
        Assert.Contains("Stage", _stdout.ToString());
    }

    [Fact]
    public async Task ProgramSet_RejectsAMixEffectTheSwitcherDoesNotHave()
    {
        await using var fake = FakeAtem.Start();
        Assert.Equal(1, await Commands().ProgramSet(
            "1", mixEffect: 2, force: true, host: "127.0.0.1", port: fake.Port));
        Assert.Contains("between 1 and 1", _stderr.ToString());
    }

    [Fact]
    public async Task Mutation_ReportsCleanlyWhenTheSwitcherIgnoresTheCommand()
    {
        // A wrong payload layout looks exactly like this: the switcher says nothing at all.
        // It has to be exit 1 with one clean line, never a success nobody verified.
        UseLocalBackups();
        await using var fake = FakeAtem.Start(ignoreCommands: true);

        var exit = await Commands().InputRename(
            "2", "Camera Two", host: "127.0.0.1", port: fake.Port, timeout: 1);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.Contains("did not report the change", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    // ---- configuration and failure ----------------------------------------------------

    [Fact]
    public async Task Commands_ErrorNamesAtemWhenNoHostIsConfigured()
    {
        Assert.Equal(1, await Commands().Info());
        Assert.Contains("bmd config set atem.host", _stderr.ToString());
    }

    [Fact]
    public async Task Commands_ReadTheirHostFromTheAtemSection()
    {
        await using var fake = FakeAtem.Start();
        File.WriteAllText(Path.Combine(_directory, "config"),
            $"[atem]\nhost = 127.0.0.1\nport = {fake.Port}\n");

        Assert.Equal(0, await Commands().Info());
        Assert.Contains("ATEM Television Studio HD", _stdout.ToString());
    }

    [Fact]
    public async Task Commands_UnreachableSwitcher_ExitsOneWithOneCleanLine()
    {
        var exit = await Commands().Info("127.0.0.1", 1, timeout: 2);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
        Assert.Equal("", _stdout.ToString());
    }

    // ---- traps that are now guarded ---------------------------------------------------

    [Fact]
    public async Task ProgramSet_WithoutATerminal_RefusesToCutToAir()
    {
        // A program cut is live the instant it lands. With nothing to confirm at, refusing beats
        // prompting into the void or quietly proceeding — a scheduled job should cut to air only
        // where someone wrote --force and meant it. Exit 2: the fix is to change the command.
        UseLocalBackups();
        await using var fake = FakeAtem.Start();

        var exit = await Commands(interactive: false).ProgramSet("1", host: "127.0.0.1", port: fake.Port);

        Assert.Equal(2, exit);
        Assert.Contains("refusing to cut to air", _stderr.ToString());
        Assert.Contains("--force", _stderr.ToString());
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public async Task PreviewSet_NeedsNoConfirmation()
    {
        // Preview changes nothing on air, so guarding it would be friction without a payoff.
        UseLocalBackups();
        await using var fake = FakeAtem.Start();

        Assert.Equal(0, await Commands(interactive: false).PreviewSet(
            "4", host: "127.0.0.1", port: fake.Port));
        Assert.Equal("CPvI", Assert.Single(fake.Commands).Name);
    }

    [Theory]
    [InlineData("Lyrics")]        // long name
    [InlineData("LYRC")]          // short name
    [InlineData("lyrics")]        // case-insensitive
    [InlineData("6")]             // still the id
    public async Task Commands_AcceptASourceByNameOrId(string source)
    {
        // An ATEM's ids are sparse — inputs 1-8, colour bars 1000, media player 3010 — so
        // requiring an id means a lookup before every command.
        UseLocalBackups();
        await using var fake = FakeAtem.Start();

        Assert.Equal(0, await Commands().AuxSet(1, source, host: "127.0.0.1", port: fake.Port));
        Assert.Contains("No change: aux 1 already shows Lyrics.", _stdout.ToString());
    }

    [Fact]
    public async Task Commands_AcceptAnInternalSourceByName()
    {
        UseLocalBackups();
        await using var fake = FakeAtem.Start();

        Assert.Equal(0, await Commands().AuxSet(1, "Color Bars", host: "127.0.0.1", port: fake.Port));
        Assert.Contains("Aux 1 now shows 1000 (Color Bars)", _stdout.ToString());
    }

    [Fact]
    public async Task Commands_RejectAnUnknownSourceName()
    {
        await using var fake = FakeAtem.Start();

        Assert.Equal(1, await Commands().AuxSet(1, "Nonexistent", host: "127.0.0.1", port: fake.Port));
        Assert.Contains("no source called 'Nonexistent'", _stderr.ToString());
        Assert.Contains("bmd atem input list --all", _stderr.ToString());
    }

    [Fact]
    public async Task InputRename_AcceptsTheInputsCurrentName()
    {
        UseLocalBackups();
        await using var fake = FakeAtem.Start();

        Assert.Equal(0, await Commands().InputRename(
            "Lyrics", "Lyrics Desk", host: "127.0.0.1", port: fake.Port));
        Assert.Contains("Renamed source 6 to 'Lyrics Desk'", _stdout.ToString());
    }
}
