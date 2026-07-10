// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// Tests for <see cref="StepMeta.Parse"/> (T04.4).
/// </summary>
public class StepMetaTests
{
    // ── Defaults when tags absent ────────────────────────────────────────────

    [Fact]
    public void Empty_source_returns_defaults()
    {
        var meta = StepMeta.Parse(string.Empty);
        Assert.Null(meta.Name);
        Assert.Null(meta.Description);
        Assert.True(meta.HaltOnError);
        Assert.False(meta.Unsafe);
    }

    [Fact]
    public void Source_without_any_tags_returns_defaults()
    {
        var meta = StepMeta.Parse("var x = 1;");
        Assert.Null(meta.Name);
        Assert.Null(meta.Description);
        Assert.True(meta.HaltOnError);
        Assert.False(meta.Unsafe);
    }

    // ── Name and description ─────────────────────────────────────────────────

    [Fact]
    public void Parses_name_tag()
    {
        var meta = StepMeta.Parse("// @name: Kunden-Import");
        Assert.Equal("Kunden-Import", meta.Name);
    }

    [Fact]
    public void Parses_description_tag()
    {
        var meta = StepMeta.Parse("// @description: Liest kunden.xlsx");
        Assert.Equal("Liest kunden.xlsx", meta.Description);
    }

    // ── haltOnError ──────────────────────────────────────────────────────────

    [Fact]
    public void HaltOnError_defaults_to_true_when_absent()
    {
        var meta = StepMeta.Parse("// @name: x");
        Assert.True(meta.HaltOnError);
    }

    [Fact]
    public void HaltOnError_false_when_set()
    {
        var meta = StepMeta.Parse("// @haltOnError: false");
        Assert.False(meta.HaltOnError);
    }

    [Fact]
    public void HaltOnError_true_when_explicitly_set()
    {
        var meta = StepMeta.Parse("// @haltOnError: true");
        Assert.True(meta.HaltOnError);
    }

    // ── unsafe ───────────────────────────────────────────────────────────────

    [Fact]
    public void Unsafe_defaults_to_false_when_absent()
    {
        var meta = StepMeta.Parse("// @name: x");
        Assert.False(meta.Unsafe);
    }

    [Fact]
    public void Unsafe_true_when_set()
    {
        var meta = StepMeta.Parse("// @unsafe: true");
        Assert.True(meta.Unsafe);
    }

    // ── @type is ignored ─────────────────────────────────────────────────────

    [Fact]
    public void Type_tag_has_no_effect()
    {
        // @type is a script-level annotation; it must be ignored entirely.
        var meta = StepMeta.Parse("""
            // @name: Test Step
            // @type: MSSQL
            // @unsafe: false
            """);

        Assert.Equal("Test Step", meta.Name);
        Assert.False(meta.Unsafe);
        // No property on StepMeta should reflect @type
        var props = typeof(StepMeta).GetProperties();
        Assert.DoesNotContain(props, p =>
            p.Name.Equals("Type", StringComparison.OrdinalIgnoreCase));
    }

    // ── Full block ───────────────────────────────────────────────────────────

    [Fact]
    public void Parses_full_meta_block()
    {
        var source = """
            // @name: Kunden-Import aus Excel
            // @description: Liest kunden.xlsx und lädt nach PGSQL
            // @haltOnError: true
            // @unsafe: false
            var x = 1;
            """;

        var meta = StepMeta.Parse(source);

        Assert.Equal("Kunden-Import aus Excel", meta.Name);
        Assert.Equal("Liest kunden.xlsx und lädt nach PGSQL", meta.Description);
        Assert.True(meta.HaltOnError);
        Assert.False(meta.Unsafe);
    }

    [Fact]
    public void Stops_parsing_at_first_non_comment_line()
    {
        var source = """
            // @name: First
            var x = 1;
            // @description: Should be ignored (after code)
            """;

        var meta = StepMeta.Parse(source);
        Assert.Equal("First", meta.Name);
        Assert.Null(meta.Description);
    }

    // ── Static Default ───────────────────────────────────────────────────────

    [Fact]
    public void Static_Default_matches_empty_parse()
    {
        var parsed = StepMeta.Parse(string.Empty);
        Assert.Equal(StepMeta.Default.HaltOnError, parsed.HaltOnError);
        Assert.Equal(StepMeta.Default.Unsafe, parsed.Unsafe);
        Assert.Equal(StepMeta.Default.Name, parsed.Name);
        Assert.Equal(StepMeta.Default.Description, parsed.Description);
    }

    // ── Garbage / unrecognised values keep defaults ───────────────────────────

    [Fact]
    public void HaltOnError_garbage_value_keeps_default_true()
    {
        // "maybe" is not a recognised bool value; default (true) must be preserved.
        var meta = StepMeta.Parse("// @haltOnError: maybe");
        Assert.True(meta.HaltOnError);
    }

    [Theory]
    [InlineData("// @NaMe: Case\n// @UnSaFe: TrUe", "Case", null, true, true)]
    [InlineData("// @description: first: second", null, "first: second", true, false)]
    [InlineData("// @name: Before\n\n// @description: After", "Before", "After", true, false)]
    [InlineData("// @unknown: ignored\n// @haltOnError: false", null, null, false, false)]
    [InlineData("//@name: Compact", "Compact", null, true, false)]
    public void Parses_supported_leading_comment_variants(
        string source,
        string? expectedName,
        string? expectedDescription,
        bool expectedHaltOnError,
        bool expectedUnsafe)
    {
        // Coverage-only: these inputs exercise the parser's established case, colon, and block rules.
        var meta = StepMeta.Parse(source);

        Assert.Equal(expectedName, meta.Name);
        Assert.Equal(expectedDescription, meta.Description);
        Assert.Equal(expectedHaltOnError, meta.HaltOnError);
        Assert.Equal(expectedUnsafe, meta.Unsafe);
    }
}
