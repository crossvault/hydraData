// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Xunit;

using static HydraData.Engine.Fn;

namespace HydraData.Engine.Tests;

// ---------------------------------------------------------------------------
// T06.2.6 - Delimiter helpers: beforeDelimiter, afterDelimiter, betweenDelimiters
// ---------------------------------------------------------------------------

public class FnDelimiterTests
{
    // ---- beforeDelimiter ----

    [Fact]
    public void BeforeDelimiter_found()
    {
        Assert.Equal("MANDANT", beforeDelimiter("MANDANT-ARTIKEL", "-"));
    }

    [Fact]
    public void BeforeDelimiter_not_found_returns_fallback()
    {
        Assert.Equal("fallback", beforeDelimiter("NODASH", "-", "fallback"));
    }

    [Fact]
    public void BeforeDelimiter_not_found_default_fallback_null()
    {
        Assert.Null(beforeDelimiter("NODASH", "-"));
    }

    [Fact]
    public void BeforeDelimiter_null_text_returns_fallback()
    {
        Assert.Equal("fb", beforeDelimiter(null, "-", "fb"));
    }

    [Fact]
    public void BeforeDelimiter_empty_delimiter_returns_fallback()
    {
        Assert.Equal("fb", beforeDelimiter("text", "", "fb"));
    }

    [Fact]
    public void BeforeDelimiter_multi_char_delimiter()
    {
        Assert.Equal("foo", beforeDelimiter("foo::bar", "::"));
    }

    [Fact]
    public void BeforeDelimiter_uses_first_occurrence()
    {
        Assert.Equal("a", beforeDelimiter("a-b-c", "-"));
    }

    // ---- afterDelimiter ----

    [Fact]
    public void AfterDelimiter_found()
    {
        Assert.Equal("ARTIKEL", afterDelimiter("MANDANT-ARTIKEL", "-"));
    }

    [Fact]
    public void AfterDelimiter_not_found_returns_fallback()
    {
        Assert.Equal("fb", afterDelimiter("NODASH", "-", "fb"));
    }

    [Fact]
    public void AfterDelimiter_null_text_returns_fallback()
    {
        Assert.Equal("fb", afterDelimiter(null, "-", "fb"));
    }

    [Fact]
    public void AfterDelimiter_empty_delimiter_returns_fallback()
    {
        Assert.Equal("fb", afterDelimiter("text", "", "fb"));
    }

    [Fact]
    public void AfterDelimiter_uses_first_occurrence()
    {
        Assert.Equal("b-c", afterDelimiter("a-b-c", "-"));
    }

    [Fact]
    public void AfterDelimiter_delimiter_at_end_returns_empty_string()
    {
        Assert.Equal("", afterDelimiter("abc-", "-"));
    }

    // ---- betweenDelimiters ----

    [Fact]
    public void BetweenDelimiters_found()
    {
        Assert.Equal("inner", betweenDelimiters("[inner]", "[", "]"));
    }

    [Fact]
    public void BetweenDelimiters_not_found_returns_fallback()
    {
        Assert.Equal("fb", betweenDelimiters("no brackets", "[", "]", "fb"));
    }

    [Fact]
    public void BetweenDelimiters_null_text_returns_fallback()
    {
        Assert.Equal("fb", betweenDelimiters(null, "[", "]", "fb"));
    }

    [Fact]
    public void BetweenDelimiters_empty_start_returns_fallback()
    {
        Assert.Equal("fb", betweenDelimiters("[x]", "", "]", "fb"));
    }

    [Fact]
    public void BetweenDelimiters_empty_end_returns_fallback()
    {
        Assert.Equal("fb", betweenDelimiters("[x]", "[", "", "fb"));
    }

    [Fact]
    public void BetweenDelimiters_wrong_order_end_before_start_returns_fallback()
    {
        // "]" appears before "[" -- endDelimiter not found after startDelimiter.
        Assert.Equal("fb", betweenDelimiters("]wrong[", "[", "]", "fb"));
    }

    [Fact]
    public void BetweenDelimiters_multi_char_delimiters()
    {
        Assert.Equal("content", betweenDelimiters("<<content>>", "<<", ">>"));
    }

    [Fact]
    public void BetweenDelimiters_start_not_found_returns_fallback()
    {
        Assert.Equal("fb", betweenDelimiters("content>>", "<<", ">>", "fb"));
    }

    [Fact]
    public void BetweenDelimiters_end_not_found_returns_fallback()
    {
        Assert.Equal("fb", betweenDelimiters("<<content", "<<", ">>", "fb"));
    }
}
