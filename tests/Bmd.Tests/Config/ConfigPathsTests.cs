using Bmd.Config;

namespace Bmd.Tests.Config;

public class ConfigPathsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void FindLocalConfig_FindsFileInStartDirectory()
    {
        var file = Path.Combine(_root, ConfigPaths.LocalFileName);
        File.WriteAllText(file, "");
        Assert.Equal(file, ConfigPaths.FindLocalConfig(_root));
    }

    [Fact]
    public void FindLocalConfig_WalksUpToParent()
    {
        var file = Path.Combine(_root, ConfigPaths.LocalFileName);
        File.WriteAllText(file, "");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "a", "b")).FullName;
        Assert.Equal(file, ConfigPaths.FindLocalConfig(nested));
    }

    [Fact]
    public void FindLocalConfig_PrefersNearestFile()
    {
        File.WriteAllText(Path.Combine(_root, ConfigPaths.LocalFileName), "");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var nearest = Path.Combine(nested, ConfigPaths.LocalFileName);
        File.WriteAllText(nearest, "");
        Assert.Equal(nearest, ConfigPaths.FindLocalConfig(nested));
    }

    [Fact]
    public void FindLocalConfig_ReturnsNull_WhenAbsent()
    {
        Assert.Null(ConfigPaths.FindLocalConfig(_root));
    }

    [Fact]
    public void GlobalConfigPath_EndsWithBmdConfig()
    {
        var path = ConfigPaths.GlobalConfigPath;
        Assert.True(Path.IsPathRooted(path));
        Assert.Equal("config", Path.GetFileName(path));
        Assert.Equal("bmd", Path.GetFileName(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void CacheDirectory_EndsInBmdAndIsRooted()
    {
        var path = ConfigPaths.CacheDirectory;
        Assert.True(Path.IsPathRooted(path));
        Assert.Equal("bmd", Path.GetFileName(path));
    }

    [Fact]
    public void CacheDirectory_IsDistinctFromTheGlobalConfigFile()
    {
        // Cache is disposable; config is not. They must never be the same location.
        Assert.NotEqual(
            Path.GetFullPath(ConfigPaths.GlobalConfigPath),
            Path.GetFullPath(ConfigPaths.CacheDirectory));
    }
}
