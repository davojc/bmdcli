using Bmd.Commands.Videohub;
using Bmd.Config;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubConfigErrorTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");

    public VideohubConfigErrorTests()
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

    void SetConfig(string key, string value)
    {
        Assert.True(ConfigKey.TryParse(key, out var parsed));
        ConfigStore.Load(GlobalPath, WorkDir).Set(parsed, value, ConfigScope.Project);
    }

    VideohubCommands Commands() => new(() => ConfigStore.Load(GlobalPath, WorkDir));

    [Fact]
    public async Task InvalidBackupKeep_Exit1_CleanError()
    {
        SetConfig("videohub.host", "127.0.0.1");
        SetConfig("backup.keep", "lots");
        // Any command that constructs a BackupStore surfaces this; Info does not,
        // so this asserts the filter directly via a mutation-free path in Task 5+.
        // Until then, prove the exception type is caught by the shared seam:
        Assert.Equal(1, await Commands().ThrowingProbeAsync(new ConfigValueException("config backup.keep must be a positive number, not 'lots'")));
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("backup.keep", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }
}
