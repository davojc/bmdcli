namespace Bmd.Config;

public static class ConfigPaths
{
    public const string LocalFileName = ".bmdconfig";

    public static string GlobalConfigPath
    {
        get
        {
            string baseDir;
            if (OperatingSystem.IsWindows())
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            else
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                baseDir = string.IsNullOrEmpty(xdg)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                    : xdg;
            }
            return Path.Combine(baseDir, "bmd", "config");
        }
    }

    /// <summary>OS state directory for data bmd keeps between runs (backups) —
    /// distinct from config (settings) and cache (disposable).</summary>
    public static string StateDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bmd");
            var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            return string.IsNullOrEmpty(xdg)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "bmd")
                : Path.Combine(xdg, "bmd");
        }
    }

    /// <summary>OS cache directory for data bmd can lose without consequence — currently just
    /// the once-a-day update check result. Distinct in meaning from config (settings the user
    /// owns) and state (backups, which must survive).
    ///
    /// On Windows this resolves to the same <c>%LOCALAPPDATA%\bmd</c> as
    /// <see cref="StateDirectory"/>, deliberately: Windows has no separate cache convention, and
    /// the spec names this exact path. The two do not collide — backups live in the
    /// <c>backups\</c> subdirectory, the update check is the file <c>update-check.json</c>.</summary>
    public static string CacheDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bmd");
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            return string.IsNullOrEmpty(xdg)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "bmd")
                : Path.Combine(xdg, "bmd");
        }
    }

    public static string? FindLocalConfig(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, LocalFileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
