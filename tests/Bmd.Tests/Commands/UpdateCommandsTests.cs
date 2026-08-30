using System.Net;
using System.Text.Json;
using Bmd.Commands;
using Bmd.Tests.Update;
using Bmd.Update;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class UpdateCommandsTests : IDisposable
{
    const string ApiBase = "https://api.example.test";
    const string LatestUrl = "https://api.example.test/repos/davojc/bmdcli/releases/latest";

    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    public UpdateCommandsTests()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    static string ReleaseJson(string tag) => $$"""
        {
          "tag_name": "{{tag}}",
          "prerelease": false,
          "assets": [
            { "name": "bmd-win-x64.zip",
              "browser_download_url": "https://downloads.example.test/bmd-win-x64.zip" },
            { "name": "checksums.txt",
              "browser_download_url": "https://downloads.example.test/checksums.txt" }
          ]
        }
        """;

    static UpdateCommands CommandFor(FakeHttpHandler handler, string currentVersion,
        string rid = "win-x64", string? exePath = null) =>
        new(new ReleaseClient(new HttpClient(handler), ApiBase), currentVersion, rid,
            exePath ?? Path.Combine(Path.GetTempPath(), "bmd.exe"));

    [Fact]
    public async Task Check_ReportsUpToDateAndExitsZero()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.2.0").Update(check: true);

        Assert.Equal(0, exit);
        Assert.Contains("latest version", _stdout.ToString());
        Assert.DoesNotContain("bmd update", _stdout.ToString());
    }

    [Fact]
    public async Task Check_ReportsAnAvailableUpdateAndStillExitsZero()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.1.0").Update(check: true);

        Assert.Equal(0, exit);
        var text = _stdout.ToString();
        Assert.Contains("0.1.0", text);
        Assert.Contains("0.2.0", text);
        Assert.Contains("bmd update", text);
    }

    [Fact]
    public async Task Check_TreatsAPreReleaseBuildAsOlderThanItsRelease()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.1.0"));

        var exit = await CommandFor(handler, "0.1.0-rc.1").Update(check: true);

        Assert.Equal(0, exit);
        Assert.Contains("0.1.0-rc.1", _stdout.ToString());
    }

    [Fact]
    public async Task Check_NeverDownloadsAnything()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        await CommandFor(handler, "0.1.0").Update(check: true);

        Assert.Equal(LatestUrl, Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task Check_Json_EmitsExactlyOneDocumentWithStableFields()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.1.0").Update(check: true, json: true);

        Assert.Equal(0, exit);
        var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("0.1.0", doc.RootElement.GetProperty("currentVersion").GetString());
        Assert.Equal("0.2.0", doc.RootElement.GetProperty("latestVersion").GetString());
        Assert.True(doc.RootElement.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task Check_Json_ReportsFalseWhenUpToDate()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        await CommandFor(handler, "0.2.0").Update(check: true, json: true);

        using var doc = JsonDocument.Parse(_stdout.ToString().Trim());
        Assert.False(doc.RootElement.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task Check_NetworkFailureIsOneStderrLineAndExitOne()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, "nope", HttpStatusCode.ServiceUnavailable);

        var exit = await CommandFor(handler, "0.1.0").Update(check: true);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString()); // no stack trace
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task Check_ErrorsStayPlainTextEvenWithJson()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, "nope", HttpStatusCode.ServiceUnavailable);

        var exit = await CommandFor(handler, "0.1.0").Update(check: true, json: true);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task Check_UnparsableReleaseTagIsAnError()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("not-a-version"));

        var exit = await CommandFor(handler, "0.1.0").Update(check: true);

        Assert.Equal(1, exit);
        Assert.Contains("not-a-version", _stderr.ToString());
    }

    [Fact]
    public async Task Check_WorksEvenForABuildWithNoRuntimeIdentifier()
    {
        // --check compares versions; it never needs to know which asset to fetch.
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.1.0", rid: "unknown").Update(check: true);

        Assert.Equal(0, exit);
        Assert.Contains("0.2.0", _stdout.ToString());
    }
}
