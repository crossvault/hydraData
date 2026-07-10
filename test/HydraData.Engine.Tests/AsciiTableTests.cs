// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T09.6 — the pure ASCII table renderer used by the <c>Table</c> script API. Asserts the exact layout
/// from the runtime contract: column widths are the widest cell including the header, a <c>null</c> cell is
/// rendered as an empty cell, and borders use <c>+</c>, <c>-</c> and <c>|</c>.
/// </summary>
public sealed class AsciiTableTests
{
    private static Dictionary<string, object?> Row(object? name, object? betrag, object? jahr) =>
        new() { ["Name"] = name, ["Betrag"] = betrag, ["Jahr"] = jahr };

    [Fact]
    public void Renders_exact_layout_with_widths_from_widest_cell_and_null_as_empty()
    {
        var rows = new object[]
        {
            Row("Müller", "1200.00", "2026"),
            Row("Schmidt", null, "2025"), // null Betrag -> empty cell
        };

        var actual = AsciiTable.Render(rows);

        // Widths: Name = max(4,6,7)=7, Betrag = max(6,7,0)=7, Jahr = max(4,4,4)=4.
        const string expected =
            "+---------+---------+------+\n" +
            "| Name    | Betrag  | Jahr |\n" +
            "+---------+---------+------+\n" +
            "| Müller  | 1200.00 | 2026 |\n" +
            "| Schmidt |         | 2025 |\n" +
            "+---------+---------+------+\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Null_cell_is_rendered_as_empty()
    {
        var rows = new object[] { Row("a", null, "b") };
        var actual = AsciiTable.Render(rows);

        // The Betrag column width equals its header "Betrag" (6) since the only data cell is empty.
        Assert.Contains("|        |", actual); // 6 spaces + 1 padding each side = 8 spaces between pipes
    }

    [Fact]
    public void Empty_rows_render_nothing()
    {
        Assert.Equal(string.Empty, AsciiTable.Render([]));
    }

    [Fact]
    public void Works_with_anonymous_objects_preserving_property_order()
    {
        var rows = new object[]
        {
            new { Id = 1, Label = "x" },
            new { Id = 22, Label = "yy" },
        };

        var actual = AsciiTable.Render(rows);

        const string expected =
            "+----+-------+\n" +
            "| Id | Label |\n" +
            "+----+-------+\n" +
            "| 1  | x     |\n" +
            "| 22 | yy    |\n" +
            "+----+-------+\n";

        Assert.Equal(expected, actual);
    }

    // ── B2: cell sanitization — newlines and pipe characters ───────────────────

    [Fact]
    public void Multiline_cell_is_collapsed_to_single_line_and_grid_stays_well_formed()
    {
        // A cell containing \r\n must not produce extra rows in the rendered table (B2).
        var rows = new object[]
        {
            new Dictionary<string, object?> { ["Name"] = "line1\r\nline2", ["Value"] = "42" },
            new Dictionary<string, object?> { ["Name"] = "plain",          ["Value"] = "7" },
        };

        var actual = AsciiTable.Render(rows);

        // Grid must have exactly 6 non-empty lines: top border, header, separator, 2 data rows, bottom border.
        var lines = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);

        // The \r\n must have been collapsed to a single space — "line1 line2" appears in the output.
        Assert.Contains("line1 line2", actual);

        // Every data row (starting with '|') must end with '|' — grid alignment intact.
        foreach (var line in lines.Where(l => l.StartsWith('|')))
            Assert.Equal('|', line[^1]);
    }

    [Fact]
    public void Pipe_in_cell_is_replaced_and_grid_stays_well_formed()
    {
        // A '|' inside a cell value must not inject a false column border (B2).
        var rows = new object[]
        {
            new Dictionary<string, object?> { ["A"] = "x|y", ["B"] = "ok" },
        };

        var actual = AsciiTable.Render(rows);

        // The table must have exactly 5 lines: top, header, separator, data, bottom.
        var lines = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);

        // The data row must contain exactly 3 ASCII pipe chars: leading, between A and B, trailing.
        // The '|' from the cell value was replaced with the box-drawing │ (U+2502), not ASCII |.
        var dataRow = lines[3]; // 0=top, 1=header, 2=separator, 3=data
        Assert.Equal(3, dataRow.Count(c => c == '|'));

        // The replacement character (U+2502 │) must appear in the data row (confirming substitution).
        Assert.Contains("│", dataRow);

        // The header row also has exactly 3 ASCII pipes (column "A" header contains no pipe).
        var headerRow = lines[1];
        Assert.Equal(3, headerRow.Count(c => c == '|'));
    }

    [Fact]
    public void Pipe_and_newline_in_headers_are_sanitized_without_losing_original_key_lookup()
    {
        var rows = new object[]
        {
            new Dictionary<string, object?>
            {
                ["a|b"] = "first",
                ["l1\nl2"] = "second",
            },
        };

        var actual = AsciiTable.Render(rows);
        var lines = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("a│b", lines[1]);
        Assert.Contains("l1 l2", lines[1]);
        Assert.Equal(3, lines[1].Count(c => c == '|'));
        Assert.Contains("first", lines[3]);
        Assert.Contains("second", lines[3]);
    }

    [Fact]
    public void Null_rows_and_inconsistent_key_sets_follow_first_row_schema_and_stay_well_formed()
    {
        var rows = new object?[]
        {
            new { A = "one", B = "two" },
            null,
            new Dictionary<string, object?> { ["A"] = "three", ["C"] = "ignored" },
        };

        var actual = AsciiTable.Render(rows!);
        var lines = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(7, lines.Length);
        Assert.All(lines, line => Assert.Equal(lines[0].Length, line.Length));
        Assert.DoesNotContain(lines[4], c => c is not ('|' or ' '));
        Assert.Contains("three", lines[5]);
        Assert.DoesNotContain("ignored", actual);
        Assert.DoesNotContain(" C ", lines[1]);
    }
}
