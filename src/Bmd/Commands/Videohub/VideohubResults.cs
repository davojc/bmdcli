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
