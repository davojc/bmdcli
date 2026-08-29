using System.Text.Json.Serialization;
using Bmd.Commands;
using Bmd.Commands.Videohub;
using Bmd.Config;

namespace Bmd.Output;

/// <summary>Single source-generated JSON context for all --json output (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigGetResult))]
[JsonSerializable(typeof(ConfigSetResult))]
[JsonSerializable(typeof(ConfigUnsetResult))]
[JsonSerializable(typeof(ConfigEntry[]))]
[JsonSerializable(typeof(VideohubInfoResult))]
[JsonSerializable(typeof(VideohubInputEntry[]))]
[JsonSerializable(typeof(VideohubOutputEntry[]))]
[JsonSerializable(typeof(VideohubRouteEntry[]))]
[JsonSerializable(typeof(VideohubExportResult))]
[JsonSerializable(typeof(VideohubRouteSetResult))]
[JsonSerializable(typeof(VideohubRenameResult))]
[JsonSerializable(typeof(VideohubLockResult))]
[JsonSerializable(typeof(VideohubRestoreResult))]
public partial class BmdJsonContext : JsonSerializerContext;
