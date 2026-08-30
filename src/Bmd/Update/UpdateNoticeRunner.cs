using Bmd.Config;

namespace Bmd.Update;

/// <summary>Runs the passive update check alongside the command the user actually asked for, and
/// prints the notice afterwards.
///
/// The check starts before the command and runs concurrently with it, so the command's own work
/// is never blocked. At exit the process gives the fetch a short bounded window
/// (<see cref="JoinWindow"/>) to finish — without it, a background task on a thread-pool thread
/// is simply killed when Main returns and the cache would never be written, which would make the
/// whole feature dead code. That wait is incurred at most once per 24 hours, and only when
/// stderr is a terminal and --json was not passed.</summary>
public sealed class UpdateNoticeRunner
{
    /// <summary>How long the process will wait at exit for an in-flight check.</summary>
    public static readonly TimeSpan JoinWindow = TimeSpan.FromMilliseconds(500);

    readonly string _currentVersion;
    readonly string? _cachedLatest;
    readonly Task<string?>? _fetch;

    UpdateNoticeRunner(string currentVersion, string? cachedLatest, Task<string?>? fetch)
    {
        _currentVersion = currentVersion;
        _cachedLatest = cachedLatest;
        _fetch = fetch;
    }

    /// <summary>A runner that will never fetch and never print.</summary>
    static UpdateNoticeRunner Inert() => new("", null, null);

    /// <summary>The production entry point: reads the real config, the real cache, and the real
    /// releases API. Any failure setting up — an unreadable config file, a missing home
    /// directory — degrades to doing nothing, because a passive check must never be able to break
    /// a command.</summary>
    public static UpdateNoticeRunner Start(string[] args)
    {
        try
        {
            return Start(args, ConfigStore.LoadDefault(), UpdateCheckCache.Default(),
                BuildInfo.Version, Console.IsErrorRedirected, FetchLatestAsync, DateTimeOffset.UtcNow);
        }
        catch
        {
            return Inert();
        }
    }

    static async Task<string?> FetchLatestAsync(CancellationToken ct)
    {
        using var http = ReleaseClient.CreateHttpClient(BuildInfo.Version);
        var release = await new ReleaseClient(http).GetLatestReleaseAsync(ct);
        return SemVer.TryParse(release.TagName, out var version) ? version.ToString() : null;
    }

    /// <summary>Test seam: every input the decision depends on, injected.</summary>
    internal static UpdateNoticeRunner Start(
        string[] args, ConfigStore config, UpdateCheckCache cache, string currentVersion,
        bool errorIsRedirected, Func<CancellationToken, Task<string?>> fetchLatest, DateTimeOffset now)
    {
        if (!UpdateNotice.IsEligible(args, config, errorIsRedirected)) return Inert();

        var entry = cache.Read();
        if (!UpdateCheckCache.IsStale(entry, now))
            return new UpdateNoticeRunner(currentVersion, entry?.LatestVersion, null);

        var fetch = Task.Run(async () =>
        {
            var latest = await fetchLatest(CancellationToken.None);
            if (latest is not null) cache.Write(new UpdateCheckEntry(latest, now));
            return latest;
        });
        return new UpdateNoticeRunner(currentVersion, entry?.LatestVersion, fetch);
    }

    /// <summary>Writes the notice, if there is one. Never throws: a failed passive check is
    /// silent by design, and this runs after the command has already produced its output.</summary>
    public void WriteIfAny(TextWriter writer)
    {
        var latest = _cachedLatest;
        if (_fetch is not null)
        {
            try
            {
                if (_fetch.Wait(JoinWindow)) latest = _fetch.Result ?? latest;
            }
            catch
            {
                // Network down, rate limited, DNS failure — none of it is the user's problem here.
            }
        }

        if (UpdateNotice.Format(_currentVersion, latest) is { } text)
            writer.WriteLine(text);
    }
}
