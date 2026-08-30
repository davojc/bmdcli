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
