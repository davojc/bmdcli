using System.Text.Json;
using Bmd.Config;

namespace Bmd.Update;

/// <summary>The result of the last passive update check: the newest release version seen, and
/// when it was seen.</summary>
public sealed record UpdateCheckEntry(string LatestVersion, DateTimeOffset CheckedAt);

/// <summary>The once-a-day passive check's memory, stored in the OS cache directory.
///
/// Every operation here is best-effort by design: a passive check exists to be helpful, so a
/// missing, corrupt, or unwritable cache file must degrade to "no notice this run" and never
/// surface an error or fail the command the user actually asked for.</summary>
public sealed class UpdateCheckCache(string filePath)
{
    /// <summary>How long a cached result is trusted before the next check is allowed.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public string FilePath { get; } = filePath;

    public static UpdateCheckCache Default() =>
        new(Path.Combine(ConfigPaths.CacheDirectory, "update-check.json"));

    /// <summary>The cached entry, or null when there is none, it cannot be read, or it does not
    /// carry a version.</summary>
    public UpdateCheckEntry? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var entry = JsonSerializer.Deserialize(
                File.ReadAllText(FilePath), UpdateJsonContext.Default.UpdateCheckEntry);
            return string.IsNullOrWhiteSpace(entry?.LatestVersion) ? null : entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Write(UpdateCheckEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(entry, UpdateJsonContext.Default.UpdateCheckEntry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do and nothing worth saying: the next run simply checks again.
        }
    }

    /// <summary>Whether a fresh check is due. A missing entry is stale, and so is one stamped in
    /// the future — clock skew or a hand-edited file must not pin the cache as fresh forever.</summary>
    public static bool IsStale(UpdateCheckEntry? entry, DateTimeOffset now) =>
        entry is null || entry.CheckedAt > now || now - entry.CheckedAt >= MaxAge;
}
