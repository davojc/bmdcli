using System.Formats.Tar;
using System.IO.Compression;

namespace Bmd.Update;

/// <summary>Unpacks a downloaded release archive and puts the new binary where the running one
/// lives. Every operation here is deliberately reversible up to the final rename: the caller has
/// already verified the archive's checksum, and this class must still leave a working bmd behind
/// if the filesystem refuses partway through.</summary>
public static class UpdateInstaller
{
    /// <summary>What the executable is called inside the release archive — the release workflow
    /// packages the published binary under its own name, `bmd.exe` on Windows and `bmd`
    /// elsewhere.</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "bmd.exe" : "bmd";

    /// <summary>Suffix the outgoing binary is renamed to on Windows, where a running image cannot
    /// be overwritten but can be renamed.</summary>
    public const string OldSuffix = ".old";

    /// <summary>Shared wording for a permissions failure while writing to the install directory.
    /// Used both by the final rename here and by the staging-directory creation in
    /// <c>UpdateCommands</c> — one string instead of two copies that could drift apart.</summary>
    public const string ElevatedPromptGuidance =
        "re-run bmd update from an elevated prompt, or reinstall bmd somewhere you can write";

    /// <summary>Extracts <paramref name="archivePath"/> into <paramref name="destinationDirectory"/>
    /// and returns the path of the bmd executable inside it. The archive format is chosen from
    /// <paramref name="assetName"/>'s extension rather than sniffed, because the release workflow
    /// controls both and a surprise is a bug worth failing on.
    ///
    /// Both BCL extractors reject entries that would escape the destination directory, so a
    /// tampered archive cannot write outside <paramref name="destinationDirectory"/> — and the
    /// checksum has already been verified before this is ever called.</summary>
    public static string ExtractExecutable(string archivePath, string assetName, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            if (assetName.EndsWith(".zip", StringComparison.Ordinal))
            {
                ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
            }
            else if (assetName.EndsWith(".tar.gz", StringComparison.Ordinal))
            {
                using var input = File.OpenRead(archivePath);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, destinationDirectory, overwriteFiles: true);
            }
            else
            {
                throw new UpdateException($"release asset '{assetName}' is not an archive bmd knows how to open");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"could not unpack {assetName}: {ex.Message}");
        }

        var executable = Path.Combine(destinationDirectory, ExecutableName);
        if (!File.Exists(executable))
            throw new UpdateException($"release asset '{assetName}' does not contain a '{ExecutableName}' executable");
        return executable;
    }

    /// <summary>Moves <paramref name="newExecutablePath"/> onto <paramref name="currentExecutablePath"/>.
    ///
    /// On Unix a rename over a running executable is atomic and safe — the running process keeps
    /// the old inode open. On Windows the running image is locked against overwrite but not
    /// against rename, so the outgoing binary is moved aside to `<name>.old` first (the approach
    /// gh and rustup take) and cleaned up by a later run. If the second move fails, the `.old`
    /// file is moved back, so a failed update always leaves a working bmd in place.
    ///
    /// Both paths must be on the same volume for the rename to be atomic, which is why callers
    /// extract into a directory beside the current executable rather than into the system temp
    /// directory.</summary>
    public static void Replace(string newExecutablePath, string currentExecutablePath) =>
        Replace(newExecutablePath, currentExecutablePath, restoreMove: File.Move);

    /// <summary>Test seam: <paramref name="restoreMove"/> stands in for the final "move .old back
    /// onto the current path" step taken after a failed replacement. Production always passes
    /// <see cref="File.Move(string, string, bool)"/> via the public overload above; a test can
    /// substitute a throwing delegate to exercise the "restore itself also failed" branch, which
    /// cannot be reached through real filesystem locks alone — the same file is renamed twice in
    /// the sequence (current → .old, then .old → current), and Windows' share-mode check has no
    /// way to allow the first rename but block the second on one held handle.</summary>
    internal static void Replace(string newExecutablePath, string currentExecutablePath,
        Action<string, string, bool> restoreMove)
    {
        if (!File.Exists(newExecutablePath))
            throw new UpdateException($"the extracted executable is missing from {newExecutablePath}");

        if (!OperatingSystem.IsWindows())
        {
            // Owner rwx, group and world r-x: the same shape a package manager would install.
            File.SetUnixFileMode(newExecutablePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Move(newExecutablePath, currentExecutablePath, overwrite: true,
                $"could not install the new binary at {currentExecutablePath}");
            return;
        }

        var old = currentExecutablePath + OldSuffix;
        TryDelete(old);
        if (File.Exists(old))
            throw new UpdateException(
                $"{old} is left over from an earlier update and could not be removed — close any running bmd and try again");

        var movedAside = File.Exists(currentExecutablePath);
        if (movedAside)
            Move(currentExecutablePath, old, overwrite: false, $"could not move the current binary aside to {old}");
        try
        {
            Move(newExecutablePath, currentExecutablePath, overwrite: false,
                $"could not install the new binary at {currentExecutablePath}");
        }
        catch
        {
            if (movedAside) TryRestore(old, currentExecutablePath, restoreMove);
            throw;
        }
    }

    /// <summary>Deletes the `<name>.old` left behind by a Windows update once the process holding
    /// it has exited. Best effort and silent: it is 5 MB of tidiness, never a reason to fail
    /// anything.</summary>
    public static void CleanUpPreviousUpdate(string currentExecutablePath) =>
        TryDelete(currentExecutablePath + OldSuffix);

    /// <summary><paramref name="failureDescription"/> says which half of the swap this call
    /// represents — moving the outgoing binary aside, or moving the new one into place — so a
    /// failure names the step that actually failed instead of a single generic "could not
    /// install" that fits both.</summary>
    static void Move(string source, string destination, bool overwrite, string failureDescription)
    {
        try
        {
            File.Move(source, destination, overwrite);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UpdateException(
                $"cannot write to {Path.GetDirectoryName(destination)}: {ex.Message} — {ElevatedPromptGuidance}");
        }
        catch (IOException ex)
        {
            throw new UpdateException($"{failureDescription}: {ex.Message}");
        }
    }

    static void TryRestore(string old, string currentExecutablePath, Action<string, string, bool> restoreMove)
    {
        try { restoreMove(old, currentExecutablePath, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original is still on disk under its .old name; say so rather than pretend.
            throw new UpdateException(
                $"the update failed and the previous binary could not be moved back — it is at {old}");
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
