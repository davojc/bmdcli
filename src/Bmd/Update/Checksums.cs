using System.Security.Cryptography;

namespace Bmd.Update;

/// <summary>Reads the <c>checksums.txt</c> published with every release and hashes a
/// downloaded file so the two can be compared before anything is installed.</summary>
public static class Checksums
{
    /// <summary>Finds the SHA-256 recorded for <paramref name="assetName"/>, returned as
    /// lowercase hex. Lines are the <c>sha256sum</c> format the release job writes: a
    /// 64-character hex digest, whitespace, then the file name — optionally prefixed with
    /// <c>*</c>, which coreutils emits in binary mode. Lines that don't match that shape are
    /// skipped rather than treated as an error, so a header or comment added to the file in a
    /// future release cannot break updating for already-shipped binaries. First match wins.</summary>
    public static bool TryFind(string checksumsText, string assetName, out string sha256Hex)
    {
        sha256Hex = "";
        foreach (var rawLine in checksumsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var space = line.IndexOfAny([' ', '\t']);
            if (space <= 0) continue;

            var digest = line[..space];
            if (!IsSha256Hex(digest)) continue;

            var name = line[(space + 1)..].TrimStart(' ', '\t');
            if (name.StartsWith('*')) name = name[1..];
            if (!name.Equals(assetName, StringComparison.Ordinal)) continue;

            sha256Hex = digest.ToLowerInvariant();
            return true;
        }
        return false;
    }

    static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>Lowercase hex SHA-256 of a file's contents, streamed rather than buffered so a
    /// multi-megabyte archive never has to be held in memory.</summary>
    public static string OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
