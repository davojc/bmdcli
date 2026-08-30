using System.Text.Json;
using Bmd.Output;
using Bmd.Update;

namespace Bmd.Commands;

/// <summary>bmd update — check for, and install, a newer release of bmd itself.</summary>
public class UpdateCommands
{
    readonly ReleaseClient _client;
    readonly string _currentVersion;
    readonly string _runtimeIdentifier;
    readonly string? _executablePath;

    public UpdateCommands()
        : this(new ReleaseClient(ReleaseClient.CreateHttpClient(BuildInfo.Version)),
               BuildInfo.Version, BuildInfo.RuntimeIdentifier, Environment.ProcessPath)
    {
    }

    /// <summary>Test seam: the release source, the identity this binary claims, and the file that
    /// would be replaced. Tests pass a dummy executable path — the swap must never be pointed at
    /// the running test host.</summary>
    public UpdateCommands(ReleaseClient client, string currentVersion, string runtimeIdentifier,
        string? executablePath)
    {
        _client = client;
        _currentVersion = currentVersion;
        _runtimeIdentifier = runtimeIdentifier;
        _executablePath = executablePath;
    }

    /// <summary>Download and install the newest release of bmd, replacing this binary in place. The download is verified against the release's SHA-256 checksums before anything is replaced; a mismatch aborts with nothing changed. Pre-release versions are never offered.</summary>
    /// <param name="check">Report whether a newer release exists and exit without downloading or changing anything.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    /// <param name="ct">Cancelled by Ctrl+C.</param>
    public async Task<int> Update(bool check = false, bool json = false, CancellationToken ct = default)
    {
        try
        {
            var release = await _client.GetLatestReleaseAsync(ct);

            if (!SemVer.TryParse(release.TagName, out var latest))
                throw new UpdateException(
                    $"the latest release is tagged '{release.TagName}', which is not a version bmd can compare");
            if (!SemVer.TryParse(_currentVersion, out var current))
                throw new UpdateException(
                    $"this binary reports version '{_currentVersion}', which is not a version bmd can compare");

            var updateAvailable = latest.CompareTo(current) > 0;

            if (check)
            {
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(
                        new UpdateCheckResult(current.ToString(), latest.ToString(), updateAvailable),
                        BmdJsonContext.Default.UpdateCheckResult));
                else if (updateAvailable)
                    Console.WriteLine($"A new release of bmd is available: {current} → {latest}{Environment.NewLine}Run `bmd update` to upgrade.");
                else
                    Console.WriteLine($"bmd {current} is the latest version.");
                return 0;
            }

            // The install path lands in Task 5.
            if (!updateAvailable)
            {
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(
                        new UpdateResult(current.ToString(), latest.ToString(), false, null),
                        BmdJsonContext.Default.UpdateResult));
                else
                    Console.WriteLine($"bmd {current} is already the latest version.");
                return 0;
            }

            throw new UpdateException("installing updates is not implemented yet");
        }
        catch (UpdateException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("error: cancelled");
            return 1;
        }
    }
}
