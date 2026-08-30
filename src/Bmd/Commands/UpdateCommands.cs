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

            return await InstallAsync(release, current, latest, json, ct);
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

    /// <summary>Downloads the asset for this platform, verifies it against the release's
    /// checksums.txt, and swaps it into place. The order matters and is fixed by the spec:
    /// nothing on disk beside the running binary is touched until the checksum matches.</summary>
    async Task<int> InstallAsync(ReleaseInfo release, SemVer current, SemVer latest, bool json,
        CancellationToken ct)
    {
        if (_executablePath is null)
            throw new UpdateException(
                "bmd could not determine its own location, so it cannot replace itself — " +
                $"download the new version from {ReleaseClient.ReleasesPageUrl}");

        if (_runtimeIdentifier is "unknown" or "")
            throw new UpdateException(
                "this build was not published for a specific platform, so bmd update cannot pick a " +
                $"release asset — download the new version from {ReleaseClient.ReleasesPageUrl}");

        var assetName = ReleaseInfo.ArchiveName(_runtimeIdentifier);
        var asset = release.FindAsset(assetName)
            ?? throw new UpdateException($"release {latest} has no asset named {assetName} for this platform");
        var checksumsAsset = release.FindAsset(ReleaseInfo.ChecksumsAssetName)
            ?? throw new UpdateException($"release {latest} has no {ReleaseInfo.ChecksumsAssetName} to verify the download against");

        // A leftover .old from a previous Windows update, now that nothing holds it open.
        UpdateInstaller.CleanUpPreviousUpdate(_executablePath);

        // Staged beside the current executable, not in the system temp directory: the final move
        // must be a same-volume rename to be atomic, and a cross-device move would not be.
        var installDirectory = Path.GetDirectoryName(_executablePath)
            ?? throw new UpdateException($"bmd could not determine the directory of {_executablePath}");
        var staging = Path.Combine(installDirectory, $".bmd-update-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(
                $"cannot write to {installDirectory}: {ex.Message} — {UpdateInstaller.ElevatedPromptGuidance}");
        }

        try
        {
            Progress(json, $"Downloading bmd {latest} ({assetName})...");
            var archivePath = Path.Combine(staging, assetName);
            await _client.DownloadToFileAsync(asset.DownloadUrl, archivePath, ct);

            var checksumsText = await _client.GetTextAsync(checksumsAsset.DownloadUrl, ct);
            if (!Checksums.TryFind(checksumsText, assetName, out var expected))
                throw new UpdateException(
                    $"{ReleaseInfo.ChecksumsAssetName} for release {latest} lists no checksum for {assetName} — nothing was changed");

            var actual = Checksums.OfFile(archivePath);
            if (!actual.Equals(expected, StringComparison.Ordinal))
                throw new UpdateException(
                    $"checksum mismatch for {assetName}: expected {expected}, got {actual} — nothing was changed");
            Progress(json, "Verified SHA-256 checksum.");

            var extracted = UpdateInstaller.ExtractExecutable(archivePath, assetName, Path.Combine(staging, "unpacked"));
            UpdateInstaller.Replace(extracted, _executablePath);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(
                new UpdateResult(current.ToString(), latest.ToString(), true, _executablePath),
                BmdJsonContext.Default.UpdateResult));
        else
            Console.WriteLine($"Updated bmd {current} → {latest} at {_executablePath}");
        return 0;
    }

    /// <summary>Progress goes to stderr, never stdout: with --json, stdout must carry exactly one
    /// document, and progress is not a result. Suppressed entirely under --json so a machine
    /// reader's stderr stays clean.</summary>
    static void Progress(bool json, string message)
    {
        if (!json) Console.Error.WriteLine(message);
    }

    static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
