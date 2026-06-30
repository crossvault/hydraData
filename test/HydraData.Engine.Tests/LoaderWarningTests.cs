// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// Tests for the StepLoader warning catalog (T04.5).
/// Each test triggers exactly one warning kind and verifies no other warnings are emitted.
/// </summary>
public class LoaderWarningTests
{
    // ── DuplicateOrder ───────────────────────────────────────────────────────

    [Fact]
    public void DuplicateOrder_emits_exactly_one_warning_of_correct_kind()
    {
        var loader = new StepLoader();

        // Two files with the same GG=01, SS=10 → duplicate
        var result = loader.LoadFiles(FakePaths(
            "01_10_first.cs",
            "01_10_second.cs"
        ));

        var warnings = result.Warnings;
        Assert.Single(warnings);
        Assert.Equal(LoaderWarningKind.DuplicateOrder, warnings[0].Kind);
        Assert.Contains("01_10", warnings[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateOrder_not_emitted_for_distinct_orders()
    {
        var loader = new StepLoader();
        var result = loader.LoadFiles(FakePaths(
            "01_10_first.cs",
            "01_20_second.cs"
        ));

        Assert.DoesNotContain(result.Warnings, w => w.Kind == LoaderWarningKind.DuplicateOrder);
    }

    [Fact]
    public void DuplicateOrder_three_files_same_order_emits_two_warnings()
    {
        // Three files at the same GG=01, SS=10: sorted as [a, b, c].
        // Adjacent pair (a,b) → one warning; adjacent pair (b,c) → second warning.
        var loader = new StepLoader();
        var result = loader.LoadFiles(FakePaths(
            "01_10_a.cs",
            "01_10_b.cs",
            "01_10_c.cs"
        ));

        var dupeWarnings = result.Warnings.Where(w => w.Kind == LoaderWarningKind.DuplicateOrder).ToList();
        Assert.Equal(2, dupeWarnings.Count);
    }

    // ── InvalidTag ───────────────────────────────────────────────────────────

    [Fact]
    public void InvalidTag_emitted_for_unclosed_bracket()
    {
        var loader = new StepLoader();

        var result = loader.LoadFiles(FakePaths(
            "01_10_[kunden_stammdaten.cs"  // opening [ without closing ]
        ));

        // The step IS loaded (warn-and-load, not drop): slug is null because the bracket was malformed.
        // Only an InvalidTag warning is emitted; the step appears in the result.
        var warnings = result.Warnings;
        Assert.Contains(warnings, w => w.Kind == LoaderWarningKind.InvalidTag);

        // No other kinds
        var otherKinds = warnings.Where(w => w.Kind != LoaderWarningKind.InvalidTag).ToList();
        Assert.Empty(otherKinds);

        // Step is loaded, with null slug (slug dropped due to malformed bracket).
        Assert.Single(result.Steps);
        Assert.Null(result.Steps[0].Order.Slug);
    }

    [Fact]
    public void InvalidTag_not_emitted_for_valid_bracket()
    {
        var loader = new StepLoader();
        var result = loader.LoadFiles(FakePaths("01_10_[kunden]_stammdaten.cs"));

        Assert.DoesNotContain(result.Warnings, w => w.Kind == LoaderWarningKind.InvalidTag);
    }

    // ── NonContiguousGroup ───────────────────────────────────────────────────

    [Fact]
    public void NonContiguousGroup_emitted_in_legacy_slug_mode_when_same_slug_interrupted()
    {
        // In LegacyGroupBySlug mode, group key = slug.
        // Sorted order (by GG,SS): (1,10)[kunden], (2,10)[auftraege], (3,20)[kunden]
        // → [kunden] slug appears at positions 0 and 2 with [auftraege] between them → non-contiguous.
        var loader = new StepLoader(new LoaderOptions { LegacyGroupBySlug = true });

        var result = loader.LoadFiles(FakePaths(
            "01_10_[kunden]_a.cs",
            "02_10_[auftraege]_b.cs",
            "03_20_[kunden]_c.cs"
        ));

        var warnings = result.Warnings;
        Assert.Contains(warnings, w => w.Kind == LoaderWarningKind.NonContiguousGroup);

        // No other kinds expected (slug inconsistency is suppressed in legacy mode,
        // no duplicates or invalid tags here)
        var otherKinds = warnings.Where(w => w.Kind != LoaderWarningKind.NonContiguousGroup).ToList();
        Assert.Empty(otherKinds);
    }

    [Fact]
    public void NonContiguousGroup_not_emitted_for_contiguous_groups()
    {
        var loader = new StepLoader();
        var result = loader.LoadFiles(FakePaths(
            "01_10_a.cs",
            "01_20_b.cs",
            "02_10_c.cs",
            "02_20_d.cs"
        ));

        Assert.DoesNotContain(result.Warnings, w => w.Kind == LoaderWarningKind.NonContiguousGroup);
    }

    // ── SlugInconsistency ────────────────────────────────────────────────────

    [Fact]
    public void SlugInconsistency_emitted_when_same_GG_has_different_slugs()
    {
        var loader = new StepLoader();

        // Group 01: first file has [kunden], second has [accounts] — inconsistent
        var result = loader.LoadFiles(FakePaths(
            "01_10_[kunden]_step.cs",
            "01_20_[accounts]_step.cs"
        ));

        var warnings = result.Warnings;
        Assert.Contains(warnings, w => w.Kind == LoaderWarningKind.SlugInconsistency);

        // No other kinds
        var otherKinds = warnings.Where(w => w.Kind != LoaderWarningKind.SlugInconsistency).ToList();
        Assert.Empty(otherKinds);
    }

    [Fact]
    public void SlugInconsistency_not_emitted_when_same_GG_same_slug()
    {
        var loader = new StepLoader();
        var result = loader.LoadFiles(FakePaths(
            "01_10_[kunden]_step.cs",
            "01_20_[kunden]_other.cs"
        ));

        Assert.DoesNotContain(result.Warnings, w => w.Kind == LoaderWarningKind.SlugInconsistency);
    }

    [Fact]
    public void SlugInconsistency_not_emitted_when_all_have_no_slug()
    {
        var loader = new StepLoader();
        var result = loader.LoadFiles(FakePaths(
            "01_10_step.cs",
            "01_20_other.cs"
        ));

        Assert.DoesNotContain(result.Warnings, w => w.Kind == LoaderWarningKind.SlugInconsistency);
    }

    [Fact]
    public void SlugInconsistency_emitted_when_one_file_has_slug_and_other_does_not()
    {
        var loader = new StepLoader();
        // null vs "[kunden]" → inconsistency
        var result = loader.LoadFiles(FakePaths(
            "01_10_[kunden]_step.cs",
            "01_20_nostep.cs"
        ));

        Assert.Contains(result.Warnings, w => w.Kind == LoaderWarningKind.SlugInconsistency);
    }

    // ── Clean directory produces no warnings ─────────────────────────────────

    [Fact]
    public void Clean_directory_produces_no_warnings()
    {
        var loader = new StepLoader();
        var result = loader.LoadFiles(FakePaths(
            "01_10_[kunden]_stammdaten_lesen.cs",
            "01_20_[kunden]_validieren.cs",
            "01_25_dublettencheck.cs",
            "01_20_05_feinschritt.cs",
            "02_10_[auftraege]_lesen.cs"
        ));

        // Note: 01_25 has no slug but so does 01_20_05 — both are group 01.
        // 01_10 and 01_20 have [kunden] but 01_25 and 01_20_05 have null → that IS a slug inconsistency.
        // This is intentional per the spec: the test above just verifies clean = no slug mismatch.
        // For this test use a truly clean set.
        var cleanResult = new StepLoader().LoadFiles(FakePaths(
            "01_10_[kunden]_stammdaten.cs",
            "01_20_[kunden]_validieren.cs",
            "02_10_[auftraege]_lesen.cs"
        ));
        Assert.Empty(cleanResult.Warnings);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> FakePaths(params string[] fileNames) =>
        fileNames.Select(f => Path.Combine("C:\\fake", f));
}
