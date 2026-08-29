namespace Bmd.Commands.Videohub;

public sealed record VideohubInfoResult(
    string ModelName, string? FriendlyName, string ProtocolVersion, int VideoInputs, int VideoOutputs);

/// <summary>One input (1-based) with its label.</summary>
public sealed record VideohubInputEntry(int N, string Label);

/// <summary>One output (1-based) with its label, routed input (1-based), the routed
/// input's label, and lock state (<c>"unlocked"</c>, <c>"owned"</c>, or <c>"locked"</c>).</summary>
public sealed record VideohubOutputEntry(int N, string Label, int Input, string InputLabel, string Lock);

/// <summary>One routed connection: an output (1-based) and the input (1-based) feeding it,
/// with both labels.</summary>
public sealed record VideohubRouteEntry(int Output, string OutputLabel, int Input, string InputLabel);

/// <summary>Result of `videohub export`: what was captured and where it went.</summary>
public sealed record VideohubExportResult(
    string Device, int VideoInputs, int VideoOutputs, int Routes, string? File, bool Verified);

/// <summary>Result of `videohub route set`: the change applied (output/input 1-based, with
/// labels) and what it replaced, plus the pre-change backup path (null if skipped).</summary>
public sealed record VideohubRouteSetResult(
    int Output, string OutputLabel, int Input, string InputLabel,
    int PreviousInput, string PreviousInputLabel, string? Backup);

/// <summary>Result of `videohub input rename` / `videohub output rename`: the entity renamed
/// (1-based), its previous and new labels, and the pre-change backup path (null if skipped).
/// <c>Kind</c> is <c>"input"</c> or <c>"output"</c>.</summary>
public sealed record VideohubRenameResult(string Kind, int N, string PreviousLabel, string Label, string? Backup);

/// <summary>Result of `videohub output lock` / `videohub output unlock`: the output (1-based)
/// with its label, the resulting and previous lock words (<c>"unlocked"</c>, <c>"owned"</c>,
/// or <c>"locked"</c>), and the pre-change backup path (null if skipped).</summary>
public sealed record VideohubLockResult(int Output, string OutputLabel, string Lock, string PreviousLock, string? Backup);

/// <summary>One change applied (or that would be applied) by `videohub restore`. Kind is
/// "inputLabel", "outputLabel", or "route"; N is the 1-based input/output number; From/To
/// are the previous and new label (for a route, the previous and new input's label).</summary>
public sealed record RestoreChangeResult(string Kind, int N, string From, string To);

/// <summary>Result of `videohub restore`: the snapshot file and device, how many changes were
/// found and how many were actually applied (equal on full success, less than Changes on a
/// dry run or a run stopped early by a timeout or rejection), whether this was a dry run, the
/// pre-change backup path (null if skipped or dry-run), and the changes themselves in the
/// order they were (or would be) applied.</summary>
public sealed record VideohubRestoreResult(
    string File, string Device, int Changes, int Applied, bool DryRun, string? Backup,
    RestoreChangeResult[] Details);
