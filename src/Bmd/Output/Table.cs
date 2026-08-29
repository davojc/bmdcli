namespace Bmd.Output;

/// <summary>Minimal aligned-column table: uppercase headers, two-space gap, no borders.</summary>
public static class Table
{
    /// <summary>Writes the table. When <paramref name="details"/> is supplied, it must have one
    /// entry per row in <paramref name="rows"/> (same index order); each row's detail lines are
    /// printed verbatim immediately beneath that row — e.g. a device's raw TXT entries indented
    /// under its own line — with no effect on column widths, which are computed from
    /// <paramref name="headers"/> and <paramref name="rows"/> only, exactly as when
    /// <paramref name="details"/> is omitted. A row with no detail lines of its own simply passes
    /// an empty list, so nothing extra is printed for it.</summary>
    public static void Write(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<IReadOnlyList<string>>? details = null)
    {
        var widths = new int[headers.Count];
        for (var c = 0; c < headers.Count; c++)
        {
            widths[c] = headers[c].Length;
            foreach (var row in rows) widths[c] = Math.Max(widths[c], row[c].Length);
        }
        Console.WriteLine(Format(headers, widths));
        for (var r = 0; r < rows.Count; r++)
        {
            Console.WriteLine(Format(rows[r], widths));
            if (details is not null)
                foreach (var line in details[r])
                    Console.WriteLine(line);
        }
    }

    static string Format(IReadOnlyList<string> cells, int[] widths)
    {
        var parts = new string[cells.Count];
        for (var c = 0; c < cells.Count; c++)
            parts[c] = c == cells.Count - 1 ? cells[c] : cells[c].PadRight(widths[c]);
        return string.Join("  ", parts).TrimEnd();
    }
}
