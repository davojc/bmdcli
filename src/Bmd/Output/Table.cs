namespace Bmd.Output;

/// <summary>Minimal aligned-column table: uppercase headers, two-space gap, no borders.</summary>
public static class Table
{
    public static void Write(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var widths = new int[headers.Count];
        for (var c = 0; c < headers.Count; c++)
        {
            widths[c] = headers[c].Length;
            foreach (var row in rows) widths[c] = Math.Max(widths[c], row[c].Length);
        }
        Console.WriteLine(Format(headers, widths));
        foreach (var row in rows) Console.WriteLine(Format(row, widths));
    }

    static string Format(IReadOnlyList<string> cells, int[] widths)
    {
        var parts = new string[cells.Count];
        for (var c = 0; c < cells.Count; c++)
            parts[c] = c == cells.Count - 1 ? cells[c] : cells[c].PadRight(widths[c]);
        return string.Join("  ", parts).TrimEnd();
    }
}
