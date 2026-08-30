namespace Bmd.Commands;

/// <summary>The `bmd update --check` result: the version of this binary, the newest published
/// release, and whether the latter is newer than the former.</summary>
public sealed record UpdateCheckResult(string CurrentVersion, string LatestVersion, bool UpdateAvailable);

/// <summary>The `bmd update` result. <c>Updated</c> is false when the binary was already current,
/// in which case <c>Path</c> is null and nothing on disk was touched.</summary>
public sealed record UpdateResult(string CurrentVersion, string LatestVersion, bool Updated, string? Path);
