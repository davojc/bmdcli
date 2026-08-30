using System.Net.Http.Headers;
using System.Text.Json;

namespace Bmd.Update;

/// <summary>Talks to the public GitHub Releases API for <see cref="Repository"/> and downloads
/// release assets. No authentication: the repository is public, and asking a user for a token to
/// install an update would be absurd. Every failure mode is translated into an
/// <see cref="UpdateException"/> whose message is ready to print — the command layer above never
/// has to know what an <c>HttpRequestException</c> is.</summary>
public sealed class ReleaseClient(HttpClient http, string apiBase = ReleaseClient.DefaultApiBase)
{
    public const string Repository = "davojc/bmdcli";
    public const string DefaultApiBase = "https://api.github.com";
    public const string ReleasesPageUrl = "https://github.com/davojc/bmdcli/releases/latest";

    readonly string _apiBase = apiBase.TrimEnd('/');

    /// <summary>An HttpClient configured the way the GitHub API expects: a User-Agent (GitHub
    /// answers 403 without one), the versioned API media type, and a timeout short enough that a
    /// black-holed connection cannot hang the CLI indefinitely.</summary>
    public static HttpClient CreateHttpClient(string userAgentVersion)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("bmd", userAgentVersion));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>The newest non-pre-release release. The <c>/releases/latest</c> endpoint excludes
    /// pre-releases and drafts on GitHub's side, which is exactly the spec's rule — so a user
    /// running v0.1.0-rc.1 is offered v0.1.0, and nobody is ever offered an rc.</summary>
    public async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken ct)
    {
        var url = $"{_apiBase}/repos/{Repository}/releases/latest";
        var body = await GetTextAsync(url, ct);
        try
        {
            return JsonSerializer.Deserialize(body, UpdateJsonContext.Default.ReleaseInfo)
                   ?? throw new UpdateException("the GitHub releases API returned an empty response");
        }
        catch (JsonException ex)
        {
            throw new UpdateException($"the GitHub releases API response could not be read: {ex.Message}");
        }
    }

    public async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Streams a URL to a file. On any failure the partial file is removed, so a caller
    /// can never mistake a truncated download for a complete one — the checksum would catch it
    /// anyway, but leaving debris behind after a failed update is its own small bug.</summary>
    public async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(url, ct);
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using (var destination = File.Create(destinationPath))
                await source.CopyToAsync(destination, ct);
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"could not reach {url}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException($"timed out contacting {url}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new UpdateException($"{url} returned HTTP {status}");
        }
        return response;
    }
}
