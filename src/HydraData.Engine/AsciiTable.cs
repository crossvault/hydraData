// Copyright (c) 2026 crossVault GmbH.

using System.Globalization;
using System.Text;

namespace HydraData.Engine;

/// <summary>
/// Renders a resultset as a pure ASCII table. No Spectre renderer is used —
/// the embedded engine does not reference Spectre — so the table is plain text suitable for capture and
/// non-TTY hosts. Column widths are the widest cell including the header; a <see langword="null"/> cell
/// renders as an empty cell; borders use <c>+</c>, <c>-</c> and <c>|</c>.
/// </summary>
internal static class AsciiTable
{
    /// <summary>
    /// Renders <paramref name="rows"/> as an ASCII table. Columns are discovered from the first row that
    /// exposes any keys (dictionary keys, or public readable property names), matching the CSV writer's
    /// "first row defines the columns" convention. Returns an empty string when there is nothing to render.
    /// </summary>
    /// <param name="rows">The rows to render.</param>
    /// <returns>The rendered table, terminated by a newline, or an empty string for no columns.</returns>
    public static string Render(IEnumerable<object> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var materialised = rows.ToList();
        var maps = materialised.Select(ToMap).ToList();
        var columns = DiscoverColumns(maps);
        if (columns.Count == 0)
            return string.Empty;

        // Build the cell matrix (header excluded); null -> empty cell.
        var cells = new List<string[]>(maps.Count);
        foreach (var map in maps)
        {
            var rowCells = new string[columns.Count];
            for (int c = 0; c < columns.Count; c++)
                rowCells[c] = map.TryGetValue(columns[c], out var v) ? Stringify(v) : string.Empty;
            cells.Add(rowCells);
        }

        // Column widths = widest cell including the header.
        var widths = new int[columns.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            widths[c] = columns[c].Length;
            foreach (var row in cells)
                if (row[c].Length > widths[c]) widths[c] = row[c].Length;
        }

        var sb = new StringBuilder();
        AppendSeparator(sb, widths);
        AppendRow(sb, columns.ToArray(), widths);
        AppendSeparator(sb, widths);
        foreach (var row in cells)
            AppendRow(sb, row, widths);
        AppendSeparator(sb, widths);
        return sb.ToString();
    }

    private static void AppendSeparator(StringBuilder sb, int[] widths)
    {
        sb.Append('+');
        foreach (var w in widths)
            sb.Append('-', w + 2).Append('+');
        sb.Append('\n');
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] widths)
    {
        sb.Append('|');
        for (int c = 0; c < cells.Length; c++)
            sb.Append(' ').Append(cells[c].PadRight(widths[c])).Append(' ').Append('|');
        sb.Append('\n');
    }

    private static List<string> DiscoverColumns(IReadOnlyList<IReadOnlyDictionary<string, object?>> maps)
    {
        // First non-empty row defines the column set (and order), like ScriptIo.WriteCsv.
        foreach (var map in maps)
        {
            if (map.Count > 0)
                return map.Keys.ToList();
        }

        return [];
    }

    private static IReadOnlyDictionary<string, object?> ToMap(object? row)
    {
        switch (row)
        {
            case null:
                return new Dictionary<string, object?>();
            case IDictionary<string, object?> dict:
                return new Dictionary<string, object?>(dict);
            case IReadOnlyDictionary<string, object?> rdict:
                return rdict;
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        // GetProperties() returns properties in declaration order for compiler-generated types
        // (anonymous objects, records) but the CLR does not guarantee order for hand-written
        // classes. Ordering by MetadataToken preserves declaration order on all runtime types
        // that emit in source order (which covers all practical cases here).
        foreach (var prop in row.GetType().GetProperties().OrderBy(p => p.MetadataToken))
        {
            if (prop.GetIndexParameters().Length == 0 && prop.CanRead)
                map[prop.Name] = prop.GetValue(row);
        }

        return map;
    }

    /// <summary>
    /// Converts a cell value to a display string and sanitizes it so it cannot corrupt the
    /// ASCII grid: newline characters are collapsed to a single space, and pipe characters
    /// (<c>|</c>) are replaced with a visually similar character (U+2502 box-drawing) to
    /// prevent false column-border injection. Column widths are computed on the sanitized
    /// string so alignment is always correct (B2).
    /// </summary>
    private static string Stringify(object? value)
    {
        var raw = value switch
        {
            null => string.Empty,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        // Sanitize: collapse \r\n / \r / \n to a space, replace | with │ (U+2502).
        // The replacement avoids injecting a false column border into the rendered table.
        if (raw.AsSpan().IndexOfAny('\r', '\n', '|') < 0)
            return raw; // fast path — no sanitization needed

        var sb = new StringBuilder(raw.Length);
        bool prevWasCr = false;
        foreach (var ch in raw)
        {
            if (ch == '\r')
            {
                sb.Append(' ');
                prevWasCr = true;
            }
            else if (ch == '\n')
            {
                if (!prevWasCr) sb.Append(' '); // \n not preceded by \r — emit space
                prevWasCr = false;
            }
            else if (ch == '|')
            {
                sb.Append('│'); // │ — box-drawing vertical, visually similar but not a border char
                prevWasCr = false;
            }
            else
            {
                sb.Append(ch);
                prevWasCr = false;
            }
        }
        return sb.ToString();
    }
}
