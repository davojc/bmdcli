using System.Formats.Tar;
using System.IO.Compression;
using Bmd.Update;

namespace Bmd.Tests.Update;

public class UpdateInstallerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"bmd-install-{Guid.NewGuid():N}");

    public UpdateInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Path_(params string[] parts) => Path.Combine([_root, .. parts]);

    /// <summary>Builds a zip holding one entry, the way the release workflow's
    /// Compress-Archive step does.</summary>
    string MakeZip(string entryName, byte[] content)
    {
        var archive = Path_("bmd-win-x64.zip");
        using var stream = File.Create(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry(entryName).Open();
        entry.Write(content);
        return archive;
    }

    /// <summary>Builds a gzipped tar holding one entry, the way the release workflow's
    /// `tar czf` step does.</summary>
    string MakeTarGz(string entryName, byte[] content)
    {
        var archive = Path_("bmd-linux-x64.tar.gz");
        var staging = Path_("staging");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, entryName), content);
        using (var output = File.Create(archive))
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            TarFile.CreateFromDirectory(staging, gzip, includeBaseDirectory: false);
        Directory.Delete(staging, recursive: true);
        return archive;
    }

    [Fact]
    public void ExtractExecutable_PullsTheBinaryOutOfAZip()
    {
        var payload = "new bmd"u8.ToArray();
        // Entry name matches UpdateInstaller.ExecutableName rather than a literal "bmd.exe" so
        // this test passes on every host OS: on Linux/macOS ExtractExecutable looks for "bmd"
        // inside the extracted directory regardless of the archive format under test.
        var archive = MakeZip(UpdateInstaller.ExecutableName, payload);
        var destination = Path_("extracted-zip");

        var extracted = UpdateInstaller.ExtractExecutable(archive, "bmd-win-x64.zip", destination);

        Assert.True(File.Exists(extracted));
        Assert.Equal(payload, File.ReadAllBytes(extracted));
    }

    [Fact]
    public void ExtractExecutable_PullsTheBinaryOutOfATarGz()
    {
        var payload = "new bmd"u8.ToArray();
        // Entry name matches UpdateInstaller.ExecutableName rather than a literal "bmd" so this
        // test passes on every host OS: on Windows ExtractExecutable looks for "bmd.exe" inside
        // the extracted directory regardless of the archive format under test.
        var archive = MakeTarGz(UpdateInstaller.ExecutableName, payload);
        var destination = Path_("extracted-tar");

        var extracted = UpdateInstaller.ExtractExecutable(archive, "bmd-linux-x64.tar.gz", destination);

        Assert.True(File.Exists(extracted));
        Assert.Equal(payload, File.ReadAllBytes(extracted));
    }

    [Fact]
    public void ExtractExecutable_ThrowsWhenTheArchiveDoesNotContainABmdBinary()
    {
        var archive = MakeZip("readme.txt", "not a binary"u8.ToArray());
        var destination = Path_("extracted-wrong");

        var ex = Assert.Throws<UpdateException>(
            () => UpdateInstaller.ExtractExecutable(archive, "bmd-win-x64.zip", destination));

        Assert.Contains("bmd", ex.Message);
    }

    [Fact]
    public void ExtractExecutable_ThrowsForAnUnrecognizedArchiveExtension()
    {
        var archive = Path_("bmd-win-x64.rar");
        File.WriteAllBytes(archive, [1, 2, 3]);

        Assert.Throws<UpdateException>(
            () => UpdateInstaller.ExtractExecutable(archive, "bmd-win-x64.rar", Path_("out")));
    }

    [Fact]
    public void ExtractExecutable_ThrowsForACorruptArchiveRatherThanLeakingTheRawException()
    {
        var archive = Path_("bmd-win-x64.zip");
        File.WriteAllBytes(archive, "definitely not a zip"u8.ToArray());

        Assert.Throws<UpdateException>(
            () => UpdateInstaller.ExtractExecutable(archive, "bmd-win-x64.zip", Path_("out")));
    }

    [Fact]
    public void Replace_PutsTheNewBinaryAtTheCurrentPath()
    {
        // A dummy file stands in for the running executable — never the test host itself.
        var current = Path_("bmd-current.bin");
        var replacement = Path_("bmd-new.bin");
        File.WriteAllText(current, "old");
        File.WriteAllText(replacement, "new");

        UpdateInstaller.Replace(replacement, current);

        Assert.Equal("new", File.ReadAllText(current));
        Assert.False(File.Exists(replacement));
    }

    [Fact]
    public void Replace_LeavesTheOriginalIntactWhenTheReplacementIsMissing()
    {
        var current = Path_("bmd-current.bin");
        File.WriteAllText(current, "old");

        Assert.ThrowsAny<Exception>(
            () => UpdateInstaller.Replace(Path_("does-not-exist.bin"), current));

        Assert.True(File.Exists(current));
        Assert.Equal("old", File.ReadAllText(current));
    }

    [Fact]
    public void Replace_OnWindowsLeavesTheOldBinaryBesideItForLaterCleanup()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix renames over the original; no .old file
        var current = Path_("bmd-current.exe");
        var replacement = Path_("bmd-new.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(replacement, "new");

        UpdateInstaller.Replace(replacement, current);

        Assert.Equal("new", File.ReadAllText(current));
        Assert.True(File.Exists(current + ".old"));
        Assert.Equal("old", File.ReadAllText(current + ".old"));
    }

    [Fact]
    public void Replace_RestoresTheOriginalWhenTheSecondMoveFails()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix renames over the original; no move-aside step
        var current = Path_("bmd-current.exe");
        var replacement = Path_("bmd-new.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(replacement, "new");

        // Holding the staged replacement open with no sharing makes the second File.Move (the
        // one that would put it at `current`) fail after the first move (current -> .old) has
        // already gone through — this is the actual move-aside -> move-fails -> restore sequence,
        // not just the pre-flight guard the replacement-missing test above stops at.
        using (new FileStream(replacement, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => UpdateInstaller.Replace(replacement, current));
        }

        Assert.True(File.Exists(current));
        Assert.Equal("old", File.ReadAllText(current));
        Assert.False(File.Exists(current + UpdateInstaller.OldSuffix));
    }

    [Fact]
    public void Replace_ThrowsNamingTheOldPathWhenTheRestoreAlsoFails()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix renames over the original; no move-aside step
        var current = Path_("bmd-current.exe");
        var replacement = Path_("bmd-new.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(replacement, "new");

        using (new FileStream(replacement, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // The move-aside (current -> .old) runs for real and succeeds; the second move fails
            // for real (the replacement is locked, same as above); only the final restore step
            // is substituted, because Windows' share-mode check has no way to let the first
            // rename of a file through while blocking the second rename of that same file.
            var ex = Assert.Throws<UpdateException>(() => UpdateInstaller.Replace(
                replacement, current,
                restoreMove: (_, _, _) => throw new IOException("simulated restore failure")));

            Assert.Contains(current + UpdateInstaller.OldSuffix, ex.Message);
        }
    }

    [Fact]
    public void Replace_NamesTheMoveAsideStepDistinctlyFromTheMoveInStepWhenItFails()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix has no move-aside step
        var current = Path_("bmd-current.exe");
        var replacement = Path_("bmd-new.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(replacement, "new");

        // FileShare.Read (no Delete) blocks the rename that would move `current` aside, without
        // preventing this test from reading it back afterwards.
        using (new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Assert.Throws<UpdateException>(() => UpdateInstaller.Replace(replacement, current));
            Assert.Contains("move the current binary aside", ex.Message);
            Assert.DoesNotContain("install the new binary", ex.Message);
        }
    }

    [Fact]
    public void Replace_NamesTheMoveInStepDistinctlyFromTheMoveAsideStepWhenItFails()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix has no move-aside step
        var current = Path_("bmd-current.exe");
        var replacement = Path_("bmd-new.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(replacement, "new");

        using (new FileStream(replacement, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<UpdateException>(() => UpdateInstaller.Replace(replacement, current));
            Assert.Contains($"install the new binary at {current}", ex.Message);
            Assert.DoesNotContain("move the current binary aside", ex.Message);
        }
    }

    [Fact]
    public void Replace_OnUnixSetsTheExecuteBit()
    {
        if (OperatingSystem.IsWindows()) return;
        var current = Path_("bmd-current");
        var replacement = Path_("bmd-new");
        File.WriteAllText(current, "old");
        File.WriteAllText(replacement, "new");
        File.SetUnixFileMode(replacement, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        UpdateInstaller.Replace(replacement, current);

        Assert.True(File.GetUnixFileMode(current).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void CleanUpPreviousUpdate_RemovesAStaleOldFile()
    {
        var current = Path_("bmd-current.exe");
        File.WriteAllText(current, "current");
        File.WriteAllText(current + ".old", "stale");

        UpdateInstaller.CleanUpPreviousUpdate(current);

        Assert.False(File.Exists(current + ".old"));
        Assert.True(File.Exists(current));
    }

    [Fact]
    public void CleanUpPreviousUpdate_IsSilentWhenThereIsNothingToClean()
    {
        var current = Path_("bmd-current.exe");
        File.WriteAllText(current, "current");

        UpdateInstaller.CleanUpPreviousUpdate(current); // must not throw

        Assert.True(File.Exists(current));
    }
}
