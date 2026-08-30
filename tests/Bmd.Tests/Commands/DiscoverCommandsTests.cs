using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
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
    readonly TextReader _origIn = Console.In;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");
    string LocalConfigPath => Path.Combine(WorkDir, ".bmdconfig");

    static readonly DiscoveredDevice Hub =
        new("Hub One", "Videohub", "videohub", IPAddress.Parse("192.168.1.10"), 9990);
    static readonly DiscoveredDevice HubTwo =
        new("Hub Two", "Videohub", "videohub", IPAddress.Parse("192.168.1.11"), 9990);
    static readonly DiscoveredDevice HubOddPort =
        new("Hub Odd Port", "Videohub", "videohub", IPAddress.Parse("192.168.1.12"), 9991);
    static readonly DiscoveredDevice Unsupported =
        new("Switcher", "AtemSwitcher", null, IPAddress.Parse("192.168.1.20"), 9910);
    static readonly DiscoveredDevice HubWithTxt =
        new("Hub One", "Videohub", "videohub", IPAddress.Parse("192.168.1.10"), 9990,
            ["class=Videohub", "name=Hub One", "nic=1"]);
    static readonly DiscoveredDevice NoTxtBridge =
        new("Streaming Bridge", "", null, IPAddress.Parse("192.168.1.30"), 9977);
    // "evil=" carries an embedded newline plus an ANSI escape sequence (\u001b[31m ... \u001b[0m)
    // that would recolor terminal output — proof that the human table sanitizes what it prints
    // rather than passing raw network bytes straight to the terminal.
    static readonly DiscoveredDevice HubWithHostileTxt =
        new("Hub One", "Videohub", "videohub", IPAddress.Parse("192.168.1.10"), 9990,
            ["class=Videohub", "evil=line1\nline2\u001b[31mFAKE ROW\u001b[0m"]);

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
        Console.SetIn(_origIn);
        Directory.Delete(_root, recursive: true);
    }

    // `interactive` defaults true so --add tests exercise the prompt/selection logic without
    // tripping the non-interactive guard — Console.IsInputRedirected is true under the test
    // runner regardless, which is exactly why that guard has to be injectable at all.
    DiscoverCommands Commands(IReadOnlyList<DiscoveredDevice> devices, bool interactive = true) =>
        new((timeout, ct) => Task.FromResult(devices), () => ConfigStore.Load(GlobalPath, WorkDir), () => interactive);

    DiscoverCommands FailingCommands(Exception exception) =>
        new((timeout, ct) => throw exception, () => ConfigStore.Load(GlobalPath, WorkDir));

    static void SetIn(string input) => Console.SetIn(new StringReader(input));

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
    public async Task Discover_Json_All_IncludesTxtArray()
    {
        Assert.Equal(0, await Commands([HubWithTxt]).Discover(all: true, json: true));
        var text = _stdout.ToString();
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)); // one JSON document
        var root = JsonDocument.Parse(text).RootElement;
        var entry = root[0];
        var txt = entry.GetProperty("txt");
        Assert.Equal(JsonValueKind.Array, txt.ValueKind);
        Assert.Equal(["class=Videohub", "name=Hub One", "nic=1"], txt.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Discover_Json_Default_AlsoIncludesTxtArray()
    {
        // The field is unconditional — present in the default (non --all) JSON shape too, not
        // gated behind --all, so a script never has to branch its parsing on which flags ran.
        Assert.Equal(0, await Commands([HubWithTxt]).Discover(json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        var txt = root[0].GetProperty("txt");
        Assert.Equal(["class=Videohub", "name=Hub One", "nic=1"], txt.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Discover_Json_NoTxtEntries_IsEmptyArray()
    {
        Assert.Equal(0, await Commands([NoTxtBridge]).Discover(all: true, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        var txt = root[0].GetProperty("txt");
        Assert.Equal(JsonValueKind.Array, txt.ValueKind);
        Assert.Equal(0, txt.GetArrayLength());
    }

    [Fact]
    public async Task Discover_All_Human_PrintsTxtEntriesIndentedUnderDeviceRow()
    {
        Assert.Equal(0, await Commands([HubWithTxt, NoTxtBridge]).Discover(all: true));
        var lines = _stdout.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        var rowIndex = Array.FindIndex(lines, l => l.Contains("Hub One"));
        Assert.True(rowIndex >= 0);
        Assert.Equal("    class=Videohub", lines[rowIndex + 1]);
        Assert.Equal("    name=Hub One", lines[rowIndex + 2]);
        Assert.Equal("    nic=1", lines[rowIndex + 3]);

        // The next real content line is the other device's row — no leaked indented lines for
        // a device that reported no TXT entries.
        Assert.Contains("Streaming Bridge", lines[rowIndex + 4]);
    }

    [Fact]
    public async Task Discover_All_Human_HostileTxtEntry_DoesNotCorruptTheTable()
    {
        // A TXT entry with an embedded newline and an ANSI escape sequence must not fake an
        // extra table row or leak a raw escape byte onto the terminal — the entry is one
        // sanitized line, and nothing else on stdout changes.
        Assert.Equal(0, await Commands([HubWithHostileTxt]).Discover(all: true));
        var stdout = _stdout.ToString();
        Assert.DoesNotContain('\u001b', stdout);
        var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var rowIndex = Array.FindIndex(lines, l => l.Contains("Hub One"));
        Assert.True(rowIndex >= 0);
        Assert.Equal("    class=Videohub", lines[rowIndex + 1]);
        // The newline inside "evil=" must not split into two lines of stdout — sanitized to a
        // single line beneath the row, replacement character standing in for the raw newline.
        Assert.Equal("    evil=line1�line2�[31mFAKE ROW�[0m", lines[rowIndex + 2]);
    }

    [Fact]
    public async Task Discover_Json_HostileTxtEntry_IsPreservedVerbatim_AndStillOneDocument()
    {
        // JSON string escaping already makes control characters and newlines safe — the raw
        // wire content is preserved exactly (no sanitizing needed there, unlike the human
        // table), and the whole output remains exactly one JSON document.
        Assert.Equal(0, await Commands([HubWithHostileTxt]).Discover(all: true, json: true));
        var text = _stdout.ToString();
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var root = JsonDocument.Parse(text).RootElement;
        var entries = root[0].GetProperty("txt").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal("class=Videohub", entries[0]);
        Assert.Equal("evil=line1\nline2\u001b[31mFAKE ROW\u001b[0m", entries[1]);
    }

    [Fact]
    public async Task Discover_Default_Human_DoesNotPrintTxtEntries()
    {
        Assert.Equal(0, await Commands([HubWithTxt]).Discover());
        var text = _stdout.ToString();
        Assert.DoesNotContain("class=Videohub", text);
        Assert.DoesNotContain("nic=1", text);
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

    [Fact]
    public async Task Discover_NothingFound_Human_HintIsDeviceTypeAgnostic()
    {
        // Fix 6: discover now recognizes more than one device type, so the empty-result hint
        // must not assume "videohub" — it names the setting the same generic way the --add
        // "needs an interactive terminal" hint already does.
        Assert.Equal(0, await Commands([]).Discover());
        var err = _stderr.ToString();
        Assert.Contains("bmd config set <type>.host <address>", err);
        Assert.DoesNotContain("videohub.host", err);
    }

    [Fact]
    public async Task Discover_DevicesAnsweredButNoneRecognized_DistinctNoteOnStderr_Exit0()
    {
        // Nine real devices answering, none of them a recognized type, must not be reported
        // as "No devices found" — that wording is only true when literally nothing answered.
        Assert.Equal(0, await Commands([Unsupported]).Discover());
        Assert.Equal("", _stdout.ToString());
        var err = _stderr.ToString();
        Assert.DoesNotContain("No devices found", err);
        Assert.Contains("1 device", err);
        Assert.Contains("bmd discover --all", err);
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
    public async Task Discover_NetworkInformationFailure_Exit1_CleanError()
    {
        // NetworkInterface.GetAllNetworkInterfaces() can throw this at the OS level; it derives
        // from Win32Exception, not SocketException, so it needs its own catch clause or it
        // becomes an unhandled crash with a stack trace instead of one clean error line.
        Assert.Equal(1, await FailingCommands(new NetworkInformationException()).Discover());
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

    // --- --add -------------------------------------------------------------------------------

    [Fact]
    public async Task Add_ValidSelection_WritesHostToLocalConfig_ConfirmsOnStdout()
    {
        SetIn("1\n");
        Assert.Equal(0, await Commands([Hub]).Discover(add: true));

        Assert.Equal("Set videohub.host = 192.168.1.10 in " + LocalConfigPath, _stdout.ToString().Trim());
        Assert.Contains("1. Hub One (videohub)", _stderr.ToString());
        Assert.Contains("Select a device [1-1] (or q to cancel):", _stderr.ToString());

        var store = ConfigStore.Load(GlobalPath, WorkDir);
        Assert.True(ConfigKey.TryParse("videohub.host", out var hostKey));
        Assert.Equal("192.168.1.10", store.GetEffective(hostKey));
        Assert.True(ConfigKey.TryParse("videohub.port", out var portKey));
        Assert.Null(store.GetEffective(portKey)); // default port is not written
    }

    [Fact]
    public async Task Add_ExactlyOneSupportedDevice_StillPrompts()
    {
        // Only one candidate on the network is not license to skip confirmation — --add must
        // still show the numbered list and wait for the user to type "1".
        SetIn("");
        Assert.Equal(0, await Commands([Hub]).Discover(add: true));
        Assert.Contains("Select a device", _stderr.ToString());
        Assert.False(File.Exists(LocalConfigPath));
    }

    [Fact]
    public async Task Add_SecondOfTwo_SelectsCorrectDeviceByNumber()
    {
        SetIn("2\n");
        Assert.Equal(0, await Commands([Hub, HubTwo]).Discover(add: true));

        var store = ConfigStore.Load(GlobalPath, WorkDir);
        Assert.True(ConfigKey.TryParse("videohub.host", out var hostKey));
        Assert.Equal("192.168.1.11", store.GetEffective(hostKey));
        Assert.Contains("1. Hub One (videohub)", _stderr.ToString());
        Assert.Contains("2. Hub Two (videohub)", _stderr.ToString());
    }

    [Fact]
    public async Task Add_NonDefaultPort_AlsoWritesPort()
    {
        SetIn("1\n");
        Assert.Equal(0, await Commands([HubOddPort]).Discover(add: true));

        var text = _stdout.ToString();
        Assert.Contains("Set videohub.host = 192.168.1.12 in " + LocalConfigPath, text);
        Assert.Contains("Set videohub.port = 9991 in " + LocalConfigPath, text);

        var store = ConfigStore.Load(GlobalPath, WorkDir);
        Assert.True(ConfigKey.TryParse("videohub.port", out var portKey));
        Assert.Equal("9991", store.GetEffective(portKey));
    }

    [Fact]
    public async Task Add_Json_EmitsSingleConfigSetResultArray()
    {
        // One JSON document, always — even the one-key case is a single-element array, not a
        // bare object, so a script never has to branch on whether a port was also written.
        SetIn("1\n");
        Assert.Equal(0, await Commands([Hub]).Discover(add: true, json: true));

        var text = _stdout.ToString().Trim();
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(1, root.GetArrayLength());
        var entry = root[0];
        Assert.Equal("videohub.host", entry.GetProperty("key").GetString());
        Assert.Equal("192.168.1.10", entry.GetProperty("value").GetString());
        Assert.Equal(LocalConfigPath, entry.GetProperty("file").GetString());
    }

    [Fact]
    public async Task Add_Json_NonDefaultPort_EmitsOneArrayWithBothEntries()
    {
        // The coverage gap that let the JSON Lines bug ship: a device on a non-default port
        // writes two keys, and the whole point of the fix is that this is still exactly one
        // JSON document — a two-element array — not two documents on two lines.
        SetIn("1\n");
        Assert.Equal(0, await Commands([HubOddPort]).Discover(add: true, json: true));

        var text = _stdout.ToString().Trim();
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());

        var host = root[0];
        Assert.Equal("videohub.host", host.GetProperty("key").GetString());
        Assert.Equal("192.168.1.12", host.GetProperty("value").GetString());
        Assert.Equal(LocalConfigPath, host.GetProperty("file").GetString());

        var port = root[1];
        Assert.Equal("videohub.port", port.GetProperty("key").GetString());
        Assert.Equal("9991", port.GetProperty("value").GetString());
        Assert.Equal(LocalConfigPath, port.GetProperty("file").GetString());
    }

    [Theory]
    [InlineData("q\n")]
    [InlineData("Q\n")]
    [InlineData("\n")]
    [InlineData("")]
    public async Task Add_CancelInput_Exit0_NothingWritten(string input)
    {
        SetIn(input);
        Assert.Equal(0, await Commands([Hub]).Discover(add: true));
        Assert.Equal("", _stdout.ToString());
        Assert.False(File.Exists(LocalConfigPath));
    }

    [Theory]
    [InlineData("0\n")]      // below range
    [InlineData("2\n")]      // above range (only one device offered)
    [InlineData("999999999999999\n")] // overflows int
    [InlineData("abc\n")]    // non-numeric
    [InlineData("1.5\n")]    // non-integer
    public async Task Add_InvalidSelection_Exit2_NothingWritten(string input)
    {
        SetIn(input);
        Assert.Equal(2, await Commands([Hub]).Discover(add: true));
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("error:", _stderr.ToString());
        Assert.False(File.Exists(LocalConfigPath));
    }

    [Fact]
    public async Task Add_SelectionWithSurroundingWhitespace_IsTrimmed()
    {
        SetIn("  1  \n");
        Assert.Equal(0, await Commands([Hub]).Discover(add: true));
        Assert.Contains("Set videohub.host", _stdout.ToString());
    }

    [Fact]
    public async Task Add_NonInteractive_Exit2_NothingWritten()
    {
        // No stdin redirect needed here — Commands(interactive: false) drives the injectable
        // seam directly, proving the guard reads the seam rather than the real console state.
        Assert.Equal(2, await Commands([Hub], interactive: false).Discover(add: true));
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("error: --add needs an interactive terminal", _stderr.ToString());
        Assert.Contains("bmd config set", _stderr.ToString());
        Assert.False(File.Exists(LocalConfigPath));
    }

    [Fact]
    public async Task Add_NothingAnsweredAtAll_Exit0_HelpfulNote_NothingWritten()
    {
        // Nothing to add is decided before the interactive guard, so this must not need stdin
        // at all — an empty SetIn would still make the point, but omitting it entirely shows
        // the command never even tries to read a line.
        Assert.Equal(0, await Commands([], interactive: false).Discover(add: true));
        Assert.Equal("", _stdout.ToString());
        var err = _stderr.ToString();
        Assert.Contains("mDNS", err);
        Assert.Contains("subnet", err);
        Assert.DoesNotContain("Select a device", err);
        Assert.False(File.Exists(LocalConfigPath));
    }

    [Fact]
    public async Task Add_DevicesAnsweredButNoneRecognized_DistinctNoteOnStderr_Exit0_NothingWritten()
    {
        // Mirrors Discover_DevicesAnsweredButNoneRecognized_DistinctNoteOnStderr_Exit0 for the
        // --add flow: a device answered but bmd doesn't recognize its type, so there is nothing
        // to add, but "No devices found" would be actively misleading — something IS on the
        // network. --add can't be combined with --all, so this is the one spot a user in this
        // exact situation is stuck without --add's own guidance pointing them at --all.
        Assert.Equal(0, await Commands([Unsupported], interactive: false).Discover(add: true));
        Assert.Equal("", _stdout.ToString());
        var err = _stderr.ToString();
        Assert.DoesNotContain("No devices found", err);
        Assert.Contains("1 device", err);
        Assert.Contains("bmd discover --all", err);
        Assert.DoesNotContain("Select a device", err);
        Assert.False(File.Exists(LocalConfigPath));
    }

    [Fact]
    public async Task AddAll_IsAUsageError_Exit2()
    {
        Assert.Equal(2, await Commands([Hub]).Discover(add: true, all: true));
        Assert.Equal("", _stdout.ToString());
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.False(File.Exists(LocalConfigPath));
    }

    [Fact]
    public async Task Add_Global_WritesToGlobalFileNotLocal()
    {
        SetIn("1\n");
        Assert.Equal(0, await Commands([Hub]).Discover(add: true, global: true));

        Assert.Contains("Set videohub.host = 192.168.1.10 in " + GlobalPath, _stdout.ToString());
        Assert.False(File.Exists(LocalConfigPath));
        Assert.True(File.Exists(GlobalPath));
        Assert.Contains("192.168.1.10", File.ReadAllText(GlobalPath));
    }

    [Fact]
    public async Task Add_StorePathUnwritable_Exit1_CleanError()
    {
        // Make the global config path itself an existing directory, so ConfigStore.Set's
        // File.WriteAllText fails with an IO/permission error instead of writing a file.
        Directory.CreateDirectory(GlobalPath);
        SetIn("1\n");
        Assert.Equal(1, await Commands([Hub]).Discover(add: true, global: true));
        var err = _stderr.ToString();
        Assert.Contains("error:", err);
        Assert.DoesNotContain("   at ", err);
        Assert.Equal("", _stdout.ToString());
    }
}
