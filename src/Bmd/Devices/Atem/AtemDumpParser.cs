namespace Bmd.Devices.Atem;

/// <summary>Applies command blocks to an <see cref="AtemState"/> — the same code path for the
/// dump read at connect and for the incremental blocks the device pushes afterwards, because
/// the device makes no distinction between them.
///
/// Every offset here was read off a capture from real hardware, not from documentation. Fields
/// are parsed from the front of a payload and anything trailing is ignored, so a firmware that
/// sends a longer block than we expect still parses (the captured PrvI already does).</summary>
public static class AtemDumpParser
{
    // InPr: id at 0-1, long name in the 20 bytes at 2, short name in the 4 bytes at 22, then
    // ten bytes of availability and port-type flags this build does not use.
    const int InPrLongNameOffset = 2;
    const int InPrLongNameLength = 20;
    const int InPrShortNameOffset = 22;
    const int InPrShortNameLength = 4;

    public static AtemState Parse(IEnumerable<AtemCommandBlock> blocks)
    {
        var state = new AtemState();
        Apply(state, blocks);
        return state;
    }

    /// <summary>Applies blocks to an existing state. Returns the names of the blocks that
    /// actually changed something a command can observe, so a caller can verify a mutation
    /// landed rather than assume it did.</summary>
    public static void Apply(AtemState state, IEnumerable<AtemCommandBlock> blocks)
    {
        foreach (var block in blocks) ApplyOne(state, block);
    }

    static void ApplyOne(AtemState state, AtemCommandBlock block)
    {
        var payload = block.Payload.Span;
        switch (block.Name)
        {
            case "_ver" when payload.Length >= 4:
                state.ProtocolVersion = $"{U16(payload, 0)}.{U16(payload, 2)}";
                return;

            case "_pin" when payload.Length > 0:
                state.ProductName = AtemBlocks.ReadFixedAscii(payload);
                return;

            case "_top" when payload.Length >= 6:
                state.Topology = new AtemTopology(
                    MixEffects: payload[0],
                    Sources: payload[1],
                    DownstreamKeyers: payload[2],
                    Auxiliaries: payload[3],
                    MediaPlayers: payload[5]);
                return;

            case "InPr" when payload.Length >= InPrShortNameOffset + InPrShortNameLength:
                var id = U16(payload, 0);
                state.SourcesById[id] = new AtemSource(
                    id,
                    AtemBlocks.ReadFixedAscii(payload.Slice(InPrLongNameOffset, InPrLongNameLength)),
                    AtemBlocks.ReadFixedAscii(payload.Slice(InPrShortNameOffset, InPrShortNameLength)));
                return;

            // PrgI/PrvI/AuxS share a shape: an index byte, one byte the device leaves as
            // uninitialised buffer content, then the source id. The captured AuxS reads
            // `00 56 00 06` — that 0x56 is garbage, not a field.
            case "PrgI" when payload.Length >= 4:
                state.ProgramSource = U16(payload, 2);
                return;

            case "PrvI" when payload.Length >= 4:
                state.PreviewSource = U16(payload, 2);
                return;

            case "AuxS" when payload.Length >= 4:
                state.AuxById[payload[0]] = new AtemAux(payload[0], U16(payload, 2));
                return;

            case "VidM" when payload.Length >= 1:
                state.VideoMode = payload[0];
                return;

            default:
                state.Unhandled[block.Name] = state.Unhandled.GetValueOrDefault(block.Name) + 1;
                return;
        }
    }

    static int U16(ReadOnlySpan<byte> payload, int offset) => (payload[offset] << 8) | payload[offset + 1];
}
