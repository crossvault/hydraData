// Copyright (c) 2026 crossVault GmbH.

using System.Globalization;
using HydraData.Engine;
using Xunit;

using static HydraData.Engine.Fn;

namespace HydraData.Engine.Tests;

// ---------------------------------------------------------------------------
// T06.2.2 - Range/set helpers: between<T> (all types + null-throw), isIn<T>
// ---------------------------------------------------------------------------

public class FnRangeSetTests
{
    // ---- between — int ----

    [Theory]
    [InlineData(5, 1, 10, true)]   // inside
    [InlineData(1, 1, 10, true)]   // equals lo (inclusive)
    [InlineData(10, 1, 10, true)]  // equals hi (inclusive)
    [InlineData(0, 1, 10, false)]  // below lo
    [InlineData(11, 1, 10, false)] // above hi
    public void Between_int_boundary_cases(int value, int lo, int hi, bool expected)
    {
        Assert.Equal(expected, between(value, lo, hi));
    }

    [Fact]
    public void Between_swapped_bounds_returns_false_without_throw()
    {
        Assert.False(between(5, 10, 1));
    }

    // ---- between — string ----

    [Theory]
    [InlineData("b", "a", "c", true)]
    [InlineData("a", "a", "c", true)]
    [InlineData("c", "a", "c", true)]
    [InlineData("z", "a", "c", false)]
    public void Between_string_boundary_cases(string value, string lo, string hi, bool expected)
    {
        Assert.Equal(expected, between(value, lo, hi));
    }

    // ---- between — double ----

    [Theory]
    [InlineData(1.5, 1.0, 2.0, true)]
    [InlineData(1.0, 1.0, 2.0, true)]
    [InlineData(2.0, 1.0, 2.0, true)]
    [InlineData(0.9, 1.0, 2.0, false)]
    [InlineData(2.1, 1.0, 2.0, false)]
    public void Between_double_boundary_cases(double value, double lo, double hi, bool expected)
    {
        Assert.Equal(expected, between(value, lo, hi));
    }

    // ---- between — decimal (P2) ----

    [Theory]
    [InlineData("1.5", "1.0", "2.0", true)]   // inside
    [InlineData("1.0", "1.0", "2.0", true)]   // equals lo
    [InlineData("2.0", "1.0", "2.0", true)]   // equals hi
    [InlineData("0.5", "1.0", "2.0", false)]  // below lo
    [InlineData("2.1", "1.0", "2.0", false)]  // above hi
    public void Between_decimal_boundary_cases(string valueStr, string loStr, string hiStr, bool expected)
    {
        var value = decimal.Parse(valueStr, CultureInfo.InvariantCulture);
        var lo = decimal.Parse(loStr, CultureInfo.InvariantCulture);
        var hi = decimal.Parse(hiStr, CultureInfo.InvariantCulture);
        Assert.Equal(expected, between(value, lo, hi));
    }

    // ---- between — DateTime (P2) — collapsed from 5 individual [Fact]s ----

    // The lo/hi bounds are fixed: 2024-01-01 to 2024-12-31.
    // Encoded as "YYYY-M-D" strings so InlineData can carry them; parsed inside.
    [Theory]
    [InlineData("2024-6-15", true)]   // inside
    [InlineData("2024-1-1", true)]    // equals lo
    [InlineData("2024-12-31", true)]  // equals hi
    [InlineData("2023-12-31", false)] // before lo
    [InlineData("2025-1-1", false)]   // after hi
    public void Between_datetime_boundary_cases(string valueStr, bool expected)
    {
        var lo = new DateTime(2024, 1, 1);
        var hi = new DateTime(2024, 12, 31);
        var value = DateTime.Parse(valueStr, CultureInfo.InvariantCulture);
        Assert.Equal(expected, between(value, lo, hi));
    }

    // ---- between — long (P2) ----

    [Theory]
    [InlineData(5L, 1L, 10L, true)]    // inside
    [InlineData(1L, 1L, 10L, true)]    // equals lo
    [InlineData(10L, 1L, 10L, true)]   // equals hi
    [InlineData(0L, 1L, 10L, false)]   // below lo
    [InlineData(11L, 1L, 10L, false)]  // above hi
    public void Between_long_boundary_cases(long value, long lo, long hi, bool expected)
    {
        Assert.Equal(expected, between(value, lo, hi));
    }

    // ---- between — null throws (P1) ----

    // FINDING: The XML doc states: "passing null will throw a NullReferenceException
    // via IComparable<T>.CompareTo". The code calls value.CompareTo(lo) first; on a
    // null! string reference that method call dereferences the null → NullReferenceException.
    // Behavior matches the documentation.
    [Fact]
    public void Between_null_value_throws_NullReferenceException()
    {
        Assert.Throws<NullReferenceException>(() => between<string>(null!, "a", "z"));
    }

    // ---- isIn ----

    [Fact]
    public void IsIn_returns_true_for_matching_element()
    {
        Assert.True(isIn("B", "A", "B", "C"));
    }

    [Fact]
    public void IsIn_returns_false_for_non_matching_element()
    {
        Assert.False(isIn("X", "A", "B", "C"));
    }

    [Fact]
    public void IsIn_empty_set_returns_false()
    {
        Assert.False(isIn("A"));
    }

    [Fact]
    public void IsIn_null_value_matches_null_in_set()
    {
        Assert.True(isIn<string?>(null, null, "A"));
    }

    [Fact]
    public void IsIn_null_value_returns_false_when_set_has_no_null()
    {
        Assert.False(isIn<string?>(null, "A", "B"));
    }

    [Fact]
    public void IsIn_int_works()
    {
        Assert.True(isIn(3, 1, 2, 3));
        Assert.False(isIn(4, 1, 2, 3));
    }
}
