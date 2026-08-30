namespace Bmd.Commands.Atem;

/// <summary>`bmd atem info --json`.</summary>
public sealed record AtemInfoResult(
    string Model, string Protocol, int MixEffects, int Sources, int DownstreamKeyers,
    int Auxiliaries, int MediaPlayers, int VideoMode);

/// <summary>One row of `bmd atem input list --json`. <c>Name</c> and <c>ShortName</c> are empty
/// strings for an input nobody has named — the captured switcher has two.</summary>
public sealed record AtemSourceEntry(int Id, string Name, string ShortName, bool External);

/// <summary>One row of `bmd atem aux list --json`. <c>Aux</c> is 1-based.</summary>
public sealed record AtemAuxEntry(int Aux, int Source, string SourceName);

public sealed record AtemStatusResult(
    int ProgramSource, string ProgramName, int PreviewSource, string PreviewName);

public sealed record AtemRenameResult(
    int Id, string Name, string ShortName, string? Backup);

public sealed record AtemAuxSetResult(
    int Aux, int Source, string SourceName, string? Backup);

public sealed record AtemBusSetResult(
    string Bus, int Source, string SourceName, string? Backup);
