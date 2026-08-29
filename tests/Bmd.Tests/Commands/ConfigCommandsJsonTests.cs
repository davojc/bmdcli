using System.Text.Json;
using Bmd.Commands;
using Bmd.Config;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class ConfigCommandsJsonTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");

    public ConfigCommandsJsonTests()
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

    JsonElement Root() => JsonDocument.Parse(_stdout.ToString()).RootElement;

    [Fact]
    public void Set_Json_ReportsKeyValueFile()
    {
        Assert.Equal(0, Commands().Set("videohub.host", "10.0.0.5", json: true));
        var root = Root();
        Assert.Equal("videohub.host", root.GetProperty("key").GetString());
        Assert.Equal("10.0.0.5", root.GetProperty("value").GetString());
        Assert.Equal(Path.Combine(WorkDir, ConfigPaths.LocalFileName), root.GetProperty("file").GetString());
    }

    [Fact]
    public void Get_Json_ReportsKeyValueOrigin()
    {
        Commands().Set("videohub.host", "10.0.0.5");
        _stdout.GetStringBuilder().Clear();
        Assert.Equal(0, Commands().Get("videohub.host", json: true));
        var root = Root();
        Assert.Equal("videohub.host", root.GetProperty("key").GetString());
        Assert.Equal("10.0.0.5", root.GetProperty("value").GetString());
        Assert.Equal(Path.Combine(WorkDir, ConfigPaths.LocalFileName), root.GetProperty("origin").GetString());
    }

    [Fact]
    public void Get_Json_MissingKey_Exit1_NothingOnStdout()
    {
        Assert.Equal(1, Commands().Get("videohub.host", json: true));
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("not set", _stderr.ToString());
    }

    [Fact]
    public void List_Json_IsArrayOfEntries()
    {
        Commands().Set("videohub.host", "10.0.0.5");
        Commands().Set("update.check", "false", global: true);
        _stdout.GetStringBuilder().Clear();
        Assert.Equal(0, Commands().List(json: true));
        var root = Root();
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());
        var host = root.EnumerateArray().Single(e => e.GetProperty("key").GetString() == "videohub.host");
        Assert.Equal("10.0.0.5", host.GetProperty("value").GetString());
        Assert.False(string.IsNullOrEmpty(host.GetProperty("origin").GetString()));
    }

    [Fact]
    public void Unset_Json_ReportsRemoved()
    {
        Commands().Set("videohub.host", "10.0.0.5");
        _stdout.GetStringBuilder().Clear();
        Assert.Equal(0, Commands().Unset("videohub.host", json: true));
        var root = Root();
        Assert.Equal("videohub.host", root.GetProperty("key").GetString());
        Assert.True(root.GetProperty("removed").GetBoolean());
    }

    [Fact]
    public void HumanOutput_Unchanged_WhenJsonOmitted()
    {
        Commands().Set("videohub.host", "10.0.0.5");
        _stdout.GetStringBuilder().Clear();
        Assert.Equal(0, Commands().Get("videohub.host"));
        Assert.Equal("10.0.0.5", _stdout.ToString().Trim());
    }
}
