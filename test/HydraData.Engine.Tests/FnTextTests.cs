// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Xunit;

using static HydraData.Engine.Fn;

namespace HydraData.Engine.Tests;

// ---------------------------------------------------------------------------
// T06.2 - Text helpers: isBlank, fmt (incl. negative), cleanText, trimToNull
// ---------------------------------------------------------------------------

public class FnTextTests
{
    // ---- isBlank ----

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("\t\n", true)]
    [InlineData("hello", false)]
    [InlineData(" x ", false)]
    public void IsBlank_covers_null_empty_whitespace(string? input, bool expected)
    {
        Assert.Equal(expected, isBlank(input));
    }

    // ---- fmt (positive cases) ----

    [Fact]
    public void Fmt_uses_invariant_culture_for_decimal()
    {
        // Decimal separator must be '.' regardless of system locale.
        var result = fmt("{0}", 1.5m);
        Assert.Equal("1.5", result);
    }

    [Fact]
    public void Fmt_uses_invariant_culture_for_double()
    {
        var result = fmt("{0:F2}", 1.5);
        Assert.Equal("1.50", result);
    }

    [Fact]
    public void Fmt_substitutes_multiple_args()
    {
        Assert.Equal("A=1 B=hello", fmt("A={0} B={1}", 1, "hello"));
    }

    [Fact]
    public void Fmt_returns_string_only_does_not_write_to_console()
    {
        // Redirect stdout; fmt must not write anything there.
        var original = Console.Out;
        using var sw = new System.IO.StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = fmt("test {0}", 42);
            Assert.Equal("test 42", result);
            Assert.Equal(string.Empty, sw.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    // ---- fmt (negative / error paths, P1) ----

    // Bad format string — missing closing brace.
    [Fact]
    public void Fmt_bad_format_string_throws_FormatException()
    {
        Assert.Throws<FormatException>(() => fmt("{0", "x"));
    }

    // Format index out of range — only one arg supplied but {1} referenced.
    [Fact]
    public void Fmt_index_out_of_range_throws_FormatException()
    {
        Assert.Throws<FormatException>(() => fmt("{1}", "only-one"));
    }

    // Null format string — string.Format(InvariantCulture, null!, ...) throws ArgumentNullException.
    [Fact]
    public void Fmt_null_format_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => fmt(null!, 1));
    }

    // Null argument in args array — string.Format substitutes "" for a null argument.
    [Fact]
    public void Fmt_null_arg_substitutes_empty_string()
    {
        var result = fmt("{0}", new object?[] { null });
        Assert.Equal("", result);
    }

    // M.Text.Format is a thin facade — it must propagate FormatException, not swallow it.
    [Fact]
    public void M_Text_Format_bad_format_string_propagates_FormatException()
    {
        Assert.Throws<FormatException>(() => M.Text.Format("{0", "x"));
    }

    // ---- trimToNull ----

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\t\n", null)]
    [InlineData("hello", "hello")]
    [InlineData("  hi ", "hi")]
    public void TrimToNull_covers_all_cases(string? input, string? expected)
    {
        Assert.Equal(expected, trimToNull(input));
    }

    // ---- cleanText ----

    [Fact]
    public void CleanText_null_returns_null()
    {
        Assert.Null(cleanText(null));
    }

    [Fact]
    public void CleanText_empty_returns_empty()
    {
        Assert.Equal("", cleanText(""));
    }

    [Fact]
    public void CleanText_visible_text_unchanged()
    {
        Assert.Equal("Hello World!", cleanText("Hello World!"));
    }

    [Fact]
    public void CleanText_removes_control_characters()
    {
        // Build dirty string using char casts to avoid embedded bytes in source files.
        // CR=13, LF=10, BEL=7 are all UnicodeCategory.Control and must be removed.
        var cr = (char)13;  // carriage return
        var lf = (char)10;  // line feed
        var bel = (char)7;   // bell
        var dirty = "a" + cr + "b" + lf + "c" + bel + "d";
        var clean = cleanText(dirty)!;

        // Use Ordinal comparison — control chars are "ignored" by culture-sensitive
        // comparisons in .NET, causing false positives with Assert.DoesNotContain defaults.
        Assert.False(clean.Contains(cr.ToString(), StringComparison.Ordinal), "CR should be removed");
        Assert.False(clean.Contains(lf.ToString(), StringComparison.Ordinal), "LF should be removed");
        Assert.False(clean.Contains(bel.ToString(), StringComparison.Ordinal), "BEL should be removed");
        Assert.True(clean.Contains("a", StringComparison.Ordinal));
        Assert.True(clean.Contains("b", StringComparison.Ordinal));
        Assert.True(clean.Contains("c", StringComparison.Ordinal));
        Assert.True(clean.Contains("d", StringComparison.Ordinal));
    }

    [Fact]
    public void CleanText_preserves_spaces_and_tabs()
    {
        // Space (U+0020) is SpaceSeparator, not Control -- kept.
        // Tab (U+0009) is Control -- removed.
        var result = cleanText("a b\tc");
        Assert.Contains("a b", result);
        // Tab is a control character and gets removed.
        Assert.DoesNotContain("\t", result);
    }

    [Fact]
    public void CleanText_text_without_control_chars_fast_path()
    {
        const string input = "No issues here 123";
        Assert.Equal(input, cleanText(input));
    }

    // ---- cleanText OtherNotAssigned codepoint branch (P2) ----

    // U+FFFF is a non-character whose Unicode category is OtherNotAssigned.
    // FINDING: char.GetUnicodeCategory((char)0xFFFF) returns OtherNotAssigned,
    // so the predicate removes it. This exercises the OtherNotAssigned arm.
    [Fact]
    public void CleanText_removes_OtherNotAssigned_codepoint()
    {
        var unassigned = (char)0xFFFF; // OtherNotAssigned
        var input = "ab" + unassigned + "cd";
        var result = cleanText(input);
        Assert.Equal("abcd", result);
        Assert.False(result!.Contains(unassigned.ToString(), StringComparison.Ordinal));
    }
}
