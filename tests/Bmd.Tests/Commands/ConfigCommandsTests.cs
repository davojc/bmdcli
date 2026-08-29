using Bmd.Commands;
using Bmd.Config;

namespace Bmd.Tests.Commands;

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
}
