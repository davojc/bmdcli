using Bmd.Devices.Videohub;

namespace Bmd.Devices.MultiView;

/// <summary>One CONFIGURATION property whose two observed values differ, as already-formatted
/// display strings ("true"/"false" for booleans, "(unset)" for a value neither side reported).
/// <see cref="From"/> is the device's current value; <see cref="To"/> is the snapshot's captured
/// value — the same shape whether the caller is deciding what to send (`multiview restore`) or
/// just reporting a mismatch (the `multiview export` verify step).</summary>
public readonly record struct ConfigurationDifference(string Property, string From, string To);

/// <summary>Compares a snapshot's captured CONFIGURATION against a device's current one,
/// property by property. This is the one place that comparison happens — both `multiview
/// restore` (to decide what to send) and the `multiview export` verify-and-retry loop (to
/// notice a device that changed configuration mid-export) call this rather than each keeping
/// their own copy, specifically so there is only one place that could regress to comparing
/// whole records. Record equality would be wrong here: <see cref="MultiViewConfiguration.Raw"/>
/// is a reference-typed list, so two configurations with identical content would compare
/// unequal, and a whole-record diff would report every property changed on every comparison.</summary>
public static class ConfigurationDiff
{
    /// <summary>A property the snapshot never captured (null) is not compared at all: that
    /// means this snapshot did not capture configuration for that property, not that the
    /// device's value changed.</summary>
    public static IEnumerable<ConfigurationDifference> Compute(
        SnapshotConfiguration snapshot, MultiViewConfiguration device)
    {
        if (snapshot.Layout is not null && snapshot.Layout != device.Layout)
            yield return new ConfigurationDifference("Layout", device.Layout ?? "(unset)", snapshot.Layout);
        if (snapshot.OutputFormat is not null && snapshot.OutputFormat != device.OutputFormat)
            yield return new ConfigurationDifference("Output format", device.OutputFormat ?? "(unset)", snapshot.OutputFormat);
        if (snapshot.SoloEnabled is not null && snapshot.SoloEnabled != device.SoloEnabled)
            yield return new ConfigurationDifference("Solo enabled", BoolText(device.SoloEnabled), BoolText(snapshot.SoloEnabled));
        if (snapshot.WidescreenSdEnabled is not null && snapshot.WidescreenSdEnabled != device.WidescreenSdEnabled)
            yield return new ConfigurationDifference(
                "Widescreen SD enabled", BoolText(device.WidescreenSdEnabled), BoolText(snapshot.WidescreenSdEnabled));
        if (snapshot.DisplayBorder is not null && snapshot.DisplayBorder != device.DisplayBorder)
            yield return new ConfigurationDifference("Display border", BoolText(device.DisplayBorder), BoolText(snapshot.DisplayBorder));
        if (snapshot.DisplayLabels is not null && snapshot.DisplayLabels != device.DisplayLabels)
            yield return new ConfigurationDifference("Display labels", BoolText(device.DisplayLabels), BoolText(snapshot.DisplayLabels));
        if (snapshot.DisplayAudioMeters is not null && snapshot.DisplayAudioMeters != device.DisplayAudioMeters)
            yield return new ConfigurationDifference(
                "Display audio meters", BoolText(device.DisplayAudioMeters), BoolText(snapshot.DisplayAudioMeters));
        if (snapshot.DisplaySdiTally is not null && snapshot.DisplaySdiTally != device.DisplaySdiTally)
            yield return new ConfigurationDifference("Display SDI tally", BoolText(device.DisplaySdiTally), BoolText(snapshot.DisplaySdiTally));
        if (snapshot.TakeMode is not null && snapshot.TakeMode != device.TakeMode)
            yield return new ConfigurationDifference("Take Mode", BoolText(device.TakeMode), BoolText(snapshot.TakeMode));
    }

    static string BoolText(bool? value) => value switch { true => "true", false => "false", null => "(unset)" };
}
