using System.Text.Json;
using System.Text.Json.Serialization;
using Bmd.Devices.Videohub;

namespace Bmd.Devices.Atem;

/// <summary>One source's names as captured in a snapshot. Auxes are recorded 1-based, matching
/// what the user types, so a snapshot can be read without knowing the wire is 0-based.</summary>
public sealed record AtemSnapshotSource(int Id, string Name, string ShortName);

public sealed record AtemSnapshotAux(int Aux, int Source);

/// <summary>A switcher's changeable state, captured before a mutation.
///
/// Scoped to exactly what bmd can change: input names, aux routing, and the program and preview
/// buses. It is deliberately not a full device backup — a switcher's transitions, keyers, media
/// pool and audio mixer are all outside what these commands touch, and a snapshot that implied
/// otherwise would be a promise bmd cannot keep on restore.</summary>
public sealed record AtemSnapshot(
    string Device,
    DateTimeOffset ExportedAt,
    AtemSnapshotSource[] Sources,
    AtemSnapshotAux[] Auxes,
    int ProgramSource,
    int PreviewSource)
{
    public static AtemSnapshot FromState(AtemState state, DateTimeOffset exportedAt) =>
        new(state.ProductName,
            exportedAt,
            [.. state.Sources.Select(s => new AtemSnapshotSource(s.Id, s.LongName, s.ShortName))],
            [.. state.Auxes.Select(a => new AtemSnapshotAux(a.Index + 1, a.Source))],
            state.ProgramSource,
            state.PreviewSource);

    public string ToJson() =>
        JsonSerializer.Serialize(this, AtemSnapshotJsonContext.Default.AtemSnapshot) + "\n";

    public static AtemSnapshot FromJson(string json)
    {
        AtemSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize(json, AtemSnapshotJsonContext.Default.AtemSnapshot);
        }
        catch (JsonException ex)
        {
            throw new SnapshotFormatException($"snapshot is not valid JSON: {ex.Message}");
        }
        if (snapshot is null) throw new SnapshotFormatException("snapshot is empty");
        if (snapshot.Sources is null || snapshot.Auxes is null)
            throw new SnapshotFormatException("snapshot is missing its sources or auxes");
        return snapshot;
    }
}

/// <summary>Source-generated JSON for ATEM snapshot files (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AtemSnapshot))]
public partial class AtemSnapshotJsonContext : JsonSerializerContext;
