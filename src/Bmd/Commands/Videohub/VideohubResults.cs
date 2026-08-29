namespace Bmd.Commands.Videohub;

public sealed record VideohubInfoResult(
    string ModelName, string? FriendlyName, string ProtocolVersion, int VideoInputs, int VideoOutputs);
