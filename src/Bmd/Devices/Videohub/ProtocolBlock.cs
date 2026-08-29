namespace Bmd.Devices.Videohub;

/// <summary>One blank-line-terminated block of the Videohub text protocol.
/// Header carries no trailing colon; ACK/NAK are header-only blocks.</summary>
public sealed record ProtocolBlock(string Header, IReadOnlyList<string> Lines);

/// <summary>Feed protocol lines one at a time; a blank line completes and returns a block.</summary>
public sealed class BlockAccumulator
{
    readonly List<string> _lines = [];

    public ProtocolBlock? Add(string line)
    {
        if (line.Length > 0)
        {
            _lines.Add(line);
            return null;
        }
        if (_lines.Count == 0) return null; // stray blank line
        var header = _lines[0].TrimEnd();
        if (header.EndsWith(':')) header = header[..^1];
        var block = new ProtocolBlock(header, _lines.Skip(1).ToArray());
        _lines.Clear();
        return block;
    }

    /// <summary>Completes a trailing block that was never closed by a blank line.</summary>
    public ProtocolBlock? Flush() => _lines.Count == 0 ? null : Add("");
}

public static class BlockReader
{
    public static IReadOnlyList<ProtocolBlock> ReadBlocks(string text)
    {
        var acc = new BlockAccumulator();
        var blocks = new List<ProtocolBlock>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            if (acc.Add(raw) is { } block) blocks.Add(block);
        if (acc.Flush() is { } trailing) blocks.Add(trailing);
        return blocks;
    }
}
