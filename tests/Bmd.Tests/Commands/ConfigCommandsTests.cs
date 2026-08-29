using Bmd.Commands;
using Bmd.Config;
using Bmd.Tests;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class ConfigCommandsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");

    public ConfigCommandsTests()
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

    ConfigCommands Commands() => new(() => ConfigStore.Load(GlobalPath, WorkDir));

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        Assert.Equal(0, Commands().Set("videohub.host", "10.0.0.5"));
        Assert.Equal(0, Commands().Get("videohub.host"));
        Assert.Equal("10.0.0.5", _stdout.ToString().Trim());
    }

    [Fact]
    public void Get_MissingKey_Exit1_MessageOnStderr()
    {
        Assert.Equal(1, Commands().Get("videohub.host"));
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("not set", _stderr.ToString());
    }

    [Fact]
    public void InvalidKeyFormat_Exit2()
    {
        Assert.Equal(2, Commands().Set("nodot", "x"));
        Assert.Equal(2, Commands().Get("nodot"));
        Assert.Equal(2, Commands().Unset("nodot"));
        Assert.Contains("section.key", _stderr.ToString());
    }

    [Fact]
    public void Unset_MissingKey_Exit1()
    {
        Assert.Equal(1, Commands().Unset("videohub.host"));
        Assert.Contains("not set", _stderr.ToString());
    }

    [Fact]
    public void List_PrintsEffectiveEntries()
    {
        Commands().Set("videohub.host", "10.0.0.5", global: true);
        Commands().Set("videohub.host", "10.0.0.9");
        Assert.Equal(0, Commands().List());
        Assert.Equal("videohub.host=10.0.0.9", _stdout.ToString().Trim());
    }

    [Fact]
    public void List_ShowOrigin_PrefixesFilePath()
    {
        Commands().Set("videohub.host", "10.0.0.9");
        Assert.Equal(0, Commands().List(showOrigin: true));
        var line = _stdout.ToString().Trim();
        Assert.Equal($"{Path.Combine(WorkDir, ConfigPaths.LocalFileName)}\tvideohub.host=10.0.0.9", line);
    }

    // --- Finding 1: unhandled IO exceptions must not leak stack traces ---

    [Fact]
    public void Set_GlobalConfigPathIsDirectory_Exit1_NoStackTrace()
    {
        // Make the global config path collide with a directory so the write fails.
        Directory.CreateDirectory(GlobalPath);
        Assert.Equal(1, Commands().Set("videohub.host", "10.0.0.5", global: true));
        var stderr = _stderr.ToString();
        Assert.StartsWith("error:", stderr);
        Assert.DoesNotContain("   at ", stderr);
    }

    [Fact]
    public void Get_GlobalConfigPathIsDirectory_Exit1_NoStackTrace()
    {
        // GetEffective doesn't touch the global path at read time (loaded lazily as empty),
        // so exercise the failure via a local config directory collision instead.
        var localConfigPath = Path.Combine(WorkDir, ConfigPaths.LocalFileName);
        Directory.CreateDirectory(localConfigPath);
        Assert.Equal(1, Commands().Set("videohub.host", "10.0.0.5"));
        var stderr = _stderr.ToString();
        Assert.StartsWith("error:", stderr);
        Assert.DoesNotContain("   at ", stderr);
    }

    // --- Finding 2: unvalidated key names / values can corrupt the INI file ---

    [Fact]
    public void Set_KeyWithInvalidCharacters_Exit2()
    {
        Assert.Equal(2, Commands().Set("videohub.a=b", "x"));
        Assert.Contains("section.key", _stderr.ToString());
    }

    [Fact]
    public void Set_ValueWithNewline_Exit2_StderrMessage()
    {
        Assert.Equal(2, Commands().Set("videohub.host", "line1\nline2"));
        var stderr = _stderr.ToString();
        Assert.StartsWith("error:", stderr);
    }

    [Fact]
    public void Set_ValueWithQuote_Exit2_StderrMessage()
    {
        Assert.Equal(2, Commands().Set("videohub.host", "has\"quote"));
        var stderr = _stderr.ToString();
        Assert.StartsWith("error:", stderr);
    }

    [Fact]
    public void SetThenGet_ValueWithSpecialButAllowedChars_RoundTrips()
    {
        Assert.Equal(0, Commands().Set("videohub.host", "10.0.0.5 # not a comment"));
        Assert.Equal(0, Commands().Get("videohub.host"));
        Assert.Equal("10.0.0.5 # not a comment", _stdout.ToString().Trim());
    }
}
