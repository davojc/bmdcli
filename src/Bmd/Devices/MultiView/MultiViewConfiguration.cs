namespace Bmd.Devices.MultiView;

/// <summary>The MultiView's <c>CONFIGURATION</c> block: layout, output format, and the display
/// and behaviour toggles.
///
/// This block is <b>not documented by Blackmagic</b> — it appears in no published version of the
/// Videohub Ethernet Protocol. Everything here is modelled from a dump captured off a real
/// MultiView 4 running firmware 2.2.5. Two consequences shape this type:
///
/// Every property is nullable, because a device that did not report a property must never be
/// confused with one that reported it as false — a MultiView 16 or a later firmware may send a
/// different set.
///
/// <see cref="Raw"/> keeps every property exactly as received, including ones this type has never
/// heard of, so `bmd multiview config` can show the truth rather than only the fields that
/// happened to be known when this was written.</summary>
public sealed record MultiViewConfiguration(
    string? Layout,
    string? OutputFormat,
    bool? SoloEnabled,
    bool? WidescreenSdEnabled,
    bool? DisplayBorder,
    bool? DisplayLabels,
    bool? DisplayAudioMeters,
    bool? DisplaySdiTally,
    bool? TakeMode,
    IReadOnlyList<KeyValuePair<string, string>> Raw)
{
    /// <summary>The protocol block header, without its trailing colon.</summary>
    public const string BlockHeader = "CONFIGURATION";

    /// <summary>A configuration with nothing set — used when a device dump has no
    /// <c>CONFIGURATION</c> block at all (i.e. a Videohub, or a MultiView that hasn't sent one yet).</summary>
    public static MultiViewConfiguration Empty { get; } =
        new(null, null, null, null, null, null, null, null, null, []);

    // Protocol property names, spelled as the device sends them. Matching is case-insensitive
    // because the real device is itself inconsistent: every property is sentence case except
    // "Take Mode".
    const string PropLayout = "Layout";
    const string PropFormat = "Output format";
    const string PropSolo = "Solo enabled";
    const string PropWidescreen = "Widescreen SD enabled";
    const string PropBorder = "Display border";
    const string PropLabels = "Display labels";
    const string PropAudioMeters = "Display audio meters";
    const string PropTally = "Display SDI tally";
    const string PropTakeMode = "Take Mode";

    /// <summary>CLI setting name to protocol property. The CLI names are kebab-case and drop the
    /// "Display" prefix, because `bmd multiview show borders on` reads better than the protocol's
    /// own spelling — but the mapping is explicit so the two vocabularies stay decoupled.</summary>
    static readonly KeyValuePair<string, string>[] CliNames =
    [
        new("layout", PropLayout),
        new("format", PropFormat),
        new("solo", PropSolo),
        new("widescreen-sd", PropWidescreen),
        new("borders", PropBorder),
        new("labels", PropLabels),
        new("audio-meters", PropAudioMeters),
        new("tally", PropTally),
        new("take-mode", PropTakeMode),
    ];

    /// <summary>Maps a CLI setting name (e.g. <c>"borders"</c>) to the protocol property name the
    /// device expects (e.g. <c>"Display border"</c>), or <c>null</c> if the name is unknown.</summary>
    public static string? ProtocolNameFor(string cliName)
    {
        foreach (var (cli, protocol) in CliNames)
            if (string.Equals(cli, cliName, StringComparison.OrdinalIgnoreCase)) return protocol;
        return null;
    }

    /// <summary>Parses the body of a <c>CONFIGURATION</c> block into a typed view plus the
    /// verbatim property list.</summary>
    public static MultiViewConfiguration FromLines(IReadOnlyList<string> lines)
    {
        var raw = new List<KeyValuePair<string, string>>(lines.Count);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            raw.Add(new KeyValuePair<string, string>(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        string? Text(string property)
        {
            foreach (var (key, value) in raw)
                if (string.Equals(key, property, StringComparison.OrdinalIgnoreCase)) return value;
            return null;
        }

        bool? Flag(string property) =>
            Text(property) is { } text && TryParseOnOff(text, out var value) ? value : null;

        return new MultiViewConfiguration(
            Text(PropLayout), Text(PropFormat),
            Flag(PropSolo), Flag(PropWidescreen), Flag(PropBorder), Flag(PropLabels),
            Flag(PropAudioMeters), Flag(PropTally), Flag(PropTakeMode),
            raw);
    }

    /// <summary>The body of a block that sets one property. The device accepts a partial
    /// CONFIGURATION block and applies only what it contains.</summary>
    public static IReadOnlyList<string> LinesFor(string protocolProperty, string value) =>
        [$"{protocolProperty}: {value}"];

    /// <summary>Accepts the CLI's <c>on</c>/<c>off</c> and the protocol's own
    /// <c>true</c>/<c>false</c>, so the same parser reads both a user's argument and a device's
    /// reply. Nothing else is accepted: "yes" and "1" are rejected rather than guessed at.</summary>
    public static bool TryParseOnOff(string text, out bool value)
    {
        if (string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }
}
