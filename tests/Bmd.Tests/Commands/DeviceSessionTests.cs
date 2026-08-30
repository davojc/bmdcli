using Bmd.Commands;
using Bmd.Config;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class DeviceSessionTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), $"bmd-session-{Guid.NewGuid():N}");
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public DeviceSessionTests()
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

    ConfigStore ConfigWith(params string[] lines)
    {
        var path = Path.Combine(_directory, "config");
        if (lines.Length > 0) File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return ConfigStore.Load(path, _directory);
    }

    [Theory]
    [InlineData("videohub")]
    [InlineData("multiview")]
    public async Task RunWithClientAsync_NamesItsOwnSectionWhenNoHostIsConfigured(string section)
    {
        var session = new DeviceSession(section, () => ConfigWith());

        var exit = await session.RunWithClientAsync(null, null, null, _ => Task.FromResult(0));

        Assert.Equal(1, exit);
        Assert.Equal(
            $"error: no host configured for {section} (run: bmd config set {section}.host <addr>)",
            _stderr.ToString().TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task RunWithClientAsync_ReadsHostFromItsOwnSectionOnly()
    {
        // A configured videohub.host must not satisfy a multiview session.
        var session = new DeviceSession("multiview", () => ConfigWith("[videohub]", "host = 10.0.0.1"));

        var exit = await session.RunWithClientAsync(null, null, null, _ => Task.FromResult(0));

        Assert.Equal(1, exit);
        Assert.Contains("multiview.host", _stderr.ToString());
    }

    [Fact]
    public async Task RunWithClientAsync_RejectsANonPositiveTimeoutWithExitTwo()
    {
        var session = new DeviceSession("multiview", () => ConfigWith("[multiview]", "host = 10.0.0.1"));

        var exit = await session.RunWithClientAsync(null, null, 0, _ => Task.FromResult(0));

        Assert.Equal(2, exit);
        Assert.Contains("timeout must be a positive number", _stderr.ToString());
    }

    [Fact]
    public async Task RunCatchingAsync_MapsAnExpectedFailureToOneStderrLineAndExitOne()
    {
        var exit = await DeviceSession.RunCatchingAsync(
            () => throw new TimeoutException("device did not answer"));

        Assert.Equal(1, exit);
        Assert.Equal("error: device did not answer", _stderr.ToString().TrimEnd('\r', '\n'));
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }
}
