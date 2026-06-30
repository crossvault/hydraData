// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// S2.1 — batch resume-from-step via <see cref="PumpEngine.ExecuteFromAsync"/>: only steps with
/// <c>order &gt;= resumeFrom</c> execute (earlier ones are <see cref="StepRunStatus.Skipped"/>), preflight
/// still validates ALL steps, exit codes stay canonical (0/1/2), group-local State applies to the executed
/// subset while Shared stays run-global, haltOnError stops the rest (recorded
/// <see cref="StepRunStatus.NotRunAfterHalt"/>), and the progress sequence / RunId mirror the full run.
/// Runs are serialised in the console-capture collection because StepOutputCapture is process-global.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class PumpEngineResumeTests
{
    private static readonly Guid FixedRunId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PumpEngine NewEngine(
        EngineScaffold scaffold,
        FakeConnectionGateway gateway,
        bool legacyGlobalState = false)
    {
        var options = new PumpOptions(
            scaffold.WorkspaceBase,
            PumpFolderPolicy.Empty,
            LegacyGlobalState: legacyGlobalState);
        return new PumpEngine(options, new FakeGuidProvider(FixedRunId), timeProvider: null, gateway, logger: null);
    }

    /// <summary>An ordering key with no slug/sub-step (the resume target form used by the host).</summary>
    private static StepOrder Order(int group, int step) => new(group, step, SubStep: null, Slug: null);

    // ── Resume executes only steps >= resumeFrom; earlier ones are Skipped ───────

    [Fact]
    public async Task Resume_from_step_runs_only_steps_at_or_after_resume_point()
    {
        // Three steps; each issues a distinct Execute so the fake gateway records which actually ran.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "Execute(\"A\"); return Ok();")
            .AddStep("01_20_b.cs", "Execute(\"B\"); return Ok();")
            .AddStep("01_30_c.cs", "Execute(\"C\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(3, report.Steps.Count);

        // Step 01_10 skipped; 01_20 and 01_30 ran.
        Assert.Equal(StepRunStatus.Skipped, report.Steps[0].Status);
        Assert.False(report.Steps[0].Ran);
        Assert.Equal(StepRunStatus.Ran, report.Steps[1].Status);
        Assert.Equal(StepRunStatus.Ran, report.Steps[2].Status);

        // Only the SQL of the executed steps reached the gateway (side-effect proof).
        var sql = gateway.Slots.SelectMany(s => s.FakeExecutor.Statements).ToList();
        Assert.DoesNotContain("A", sql);
        Assert.Contains("B", sql);
        Assert.Contains("C", sql);
    }

    [Fact]
    public async Task Resume_executed_steps_match_full_run_outcomes()
    {
        // The full run produces Ok for all three; the executed subset of the resume must match those.
        const string a = "Execute(\"A\"); return Ok();";
        const string b = "Execute(\"B\"); return Warn(\"w\");";
        const string c = "Execute(\"C\"); return Ok();";

        using var fullScaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", a).AddStep("01_20_b.cs", b).AddStep("01_30_c.cs", c);
        var fullGateway = new FakeConnectionGateway();
        var fullReport = await NewEngine(fullScaffold, fullGateway).ExecuteAsync(
            fullScaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        using var resumeScaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", a).AddStep("01_20_b.cs", b).AddStep("01_30_c.cs", c);
        var resumeGateway = new FakeConnectionGateway();
        var resumeReport = await NewEngine(resumeScaffold, resumeGateway).ExecuteFromAsync(
            resumeScaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        // Executed steps (index 1 and 2) have identical outcomes to the full run.
        for (int i = 1; i < 3; i++)
        {
            Assert.Equal(fullReport.Steps[i].EffectiveSeverity, resumeReport.Steps[i].EffectiveSeverity);
            Assert.Equal(fullReport.Steps[i].Committed, resumeReport.Steps[i].Committed);
            Assert.Equal(fullReport.Steps[i].Ran, resumeReport.Steps[i].Ran);
        }
    }

    // ── Exit codes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_all_ok_yields_exit_code_0()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "return Ok();")
            .AddStep("01_20_b.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(FixedRunId, report.RunId);
        Assert.Empty(report.PreflightErrors);
    }

    [Fact]
    public async Task Fail_after_resume_yields_exit_code_2()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_skipped.cs", "return Ok();")
            .AddStep("01_20_fail.cs", "Execute(\"x\"); return Fail(\"boom\");");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);
        Assert.Equal(StepRunStatus.Skipped, report.Steps[0].Status);
        Assert.Equal(StepRunStatus.Ran, report.Steps[1].Status);
        Assert.False(report.Steps[1].Committed);
    }

    [Fact]
    public async Task Error_note_after_resume_yields_exit_code_2()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_skipped.cs", "return Ok();")
            .AddStep("01_20_note.cs", "Execute(\"x\"); Note(\"bad\", Severity.Error); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);
        Assert.Equal(Severity.Error, report.Steps[1].EffectiveSeverity);
    }

    [Fact]
    public async Task Compile_error_in_a_skipped_step_aborts_at_preflight_exit_code_1_no_execution()
    {
        // The compile error is in the step BEFORE the resume point; preflight validates ALL steps, so the
        // run still aborts at preflight with exit 1 and NO step runs.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_typo.cs", "Qery(\"select 1\"); return Ok();")  // CS0103 in a would-be-skipped step
            .AddStep("01_20_ok.cs", "Execute(\"x\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ExitCode);
        Assert.NotEmpty(report.PreflightErrors);
        Assert.Empty(report.Steps);    // aborted before any step ran
        Assert.Empty(gateway.Slots);   // no DB slot opened
    }

    [Fact]
    public async Task Pump010_in_a_skipped_step_aborts_at_preflight_exit_code_1()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_unsafe.cs", "// @unsafe: true\nreturn Ok();")  // PUMP010 in a would-be-skipped step
            .AddStep("01_20_ok.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway); // AllowUnsafeDirectAccess defaults to false

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.PreflightErrors, d => d.Code == PumpDiagnostics.UnsafeWithoutGrant);
        Assert.Empty(report.Steps);
        Assert.Empty(gateway.Slots);
    }

    [Fact]
    public async Task Resume_past_all_steps_yields_empty_run_exit_code_0_all_skipped()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "Execute(\"A\"); return Ok();")
            .AddStep("01_20_b.cs", "Execute(\"B\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(9, 99),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(2, report.Steps.Count);
        Assert.All(report.Steps, s => Assert.Equal(StepRunStatus.Skipped, s.Status));
        Assert.All(report.Steps, s => Assert.False(s.Ran));
        Assert.Empty(gateway.Slots); // nothing executed
    }

    // ── Status tagging + ComputeExitCode ignores Skipped/NotRunAfterHalt ─────────

    [Fact]
    public async Task Skipped_steps_carry_Skipped_status_and_null_result()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "return Ok();")
            .AddStep("01_20_b.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        var skipped = report.Steps[0];
        Assert.Equal(StepRunStatus.Skipped, skipped.Status);
        Assert.False(skipped.Ran);
        Assert.Null(skipped.Result);
        Assert.Equal(Severity.Success, skipped.EffectiveSeverity);
        Assert.False(skipped.Committed);
    }

    [Fact]
    public async Task HaltOnError_within_resumed_subset_marks_rest_NotRunAfterHalt_and_exit_2()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_skipped.cs", "return Ok();")
            .AddStep("01_20_fail.cs", "return Fail(\"stop here\");") // @haltOnError defaults to true
            .AddStep("01_30_after.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);
        Assert.Equal(StepRunStatus.Skipped, report.Steps[0].Status);
        Assert.Equal(StepRunStatus.Ran, report.Steps[1].Status);
        Assert.Equal(StepRunStatus.NotRunAfterHalt, report.Steps[2].Status);
        Assert.False(report.Steps[2].Ran);
        Assert.Null(report.Steps[2].Result);
        Assert.Equal(Severity.Success, report.Steps[2].EffectiveSeverity);
    }

    // ── Group-local State for the resumed subset; Shared run-global ──────────────

    [Fact]
    public async Task Resume_group_local_state_starts_fresh_at_first_executed_group()
    {
        // Group 01 writes State; group 02 reads it. Resuming at 02_10 means group 01 never runs, so its
        // State is absent — and group 02's fresh State bag must not see it. Shared is run-global, but it
        // too was only written by the skipped group, so it is also absent here.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_write.cs", "State.Set(\"k\", 1); Shared.Set(\"s\", 9); return Ok();")
            .AddStep("02_10_read.cs",
                "Expect(!State.Has(\"k\"), \"State leaked from skipped group\"); " +
                "Expect(!Shared.Has(\"s\"), \"Shared from a skipped step must be absent\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(2, 10),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task Resume_shared_is_run_global_across_executed_groups()
    {
        // Resume from group 01 step 20: 01_20 writes Shared and State, 02_10 reads both. State is group-
        // local (invisible across groups), Shared is run-global (visible). 01_10 is skipped.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_skipped.cs", "return Ok();")
            .AddStep("01_20_write.cs", "State.Set(\"k\", 1); Shared.Set(\"s\", 9); return Ok();")
            .AddStep("02_10_read.cs",
                "Expect(!State.Has(\"k\"), \"State leaked across groups\"); " +
                "Expect(Shared.Get<int>(\"s\") == 9, \"Shared not visible\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
    }

    // ── Progress ordering + RunId for the resume run ─────────────────────────────

    [Fact]
    public async Task Resume_emits_Discovered_Validated_per_executed_step_then_RunFinished()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_skipped.cs", "return Ok();")
            .AddStep("01_20_run.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var phases = new List<PumpPhase>();
        var progress = new PhaseSink(phases);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            progress, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(FixedRunId, report.RunId);

        // Discovered, Validated, then exactly one StepStarted/StepFinished pair (for 01_20), then RunFinished.
        Assert.Equal(PumpPhase.Discovered, phases[0]);
        Assert.Equal(PumpPhase.Validated, phases[1]);
        Assert.Equal(PumpPhase.RunFinished, phases[^1]);
        Assert.Single(phases, p => p == PumpPhase.StepStarted);
        Assert.Single(phases, p => p == PumpPhase.StepFinished);
    }

    [Fact]
    public async Task Resume_past_all_still_emits_Discovered_Validated_RunFinished_no_step_events()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var phases = new List<PumpPhase>();
        var progress = new PhaseSink(phases);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(9, 99),
            progress, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(3, phases.Count);
        Assert.Equal(PumpPhase.Discovered, phases[0]);
        Assert.Equal(PumpPhase.Validated, phases[1]);
        Assert.Equal(PumpPhase.RunFinished, phases[2]);
        Assert.DoesNotContain(PumpPhase.StepStarted, phases);
    }

    // ── 1. Cancellation in the resume path ──────────────────────────────────────

    [Fact]
    public async Task Resume_cancellation_rolls_back_and_propagates()
    {
        // Skip 01_10; the executed step (01_20) opens a DB slot then loops — cancel after the slot opens so
        // the gateway records a Rollback and RunFinished is emitted before the exception re-throws.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_skipped.cs", "return Ok();")
            .AddStep("01_20_loop.cs",
                "Execute(\"x\"); " +
                "while (true) { Cancellation.ThrowIfCancellationRequested(); } return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);
        using var cts = new CancellationTokenSource();

        var phases = new List<PumpPhase>();
        var progress = new PhaseSink(phases);

        var runTask = engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            progress, ct: cts.Token);

        // Poll until the slot is open (the Execute inside 01_20 records it).
        while (gateway.Slots.Count == 0 && !runTask.IsCompleted)
            await Task.Yield();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        // The in-flight step's slot must have been rolled back.
        Assert.NotEmpty(gateway.Slots);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);

        // RunFinished must have been emitted (the engine cleans up even on cancellation).
        Assert.Contains(PumpPhase.RunFinished, phases);
    }

    // ── 2. PUMP001 in a would-be-skipped step ───────────────────────────────────

    [Fact]
    public async Task Pump001_in_a_skipped_step_aborts_at_preflight_exit_code_1()
    {
        // PUMP001 is a preflight error (nuget directive): even if the step would be skipped at
        // runtime, validate-all catches it and aborts before any execution.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_nuget.cs", "#r \"nuget:Foo\"\nreturn Ok();")  // PUMP001 in a would-be-skipped step
            .AddStep("01_20_ok.cs", "Execute(\"x\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ExitCode);
        Assert.NotEmpty(report.PreflightErrors);
        Assert.Contains(report.PreflightErrors, d => d.Code == PumpDiagnostics.NuGetDirective);
        Assert.Empty(report.Steps);    // aborted before any step ran
        Assert.Empty(gateway.Slots);   // gateway never touched
    }

    // ── 3. Resume INTO the middle of a group ────────────────────────────────────

    [Fact]
    public async Task Resume_into_middle_of_group_gives_fresh_state_but_shares_within_group()
    {
        // 02_10 is skipped; 02_20 writes to State (fresh bag for group 02); 02_30 reads State["x"] and
        // asserts it equals 7. If 02_10 had written State first (it doesn't run), the fresh State for
        // 02_20 must NOT see it. The gateway side-effect proves 02_10 never ran.
        using var scaffold = new EngineScaffold()
            .AddStep("02_10_skipped.cs", "Execute(\"SkippedSideEffect\"); return Ok();")  // must NOT execute
            .AddStep("02_20_write.cs", "State.Set(\"x\", 7); return Ok();")
            .AddStep("02_30_read.cs",
                "Expect(State.Get<int>(\"x\") == 7, \"State from 02_20 not visible in 02_30\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(2, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(StepRunStatus.Skipped, report.Steps[0].Status);
        Assert.Equal(StepRunStatus.Ran, report.Steps[1].Status);
        Assert.Equal(StepRunStatus.Ran, report.Steps[2].Status);

        // 02_10 must not have touched the gateway (its Execute("SkippedSideEffect") never ran).
        var allSql = gateway.Slots.SelectMany(s => s.FakeExecutor.Statements).ToList();
        Assert.DoesNotContain("SkippedSideEffect", allSql);
    }

    // ── 4. Resume BETWEEN orders ─────────────────────────────────────────────────

    [Fact]
    public async Task Resume_between_orders_skips_lower_and_runs_higher()
    {
        // Steps are 01_10 and 01_20; resumeFrom is (Group=1, Step=15) — between them.
        // 01_10 (order 10) < 15 → Skipped; 01_20 (order 20) >= 15 → Ran.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "Execute(\"A\"); return Ok();")
            .AddStep("01_20_b.cs", "Execute(\"B\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 15),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(StepRunStatus.Skipped, report.Steps[0].Status);
        Assert.Equal(StepRunStatus.Ran, report.Steps[1].Status);

        // Only 01_20's SQL reached the gateway.
        var sql = gateway.Slots.SelectMany(s => s.FakeExecutor.Statements).ToList();
        Assert.DoesNotContain("A", sql);
        Assert.Contains("B", sql);
    }

    // ── 5. PUMP020 on resume ─────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_with_no_default_connection_aborts_at_preflight_pump020()
    {
        // Even when resuming (some steps would be skipped), if the connection directory has no Default
        // the engine must abort at preflight (PUMP020) and never execute any step.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_skipped.cs", "return Ok();")
            .AddStep("01_20_ok.cs", "Execute(\"x\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), new EmptyConnectionDirectory(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.PreflightErrors, d => d.Code == PumpDiagnostics.MissingConnection);
        Assert.Empty(report.Steps);
        Assert.Empty(gateway.Slots);
    }

    // ── 6. haltOnError:false within the resumed subset ───────────────────────────

    [Fact]
    public async Task Resume_haltOnError_false_continues_after_error_and_exit_code_2()
    {
        // 01_10 is skipped; 01_20 fails with @haltOnError: false → the run continues to 01_30.
        // Exit code 2 because 01_20 errored; 01_30 still executes (gateway side-effect).
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_skipped.cs", "return Ok();")
            .AddStep("01_20_fail.cs", "// @haltOnError: false\nreturn Fail(\"keep going\");")
            .AddStep("01_30_after.cs", "Execute(\"AfterFail\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);  // error still drives the exit code
        Assert.Equal(StepRunStatus.Skipped, report.Steps[0].Status);
        Assert.Equal(StepRunStatus.Ran, report.Steps[1].Status);
        Assert.Equal(StepRunStatus.Ran, report.Steps[2].Status);  // run continued

        // 01_30 must have reached the gateway.
        var sql = gateway.Slots.SelectMany(s => s.FakeExecutor.Statements).ToList();
        Assert.Contains("AfterFail", sql);
    }

    // ── 7. Output parity (extends Resume_executed_steps_match_full_run_outcomes) ──

    [Fact]
    public async Task Resume_executed_steps_Output_matches_full_run_output()
    {
        // Steps emit console output via Print so the captured Output field is non-empty and comparable.
        const string a = "return Ok();";
        const string b = "Print(\"hello from b\"); return Ok();";
        const string c = "Print(\"hello from c\"); return Ok();";

        using var fullScaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", a).AddStep("01_20_b.cs", b).AddStep("01_30_c.cs", c);
        var fullGateway = new FakeConnectionGateway();
        var fullReport = await NewEngine(fullScaffold, fullGateway).ExecuteAsync(
            fullScaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        using var resumeScaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", a).AddStep("01_20_b.cs", b).AddStep("01_30_c.cs", c);
        var resumeGateway = new FakeConnectionGateway();
        var resumeReport = await NewEngine(resumeScaffold, resumeGateway).ExecuteFromAsync(
            resumeScaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, fullReport.ExitCode);
        Assert.Equal(0, resumeReport.ExitCode);

        // Executed steps (index 1 and 2) must have identical EffectiveSeverity, Committed, Ran AND Output.
        for (int i = 1; i < 3; i++)
        {
            Assert.Equal(fullReport.Steps[i].EffectiveSeverity, resumeReport.Steps[i].EffectiveSeverity);
            Assert.Equal(fullReport.Steps[i].Committed, resumeReport.Steps[i].Committed);
            Assert.Equal(fullReport.Steps[i].Ran, resumeReport.Steps[i].Ran);
            Assert.Equal(fullReport.Steps[i].Output, resumeReport.Steps[i].Output);
        }
    }

    // ── 8. LegacyGlobalState + resume ──────────────────────────────────────────

    [Fact]
    public async Task Resume_with_legacyGlobalState_shares_state_across_groups()
    {
        // With legacyGlobalState=true, State is run-global. 01_10 is skipped (so never writes anything);
        // 01_20 writes State["k"]=42; 02_10 reads it across group boundary (only possible in legacy mode).
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_skipped.cs", "Execute(\"ShouldNotRun\"); return Ok();")
            .AddStep("01_20_write.cs", "State.Set(\"k\", 42); return Ok();")
            .AddStep("02_10_read.cs",
                "Expect(State.Get<int>(\"k\") == 42, \"legacy global state not shared across groups\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway, legacyGlobalState: true);

        var report = await engine.ExecuteFromAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), Order(1, 20),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(StepRunStatus.Skipped, report.Steps[0].Status);
        Assert.Equal(StepRunStatus.Ran, report.Steps[1].Status);
        Assert.Equal(StepRunStatus.Ran, report.Steps[2].Status);

        // Skipped step must not have touched the gateway.
        var sql = gateway.Slots.SelectMany(s => s.FakeExecutor.Statements).ToList();
        Assert.DoesNotContain("ShouldNotRun", sql);
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> that records each phase into a list.</summary>
    private sealed class PhaseSink(List<PumpPhase> target) : IProgress<PumpProgress>
    {
        public void Report(PumpProgress value) => target.Add(value.Phase);
    }
}
