using System.Text.Json;
using Bmd.Output;

namespace Bmd.Commands;

/// <summary>bmd version — what this binary is.</summary>
public class VersionCommands
{
    /// <summary>Show the version and platform of this bmd binary.</summary>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public int Version(bool json = false)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(
                new VersionResult(BuildInfo.Version, BuildInfo.RuntimeIdentifier),
                BmdJsonContext.Default.VersionResult));
        else
            Console.WriteLine($"bmd {BuildInfo.Version} ({BuildInfo.RuntimeIdentifier})");
        return 0;
    }
}

/// <summary>The bmd version result: the build-stamped version string and target runtime
/// identifier (e.g. "win-x64", or "unknown" for a build that didn't specify one).</summary>
public sealed record VersionResult(string Version, string RuntimeIdentifier);
