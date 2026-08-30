namespace Bmd.Devices.Atem;

/// <summary>Builds the payloads for the commands bmd sends to a switcher.
///
/// <b>These layouts are the least-verified code in the ATEM support, and deliberately so.</b>
/// The hardware capture this device support is built on contains only what the switcher *sent*;
/// a client's commands never appear in it. So while every read layout in
/// <see cref="AtemDumpParser"/> was read off real bytes, everything here is derived from the
/// shape of the corresponding state block plus community documentation, and is confirmed only
/// when a real switcher acts on it.
///
/// Two things contain that risk. Each command mirrors the state block it changes — CAuS is AuxS
/// with a mask byte in front — so a layout error is a shifted field rather than a wild guess.
/// And every send goes through <see cref="AtemClient.SendCommandAsync"/>, which waits for the
/// device to report the change back: a wrong layout produces a clean timeout saying the switcher
/// did not report the change, never a silent no-op reported as success.</summary>
public static class AtemChanges
{
    /// <summary>Change an input's long and/or short name.
    ///
    /// Mirrors InPr (id, then a 20-byte long name, then a 4-byte short name) with a mask byte and
    /// one pad byte in front, which puts the id on the same 2-byte boundary the device uses.
    /// Bit 0 of the mask sets the long name, bit 1 the short name; a name whose bit is clear is
    /// left alone, so renaming one does not blank the other.</summary>
    public static byte[] SetInputName(int sourceId, string? longName, string? shortName)
    {
        if (longName is null && shortName is null)
            throw new ArgumentException("at least one of longName or shortName must be given");

        var payload = new byte[28];
        payload[0] = (byte)((longName is not null ? 1 : 0) | (shortName is not null ? 2 : 0));
        payload[2] = (byte)(sourceId >> 8);
        payload[3] = (byte)sourceId;
        if (longName is not null) AtemBlocks.WriteFixedAscii(payload.AsSpan(4, 20), longName);
        if (shortName is not null) AtemBlocks.WriteFixedAscii(payload.AsSpan(24, 4), shortName);
        return payload;
    }

    /// <summary>Longest long name an input can carry, in characters.</summary>
    public const int MaxLongNameLength = 20;

    /// <summary>Longest short name an input can carry. This is what the switcher shows on its
    /// multiviewer labels, so it is the one users notice being truncated.</summary>
    public const int MaxShortNameLength = 4;

    /// <summary>Route a source to an auxiliary output. <paramref name="auxIndex"/> is the 0-based
    /// wire index. Mirrors AuxS (index, pad, source) with a mask byte in front.</summary>
    public static byte[] SetAuxSource(int auxIndex, int sourceId) =>
        [1, (byte)auxIndex, (byte)(sourceId >> 8), (byte)sourceId];

    /// <summary>Put a source on a mix effect's program bus — a hard cut on air.</summary>
    public static byte[] SetProgramSource(int mixEffect, int sourceId) =>
        [(byte)mixEffect, 0, (byte)(sourceId >> 8), (byte)sourceId];

    /// <summary>Put a source on a mix effect's preview bus. Changes nothing on air.</summary>
    public static byte[] SetPreviewSource(int mixEffect, int sourceId) =>
        [(byte)mixEffect, 0, (byte)(sourceId >> 8), (byte)sourceId];
}
