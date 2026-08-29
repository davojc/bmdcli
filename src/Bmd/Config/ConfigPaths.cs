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
