// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T09.1 — <see cref="DiscoveryService"/> multi-folder merge, cross-folder group
/// distribution, global sort, duplicate-order and non-contiguous-group warnings,
/// and <see cref="ScriptContext"/> immutability.
/// </summary>
public sealed class DiscoveryServiceTests : IDisposable
{
    // Temporary directories created per test and cleaned up in Dispose.
    private readonly List<string> _tempDirs = [];

    // ── helpers ───────────────────────────────────────────────────────────────

    private string MakeDir(params (string FileName, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hydradata-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(dir, name), content);
        return dir;
    }

    private static DiscoveryService Svc() => new();

    // ── multi-folder merge ────────────────────────────────────────────────────

    [Fact]
    public void Two_folders_steps_are_merged()
    {
        var dir1 = MakeDir(("01_10_step_a.cs", "return Ok();"));
        var dir2 = MakeDir(("02_10_step_b.cs", "return Ok();"));

        var ctx = Svc().Discover([dir1, dir2]);

        Assert.Equal(2, ctx.Steps.Count);
        Assert.Equal("01_10_step_a.cs", ctx.Steps[0].FileName);
        Assert.Equal("02_10_step_b.cs", ctx.Steps[1].FileName);
    }

    [Fact]
    public void Steps_from_multiple_folders_are_globally_sorted()
    {
        // dir1 has group 02 steps, dir2 has group 01 steps — global sort must interleave correctly.
        var dir1 = MakeDir(
            ("02_10_step.cs", "return Ok();"),
            ("02_20_step.cs", "return Ok();"));
        var dir2 = MakeDir(
            ("01_10_step.cs", "return Ok();"),
            ("01_20_step.cs", "return Ok();"));

        var ctx = Svc().Discover([dir1, dir2]);

        var orders = ctx.Steps.Select(s => (s.Order.Group, s.Order.Step)).ToList();
        Assert.Equal([(1, 10), (1, 20), (2, 10), (2, 20)], orders);
    }

    [Fact]
    public void Groups_list_is_sorted_and_deduplicated()
    {
        var dir1 = MakeDir(
            ("02_10_step.cs", "return Ok();"),
            ("02_20_step.cs", "return Ok();"));
        var dir2 = MakeDir(
            ("01_10_step.cs", "return Ok();"),
            ("02_30_step.cs", "return Ok();")); // group 02 also appears in dir2

        var ctx = Svc().Discover([dir1, dir2]);

        Assert.Equal([1, 2], ctx.Groups);
    }

    // ── group spanning two folders ────────────────────────────────────────────

    [Fact]
    public void Group_spanning_two_folders_is_allowed_and_steps_are_merged()
    {
        // Group 01 has steps in both folders — they must be merged and globally sorted.
        var dir1 = MakeDir(("01_10_first.cs", "return Ok();"));
        var dir2 = MakeDir(("01_20_second.cs", "return Ok();"));

        var ctx = Svc().Discover([dir1, dir2]);

        Assert.Equal(2, ctx.Steps.Count);
        Assert.Equal(1, ctx.Groups.Single());
        Assert.Equal([(1, 10), (1, 20)],
            ctx.Steps.Select(s => (s.Order.Group, s.Order.Step)).ToList());
    }

    // ── duplicate order across folders ───────────────────────────────────────

    [Fact]
    public void Duplicate_full_order_across_folders_produces_DuplicateOrder_warning()
    {
        // Both folders have 01_10 — that is a duplicate order collision.
        var dir1 = MakeDir(("01_10_step_a.cs", "return Ok();"));
        var dir2 = MakeDir(("01_10_step_b.cs", "return Ok();"));

        var ctx = Svc().Discover([dir1, dir2]);

        Assert.Contains(ctx.Warnings,
            w => w.Kind == LoaderWarningKind.DuplicateOrder);
    }

    [Fact]
    public void Duplicate_order_warning_message_names_both_files()
    {
        var dir1 = MakeDir(("01_10_step_a.cs", "return Ok();"));
        var dir2 = MakeDir(("01_10_step_b.cs", "return Ok();"));

        var ctx = Svc().Discover([dir1, dir2]);

        var dup = ctx.Warnings.Single(w => w.Kind == LoaderWarningKind.DuplicateOrder);
        Assert.Contains("01_10_step_a.cs", dup.Message, StringComparison.Ordinal);
        Assert.Contains("01_10_step_b.cs", dup.Message, StringComparison.Ordinal);
    }

    // ── non-contiguous group across folders ──────────────────────────────────

    [Fact]
    public void Non_contiguous_group_across_folders_produces_warning_in_legacy_slug_mode()
    {
        // In LegacyGroupBySlug mode the group key is the slug label, not the numeric GG.
        // Two files share slug "[kunden]" but a file with slug "[auftraege]" (different GG) sits
        // between them in the global sort order, producing a NonContiguousGroup warning.
        //
        //   01_10_[kunden]_a.cs   → GG=1, slug=[kunden]
        //   01_20_[auftraege]_b.cs → GG=1, slug=[auftraege]  ← different slug/group key
        //   01_30_[kunden]_c.cs   → GG=1, slug=[kunden]      ← [kunden] reappears → non-contiguous
        //
        // A single directory is sufficient here because the non-contiguity is in the slug keys.
        var dir = MakeDir(
            ("01_10_[kunden]_a.cs", "return Ok();"),
            ("01_20_[auftraege]_b.cs", "return Ok();"),
            ("01_30_[kunden]_c.cs", "return Ok();"));

        var svc = new DiscoveryService(new LoaderOptions { LegacyGroupBySlug = true });
        var ctx = svc.Discover([dir]);

        Assert.Contains(ctx.Warnings,
            w => w.Kind == LoaderWarningKind.NonContiguousGroup);
    }

    [Fact]
    public void New_schema_cross_folder_merge_no_NonContiguous_warning()
    {
        // In new schema (GG-based), global numeric sort always keeps groups contiguous —
        // this warning cannot fire from cross-folder merges in new schema.
        var dir1 = MakeDir(
            ("01_10_step.cs", "return Ok();"),
            ("02_10_step.cs", "return Ok();"));
        var dir2 = MakeDir(("01_20_step.cs", "return Ok();"));

        // After global sort: 01_10, 01_20, 02_10 — group 01 is contiguous.
        var ctx = Svc().Discover([dir1, dir2]);

        Assert.DoesNotContain(ctx.Warnings,
            w => w.Kind == LoaderWarningKind.NonContiguousGroup);
    }

    [Fact]
    public void Contiguous_group_across_folders_no_NonContiguous_warning()
    {
        // Group 01 in dir1, group 02 in dir2 — perfectly contiguous after global sort.
        var dir1 = MakeDir(("01_10_step.cs", "return Ok();"));
        var dir2 = MakeDir(("02_10_step.cs", "return Ok();"));

        var ctx = Svc().Discover([dir1, dir2]);

        Assert.DoesNotContain(ctx.Warnings,
            w => w.Kind == LoaderWarningKind.NonContiguousGroup);
    }

    // ── ScriptContext immutability ────────────────────────────────────────────

    [Fact]
    public void ScriptContext_Groups_is_IReadOnlyList()
    {
        var dir = MakeDir(("01_10_step.cs", "return Ok();"));
        var ctx = Svc().Discover([dir]);

        // The declared type of Groups must be IReadOnlyList<int> — no Add/Remove API.
        Assert.IsAssignableFrom<IReadOnlyList<int>>(ctx.Groups);
    }

    [Fact]
    public void ScriptContext_Steps_is_IReadOnlyList()
    {
        var dir = MakeDir(("01_10_step.cs", "return Ok();"));
        var ctx = Svc().Discover([dir]);

        Assert.IsAssignableFrom<IReadOnlyList<StepDescriptor>>(ctx.Steps);
    }

    [Fact]
    public void ScriptContext_Warnings_is_IReadOnlyList()
    {
        var dir = MakeDir(("01_10_step.cs", "return Ok();"));
        var ctx = Svc().Discover([dir]);

        Assert.IsAssignableFrom<IReadOnlyList<LoaderWarning>>(ctx.Warnings);
    }

    [Fact]
    public void ScriptContext_has_no_mutable_setters()
    {
        // Compile-time guarantee: Groups, Steps, Warnings are get-only on ScriptContext.
        // Verified by reflection: no public setters exist.
        var type = typeof(ScriptContext);
        foreach (var prop in new[] { "Groups", "Steps", "Warnings" })
        {
            var pi = type.GetProperty(prop);
            Assert.NotNull(pi);
            var setter = pi!.GetSetMethod(nonPublic: false);
            Assert.True(setter is null,
                $"Property {prop} should not have a public setter.");
        }
    }

    [Fact]
    public void ScriptContext_Steps_is_not_castable_to_mutable_List()
    {
        // Steps must be a true read-only wrapper, not a raw List<T> that callers can cast and mutate.
        var dir = MakeDir(("01_10_step.cs", "return Ok();"));
        var ctx = Svc().Discover([dir]);

        Assert.IsNotType<List<StepDescriptor>>(ctx.Steps);
    }

    [Fact]
    public void ScriptContext_Warnings_is_not_castable_to_mutable_List()
    {
        // Warnings must be a true read-only wrapper, not a raw List<T> that callers can cast and mutate.
        // Trigger a DuplicateOrder warning to ensure Warnings is non-empty.
        var dir1 = MakeDir(("01_10_step_a.cs", "return Ok();"));
        var dir2 = MakeDir(("01_10_step_b.cs", "return Ok();"));
        var ctx = Svc().Discover([dir1, dir2]);

        Assert.IsNotType<List<LoaderWarning>>(ctx.Warnings);
    }

    // ── nonexistent folder → DirectoryNotFoundException ───────────────────────

    [Fact]
    public void Nonexistent_folder_throws_DirectoryNotFoundException_with_path()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "hydradata-tests", "no-such-folder-" + Path.GetRandomFileName());

        var ex = Assert.Throws<DirectoryNotFoundException>(() =>
            Svc().Discover([missingPath]));

        // The exception message must name the missing folder path.
        Assert.Contains(missingPath, ex.Message, StringComparison.Ordinal);
    }

    // ── empty folder list ─────────────────────────────────────────────────────

    [Fact]
    public void Empty_folder_list_returns_empty_context()
    {
        var ctx = Svc().Discover([]);

        Assert.Empty(ctx.Steps);
        Assert.Empty(ctx.Groups);
        Assert.Empty(ctx.Warnings);
    }

    // ── single folder (regression / equivalence) ─────────────────────────────

    [Fact]
    public void Single_folder_behaves_like_StepLoader()
    {
        var dir = MakeDir(
            ("01_10_step.cs", "return Ok();"),
            ("01_20_step.cs", "return Ok();"),
            ("02_10_step.cs", "return Ok();"));

        var ctx = Svc().Discover([dir]);

        Assert.Equal(3, ctx.Steps.Count);
        Assert.Equal([1, 2], ctx.Groups);
        Assert.Empty(ctx.Warnings);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
