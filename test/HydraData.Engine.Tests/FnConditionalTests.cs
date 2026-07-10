// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Xunit;

using static HydraData.Engine.Fn;

namespace HydraData.Engine.Tests;

// ---------------------------------------------------------------------------
// T06.2 - Conditional helpers: iif, icase, coalesce, coalesceBlank, nullIf
// ---------------------------------------------------------------------------

public class FnConditionalTests
{
    // ---- iif ----

    [Theory]
    [InlineData(true, "yes", "no", "yes")]
    [InlineData(false, "yes", "no", "no")]
    public void Iif_returns_correct_branch(bool cond, string then, string otherwise, string expected)
    {
        Assert.Equal(expected, iif(cond, then, otherwise));
    }

    [Fact]
    public void Iif_evaluates_both_branches_eagerly()
    {
        // Both branches are evaluated; side-effects in both branches run.
        var sideEffect = 0;
        var result = iif(true, ++sideEffect, ++sideEffect);
        Assert.Equal(2, sideEffect); // both ++ ran
        Assert.Equal(1, result);
    }

    [Theory]
    [InlineData(true, 42, 0, 42)]
    [InlineData(false, 42, 0, 0)]
    public void Iif_works_with_value_types(bool cond, int then, int otherwise, int expected)
    {
        Assert.Equal(expected, iif(cond, then, otherwise));
    }

    // ---- icase ----

    [Fact]
    public void Icase_returns_first_matching_case()
    {
        var result = icase("C",
            (true, "A"),
            (true, "B"));
        Assert.Equal("A", result);
    }

    [Fact]
    public void Icase_skips_false_cases_and_returns_first_true()
    {
        var amount = 500m;
        var result = icase("C",
            (amount >= 1000m, "A"),
            (amount >= 100m, "B"));
        Assert.Equal("B", result);
    }

    [Fact]
    public void Icase_returns_elseValue_when_no_case_matches()
    {
        var result = icase("fallback",
            (false, "A"),
            (false, "B"));
        Assert.Equal("fallback", result);
    }

    [Fact]
    public void Icase_with_empty_cases_returns_elseValue()
    {
        var result = icase(99);
        Assert.Equal(99, result);
    }

    [Fact]
    public void Icase_elseValue_null_is_allowed()
    {
        string? result = icase<string?>(null, (false, "x"));
        Assert.Null(result);
    }

    [Fact]
    public void Icase_evaluates_all_branch_values_eagerly()
    {
        var sideEffect = 0;

        var result = icase(0,
            (true, ++sideEffect),
            (false, ++sideEffect));

        Assert.Equal(1, result);
        Assert.Equal(2, sideEffect);
    }

    // ---- coalesce ----

    [Fact]
    public void Coalesce_returns_first_non_null()
    {
        Assert.Equal("b", coalesce<string>(null, "b", "c"));
    }

    [Fact]
    public void Coalesce_all_null_returns_null()
    {
        Assert.Null(coalesce<string>(null, null, null));
    }

    [Fact]
    public void Coalesce_first_element_non_null_returns_it()
    {
        Assert.Equal("a", coalesce<string>("a", "b"));
    }

    [Fact]
    public void Coalesce_single_null_returns_null()
    {
        Assert.Null(coalesce<string>((string?)null));
    }

    [Fact]
    public void Coalesce_empty_returns_null()
    {
        Assert.Null(coalesce<string>());
    }

    // ---- coalesce nullable value types (P2) ----

    [Fact]
    public void Coalesce_nullable_int_returns_first_non_null()
    {
        // T = int?, params T?[] is int?[] (Nullable<int>[]); null entries skipped.
        int? result = coalesce<int?>(null, 5, 9);
        Assert.Equal(5, result);
    }

    [Fact]
    public void Coalesce_nullable_int_all_null_returns_null()
    {
        int? result = coalesce<int?>(null, null);
        Assert.Null(result);
    }

    // ---- coalesceBlank ----

    [Fact]
    public void CoalesceBlank_all_blank_returns_null()
    {
        Assert.Null(coalesceBlank(null, "", "   "));
    }

    [Fact]
    public void CoalesceBlank_returns_first_real_value()
    {
        Assert.Equal("hello", coalesceBlank(null, "", "hello", "world"));
    }

    [Fact]
    public void CoalesceBlank_skips_whitespace_only()
    {
        Assert.Equal("x", coalesceBlank("   ", "\t", "x"));
    }

    [Fact]
    public void CoalesceBlank_single_valid_value()
    {
        Assert.Equal("a", coalesceBlank("a"));
    }

    [Fact]
    public void CoalesceBlank_empty_params_returns_null()
    {
        Assert.Null(coalesceBlank());
    }

    [Fact]
    public void CoalesceBlank_coalesce_passes_empty_string_but_coalesceBlank_skips_it()
    {
        // coalesce sees "" as non-null and returns it; coalesceBlank skips it.
        var withCoalesce = coalesce<string>(null, "");
        var withBlankCoalesce = coalesceBlank(null, "", "real");

        Assert.Equal("", withCoalesce);          // coalesce stops at ""
        Assert.Equal("real", withBlankCoalesce); // coalesceBlank skips ""
    }

    // ---- nullIf — reference types ----

    [Fact]
    public void NullIf_reference_hit_returns_null()
    {
        Assert.Null(nullIf("N/A", "N/A"));
    }

    [Fact]
    public void NullIf_reference_miss_returns_value()
    {
        Assert.Equal("real", nullIf("real", "N/A"));
    }

    [Fact]
    public void NullIf_reference_null_input_non_null_sentinel_returns_null_input()
    {
        // null is not equal to "N/A", so the null input is returned as-is (which is null).
        string? result = nullIf<string>(null, "N/A");
        Assert.Null(result);
    }

    // ---- nullIf — value types ----

    [Fact]
    public void NullIf_struct_hit_returns_null()
    {
        Assert.Null(nullIf<int>(-1, -1));
    }

    [Fact]
    public void NullIf_struct_miss_returns_value()
    {
        Assert.Equal((int?)42, nullIf<int>(42, -1));
    }

    [Fact]
    public void NullIf_struct_null_input_returns_null()
    {
        int? input = null;
        Assert.Null(nullIf(input, -1));
    }

    [Fact]
    public void NullIf_decimal_sentinel()
    {
        Assert.Null(nullIf<decimal>(0m, 0m));
        Assert.Equal((decimal?)1.5m, nullIf<decimal>(1.5m, 0m));
    }
}
