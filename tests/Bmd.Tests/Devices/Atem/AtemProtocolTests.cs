using System.Text;
using Bmd.Devices.Atem;

namespace Bmd.Tests.Devices.Atem;

/// <summary>Header codec, block framing and dump parsing, all asserted against the captured
/// bytes of a real switcher rather than against documentation.</summary>
public class AtemProtocolTests
{
    static List<AtemCommandBlock> AllBlocks() =>
        [.. AtemFixtures.DataPackets.SelectMany(p => AtemBlocks.ReadBlocks(p))];

    static AtemState State() => AtemDumpParser.Parse(AllBlocks());

    // ---- fixture ----------------------------------------------------------------------

    [Fact]
    public void Fixture_MatchesTheCapturedShape()
    {
        var packets = AtemFixtures.StateDumpPackets;
        Assert.Equal(19, packets.Count);
        Assert.Equal(6514, packets.Sum(p => p.Length));
        Assert.Equal([1422, 1420, 1416, 1408, 680], packets.Take(5).Select(p => p.Length));
        Assert.All(packets.Skip(5), p => Assert.Equal(12, p.Length));
        Assert.Equal(5, AtemFixtures.DataPackets.Count);
    }

    // ---- header codec -----------------------------------------------------------------

    [Fact]
    public void Header_DecodesTheFirstRealDumpPacket()
    {
        Assert.True(AtemPacket.TryReadHeader(AtemFixtures.StateDumpPackets[0], out var h));
        Assert.Equal(AtemFlags.AckRequest, h.Flags);
        Assert.Equal(1422, h.Length);
        Assert.Equal(0x8004, h.Session);
        Assert.Equal(1, h.SequenceId);
    }

    [Fact]
    public void Header_LengthIsTheWholePacketIncludingTheHeader()
    {
        // Length is 11 bits sharing two bytes with 5 bits of flags. Reading those as two
        // independent fields decodes packet 1 as 910 instead of 1422 — the first available bug.
        foreach (var packet in AtemFixtures.StateDumpPackets)
        {
            Assert.True(AtemPacket.TryReadHeader(packet, out var h));
            Assert.Equal(packet.Length, h.Length);
        }
    }

    [Fact]
    public void Header_ReadsKeepaliveAndResendFlags()
    {
        Assert.True(AtemPacket.TryReadHeader(AtemFixtures.StateDumpPackets[5], out var keepalive));
        Assert.Equal(AtemFlags.AckRequest | AtemFlags.Ack, keepalive.Flags);

        // Packet 8 repeats sequence 7 flagged Resend: the switcher retransmitting because the
        // capture was slow to acknowledge. Routine, not exceptional.
        Assert.True(AtemPacket.TryReadHeader(AtemFixtures.StateDumpPackets[7], out var resend));
        Assert.True(resend.Flags.HasFlag(AtemFlags.Resend));
        Assert.Equal(7, resend.SequenceId);
    }

    [Fact]
    public void Header_RejectsAPacketShorterThanAHeader()
    {
        Assert.False(AtemPacket.TryReadHeader(new byte[11], out _));
        Assert.False(AtemPacket.TryReadHeader([], out _));
    }

    [Fact]
    public void Header_RoundTrips()
    {
        var bytes = AtemPacket.WriteHeader(AtemFlags.AckRequest, 1422, 0x8004, 0, 1);
        Assert.Equal(0x0d, bytes[0]);   // flags and length share these two bytes
        Assert.Equal(0x8e, bytes[1]);
        Assert.True(AtemPacket.TryReadHeader(bytes, out var h));
        Assert.Equal(AtemFlags.AckRequest, h.Flags);
        Assert.Equal(1422, h.Length);
        Assert.Equal(0x8004, h.Session);
    }

    // ---- block framing ----------------------------------------------------------------

    [Fact]
    public void Blocks_ReadsEveryBlockWithoutDesynchronising()
    {
        var blocks = AllBlocks();
        Assert.Equal(287, blocks.Count);
        Assert.Equal(73, blocks.Select(b => b.Name).Distinct().Count());
        Assert.All(blocks, b => Assert.True(
            b.Name.Length == 4 && b.Name.All(c =>
                c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'),
            $"block name '{b.Name}' is not plausible ASCII — the parser has desynchronised"));
        Assert.Equal(24, blocks.Count(b => b.Name == "InPr"));
    }

    [Fact]
    public void Blocks_KeepAnOverLongPayloadRatherThanTruncatingIt()
    {
        // PrvI is documented as a 4-byte payload; this firmware sends 8. Advancing by the
        // documented size would desynchronise every remaining block in that packet.
        var prvi = Assert.Single(AllBlocks(), b => b.Name == "PrvI");
        Assert.Equal(8, prvi.Payload.Length);
    }

    [Fact]
    public void Blocks_Bytes2To3AreGarbageAndMustNotBeValidated()
    {
        // Read only the first block of each packet and this field looks like a constant 0x0014.
        // It is not: across all 287 blocks it is zero in 96 and arbitrary in the rest — the same
        // uninitialised send-buffer content that shows up in name padding. A parser validating
        // it, as either zero or a marker, rejects most real packets.
        var values = AtemFixtures.DataPackets
            .SelectMany(p => AtemBlocks.ReadBlocksWithReserved(p))
            .Select(b => b.Reserved)
            .ToList();

        Assert.Equal(287, values.Count);
        Assert.True(values.Distinct().Count() > 50,
            "this field was treated as a constant; it is not one");
        Assert.Equal(96, values.Count(v => v == 0));
        Assert.Equal(5, values.Count(v => v == 0x0014));   // one per packet: the first block only
    }

    [Fact]
    public void Blocks_BuildSendsZeroInThatField()
    {
        // Zero is the commonest observed value and the safe thing to send, since the device
        // plainly does not rely on the field itself.
        var block = AtemBlocks.Build("CAuS", [1, 0, 0, 6]);
        Assert.Equal(0x00, block[2]);
        Assert.Equal(0x00, block[3]);
    }

    [Fact]
    public void Blocks_AdvanceByTheDeclaredLengthNotAnAssumedOne()
    {
        var packet = new byte[AtemPacket.HeaderSize + 20 + 12];
        packet[12] = 0x00; packet[13] = 20; packet[14] = 0x00; packet[15] = 0x14;
        Encoding.ASCII.GetBytes("AAAA").CopyTo(packet, 16);
        packet[32] = 0x00; packet[33] = 12; packet[34] = 0x00; packet[35] = 0x14;
        Encoding.ASCII.GetBytes("BBBB").CopyTo(packet, 36);

        var blocks = AtemBlocks.ReadBlocks(packet);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(12, blocks[0].Payload.Length);
        Assert.Equal("BBBB", blocks[1].Name);
    }

    [Theory]
    [InlineData(60)]   // claims more than remains
    [InlineData(4)]    // shorter than a block header
    public void Blocks_StopCleanlyOnAnImpossibleLength(int declared)
    {
        var packet = new byte[AtemPacket.HeaderSize + 8];
        packet[12] = (byte)(declared >> 8); packet[13] = (byte)declared;
        Encoding.ASCII.GetBytes("CCCC").CopyTo(packet, 16);
        Assert.Empty(AtemBlocks.ReadBlocks(packet));
    }

    [Fact]
    public void Blocks_ReturnNothingForAHeaderOnlyPacket()
        => Assert.Empty(AtemBlocks.ReadBlocks(AtemFixtures.StateDumpPackets[5]));

    [Fact]
    public void Blocks_BuildFramesACommandTheDeviceWouldRecognise()
    {
        var block = AtemBlocks.Build("CAuS", [1, 0, 0, 6]);
        Assert.Equal(12, block.Length);
        Assert.Equal(0x00, block[0]);
        Assert.Equal(0x0c, block[1]);
        Assert.Equal("CAuS", Encoding.ASCII.GetString(block, 4, 4));
    }

    // ---- dump parsing -----------------------------------------------------------------

    [Fact]
    public void Parse_ReadsIdentityAndTopology()
    {
        var state = State();
        Assert.Equal("ATEM Television Studio HD", state.ProductName);
        Assert.Equal("2.30", state.ProtocolVersion);
        Assert.Equal(new AtemTopology(1, 24, 2, 1, 2), state.Topology);
        Assert.Equal(11, state.VideoMode);
    }

    [Fact]
    public void Parse_ReadsEverySourceWithItsNames()
    {
        var state = State();
        Assert.Equal(24, state.Sources.Count);
        Assert.Equal("Presenter", state.FindSource(1)!.LongName);
        Assert.Equal("PRES", state.FindSource(1)!.ShortName);
        Assert.Equal("Lyrics", state.FindSource(6)!.LongName);
        Assert.Equal("Black", state.FindSource(0)!.LongName);
        Assert.Equal("Color Bars", state.FindSource(1000)!.LongName);
        // The operator renamed program and preview on this switcher.
        Assert.Equal("Live On Screens", state.FindSource(10010)!.LongName);
        Assert.Equal("Not Live", state.FindSource(10011)!.LongName);
    }

    [Fact]
    public void Parse_ReadsNamesToTheFirstNulNotTheFieldWidth()
    {
        // Inputs 2 and 3 are genuinely unnamed on the captured switcher, and the padding after
        // their terminator holds uninitialised buffer content — input 2's contains the bytes
        // "MPrp", a real command name left in the device's send buffer. Reading to the field
        // width would surface that as the input's label.
        var state = State();
        Assert.Equal("", state.FindSource(2)!.LongName);
        Assert.Equal("", state.FindSource(2)!.ShortName);
        Assert.Equal("", state.FindSource(3)!.LongName);
        Assert.Equal("source 2", state.NameOf(2));
    }

    [Fact]
    public void Parse_SeparatesRealInputsFromInternalSources()
    {
        var state = State();
        Assert.Equal(8, state.Inputs.Count);
        Assert.All(state.Inputs, s => Assert.InRange(s.Id, 1, 999));
        Assert.DoesNotContain(state.Inputs, s => s.Id == 0);        // black
        Assert.DoesNotContain(state.Inputs, s => s.Id == 1000);     // colour bars
        Assert.DoesNotContain(state.Inputs, s => s.Id == 10010);    // program output
    }

    [Fact]
    public void Parse_ReadsProgramPreviewAndAux()
    {
        var state = State();
        Assert.Equal(6, state.ProgramSource);
        Assert.Equal("Lyrics", state.NameOf(state.ProgramSource));
        Assert.Equal(1, state.PreviewSource);      // PrvI's trailing 4 undocumented bytes ignored
        Assert.Equal("Presenter", state.NameOf(state.PreviewSource));

        // AuxS reads `00 56 00 06`: aux 0, source 6. That 0x56 is uninitialised buffer content
        // in the byte between them, not a field — the same garbage seen in name padding.
        var aux = Assert.Single(state.Auxes);
        Assert.Equal(0, aux.Index);
        Assert.Equal(6, aux.Source);
    }

    [Fact]
    public void Parse_RetainsACountOfEveryBlockItDoesNotModel()
    {
        // 73 types arrive and 8 are modelled; the other 65 are what later work needs, and
        // discarding them silently would mean re-solving the transport to find them again.
        var unhandled = State().UnhandledBlockCounts;
        Assert.Equal(65, unhandled.Count);
        Assert.Equal(100, unhandled["MPrp"]);
        Assert.DoesNotContain("InPr", unhandled.Keys);
        Assert.DoesNotContain("AuxS", unhandled.Keys);
    }

    [Fact]
    public void Parse_ToleratesADumpMissingEverythingItLooksFor()
    {
        var state = AtemDumpParser.Parse([]);
        Assert.Equal("", state.ProductName);
        Assert.Empty(state.Sources);
        Assert.Empty(state.Auxes);
        Assert.Null(state.FindSource(1));
        Assert.Equal("source 1", state.NameOf(1));
    }

    // ---- command payloads -------------------------------------------------------------

    [Fact]
    public void SetInputName_MirrorsInPrWithAMaskInFront()
    {
        var payload = AtemChanges.SetInputName(6, "Lyrics Desk", "LYR2");

        // 32, not the 28 the fields themselves account for. Confirmed by experiment against a
        // real switcher: at 28 or 27 it is ignored without comment.
        Assert.Equal(32, payload.Length);
        Assert.Equal(3, payload[0]);                                    // both names
        Assert.Equal(6, (payload[2] << 8) | payload[3]);
        Assert.Equal("Lyrics Desk", AtemBlocks.ReadFixedAscii(payload.AsSpan(4, 20)));
        Assert.Equal("LYR2", AtemBlocks.ReadFixedAscii(payload.AsSpan(24, 4)));
    }

    [Theory]
    [InlineData("Name", null, 1)]
    [InlineData(null, "NAME", 2)]
    [InlineData("Name", "NAME", 3)]
    public void SetInputName_MaskSaysWhichNamesToChange(string? longName, string? shortName, int mask)
    {
        // A cleared bit leaves that name alone, so setting one does not blank the other.
        Assert.Equal((byte)mask, AtemChanges.SetInputName(1, longName, shortName)[0]);
    }

    [Fact]
    public void SetInputName_RequiresAtLeastOneName()
        => Assert.Throws<ArgumentException>(() => AtemChanges.SetInputName(1, null, null));

    [Fact]
    public void SetAuxSource_MirrorsAuxS()
    {
        var payload = AtemChanges.SetAuxSource(0, 3010);
        Assert.Equal([1, 0, 0x0b, 0xc2], payload);
    }

    [Fact]
    public void CommandPayloadLengths_AreExactAndDifferPerCommand()
    {
        // The single most surprising thing found on hardware, and the thing to check first when
        // adding a command: padding CInL to 32 is required, and padding CAuS/CPgI/CPvI beyond 4
        // makes the switcher ignore them. There is no NAK, so a wrong length is silent.
        Assert.Equal(32, AtemChanges.SetInputName(1, "x", null).Length);
        Assert.Equal(4, AtemChanges.SetAuxSource(0, 1).Length);
        Assert.Equal(4, AtemChanges.SetProgramSource(0, 1).Length);
        Assert.Equal(4, AtemChanges.SetPreviewSource(0, 1).Length);
    }

    [Fact]
    public void WriteFixedAscii_TruncatesAndPadsWithNuls()
    {
        var field = new byte[4];
        AtemBlocks.WriteFixedAscii(field, "TOOLONG");
        Assert.Equal("TOOL", Encoding.ASCII.GetString(field));

        AtemBlocks.WriteFixedAscii(field, "AB");
        Assert.Equal([(byte)'A', (byte)'B', 0, 0], field);
    }
}
