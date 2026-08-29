using System.Text.Json;
using Bmd.Commands;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VersionCommandsTests : IDisposable
{
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public VersionCommandsTests()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    [Fact]
    public void Version_Human_PrintsVersionAndRid()
    {
        Assert.Equal(0, new VersionCommands().Version());
        var text = _stdout.ToString().TrimEnd('\r', '\n');
        Assert.StartsWith("bmd ", text);
        Assert.Matches(@"^bmd \S.* \(\S+\)$", text);
        Assert.DoesNotContain("{", text);
        Assert.DoesNotContain("}", text);
        Assert.Equal("", _stderr.ToString());
    }

    [Fact]
    public void Version_Json_HasVersionAndRuntimeIdentifier()
    {
        Assert.Equal(0, new VersionCommands().Version(json: true));
        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines); // exactly one JSON document on stdout
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("version", out var version));
        Assert.False(string.IsNullOrWhiteSpace(version.GetString()));
        Assert.True(root.TryGetProperty("runtimeIdentifier", out var rid));
        Assert.False(string.IsNullOrWhiteSpace(rid.GetString()));
    }

    [Fact]
    public void Version_AlwaysExitsZero()
    {
        Assert.Equal(0, new VersionCommands().Version());
        Assert.Equal(0, new VersionCommands().Version(json: true));
    }
}
