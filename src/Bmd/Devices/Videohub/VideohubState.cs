namespace Bmd.Devices.Videohub;

public sealed record VideohubDeviceInfo(
    string ModelName, string? FriendlyName, int VideoInputs, int VideoOutputs, string ProtocolVersion);

public enum LockState { Unlocked, Owned, Locked }

public sealed class VideohubProtocolException(string message) : Exception(message);

/// <summary>Snapshot of a Videohub's state. ALL public numbering is 1-based
/// (matching front panels); internal storage mirrors the 0-based wire.</summary>
public sealed class VideohubState
{
    readonly string[] _inputLabels;
    readonly string[] _outputLabels;
    readonly int[] _routes;      // 0-based: _routes[out] = in
    readonly LockState[] _locks;

    public VideohubDeviceInfo Device { get; }

    internal VideohubState(VideohubDeviceInfo device, string[] inputLabels, string[] outputLabels, int[] routes, LockState[] locks)
    {
        Device = device;
        _inputLabels = inputLabels;
        _outputLabels = outputLabels;
        _routes = routes;
        _locks = locks;
    }

    public string GetInputLabel(int input) => _inputLabels[CheckIndex(input, Device.VideoInputs, nameof(input))];
    public string GetOutputLabel(int output) => _outputLabels[CheckIndex(output, Device.VideoOutputs, nameof(output))];
    public int GetRoute(int output) => _routes[CheckIndex(output, Device.VideoOutputs, nameof(output))] + 1;
    public LockState GetLock(int output) => _locks[CheckIndex(output, Device.VideoOutputs, nameof(output))];

    static int CheckIndex(int n, int count, string name) =>
        n >= 1 && n <= count ? n - 1 : throw new ArgumentOutOfRangeException(name, n, $"must be between 1 and {count}");
}
