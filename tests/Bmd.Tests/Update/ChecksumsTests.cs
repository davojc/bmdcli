using System.Security.Cryptography;
using System.Text;
using Bmd.Update;

namespace Bmd.Tests.Update;

public class ChecksumsTests
{
    // The exact shape `sha256sum * > checksums.txt` produces in .github/workflows/release.yml:
    // a 64-character lowercase digest, two spaces, the bare archive name.
    const string RealisticFile =
        "3b1f2c4d5e6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e  bmd-linux-x64.tar.gz\n" +
        "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90  bmd-win-x64.zip\n";

    [Fact]
    public void TryFind_ReturnsTheDigestForTheNamedAsset()
    {
        Assert.True(Checksums.TryFind(RealisticFile, "bmd-win-x64.zip", out var digest));
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", digest);
    }

    [Fact]
    public void TryFind_IsNotFooledByAPrefixOfAnotherName()
    {
        Assert.False(Checksums.TryFind(RealisticFile, "bmd-win-x64", out _));
        Assert.False(Checksums.TryFind(RealisticFile, "bmd-osx-arm64.tar.gz", out _));
    }

    [Fact]
    public void TryFind_AcceptsTheBinaryModeStarAndCrLfLineEndings()
    {
        const string text =
            "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90 *bmd-win-x64.zip\r\n";
        Assert.True(Checksums.TryFind(text, "bmd-win-x64.zip", out var digest));
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", digest);
    }

    [Fact]
    public void TryFind_NormalizesUppercaseDigestsToLowercase()
    {
        const string text =
            "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90  bmd-win-x64.zip\n";
        Assert.True(Checksums.TryFind(text, "bmd-win-x64.zip", out var digest));
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", digest);
    }

    [Fact]
    public void TryFind_SkipsBlankAndMalformedLinesRatherThanFailing()
    {
        const string text =
            "\n" +
            "# a comment some future release job might add\n" +
            "not-a-digest  bmd-win-x64.zip\n" +
            "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90  bmd-win-x64.zip\n";
        Assert.True(Checksums.TryFind(text, "bmd-win-x64.zip", out var digest));
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", digest);
    }

    [Fact]
    public void TryFind_ReturnsFalseForAnAssetThatIsNotListed()
    {
        Assert.False(Checksums.TryFind(RealisticFile, "bmd-osx-x64.tar.gz", out var digest));
        Assert.Equal("", digest);
    }

    [Fact]
    public void TryFind_ReturnsFalseForEmptyText()
    {
        Assert.False(Checksums.TryFind("", "bmd-win-x64.zip", out _));
    }

    [Fact]
    public void OfFile_MatchesAKnownSha256()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bmd-checksum-{Guid.NewGuid():N}.bin");
        var content = "the quick brown fox"u8.ToArray();
        File.WriteAllBytes(path, content);
        try
        {
            var expected = Convert.ToHexStringLower(SHA256.HashData(content));
            Assert.Equal(expected, Checksums.OfFile(path));
            Assert.Equal(64, Checksums.OfFile(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OfFile_DiffersWhenAsingleByteChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-checksum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var a = Path.Combine(directory, "a.bin");
            var b = Path.Combine(directory, "b.bin");
            File.WriteAllBytes(a, [1, 2, 3, 4]);
            File.WriteAllBytes(b, [1, 2, 3, 5]);
            Assert.NotEqual(Checksums.OfFile(a), Checksums.OfFile(b));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
