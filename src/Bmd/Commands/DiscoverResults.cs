namespace Bmd.Commands;

/// <summary>JSON shape for one discovered device. <c>Address</c> is the plain textual form of
/// the device's IP (no port, no brackets around an IPv6 literal) — the port is its own field —
/// so a caller can pass it straight to whatever needs a bare host string. <c>Txt</c> is the
/// device's raw TXT <c>key=value</c> entries, verbatim and in advertised order (empty array
/// when the device sent none) — always present regardless of <c>--all</c>, so the JSON shape
/// stays stable for anything parsing it.</summary>
public sealed record DiscoveredDeviceResult(
    string Name, string DeviceClass, string? DeviceType, string Address, int Port, IReadOnlyList<string> Txt);
