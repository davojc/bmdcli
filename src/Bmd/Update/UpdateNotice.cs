using Bmd.Config;

namespace Bmd.Update;

/// <summary>The decision and the wording behind the passive "a new release is available" notice.
/// Both halves are pure so the rules can be tested without a network, a terminal, or a clock.</summary>
public static class UpdateNotice
{
    /// <summary>The config key that turns the passive check off entirely.</summary>
    public const string ConfigKeyName = "update.check";

    /// <summary>Whether this invocation may run a passive check and print a notice. The four
    /// suppression rules come straight from the spec: the notice is a courtesy for a human
    /// watching a terminal, so it stays out of pipes, out of machine-readable output, and out of
    /// the two commands that already report versions themselves.</summary>
    public static bool IsEligible(string[] args, ConfigStore config, bool errorIsRedirected)
    {
        if (errorIsRedirected) return false;
        if (args.Contains("--json", StringComparer.Ordinal)) return false;
        if (args.Length > 0 && args[0] is "update" or "version") return false;

        if (!ConfigKey.TryParse(ConfigKeyName, out var key)) return false;
        var configured = config.GetEffective(key);
        return configured is null || !configured.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The two-line notice, or null when there is nothing worth saying — no cached
    /// version, a version that is not newer, or either version being unparsable. Unparsable is
    /// silence rather than an error: a passive check must never turn into a complaint about the
    /// command the user actually ran.</summary>
    public static string? Format(string currentVersion, string? latestVersion)
    {
        if (latestVersion is null) return null;
        if (!SemVer.TryParse(currentVersion, out var current)) return null;
        if (!SemVer.TryParse(latestVersion, out var latest)) return null;
        if (latest.CompareTo(current) <= 0) return null;

        return $"A new release of bmd is available: {current} → {latest}{Environment.NewLine}" +
               "Run `bmd update` to upgrade.";
    }
}
