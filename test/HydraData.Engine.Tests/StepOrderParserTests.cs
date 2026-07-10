// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// Tests for <see cref="StepLoader.TryParseOrder"/> — the filename-to-StepOrder parser.
/// Covers all runtime contract
/// </summary>
public class StepOrderParserTests
{
    // ── runtime contract─────────────────────────────────────────────

    [Fact]
    public void Parses_01_10_kunden_slug()
    {
        // 01_10_[kunden]_stammdaten_lesen
        Assert.True(StepLoader.TryParseOrder(
            "01_10_[kunden]_stammdaten_lesen", out var order, out _));
        Assert.Equal(1, order.Group);
        Assert.Equal(10, order.Step);
        Assert.Null(order.SubStep);
        Assert.Equal("[kunden]", order.Slug);
    }

    [Fact]
    public void Parses_01_20_kunden_validieren()
    {
        Assert.True(StepLoader.TryParseOrder(
            "01_20_[kunden]_validieren", out var order, out _));
        Assert.Equal(1, order.Group);
        Assert.Equal(20, order.Step);
        Assert.Null(order.SubStep);
        Assert.Equal("[kunden]", order.Slug);
    }

    [Fact]
    public void Parses_01_25_dublettencheck_no_slug()
    {
        Assert.True(StepLoader.TryParseOrder(
            "01_25_dublettencheck", out var order, out _));
        Assert.Equal(1, order.Group);
        Assert.Equal(25, order.Step);
        Assert.Null(order.SubStep);
        Assert.Null(order.Slug);
    }

    [Fact]
    public void Parses_01_20_05_feinschritt_with_substep()
    {
        Assert.True(StepLoader.TryParseOrder(
            "01_20_05_feinschritt", out var order, out _));
        Assert.Equal(1, order.Group);
        Assert.Equal(20, order.Step);
        Assert.Equal(5, order.SubStep);
        Assert.Null(order.Slug);
    }

    [Fact]
    public void Parses_02_10_auftraege_slug()
    {
        Assert.True(StepLoader.TryParseOrder(
            "02_10_[auftraege]_lesen", out var order, out _));
        Assert.Equal(2, order.Group);
        Assert.Equal(10, order.Step);
        Assert.Null(order.SubStep);
        Assert.Equal("[auftraege]", order.Slug);
    }

    // ── Dot separator (compatibility) ────────────────────────────────────────

    [Fact]
    public void Parses_dot_separator_for_compat()
    {
        // "01.10.description" — dot is compat separator
        Assert.True(StepLoader.TryParseOrder(
            "01.10.description", out var order, out _));
        Assert.Equal(1, order.Group);
        Assert.Equal(10, order.Step);
    }

    [Theory]
    [InlineData("01.20.05.fein", 20, 5, null)]
    [InlineData("01.10.[kunden].desc", 10, null, "[kunden]")]
    [InlineData("01.10_[kunden]_x", 10, null, "[kunden]")]
    public void Parses_dotted_and_mixed_separator_compatibility(
        string stem,
        int expectedStep,
        int? expectedSubStep,
        string? expectedSlug)
    {
        // Coverage-only for the existing dotted/mixed compatibility contract.
        Assert.True(StepLoader.TryParseOrder(stem, out var order, out var warning));
        Assert.Equal(1, order.Group);
        Assert.Equal(expectedStep, order.Step);
        Assert.Equal(expectedSubStep, order.SubStep);
        Assert.Equal(expectedSlug, order.Slug);
        Assert.Null(warning);
    }

    [Fact]
    public void Preserves_underscores_inside_bracketed_slug()
    {
        Assert.True(StepLoader.TryParseOrder(
            "01_10_[kunden_daten]_laden", out var order, out var warning));

        Assert.Equal("[kunden_daten]", order.Slug);
        Assert.Null(warning);
    }

    [Fact]
    public void Slug_at_end_of_stem_is_valid()
    {
        // Coverage-only: end-of-stem is an established valid boundary after the closing bracket.
        Assert.True(StepLoader.TryParseOrder(
            "01_10_[kunden]", out var order, out var warning));

        Assert.Equal("[kunden]", order.Slug);
        Assert.Null(warning);
    }

    [Fact]
    public void Slug_followed_immediately_by_description_warns_and_is_not_promoted()
    {
        Assert.True(StepLoader.TryParseOrder(
            "01_10_[kunden]description", out var order, out var warning));

        Assert.Equal(1, order.Group);
        Assert.Equal(10, order.Step);
        Assert.Null(order.SubStep);
        Assert.Null(order.Slug);
        Assert.NotNull(warning);
        Assert.Equal(LoaderWarningKind.InvalidTag, warning!.Kind);
        Assert.Contains("[kunden]description", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bracket_after_description_is_not_promoted_to_slug()
    {
        Assert.True(StepLoader.TryParseOrder(
            "01_10_description_[kunden]", out var order, out var warning));

        Assert.Equal(1, order.Group);
        Assert.Equal(10, order.Step);
        Assert.Null(order.SubStep);
        Assert.Null(order.Slug);
        Assert.Null(warning);
    }

    // ── Numeric vs. lexicographic sort proof ─────────────────────────────────

    [Fact]
    public void Numeric_sort_not_lexicographic()
    {
        // Lexicographically "09" > "10", but numerically 9 < 10.
        // The sort must produce: 01_09, 01_10, 01_20, 02_01 (numeric order).
        var loader = new StepLoader();

        // Fake file paths — no actual files needed (meta defaults)
        var result = loader.LoadFiles(FakePaths(
            "09_10_last.cs",
            "01_10_first.cs",
            "01_09_second.cs",
            "02_01_third.cs"
        ));

        var groups = result.Steps.Select(s => (s.Order.Group, s.Order.Step)).ToList();
        Assert.Equal([(1, 9), (1, 10), (2, 1), (9, 10)], groups);
    }

    [Fact]
    public void SubStep_sorts_between_parent_steps()
    {
        var loader = new StepLoader();
        var result = loader.LoadFiles(FakePaths(
            "01_20_desc.cs",        // 1,20,null
            "01_20_05_sub.cs",      // 1,20,5
            "01_21_next.cs"         // 1,21,null
        ));

        var orders = result.Steps.Select(s => (s.Order.Step, s.Order.SubStep)).ToList();
        Assert.Equal([(20, (int?)null), (20, (int?)5), (21, (int?)null)], orders);
    }

    [Fact]
    public void SubStep_null_sorts_before_any_substep()
    {
        // GG=1, SS=20, TT=null should precede GG=1, SS=20, TT=5
        var a = new StepOrder(1, 20, null, null);
        var b = new StepOrder(1, 20, 5, null);
        Assert.True(a.CompareTo(b) < 0);
    }

    // ── Invalid / edge cases ─────────────────────────────────────────────────

    [Fact]
    public void Returns_false_for_filename_without_two_numeric_segments()
    {
        Assert.False(StepLoader.TryParseOrder("readme", out _, out _));
        Assert.False(StepLoader.TryParseOrder("01_description_only", out _, out _));
    }

    [Theory]
    [InlineData("+01_10_desc")]
    [InlineData("-01_10_desc")]
    [InlineData(" 01_10_desc")]
    [InlineData("01_ 10_desc")]
    public void Rejects_signed_or_whitespace_padded_numeric_segments(string stem)
    {
        Assert.False(StepLoader.TryParseOrder(stem, out _, out _));
    }

    [Fact]
    public void Returns_true_and_warning_for_unclosed_bracket_with_enough_numerics()
    {
        // Warn-and-load: when the numeric prefix (GG+SS) is valid, the step is loaded
        // with Slug=null even if the bracket is malformed. The slug typo is surfaced
        // as an InvalidTag warning but the step is not dropped.
        var ok = StepLoader.TryParseOrder(
            "01_10_[kunden_stammdaten", out var order, out var warning);

        Assert.True(ok);  // step IS loaded (warn-and-load, not drop)
        Assert.NotNull(warning);
        Assert.Equal(LoaderWarningKind.InvalidTag, warning!.Kind);
        Assert.Null(order.Slug);  // slug not set due to malformed bracket
    }

    [Fact]
    public void Bracket_with_only_open_no_close_produces_InvalidTag_warning()
    {
        StepLoader.TryParseOrder("01_10_[kunden", out _, out var warning);
        Assert.NotNull(warning);
        Assert.Equal(LoaderWarningKind.InvalidTag, warning!.Kind);
        Assert.Contains("[kunden", warning.Message, StringComparison.Ordinal);
    }

    // ── >3 numeric segments: extras silently dropped ─────────────────────────

    [Fact]
    public void Four_numeric_segments_Group_Step_SubStep_and_fourth_silently_dropped()
    {
        // 01_10_20_30_desc → Group=1, Step=10, SubStep=20; 30 is silently dropped
        // (the parser stops collecting after 3 numeric segments).
        Assert.True(StepLoader.TryParseOrder(
            "01_10_20_30_desc", out var order, out var warning));
        Assert.Equal(1, order.Group);
        Assert.Equal(10, order.Step);
        Assert.Equal(20, order.SubStep);
        Assert.Null(warning); // no warning — extras are silently ignored
    }

    // ── StepLoader.Load reads @meta from a real file on disk ─────────────────

    [Fact]
    public void Load_directory_reads_meta_from_file_on_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "01_10_teststep.cs");
            File.WriteAllText(filePath, """
                // @name: My Test Step
                // @haltOnError: false
                var x = 1;
                """);

            var loader = new StepLoader();
            var result = loader.Load(dir);

            Assert.Single(result.Steps);
            var step = result.Steps[0];
            Assert.Equal("My Test Step", step.Meta.Name);
            Assert.False(step.Meta.HaltOnError);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadFiles_unreadable_meta_falls_back_to_default_and_loads_other_steps()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        FileStream? lockHandle = null;
        try
        {
            var unreadablePath = Path.Combine(dir, "01_10_unreadable.cs");
            var readablePath = Path.Combine(dir, "01_20_readable.cs");
            File.WriteAllText(readablePath, "// @name: Readable\nreturn Ok();");

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(unreadablePath, "// @name: Locked\nreturn Ok();");
                lockHandle = new FileStream(
                    unreadablePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            else
            {
                // Reading a directory as text deterministically raises UnauthorizedAccessException on Unix.
                Directory.CreateDirectory(unreadablePath);
            }

            var result = new StepLoader().LoadFiles([unreadablePath, readablePath]);

            Assert.Equal(2, result.Steps.Count);
            Assert.Equal(StepMeta.Default,
                result.Steps.Single(step => step.FileName == "01_10_unreadable.cs").Meta);
            Assert.Equal("Readable",
                result.Steps.Single(step => step.FileName == "01_20_readable.cs").Meta.Name);
        }
        finally
        {
            lockHandle?.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> FakePaths(params string[] fileNames) =>
        fileNames.Select(f => Path.Combine("C:\\fake", f));
}
