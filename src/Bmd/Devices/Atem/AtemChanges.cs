namespace Bmd.Devices.Atem;

/// <summary>Builds the payloads for the commands bmd sends to a switcher.
///
/// Every layout here was confirmed against a real ATEM Television Studio HD III: each was sent in
/// several candidate shapes and the one the switcher acted on — pushing the corresponding state
/// block back — is the one kept. The capture the read path is built on could not settle these,
/// because it contains only what the switcher *sent*, never what a client sends.
///
/// <b>Payload length is exact, and it differs per command.</b> That is the thing to know before
/// adding another. CInL takes 32 bytes and is ignored at 28 or 27; CAuS, CPgI and CPvI take
/// exactly 4 and are ignored at 8 or 12. The switcher never complains — there is no NAK in this
/// protocol — it simply does nothing, which is indistinguishable from a command it does not
/// support. So every send goes through <see cref="AtemClient.SendCommandAsync"/>, which waits for
/// the device to report the change back and treats silence as failure.</summary>
public static class AtemChanges
{
    /// <summary>Change an input's long and/or short name.
    ///
    /// Mirrors InPr (id, then a 20-byte long name, then a 4-byte short name) with a mask byte and
    /// one pad byte in front, which puts the id on the same 2-byte boundary the device uses.
    /// Bit 0 of the mask sets the long name, bit 1 the short name; a name whose bit is clear is
    /// left alone, so renaming one does not blank the other.
    ///
    /// The four trailing bytes matter: mask + pad + id + 20 + 4 accounts for 28, and a 28-byte
    /// payload is silently ignored by the switcher. Only the full 32 works.</summary>
    public static byte[] SetInputName(int sourceId, string? longName, string? shortName)
    {
        if (longName is null && shortName is null)
            throw new ArgumentException("at least one of longName or shortName must be given");

        var payload = new byte[SetInputNamePayloadSize];
        payload[0] = (byte)((longName is not null ? 1 : 0) | (shortName is not null ? 2 : 0));
        payload[2] = (byte)(sourceId >> 8);
        payload[3] = (byte)sourceId;
        if (longName is not null) AtemBlocks.WriteFixedAscii(payload.AsSpan(4, 20), longName);
        if (shortName is not null) AtemBlocks.WriteFixedAscii(payload.AsSpan(24, 4), shortName);
        return payload;
    }

    /// <summary>Exact payload size the switcher requires for CInL. Verified by experiment: 28
    /// (the size of the fields alone) and 27 are both ignored without comment.</summary>
    public const int SetInputNamePayloadSize = 32;

    /// <summary>Longest long name an input can carry, in characters.</summary>
    public const int MaxLongNameLength = 20;

    /// <summary>Longest short name an input can carry. This is what the switcher shows on its
    /// multiviewer labels, so it is the one users notice being truncated.</summary>
    public const int MaxShortNameLength = 4;

    /// <summary>Route a source to an auxiliary output. <paramref name="auxIndex"/> is the 0-based
    /// wire index. Mirrors AuxS (index, pad, source) with a mask byte in front.
    ///
    /// Exactly 4 bytes — unlike CInL, padding this one to 8 or 12 makes the switcher ignore it.</summary>
    public static byte[] SetAuxSource(int auxIndex, int sourceId) =>
        [1, (byte)auxIndex, (byte)(sourceId >> 8), (byte)sourceId];

    /// <summary>Put a source on a mix effect's program bus — a hard cut on air.</summary>
    public static byte[] SetProgramSource(int mixEffect, int sourceId) =>
        [(byte)mixEffect, 0, (byte)(sourceId >> 8), (byte)sourceId];

    /// <summary>Put a source on a mix effect's preview bus. Changes nothing on air.</summary>
    public static byte[] SetPreviewSource(int mixEffect, int sourceId) =>
        [(byte)mixEffect, 0, (byte)(sourceId >> 8), (byte)sourceId];
}
