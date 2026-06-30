// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Xunit;

using static HydraData.Engine.Fn;

namespace HydraData.Engine.Tests;

// ---------------------------------------------------------------------------
// T06.1a - M.* alias facade: Text / Value / Number / Logical / Guid / DateTime / List
// ---------------------------------------------------------------------------

public class MFacadeTests
{
    // ---- M.Text ----

    [Fact]
    public void M_Text_Format_matches_fmt()
    {
        Assert.Equal(fmt("{0}", 1.5m), M.Text.Format("{0}", 1.5m));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("helloworld")]
    [InlineData("clean text")]
    public void M_Text_Clean_matches_cleanText(string? input)
    {
        Assert.Equal(cleanText(input), M.Text.Clean(input));
    }

    [Fact]
    public void M_Text_Clean_with_control_char_matches_cleanText_and_removes_it()
    {
        // Build input with a real control character (BEL, U+0007) to exercise the actual
        // removal logic — not just a no-op pass-through on already-clean text.
        var input = "hello" + (char)7 + "world";
        var expected = cleanText(input);  // canonical implementation
        var actual = M.Text.Clean(input);
        Assert.Equal(expected, actual);
        // Verify the control char was actually removed (not just that the alias delegates).
        Assert.DoesNotContain("\a", actual, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  hello  ")]
    [InlineData("   ")]
    public void M_Text_Trim_matches_trimToNull(string? input)
    {
        Assert.Equal(trimToNull(input), M.Text.Trim(input));
    }

    [Theory]
    [InlineData("a-b", "-", null, "a")]
    [InlineData("ab", "-", "fb", "fb")]
    [InlineData(null, "-", "fb", "fb")]
    public void M_Text_BeforeDelimiter_matches_beforeDelimiter(string? text, string delim, string? fb, string? expected)
    {
        Assert.Equal(beforeDelimiter(text, delim, fb), M.Text.BeforeDelimiter(text, delim, fb));
        Assert.Equal(expected, M.Text.BeforeDelimiter(text, delim, fb));
    }

    [Theory]
    [InlineData("a-b", "-", null, "b")]
    [InlineData("ab", "-", "fb", "fb")]
    [InlineData(null, "-", "fb", "fb")]
    public void M_Text_AfterDelimiter_matches_afterDelimiter(string? text, string delim, string? fb, string? expected)
    {
        Assert.Equal(afterDelimiter(text, delim, fb), M.Text.AfterDelimiter(text, delim, fb));
        Assert.Equal(expected, M.Text.AfterDelimiter(text, delim, fb));
    }

    [Theory]
    [InlineData("[x]", "[", "]", null, "x")]
    [InlineData("nope", "[", "]", "fb", "fb")]
    [InlineData(null, "[", "]", "fb", "fb")]
    public void M_Text_BetweenDelimiters_matches_betweenDelimiters(
        string? text, string start, string end, string? fb, string? expected)
    {
        Assert.Equal(betweenDelimiters(text, start, end, fb), M.Text.BetweenDelimiters(text, start, end, fb));
        Assert.Equal(expected, M.Text.BetweenDelimiters(text, start, end, fb));
    }

    // ---- M.Value ----

    [Fact]
    public void M_Value_FromText_matches_parseOr()
    {
        Assert.Equal(parseOr<int>("42", -1), M.Value.FromText<int>("42", -1));
        Assert.Equal(parseOr<decimal>("1.5", 0m), M.Value.FromText<decimal>("1.5", 0m));
    }

    [Fact]
    public void M_Value_FromText_long_invalid_returns_fallback()
    {
        Assert.Equal(0L, M.Value.FromText<long>("bad", 0L));
    }

    [Fact]
    public void M_Value_FromText_long_valid()
    {
        Assert.Equal(9_999_999_999L, M.Value.FromText<long>("9999999999", 0L));
    }

    [Fact]
    public void M_Value_FromText_double_invalid_returns_fallback()
    {
        Assert.Equal(-1.0, M.Value.FromText<double>("nope", -1.0));
    }

    [Fact]
    public void M_Value_FromText_double_valid()
    {
        Assert.Equal(3.14, M.Value.FromText<double>("3.14", 0.0), precision: 10);
    }

    [Fact]
    public void M_Value_FromText_bool_true_string_returns_true()
    {
        Assert.True(M.Value.FromText<bool>("true", false));
    }

    [Fact]
    public void M_Value_FromText_bool_invalid_returns_fallback()
    {
        Assert.False(M.Value.FromText<bool>("garbage", false));
    }

    [Fact]
    public void M_Value_FromText_Guid_invalid_returns_Empty()
    {
        Assert.Equal(Guid.Empty, M.Value.FromText<Guid>("bad", Guid.Empty));
    }

    [Fact]
    public void M_Value_FromText_DateTime_invalid_returns_MinValue()
    {
        Assert.Equal(DateTime.MinValue, M.Value.FromText<DateTime>("garbage", DateTime.MinValue));
    }

    [Fact]
    public void M_Value_FromText_nullable_int_invalid_returns_null()
    {
        Assert.Null(M.Value.FromText<int?>("bad", null));
    }

    // ---- M.Number ----

    [Fact]
    public void M_Number_FromText_int_matches_parseOr()
    {
        Assert.Equal(parseOr<int>("7", 0), M.Number.FromText("7", 0));
        Assert.Equal(parseOr<int>("bad", 0), M.Number.FromText("bad", 0));
    }

    [Fact]
    public void M_Number_FromText_long_matches_parseOr()
    {
        Assert.Equal(parseOr<long>("9999999999", 0L), M.Number.FromText("9999999999", 0L));
    }

    [Fact]
    public void M_Number_FromText_decimal_matches_parseOr()
    {
        Assert.Equal(parseOr<decimal>("1.5", 0m), M.Number.FromText("1.5", 0m));
    }

    [Fact]
    public void M_Number_FromText_double_matches_parseOr()
    {
        Assert.Equal(parseOr<double>("3.14", 0.0), M.Number.FromText("3.14", 0.0));
    }

    [Fact]
    public void M_Number_FromText_long_invalid_returns_fallback()
    {
        Assert.Equal(0L, M.Number.FromText("bad", 0L));
    }

    // Comma as decimal separator is not valid under InvariantCulture → fallback.
    [Fact]
    public void M_Number_FromText_decimal_comma_separator_returns_fallback()
    {
        Assert.Equal(0m, M.Number.FromText("1,5", 0m));
    }

    [Fact]
    public void M_Number_FromText_double_invalid_returns_fallback()
    {
        Assert.Equal(-1.0, M.Number.FromText("nope", -1.0));
    }

    // ---- M.Logical ----

    [Fact]
    public void M_Logical_FromText_matches_parseOr()
    {
        Assert.Equal(parseOr<bool>("true", false), M.Logical.FromText("true", false));
        Assert.Equal(parseOr<bool>("nope", false), M.Logical.FromText("nope", false));
    }

    // ---- M.Guid ----

    [Fact]
    public void M_Guid_From_matches_parseOr()
    {
        var id = System.Guid.NewGuid();
        Assert.Equal(parseOr<Guid>(id.ToString(), Guid.Empty), M.Guid.From(id.ToString(), Guid.Empty));
        Assert.Equal(parseOr<Guid>("bad", Guid.Empty), M.Guid.From("bad", Guid.Empty));
    }

    // ---- M.DateTime ----

    [Fact]
    public void M_DateTime_FromText_matches_parseOr()
    {
        Assert.Equal(
            parseOr<DateTime>("2024-01-15", DateTime.MinValue),
            M.DateTime.FromText("2024-01-15", DateTime.MinValue));
        Assert.Equal(
            parseOr<DateTime>("garbage", DateTime.MinValue),
            M.DateTime.FromText("garbage", DateTime.MinValue));
    }

    // ---- M.List ----

    [Fact]
    public void M_List_Contains_matches_isIn()
    {
        Assert.Equal(isIn("B", "A", "B", "C"), M.List.Contains("B", "A", "B", "C"));
        Assert.Equal(isIn("X", "A", "B", "C"), M.List.Contains("X", "A", "B", "C"));
        Assert.Equal(isIn("A"), M.List.Contains("A")); // empty set
    }

    // Null value matched against a set containing null.
    [Fact]
    public void M_List_Contains_null_value_matches_null_in_set()
    {
        Assert.True(M.List.Contains<string?>(null, null, "A"));
    }

    // Integer membership check.
    [Fact]
    public void M_List_Contains_int_found()
    {
        Assert.True(M.List.Contains(3, 1, 2, 3));
    }

    [Fact]
    public void M_List_Contains_int_not_found()
    {
        Assert.False(M.List.Contains(4, 1, 2, 3));
    }
}
