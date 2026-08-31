using System.Text.Json;
using Bmd.Commands;
using Bmd.Config;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class ContextCommandsTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), $"bmd-ctx-{Guid.NewGuid():N}");
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public ContextCommandsTests()
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

    string ConfigPath => Path.Combine(_directory, "config");
    ConfigStore Store() => ConfigStore.Load(ConfigPath, _directory);
    ContextCommands Contexts(bool interactive = false) =>
        new("atem", Store, () => interactive);
    ConfigCommands Config() => new(Store);

    void WriteConfig(string text) => File.WriteAllText(ConfigPath, text);

    // ---- storage shape ----------------------------------------------------------------

    [Fact]
    public void ConfigSet_WithContext_WritesAGitStyleSubsection()
    {
        Assert.Equal(0, Config().Set("atem.host", "192.168.4.30", context: "gallery"));

        // The subsection spelling is the whole point: one file, two devices of the same type.
        Assert.Contains("[atem \"gallery\"]", File.ReadAllText(ConfigPath));
        Assert.Equal("192.168.4.30", Store().GetEffective(new ConfigKey("atem", "host", "gallery")));
    }

    [Fact]
    public void ConfigSet_WithContext_DoesNotDisturbTheDefault()
    {
        Assert.Equal(0, Config().Set("atem.host", "192.168.4.98"));
        Assert.Equal(0, Config().Set("atem.host", "192.168.4.30", context: "gallery"));

        Assert.Equal("192.168.4.98", Store().GetEffective(new ConfigKey("atem", "host")));
        Assert.Equal("192.168.4.30", Store().GetEffective(new ConfigKey("atem", "host", "gallery")));
    }

    [Fact]
    public void ConfigList_ShowsWhichContextAKeyBelongsTo()
    {
        Config().Set("atem.host", "192.168.4.98");
        Config().Set("atem.host", "192.168.4.30", context: "gallery");
        _stdout.GetStringBuilder().Clear();

        Assert.Equal(0, Config().List());

        var text = _stdout.ToString();
        Assert.Contains("atem.host=192.168.4.98", text);
        Assert.Contains("atem.host [gallery]=192.168.4.30", text);
    }

    [Theory]
    [InlineData("default")]     // reserved: it names the unlabelled section
    [InlineData("DEFAULT")]
    [InlineData("has space")]
    [InlineData("quote\"d")]
    public void ConfigSet_RejectsAnUnusableContextName(string context)
    {
        Assert.Equal(2, Config().Set("atem.host", "10.0.0.1", context: context));
        Assert.Contains("not a usable context name", _stderr.ToString());
    }

    // ---- listing ----------------------------------------------------------------------

    [Fact]
    public void List_ShowsEveryContextAndMarksTheActiveOne()
    {
        WriteConfig("""
            [atem]
            host = 192.168.4.98

            [atem "gallery"]
            host = 192.168.4.30
            """);

        Assert.Equal(0, Contexts().List(json: true));

        var entries = JsonDocument.Parse(_stdout.ToString().Trim()).RootElement.EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("default", entries[0].GetProperty("context").GetString());
        Assert.True(entries[0].GetProperty("active").GetBoolean());
        Assert.Equal("gallery", entries[1].GetProperty("context").GetString());
        Assert.Equal("192.168.4.30", entries[1].GetProperty("host").GetString());
        Assert.False(entries[1].GetProperty("active").GetBoolean());
    }

    [Fact]
    public void List_SaysSoWhenNothingIsConfigured()
    {
        Assert.Equal(0, Contexts().List());
        Assert.Contains("No atem device configured", _stdout.ToString());
    }

    // ---- selecting --------------------------------------------------------------------

    [Fact]
    public void Set_SelectsAContextAndSubsequentReadsFollowIt()
    {
        WriteConfig("""
            [atem]
            host = 192.168.4.98

            [atem "gallery"]
            host = 192.168.4.30
            """);

        Assert.Equal(0, Contexts().Set("gallery"));

        Assert.Equal("gallery", Store().ActiveContext("atem"));
        Assert.Contains("Now using atem context 'gallery' (192.168.4.30)", _stdout.ToString());
    }

    [Fact]
    public void Set_Default_ClearsTheActiveContextRatherThanNamingIt()
    {
        // Writing "default" into the key would leave a config pointing at a context that
        // deliberately has no section of its own.
        WriteConfig("""
            [atem]
            host = 192.168.4.98
            context = gallery

            [atem "gallery"]
            host = 192.168.4.30
            """);

        Assert.Equal(0, Contexts().Set("default"));

        Assert.Null(Store().ActiveContext("atem"));
        Assert.DoesNotContain("context = default", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Set_RefusesAContextThatHasNoHost()
    {
        // Selecting it would leave every later command failing with a less obvious message —
        // one that reads like the device being unreachable rather than unconfigured.
        WriteConfig("""
            [atem]
            host = 192.168.4.98

            [atem "gallery"]
            timeout = 10
            """);

        Assert.Equal(1, Contexts().Set("gallery"));
        Assert.Contains("no atem context named 'gallery' with a host", _stderr.ToString());
        Assert.Null(Store().ActiveContext("atem"));
    }

    [Fact]
    public void Set_RefusesAContextThatDoesNotExist()
    {
        WriteConfig("[atem]\nhost = 192.168.4.98\n");
        Assert.Equal(1, Contexts().Set("nowhere"));
        Assert.Contains("bmd atem context list", _stderr.ToString());
    }

    [Fact]
    public void Set_WithoutANameAndWithoutATerminal_FailsRatherThanHangs()
    {
        WriteConfig("[atem]\nhost = 192.168.4.98\n");

        Assert.Equal(2, Contexts(interactive: false).Set());
        Assert.Contains("bmd atem context set <name>", _stderr.ToString());
    }

    // ---- resolution -------------------------------------------------------------------

    [Fact]
    public void ActiveContext_IsNullWhenNoneIsSelected()
    {
        WriteConfig("[atem]\nhost = 192.168.4.98\n");
        Assert.Null(Store().ActiveContext("atem"));
    }

    [Fact]
    public void Contexts_AreScopedToOneDeviceType()
    {
        // Selecting a Videohub must not move the ATEM, and vice versa.
        WriteConfig("""
            [atem]
            host = 192.168.4.98
            context = gallery

            [atem "gallery"]
            host = 192.168.4.30

            [videohub]
            host = 192.168.4.100

            [videohub "spare"]
            host = 192.168.4.101
            """);

        var store = Store();
        Assert.Equal("gallery", store.ActiveContext("atem"));
        Assert.Null(store.ActiveContext("videohub"));
        Assert.Equal(2, store.Contexts("videohub").Count);
        Assert.DoesNotContain(store.Contexts("videohub"), c => c.Name == "gallery");
    }

    [Fact]
    public void ProjectConfig_OverridesTheUserContextForOneDirectoryTree()
    {
        WriteConfig("""
            [atem]
            host = 192.168.4.98

            [atem "gallery"]
            host = 192.168.4.30
            """);
        File.WriteAllText(Path.Combine(_directory, ".bmdconfig"), "[atem]\ncontext = gallery\n");

        Assert.Equal("gallery", Store().ActiveContext("atem"));
    }
}
