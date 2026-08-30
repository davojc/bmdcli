using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
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

    [Fact]
    public async Task Check_UnparsableCurrentVersionIsAnError()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "not-a-version").Update(check: true);

        Assert.Equal(1, exit);
        Assert.StartsWith("error: ", _stderr.ToString());
        Assert.Contains("not-a-version", _stderr.ToString());
        Assert.Single(_stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("", _stdout.ToString());
    }

    const string ChecksumsUrl = "https://downloads.example.test/checksums.txt";
    const string AssetUrl = "https://downloads.example.test/bmd-win-x64.zip";

    /// <summary>A one-entry zip holding the given bytes, matching what the release workflow
    /// publishes for win-x64.</summary>
    static byte[] ZipContaining(string entryName, byte[] content)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry(entryName).Open())
            entry.Write(content);
        return memory.ToArray();
    }

    static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task Update_VerifiesTheChecksumThenReplacesTheBinary()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "old binary");

        var archive = ZipContaining(UpdateInstaller.ExecutableName, "new binary"u8.ToArray());
        var handler = new FakeHttpHandler()
            .Text(LatestUrl, ReleaseJson("v0.2.0"))
            .Text(ChecksumsUrl, $"{Sha256Of(archive)}  bmd-win-x64.zip\n")
            .Bytes(AssetUrl, archive);

        try
        {
            var exit = await CommandFor(handler, "0.1.0", exePath: current).Update();

            Assert.Equal(0, exit);
            Assert.Equal("new binary", File.ReadAllText(current));
            Assert.Contains("0.2.0", _stdout.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Update_AbortsWithNothingChangedWhenTheChecksumDoesNotMatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "old binary");

        var archive = ZipContaining(UpdateInstaller.ExecutableName, "new binary"u8.ToArray());
        var handler = new FakeHttpHandler()
            .Text(LatestUrl, ReleaseJson("v0.2.0"))
            .Text(ChecksumsUrl, $"{new string('a', 64)}  bmd-win-x64.zip\n")
            .Bytes(AssetUrl, archive);

        try
        {
            var exit = await CommandFor(handler, "0.1.0", exePath: current).Update();

            Assert.Equal(1, exit);
            Assert.Contains("checksum", _stderr.ToString());
            Assert.Equal("old binary", File.ReadAllText(current)); // untouched
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Update_FailsWhenTheReleaseHasNoChecksumsForTheAsset()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "old binary");

        var archive = ZipContaining(UpdateInstaller.ExecutableName, "new binary"u8.ToArray());
        var handler = new FakeHttpHandler()
            .Text(LatestUrl, ReleaseJson("v0.2.0"))
            .Text(ChecksumsUrl, "deadbeef  something-else.tar.gz\n")
            .Bytes(AssetUrl, archive);

        try
        {
            var exit = await CommandFor(handler, "0.1.0", exePath: current).Update();

            Assert.Equal(1, exit);
            Assert.Equal("old binary", File.ReadAllText(current));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Update_FailsWhenTheReleaseHasNoAssetForThisPlatform()
    {
        const string json = """
            {
              "tag_name": "v0.2.0",
              "prerelease": false,
              "assets": [
                { "name": "checksums.txt",
                  "browser_download_url": "https://downloads.example.test/checksums.txt" }
              ]
            }
            """;
        var handler = new FakeHttpHandler().Text(LatestUrl, json);

        var exit = await CommandFor(handler, "0.1.0").Update();

        Assert.Equal(1, exit);
        Assert.Contains("bmd-win-x64.zip", _stderr.ToString());
    }

    [Fact]
    public async Task Update_RefusesToInstallWhenTheBuildHasNoRuntimeIdentifier()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.1.0", rid: "unknown").Update();

        Assert.Equal(1, exit);
        Assert.Contains("github.com/davojc/bmdcli/releases", _stderr.ToString());
    }

    [Fact]
    public async Task Update_UpToDate_DownloadsNothingAndExitsZero()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, ReleaseJson("v0.2.0"));

        var exit = await CommandFor(handler, "0.2.0").Update();

        Assert.Equal(0, exit);
        Assert.Equal(LatestUrl, Assert.Single(handler.Requests));
        Assert.Contains("already the latest", _stdout.ToString());
    }

    [Fact]
    public async Task Update_Json_EmitsExactlyOneDocumentAfterInstalling()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "old binary");

        var archive = ZipContaining(UpdateInstaller.ExecutableName, "new binary"u8.ToArray());
        var handler = new FakeHttpHandler()
            .Text(LatestUrl, ReleaseJson("v0.2.0"))
            .Text(ChecksumsUrl, $"{Sha256Of(archive)}  bmd-win-x64.zip\n")
            .Bytes(AssetUrl, archive);

        try
        {
            var exit = await CommandFor(handler, "0.1.0", exePath: current).Update(json: true);

            Assert.Equal(0, exit);
            var lines = _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
            using var doc = JsonDocument.Parse(lines[0]);
            Assert.True(doc.RootElement.GetProperty("updated").GetBoolean());
            Assert.Equal("0.2.0", doc.RootElement.GetProperty("latestVersion").GetString());
            Assert.Equal(current, doc.RootElement.GetProperty("path").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Update_LeavesNoTemporaryFilesBesideTheExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "old binary");

        var archive = ZipContaining(UpdateInstaller.ExecutableName, "new binary"u8.ToArray());
        var handler = new FakeHttpHandler()
            .Text(LatestUrl, ReleaseJson("v0.2.0"))
            .Text(ChecksumsUrl, $"{Sha256Of(archive)}  bmd-win-x64.zip\n")
            .Bytes(AssetUrl, archive);

        try
        {
            await CommandFor(handler, "0.1.0", exePath: current).Update();

            // Only the installed binary should remain — no staging directory, no downloaded
            // archive. A leftover `.old` is expected on Windows and is cleaned by a later run.
            var leftovers = Directory.GetFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .Where(name => name != UpdateInstaller.ExecutableName
                            && name != UpdateInstaller.ExecutableName + UpdateInstaller.OldSuffix)
                .ToArray();
            Assert.Empty(leftovers);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
