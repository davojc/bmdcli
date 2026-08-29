namespace Bmd.Commands;

/// <summary>JSON shape for one discovered device. <c>Address</c> is the plain textual form of
/// the device's IP (no port, no brackets around an IPv6 literal) — the port is its own field —
/// so a caller can pass it straight to whatever needs a bare host string.</summary>
public sealed record DiscoveredDeviceResult(string Name, string DeviceClass, string? DeviceType, string Address, int Port);
