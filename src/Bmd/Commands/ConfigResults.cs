namespace Bmd.Commands;

public sealed record ConfigGetResult(string Key, string Value, string? Origin);
public sealed record ConfigSetResult(string Key, string Value, string File);
public sealed record ConfigUnsetResult(string Key, bool Removed);
