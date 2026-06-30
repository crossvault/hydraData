// Copyright (c) 2026 crossVault GmbH.

using System.Globalization;
using HydraData.Engine;
using Xunit;

using static HydraData.Engine.Fn;

namespace HydraData.Engine.Tests;

// ---------------------------------------------------------------------------
// T06.2.1 - parseOr<T>: all types, nullable, overflow, non-finite, trim
// ---------------------------------------------------------------------------

public class FnParseTests
{
    // ---- int ----

    [Theory]
    [InlineData("42", 42)]
    [InlineData("-7", -7)]
    [InlineData("0", 0)]
    public void ParseOr_int_valid(string input, int expected)
    {
        Assert.Equal(expected, parseOr<int>(input, -1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1,5")]
    public void ParseOr_int_invalid_returns_fallback(string? input)
    {
        Assert.Equal(-1, parseOr<int>(input, -1));
    }

    // ---- long ----

    [Theory]
    [InlineData("9999999999", 9_999_999_999L)]
    [InlineData("-1", -1L)]
    public void ParseOr_long_valid(string input, long expected)
    {
        Assert.Equal(expected, parseOr<long>(input, 0L));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("xyz")]
    [InlineData("1.5")]
    public void ParseOr_long_invalid_returns_fallback(string? input)
    {
        Assert.Equal(0L, parseOr<long>(input, 0L));
    }

    // ---- decimal (InvariantCulture = period separator) ----

    [Theory]
    [InlineData("1.5", "1.5")]
    [InlineData("-3.14", "-3.14")]
    [InlineData("0", "0")]
    public void ParseOr_decimal_valid_with_period(string input, string expectedStr)
    {
        var expected = decimal.Parse(expectedStr, CultureInfo.InvariantCulture);
        Assert.Equal(expected, parseOr<decimal>(input, 0m));
    }

    [Theory]
    [InlineData("1,5")]   // comma separator -- fallback (invariant: comma is not decimal point)
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("")]
    public void ParseOr_decimal_comma_and_invalid_return_fallback(string? input)
    {
        Assert.Equal(0m, parseOr<decimal>(input, 0m));
    }

    // ---- double ----

    [Theory]
    [InlineData("3.14", 3.14)]
    [InlineData("-1.0", -1.0)]
    public void ParseOr_double_valid(string input, double expected)
    {
        Assert.Equal(expected, parseOr<double>(input, 0.0), precision: 10);
    }

    [Theory]
    [InlineData("3,14")]
    [InlineData(null)]
    [InlineData("nope")]
    public void ParseOr_double_invalid_returns_fallback(string? input)
    {
        Assert.Equal(0.0, parseOr<double>(input, 0.0));
    }

    // ---- bool ----

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("false", false)]
    public void ParseOr_bool_valid(string input, bool expected)
    {
        Assert.Equal(expected, parseOr<bool>(input, false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ja")]
    [InlineData("1")]
    [InlineData("yes")]
    public void ParseOr_bool_invalid_returns_fallback(string? input)
    {
        Assert.True(parseOr<bool>(input, true));
    }

    // ---- Guid ----

    [Fact]
    public void ParseOr_guid_valid()
    {
        var id = System.Guid.NewGuid();
        Assert.Equal(id, parseOr<Guid>(id.ToString(), Guid.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("ZZZZZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZZZZZZZZZ")]
    public void ParseOr_guid_invalid_returns_fallback(string? input)
    {
        Assert.Equal(Guid.Empty, parseOr<Guid>(input, Guid.Empty));
    }

    // ---- Unsupported type throws NotSupportedException at runtime ----

    [Fact]
    public void ParseOr_unsupported_type_throws_NotSupportedException()
    {
        // float (System.Single) is not a supported type — must throw at runtime.
        Assert.Throws<NotSupportedException>(() => parseOr<float>("1.5", 0f));
    }

    // ---- DateTime ----

    [Theory]
    [InlineData("2024-01-15")]
    [InlineData("01/15/2024")]   // invariant short date
    public void ParseOr_datetime_valid(string input)
    {
        var result = parseOr<DateTime>(input, DateTime.MinValue);
        Assert.NotEqual(DateTime.MinValue, result);
    }

    [Fact]
    public void ParseOr_datetime_value_assertion()
    {
        // Assert the actual parsed value, not just that it is not MinValue.
        var result = parseOr<DateTime>("2024-06-15", DateTime.MinValue);
        Assert.Equal(new DateTime(2024, 6, 15), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("32.13.2024")]
    public void ParseOr_datetime_invalid_returns_fallback(string? input)
    {
        Assert.Equal(DateTime.MinValue, parseOr<DateTime>(input, DateTime.MinValue));
    }

    // ---- Nullable forms ----

    [Fact]
    public void ParseOr_nullable_int_valid()
    {
        Assert.Equal((int?)42, parseOr<int?>("42", null));
    }

    [Fact]
    public void ParseOr_nullable_int_invalid_returns_fallback()
    {
        Assert.Null(parseOr<int?>("abc", null));
    }

    [Fact]
    public void ParseOr_nullable_decimal_valid()
    {
        Assert.Equal((decimal?)1.5m, parseOr<decimal?>("1.5", null));
    }

    [Fact]
    public void ParseOr_nullable_guid_valid()
    {
        var id = System.Guid.NewGuid();
        Assert.Equal((Guid?)id, parseOr<Guid?>(id.ToString(), null));
    }

    [Fact]
    public void ParseOr_nullable_datetime_invalid_returns_null_fallback()
    {
        Assert.Null(parseOr<DateTime?>("nope", null));
    }

    // ---- No exception ever escapes ----

    [Theory]
    [InlineData("XYZ_GARBAGE")]
    [InlineData("99999999999999999999999999999999999999")]
    public void ParseOr_never_throws_on_garbage(string input)
    {
        // None of these should throw regardless of type.
        var ex1 = Record.Exception(() => parseOr<int>(input, 0));
        var ex2 = Record.Exception(() => parseOr<long>(input, 0L));
        var ex3 = Record.Exception(() => parseOr<decimal>(input, 0m));
        var ex4 = Record.Exception(() => parseOr<double>(input, 0.0));
        var ex5 = Record.Exception(() => parseOr<bool>(input, false));
        var ex6 = Record.Exception(() => parseOr<Guid>(input, Guid.Empty));
        var ex7 = Record.Exception(() => parseOr<DateTime>(input, DateTime.MinValue));

        Assert.Null(ex1);
        Assert.Null(ex2);
        Assert.Null(ex3);
        Assert.Null(ex4);
        Assert.Null(ex5);
        Assert.Null(ex6);
        Assert.Null(ex7);
    }

    // ---- Overflow (P1) ----

    // int overflow: "99999999999" exceeds Int32.MaxValue → fallback.
    [Fact]
    public void ParseOr_int_overflow_returns_fallback()
    {
        Assert.Equal(-1, parseOr<int>("99999999999", -1));
    }

    // long overflow: a number exceeding Int64.MaxValue → fallback.
    [Fact]
    public void ParseOr_long_overflow_returns_fallback()
    {
        Assert.Equal(0L, parseOr<long>("99999999999999999999999999", 0L));
    }

    // decimal overflow: a number exceeding Decimal.MaxValue → fallback.
    [Fact]
    public void ParseOr_decimal_overflow_returns_fallback()
    {
        // 30-digit string that cannot be represented as decimal.
        Assert.Equal(0m, parseOr<decimal>("79228162514264337593543950336", 0m));
    }

    // parseOr<double> treats a non-finite result as a parse failure → fallback.
    [Theory]
    [InlineData("1E400")]    // exponent overflow → +Infinity
    [InlineData("-1E400")]   // exponent overflow → -Infinity
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("NaN")]
    public void ParseOr_double_non_finite_returns_fallback(string text)
    {
        Assert.Equal(-1.0, parseOr<double>(text, -1.0));
    }

    // A finite double still parses normally (guard does not over-reject).
    [Fact]
    public void ParseOr_double_finite_value_still_parses()
    {
        Assert.Equal(3.5, parseOr<double>("3.5", -1.0));
        Assert.Equal(-1.0, parseOr<double>("garbage", -1.0));
    }

    // DateTimeOffset is not a supported type — throws NotSupportedException.
    [Fact]
    public void ParseOr_DateTimeOffset_unsupported_throws_NotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() =>
            parseOr<DateTimeOffset>("2024-01-15T00:00:00+01:00", DateTimeOffset.MinValue));
    }

    // ---- Nullable forms not yet covered (P2) ----

    [Fact]
    public void ParseOr_nullable_long_null_input_returns_null_fallback()
    {
        Assert.Null(parseOr<long?>(null, null));
    }

    [Fact]
    public void ParseOr_nullable_long_valid_returns_parsed_value()
    {
        Assert.Equal((long?)7L, parseOr<long?>("7", null));
    }

    [Fact]
    public void ParseOr_nullable_double_invalid_returns_null()
    {
        Assert.Null(parseOr<double?>("bad", null));
    }

    [Fact]
    public void ParseOr_nullable_bool_true_string_returns_true()
    {
        Assert.Equal((bool?)true, parseOr<bool?>("true", null));
    }

    [Fact]
    public void ParseOr_nullable_bool_invalid_returns_null()
    {
        Assert.Null(parseOr<bool?>("x", null));
    }

    [Fact]
    public void ParseOr_nullable_guid_invalid_returns_null()
    {
        Assert.Null(parseOr<Guid?>("bad", null));
    }

    // ---- Whitespace-padded inputs (P2) ----

    // FINDING: The code calls string.IsNullOrWhiteSpace(text) first (pure-whitespace → fallback),
    // then text.Trim() before parsing. So " 42 " reaches TryParse as "42" → succeeds.
    [Fact]
    public void ParseOr_int_whitespace_padded_is_trimmed_and_parsed()
    {
        Assert.Equal(42, parseOr<int>(" 42 ", -1));
    }

    // The decimal NumberStyles also includes AllowLeadingWhite / AllowTrailingWhite, and the
    // outer Trim() runs first. Either way " 1.5 " should parse to 1.5m.
    [Fact]
    public void ParseOr_decimal_whitespace_padded_is_trimmed_and_parsed()
    {
        Assert.Equal(1.5m, parseOr<decimal>(" 1.5 ", 0m));
    }
}
