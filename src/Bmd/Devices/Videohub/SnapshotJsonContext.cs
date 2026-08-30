using System.Text.Json.Serialization;

namespace Bmd.Devices.Videohub;

/// <summary>Source-generated JSON for snapshot files (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(VideohubSnapshot))]
[JsonSerializable(typeof(SnapshotConfiguration))]
public partial class SnapshotJsonContext : JsonSerializerContext;
