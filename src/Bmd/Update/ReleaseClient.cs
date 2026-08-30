using System.Net.Http.Headers;
using System.Text.Json;

namespace Bmd.Update;

/// <summary>Talks to the public GitHub Releases API for <see cref="Repository"/> and downloads
/// release assets. No authentication: the repository is public, and asking a user for a token to
/// install an update would be absurd. Every failure mode — a non-success status, a connection
/// that never completes, one that drops mid-transfer, a response that isn't valid JSON — is
/// translated into an <see cref="UpdateException"/> whose message is ready to print. The command
/// layer above never has to know what an <c>HttpRequestException</c> is, and it stays able to
/// tell a budget timeout (translated) apart from the caller's own cancellation (left as
/// <see cref="OperationCanceledException"/> so a Ctrl+C is reported as a cancellation, not an
/// update failure).</summary>
public sealed class ReleaseClient(HttpClient http, string apiBase = ReleaseClient.DefaultApiBase)
{
    public const string Repository = "davojc/bmdcli";
    public const string DefaultApiBase = "https://api.github.com";
    public const string ReleasesPageUrl = "https://github.com/davojc/bmdcli/releases/latest";

    /// <summary>Budget for a metadata/text request. Small payloads (release JSON, a checksums
    /// file), so 30 seconds is already generous headroom for a hung connection.</summary>
    public static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Budget for downloading a release archive. Multi-megabyte, so it needs enough
    /// room for a slow connection to finish rather than a fast one — 10 minutes, not 30 seconds.</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    readonly string _apiBase = apiBase.TrimEnd('/');

    /// <summary>An HttpClient configured the way the GitHub API expects: a User-Agent (GitHub
    /// answers 403 without one) and the versioned API media type.</summary>
    public static HttpClient CreateHttpClient(string userAgentVersion)
    {
        var client = new HttpClient
        {
            // HttpClient.Timeout is an end-to-end ceiling on the whole request — including the
            // response body — and ResponseHeadersRead does not exempt it. Left at a fixed value
            // it would bound a multi-megabyte archive download by the same clock that suits a
            // small JSON call. Each request instead gets its own budget via a linked
            // CancellationTokenSource (see ExecuteAsync), sized to what that request is doing.
            Timeout = Timeout.InfiniteTimeSpan
        };
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

    public Task<string> GetTextAsync(string url, CancellationToken ct) =>
        ExecuteAsync(url, MetadataTimeout, ct,
            (response, linkedCt) => response.Content.ReadAsStringAsync(linkedCt));

    /// <summary>Streams a URL to a file. On any failure — including one that happens mid-transfer,
    /// after some bytes have already landed on disk — the partial file is removed, so a caller can
    /// never mistake a truncated download for a complete one. The checksum would catch it anyway,
    /// but leaving debris behind after a failed update is its own small bug.
    ///
    /// A failure to write the bytes locally (permissions, a full disk) is reported as a local
    /// write failure rather than "could not reach" the URL: the two have completely different
    /// remedies, and conflating them sends a user with a full disk hunting for a network problem
    /// they don't have. It is translated here, before the raw <see cref="IOException"/> ever
    /// reaches <see cref="ExecuteAsync{T}"/>'s own translation for a dropped connection.</summary>
    public Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct) =>
        ExecuteAsync<object?>(url, DownloadTimeout, ct, async (response, linkedCt) =>
        {
            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(linkedCt);

                FileStream destination;
                try
                {
                    destination = File.Create(destinationPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new UpdateException($"could not write the download to {destinationPath}: {ex.Message}");
                }

                await using (destination)
                {
                    var buffer = new byte[81920];
                    int read;
                    // A manual copy loop rather than Stream.CopyToAsync: reading from `source`
                    // (the network) and writing to `destination` (local disk) can both throw
                    // IOException, and only keeping them as separate calls lets a write failure
                    // be told apart from a dropped connection.
                    while ((read = await source.ReadAsync(buffer, linkedCt)) != 0)
                    {
                        try
                        {
                            await destination.WriteAsync(buffer.AsMemory(0, read), linkedCt);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            throw new UpdateException($"could not write the download to {destinationPath}: {ex.Message}");
                        }
                    }
                }
            }
            catch
            {
                TryDelete(destinationPath);
                throw;
            }
            return null;
        });

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Runs one GET request end to end — headers, status check, and <paramref
    /// name="readBody"/> — under a single per-operation budget, and translates every failure
    /// along the way into an <see cref="UpdateException"/>. This is the one place that
    /// distinguishes the two things that can cancel the linked token: the caller's own
    /// <paramref name="ct"/> (a real cancellation, left to propagate as-is so the caller can tell
    /// it apart from an update failure) versus the budget's own <see
    /// cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> firing (translated into a timeout
    /// message).
    ///
    /// Connecting and reading the body are translated separately and worded differently: never
    /// reaching <paramref name="url"/> at all is "could not reach", but a connection dropping
    /// after the host already answered — by far the likelier failure for a multi-megabyte archive
    /// — is a different claim, so it gets its own wording instead of being lumped in with "could
    /// not reach". <paramref name="readBody"/> itself (see <see cref="DownloadToFileAsync"/>)
    /// further separates a local disk failure from either of those by translating it to an
    /// <see cref="UpdateException"/> before it ever reaches this method's own catch.</summary>
    async Task<T> ExecuteAsync<T>(string url, TimeSpan budget, CancellationToken ct,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readBody)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(budget);
        var linkedCt = budgetCts.Token;

        HttpResponseMessage response;
        try
        {
            // Checked explicitly rather than left to HttpClient/the handler to notice: a fake
            // handler in tests (or a real one that has already started work) is not guaranteed
            // to observe a token that was already cancelled before the call began.
            linkedCt.ThrowIfCancellationRequested();
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linkedCt);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateException($"could not reach {url}: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linked token fired but the caller's own token did not, so this was the budget
            // expiring, not a Ctrl+C — TaskCanceledException (thrown by HttpClient) derives from
            // OperationCanceledException, so this one filter covers both.
            throw new UpdateException($"timed out contacting {url}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"{url} returned HTTP {(int)response.StatusCode}");

            try
            {
                return await readBody(response, linkedCt);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new UpdateException(
                    $"the connection to {url} was interrupted before the response finished: {ex.Message}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new UpdateException($"timed out contacting {url}");
            }
        }
    }
}
