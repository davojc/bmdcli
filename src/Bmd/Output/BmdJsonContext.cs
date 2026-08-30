using System.Text.Json.Serialization;
using Bmd.Commands;
using Bmd.Commands.MultiView;
using Bmd.Commands.Videohub;
using Bmd.Config;

namespace Bmd.Output;

/// <summary>Single source-generated JSON context for all --json output (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiscoveredDeviceResult[]))]
[JsonSerializable(typeof(ConfigGetResult))]
[JsonSerializable(typeof(ConfigSetResult))]
[JsonSerializable(typeof(ConfigSetResult[]))]
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
[JsonSerializable(typeof(VideohubUpdateResult))]
[JsonSerializable(typeof(MultiViewInfoResult))]
[JsonSerializable(typeof(MultiViewInputEntry[]))]
[JsonSerializable(typeof(MultiViewViewEntry[]))]
[JsonSerializable(typeof(MultiViewConfigEntry[]))]
[JsonSerializable(typeof(MultiViewRouteSetResult))]
[JsonSerializable(typeof(MultiViewRenameResult))]
[JsonSerializable(typeof(MultiViewLockResult))]
[JsonSerializable(typeof(MultiViewConfigSetResult))]
[JsonSerializable(typeof(VersionResult))]
[JsonSerializable(typeof(UpdateCheckResult))]
[JsonSerializable(typeof(UpdateResult))]
public partial class BmdJsonContext : JsonSerializerContext;
