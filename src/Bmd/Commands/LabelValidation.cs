namespace Bmd.Commands;

/// <summary>Validates a label before it is sent to a device. Newlines would break the
/// line-oriented Videohub Ethernet Protocol, so they are rejected here — at the command layer,
/// before any client call — rather than left to <c>VideohubClient</c>'s own guard, whose
/// <see cref="System.ArgumentException"/> is not in <see cref="DeviceSession.RunCatchingAsync"/>'s
/// exception filter and would otherwise escape as a raw stack trace instead of a clean exit-2
/// usage error. Shared by every command group that renames something on a device (`videohub
/// input/output rename`, `multiview input/view rename`), so there is exactly one place this
/// check can regress.</summary>
internal static class LabelValidation
{
    public static bool TryValidate(string label, out string error)
    {
        if (label.Contains('\n') || label.Contains('\r'))
        {
            error = "error: label must not contain newlines";
            return false;
        }
        error = "";
        return true;
    }
}
