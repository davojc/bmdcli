namespace Bmd.Commands.MultiView;

/// <summary>`bmd multiview info`. The device reports six "video outputs" on the wire; they are
/// the multiview's windows plus its Solo and Audio inputs, so the field is named views.</summary>
public sealed record MultiViewInfoResult(
    string Model, string? FriendlyName, string Protocol, int Inputs, int Views,
    string? Layout, string? OutputFormat);

public sealed record MultiViewInputEntry(int N, string Label);

/// <summary>One multiview window: its label, the source feeding it, and its lock state.</summary>
public sealed record MultiViewViewEntry(int N, string Label, int Input, string InputLabel, string Lock);

/// <summary>One CONFIGURATION property, named exactly as the device spelled it.</summary>
public sealed record MultiViewConfigEntry(string Name, string Value);

public sealed record MultiViewRouteSetResult(
    int View, string ViewLabel, int Input, string InputLabel, string? Backup);

/// <summary>Kind is "input" or "view", so one record covers both renames.</summary>
public sealed record MultiViewRenameResult(
    string Kind, int N, string OldLabel, string NewLabel, string? Backup);

public sealed record MultiViewLockResult(int View, string ViewLabel, string Lock, string? Backup);

/// <summary>One CONFIGURATION property write. Setting is the protocol's own property name, so
/// the output says exactly what was sent to the device.</summary>
public sealed record MultiViewConfigSetResult(string Setting, string Value, string? Backup);

/// <summary>Result of `multiview export`: what was captured and where it went.</summary>
public sealed record MultiViewExportResult(
    string Device, int Inputs, int Views, int Routes, string? File, bool Verified);

/// <summary>One change applied (or that would be applied) by `multiview restore`. Kind is
/// "inputLabel", "viewLabel", "route", or "config"; N is the 1-based input/view number for the
/// first three kinds (0 for "config", which instead names the changed property via Setting);
/// From/To are the previous and new value (for a route, the previous and new input's label).</summary>
public sealed record MultiViewRestoreChangeResult(string Kind, int N, string? Setting, string From, string To);

/// <summary>Result of `multiview restore`: the snapshot file and device, how many changes were
/// found and how many were actually applied (equal on full success, less than Changes on a dry
/// run or a run stopped early by a timeout or rejection), whether this was a dry run, the
/// pre-change backup path (null if skipped or dry-run), and the changes themselves in the order
/// they were (or would be) applied.</summary>
public sealed record MultiViewRestoreResult(
    string File, string Device, int Changes, int Applied, bool DryRun, string? Backup,
    MultiViewRestoreChangeResult[] Details);

/// <summary>One line of `multiview watch --json` output (JSON Lines: one object per update, not
/// a single document, since a stream has no end). Kind is "inputLabel", "viewLabel", "route", or
/// "lock"; N is the 1-based input/view number; From/To are the previous and new label (for a
/// route, the previous and new input's label; for a lock, the previous and new lock word).</summary>
public sealed record MultiViewUpdateResult(string Kind, int N, string From, string To);
