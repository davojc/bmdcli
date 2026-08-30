namespace Bmd.Devices.Videohub;

public sealed record VideohubDeviceInfo(
    string ModelName, string? FriendlyName, int VideoInputs, int VideoOutputs, string ProtocolVersion);

public enum LockState { Unlocked, Owned, Locked }

public sealed class VideohubProtocolException(string message) : Exception(message);

/// <summary>Thrown when the device NAKs a block sent by the client.</summary>
public sealed class VideohubCommandRejectedException(string message) : Exception(message);

/// <summary>Snapshot of a Videohub's state. ALL public numbering is 1-based
/// (matching front panels); internal storage mirrors the 0-based wire.</summary>
public sealed class VideohubState
{
    readonly string[] _inputLabels;
    readonly string[] _outputLabels;
    readonly int[] _routes;      // 0-based: _routes[out] = in
    readonly LockState[] _locks;
    readonly Dictionary<string, IReadOnlyList<string>> _extraBlocks;

    public VideohubDeviceInfo Device { get; }

    /// <summary>Blocks the parser does not interpret, kept verbatim and keyed by header (no
    /// trailing colon). A plain Videohub has none; a MultiView carries CONFIGURATION here.
    /// Retaining them rather than discarding them is what lets a device-specific layer above
    /// read a block the protocol layer knows nothing about.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExtraBlocks => _extraBlocks;

    internal VideohubState(
        VideohubDeviceInfo device, string[] inputLabels, string[] outputLabels, int[] routes,
        LockState[] locks, Dictionary<string, IReadOnlyList<string>>? extraBlocks = null)
    {
        Device = device;
        _inputLabels = inputLabels;
        _outputLabels = outputLabels;
        _routes = routes;
        _locks = locks;
        _extraBlocks = extraBlocks ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    }

    public string GetInputLabel(int input) => _inputLabels[CheckIndex(input, Device.VideoInputs, nameof(input))];
    public string GetOutputLabel(int output) => _outputLabels[CheckIndex(output, Device.VideoOutputs, nameof(output))];
    public int GetRoute(int output) => _routes[CheckIndex(output, Device.VideoOutputs, nameof(output))] + 1;
    public LockState GetLock(int output) => _locks[CheckIndex(output, Device.VideoOutputs, nameof(output))];

    internal VideohubState WithInputLabels(string[] inputLabels) =>
        new(Device, inputLabels, _outputLabels, _routes, _locks, _extraBlocks);
    internal VideohubState WithOutputLabels(string[] outputLabels) =>
        new(Device, _inputLabels, outputLabels, _routes, _locks, _extraBlocks);
    internal VideohubState WithRoutes(int[] routes) =>
        new(Device, _inputLabels, _outputLabels, routes, _locks, _extraBlocks);
    internal VideohubState WithLocks(LockState[] locks) =>
        new(Device, _inputLabels, _outputLabels, _routes, locks, _extraBlocks);

    /// <summary>Replaces (or adds) one unrecognised block. The device pushes only the properties
    /// that changed, but it pushes them as a whole block, so this replaces rather than merges.</summary>
    internal VideohubState WithExtraBlock(string header, IReadOnlyList<string> lines)
    {
        var copy = new Dictionary<string, IReadOnlyList<string>>(_extraBlocks, StringComparer.Ordinal)
        {
            [header] = lines,
        };
        return new VideohubState(Device, _inputLabels, _outputLabels, _routes, _locks, copy);
    }

    static int CheckIndex(int n, int count, string name) =>
        n >= 1 && n <= count ? n - 1 : throw new ArgumentOutOfRangeException(name, n, $"must be between 1 and {count}");
}
