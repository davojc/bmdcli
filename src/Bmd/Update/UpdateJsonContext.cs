using System.Text.Json.Serialization;

namespace Bmd.Update;

/// <summary>Source-generated JSON for everything the update feature reads or writes: the
/// GitHub Releases API responses and bmd's own update-check cache file. Separate from
/// <c>BmdJsonContext</c>, which is strictly the shape of <c>--json</c> command output — these
/// types are internal plumbing and are not part of the CLI's published contract.
///
/// Reflection-based serialization is forbidden project-wide (Native AOT), so every type
/// crossing this boundary is registered here.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateCheckEntry))]
public partial class UpdateJsonContext : JsonSerializerContext;
