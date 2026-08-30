namespace Bmd.Devices.Atem;

/// <summary>One video source the switcher can route: a physical input, or an internal source
/// such as colour bars, a media player, or a mix effect's program output.
///
/// <paramref name="LongName"/> and <paramref name="ShortName"/> are genuinely empty on inputs
/// nobody has named — two of the eight inputs on the captured switcher are — so callers must
/// render blanks rather than assume a name is always present.</summary>
public sealed record AtemSource(int Id, string LongName, string ShortName)
{
    /// <summary>Whether this is a physical input rather than an internal source.
    ///
    /// Ids are banded: 0 is black, 1-999 are physical inputs, and everything from 1000 up is
    /// internal — colour bars, colour generators, media players, key and DSK masks, clean feeds,
    /// auxiliaries, and each mix effect's program and preview outputs. Confirmed two ways:
    /// observed on hardware, and matching PyATEMMax's independently derived constants.</summary>
    public bool IsExternalInput => Id is >= 1 and < 1000;
}

/// <summary>An auxiliary output and the source currently feeding it. <paramref name="Index"/> is
/// 0-based on the wire; the command layer presents auxes 1-based like everything else in bmd.</summary>
public sealed record AtemAux(int Index, int Source);

/// <summary>What the switcher says it has. Always read this rather than assuming a model's
/// shape — an ATEM Mini Pro and a 1 M/E Production Studio differ substantially.</summary>
public sealed record AtemTopology(
    int MixEffects, int Sources, int DownstreamKeyers, int Auxiliaries, int MediaPlayers);

/// <summary>A switcher's state as of the dump read at connect, updated in place as the device
/// pushes changes.</summary>
public sealed class AtemState
{
    public string ProductName { get; internal set; } = "";
    public string ProtocolVersion { get; internal set; } = "";
    public AtemTopology Topology { get; internal set; } = new(0, 0, 0, 0, 0);
    public int ProgramSource { get; internal set; }
    public int PreviewSource { get; internal set; }

    /// <summary>The device's raw video-mode number. Deliberately not mapped to a format name:
    /// the mode table is not published, and printing a confidently wrong "1080i5994" is worse
    /// than printing the number the device actually reported.</summary>
    public int VideoMode { get; internal set; }

    internal readonly Dictionary<int, AtemSource> SourcesById = [];
    internal readonly Dictionary<int, AtemAux> AuxById = [];
    internal readonly Dictionary<string, int> Unhandled = new(StringComparer.Ordinal);

    /// <summary>Every source the switcher reports, ordered by id.</summary>
    public IReadOnlyList<AtemSource> Sources => [.. SourcesById.Values.OrderBy(s => s.Id)];

    /// <summary>Physical inputs only — the ones an operator names and cares about.</summary>
    public IReadOnlyList<AtemSource> Inputs => [.. Sources.Where(s => s.IsExternalInput)];

    /// <summary>Auxiliary outputs, ordered by wire index.</summary>
    public IReadOnlyList<AtemAux> Auxes => [.. AuxById.Values.OrderBy(a => a.Index)];

    /// <summary>How many of each block type arrived that this parser does not model, keyed by the
    /// 4-character command name. Seventy-three types arrive and a handful are modelled; counting
    /// the rest means a firmware sending something new is visible rather than silently dropped.</summary>
    public IReadOnlyDictionary<string, int> UnhandledBlockCounts => Unhandled;

    public AtemSource? FindSource(int id) => SourcesById.GetValueOrDefault(id);

    /// <summary>A source's best display name: its long name, falling back to its short name, then
    /// to a bare id for the unnamed inputs the captured switcher actually has.</summary>
    public string NameOf(int id)
    {
        var source = FindSource(id);
        if (source is null) return $"source {id}";
        if (source.LongName.Length > 0) return source.LongName;
        if (source.ShortName.Length > 0) return source.ShortName;
        return $"source {id}";
    }
}
