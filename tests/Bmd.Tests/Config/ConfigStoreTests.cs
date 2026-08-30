using Bmd.Config;

namespace Bmd.Tests.Config;

public class ConfigStoreTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    string GlobalPath => Path.Combine(_root, "global", "bmd", "config");
    string WorkDir => Path.Combine(_root, "work");

    public ConfigStoreTests() => Directory.CreateDirectory(WorkDir);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    static ConfigKey Key(string raw)
    {
        Assert.True(ConfigKey.TryParse(raw, out var key));
        return key;
    }

    ConfigStore Load() => ConfigStore.Load(GlobalPath, WorkDir);

    [Fact]
    public void GetEffective_ReturnsNull_WhenNothingConfigured()
    {
        Assert.Null(Load().GetEffective(Key("videohub.host")));
    }

    [Fact]
    public void Set_Global_CreatesFileAndDirectories_GetEffectiveReadsIt()
    {
        var store = Load();
        var written = store.Set(Key("videohub.host"), "10.0.0.5", ConfigScope.User);
        Assert.Equal(GlobalPath, written);
        Assert.Equal("10.0.0.5", Load().GetEffective(Key("videohub.host")));
    }

    [Fact]
    public void Set_Local_CreatesBmdconfigInStartDirectory()
    {
        var store = Load();
        var written = store.Set(Key("videohub.host"), "10.0.0.9", ConfigScope.Project);
        Assert.Equal(Path.Combine(WorkDir, ConfigPaths.LocalFileName), written);
        Assert.Equal("10.0.0.9", Load().GetEffective(Key("videohub.host")));
    }

    [Fact]
    public void Set_Local_WritesToDiscoveredFileInParent()
    {
        var parentConfig = Path.Combine(WorkDir, ConfigPaths.LocalFileName);
        File.WriteAllText(parentConfig, "[videohub]\n\tport = 9990\n");
        var nested = Directory.CreateDirectory(Path.Combine(WorkDir, "nested")).FullName;
        var store = ConfigStore.Load(GlobalPath, nested);
        var written = store.Set(Key("videohub.host"), "10.0.0.9", ConfigScope.Project);
        Assert.Equal(parentConfig, written);
        Assert.Contains("port = 9990", File.ReadAllText(parentConfig));
        Assert.Contains("host = 10.0.0.9", File.ReadAllText(parentConfig));
    }

    [Fact]
    public void GetEffective_LocalOverridesGlobal()
    {
        var store = Load();
        store.Set(Key("videohub.host"), "10.0.0.5", ConfigScope.User);
        store.Set(Key("videohub.host"), "10.0.0.9", ConfigScope.Project);
        Assert.Equal("10.0.0.9", Load().GetEffective(Key("videohub.host")));
    }

    [Fact]
    public void ListEffective_ShadowsGlobalWithLocal_AndReportsOrigin()
    {
        var store = Load();
        store.Set(Key("videohub.host"), "10.0.0.5", ConfigScope.User);
        store.Set(Key("update.check"), "false", ConfigScope.User);
        store.Set(Key("videohub.host"), "10.0.0.9", ConfigScope.Project);

        var entries = ConfigStore.Load(GlobalPath, WorkDir).ListEffective();

        Assert.Equal(2, entries.Count);
        var host = Assert.Single(entries, e => e.Key == "videohub.host");
        Assert.Equal("10.0.0.9", host.Value);
        Assert.Equal(Path.Combine(WorkDir, ConfigPaths.LocalFileName), host.Origin);
        var check = Assert.Single(entries, e => e.Key == "update.check");
        Assert.Equal("false", check.Value);
        Assert.Equal(GlobalPath, check.Origin);
    }

    [Fact]
    public void Unset_RemovesFromChosenLayer()
    {
        var store = Load();
        store.Set(Key("videohub.host"), "10.0.0.5", ConfigScope.User);
        Assert.True(Load().Unset(Key("videohub.host"), ConfigScope.User));
        Assert.Null(Load().GetEffective(Key("videohub.host")));
    }

    [Fact]
    public void Unset_ReturnsFalse_WhenKeyAbsentInLayer()
    {
        var store = Load();
        store.Set(Key("videohub.host"), "10.0.0.5", ConfigScope.User);
        Assert.False(Load().Unset(Key("videohub.host"), ConfigScope.Project));
    }
}
