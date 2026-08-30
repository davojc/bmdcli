using System.Net;
using Bmd.Update;

namespace Bmd.Tests.Update;

public class ReleaseClientTests
{
    const string ApiBase = "https://api.example.test";
    const string LatestUrl = "https://api.example.test/repos/davojc/bmdcli/releases/latest";

    const string LatestJson = """
        {
          "tag_name": "v0.2.0",
          "prerelease": false,
          "assets": [
            { "name": "bmd-win-x64.zip",
              "browser_download_url": "https://downloads.example.test/bmd-win-x64.zip" },
            { "name": "bmd-linux-x64.tar.gz",
              "browser_download_url": "https://downloads.example.test/bmd-linux-x64.tar.gz" },
            { "name": "checksums.txt",
              "browser_download_url": "https://downloads.example.test/checksums.txt" }
          ]
        }
        """;

    static ReleaseClient ClientFor(FakeHttpHandler handler) =>
        new(new HttpClient(handler), ApiBase);

    [Fact]
    public async Task GetLatestReleaseAsync_ParsesTagAssetsAndPreReleaseFlag()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, LatestJson);

        var release = await ClientFor(handler).GetLatestReleaseAsync(CancellationToken.None);

        Assert.Equal("v0.2.0", release.TagName);
        Assert.False(release.PreRelease);
        Assert.Equal(3, release.Assets.Length);
        Assert.Equal(LatestUrl, Assert.Single(handler.Requests));
    }

    [Fact]
    public void CreateHttpClient_SendsAUserAgentBecauseGitHubRejectsRequestsWithout()
    {
        // GitHub answers 403 to an API request carrying no User-Agent, so this is not cosmetic.
        using var http = ReleaseClient.CreateHttpClient("0.1.0");

        var agent = Assert.Single(http.DefaultRequestHeaders.UserAgent);
        Assert.Equal("bmd", agent.Product?.Name);
        Assert.Equal("0.1.0", agent.Product?.Version);
        Assert.Contains(http.DefaultRequestHeaders.Accept,
            media => media.MediaType == "application/vnd.github+json");
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsAReadableUpdateExceptionOnHttpFailure()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, "rate limited", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => ClientFor(handler).GetLatestReleaseAsync(CancellationToken.None));

        Assert.Contains("403", ex.Message);
        Assert.DoesNotContain("Exception", ex.Message);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsUpdateExceptionOnMalformedJson()
    {
        var handler = new FakeHttpHandler().Text(LatestUrl, "{ this is not json");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => ClientFor(handler).GetLatestReleaseAsync(CancellationToken.None));

        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsUpdateExceptionWhenTheNetworkIsUnreachable()
    {
        var handler = new FakeHttpHandler().Throws(LatestUrl, new HttpRequestException("no such host"));

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => ClientFor(handler).GetLatestReleaseAsync(CancellationToken.None));

        Assert.Contains("no such host", ex.Message);
    }

    [Fact]
    public async Task GetTextAsync_ReturnsTheBody()
    {
        const string url = "https://downloads.example.test/checksums.txt";
        var handler = new FakeHttpHandler().Text(url, "abc  bmd-win-x64.zip\n");

        var text = await ClientFor(handler).GetTextAsync(url, CancellationToken.None);

        Assert.Equal("abc  bmd-win-x64.zip\n", text);
    }

    [Fact]
    public async Task DownloadToFileAsync_WritesTheBytesToDisk()
    {
        const string url = "https://downloads.example.test/bmd-win-x64.zip";
        var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 };
        var handler = new FakeHttpHandler().Bytes(url, payload);
        var destination = Path.Combine(Path.GetTempPath(), $"bmd-dl-{Guid.NewGuid():N}.zip");

        try
        {
            await ClientFor(handler).DownloadToFileAsync(url, destination, CancellationToken.None);
            Assert.Equal(payload, File.ReadAllBytes(destination));
        }
        finally { if (File.Exists(destination)) File.Delete(destination); }
    }

    [Fact]
    public async Task DownloadToFileAsync_LeavesNoPartialFileWhenTheServerErrors()
    {
        const string url = "https://downloads.example.test/bmd-win-x64.zip";
        var handler = new FakeHttpHandler().Text(url, "gone", HttpStatusCode.NotFound);
        var destination = Path.Combine(Path.GetTempPath(), $"bmd-dl-{Guid.NewGuid():N}.zip");

        try
        {
            await Assert.ThrowsAsync<UpdateException>(
                () => ClientFor(handler).DownloadToFileAsync(url, destination, CancellationToken.None));
            Assert.False(File.Exists(destination));
        }
        finally { if (File.Exists(destination)) File.Delete(destination); }
    }

    [Theory]
    [InlineData("win-x64", "bmd-win-x64.zip")]
    [InlineData("linux-x64", "bmd-linux-x64.tar.gz")]
    [InlineData("linux-arm64", "bmd-linux-arm64.tar.gz")]
    [InlineData("osx-x64", "bmd-osx-x64.tar.gz")]
    [InlineData("osx-arm64", "bmd-osx-arm64.tar.gz")]
    public void ArchiveName_MatchesTheNamesTheReleaseWorkflowPublishes(string rid, string expected)
    {
        Assert.Equal(expected, ReleaseInfo.ArchiveName(rid));
    }

    [Fact]
    public void FindAsset_MatchesByExactNameAndReturnsNullOtherwise()
    {
        var release = new ReleaseInfo("v0.2.0", false,
        [
            new ReleaseAsset("bmd-win-x64.zip", "https://downloads.example.test/bmd-win-x64.zip"),
        ]);

        Assert.NotNull(release.FindAsset("bmd-win-x64.zip"));
        Assert.Null(release.FindAsset("bmd-osx-arm64.tar.gz"));
    }
}
