using System.Text.Json.Serialization;

namespace Bmd.Update;

/// <summary>An update failed for a reason the user can act on. Carries a message that is already
/// fit to print after "error: " — no stack trace, no exception type names.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>One downloadable file attached to a GitHub release. Property names are pinned with
/// explicit attributes rather than left to a naming policy: this is an external API's contract,
/// not ours, and <c>browser_download_url</c> is not any camelCase policy's output.</summary>
public sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl);

/// <summary>A GitHub release, as much of it as updating needs.</summary>
public sealed record ReleaseInfo(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("prerelease")] bool PreRelease,
    [property: JsonPropertyName("assets")] ReleaseAsset[] Assets)
{
    public ReleaseAsset? FindAsset(string name) =>
        Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.Ordinal));

    /// <summary>The archive name the release workflow publishes for a runtime identifier.
    /// Keyed off the RID string rather than the running OS so it stays a pure function —
    /// and so a win-x64 binary asks for the zip even when something exotic hosts it.
    /// Mirrors the `archive:` entries in .github/workflows/release.yml; the Unix RIDs use
    /// tar.gz because zip does not reliably preserve the executable bit.</summary>
    public static string ArchiveName(string runtimeIdentifier) =>
        runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal)
            ? $"bmd-{runtimeIdentifier}.zip"
            : $"bmd-{runtimeIdentifier}.tar.gz";

    /// <summary>The checksums file every release carries, listing the SHA-256 of each archive.</summary>
    public const string ChecksumsAssetName = "checksums.txt";
}
