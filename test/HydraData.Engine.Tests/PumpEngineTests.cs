// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T09.3/T09.7 — end-to-end engine orchestration: Discover → Validate → Execute against a fake gateway
/// and a temp workspace, exit codes 0/1/2, RunId-before-validate, group-local State vs run-global Shared,
/// LegacyGlobalState, per-step timeout, haltOnError, and the no-execution-on-preflight guarantee.
/// Runs are serialised in the console-capture collection because StepOutputCapture is process-global.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class PumpEngineTests
{
    private static readonly Guid FixedRunId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static PumpEngine NewEngine(
        EngineScaffold scaffold,
        FakeConnectionGateway gateway,
        TimeSpan? timeout = null,
        bool legacyGlobalState = false,
        TimeProvider? time = null,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var options = new PumpOptions(
            scaffold.WorkspaceBase,
            PumpFolderPolicy.Empty,
            StepTimeout: timeout,
            LegacyGlobalState: legacyGlobalState);
        return new PumpEngine(options, new FakeGuidProvider(FixedRunId), time, gateway, logger);
    }

    // ── Exit code 0: all Ok/Warn ────────────────────────────────────────────────

    [Fact]
    public async Task All_ok_steps_yield_exit_code_0()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "Execute(\"insert\"); return Ok();")
            .AddStep("01_20_b.cs", "return Warn(\"heads up\");");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(FixedRunId, report.RunId);
        Assert.Empty(report.PreflightErrors);
        Assert.Equal(2, report.Steps.Count);
        Assert.All(report.Steps, s => Assert.True(s.Ran));
        Assert.All(report.Steps, s => Assert.True(s.Committed));
    }

    // ── Exit code 1: preflight (typo) — no execution ────────────────────────────

    [Fact]
    public async Task Typo_aborts_at_preflight_with_exit_code_1_and_no_execution()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_ok.cs", "Execute(\"x\"); return Ok();")
            .AddStep("01_20_typo.cs", "Qery(\"select 1\"); return Ok();"); // CS0103: Qery does not exist
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ExitCode);
        Assert.NotEmpty(report.PreflightErrors);
        Assert.Empty(report.Steps); // aborted before any step ran
        Assert.Empty(gateway.Slots); // no DB slot was opened
    }

    [Fact]
    public async Task Missing_connection_is_a_preflight_error_exit_code_1()
    {
        using var scaffold = new EngineScaffold().AddStep("01_10_a.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), new EmptyConnectionDirectory(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.PreflightErrors, d => d.Code == PumpDiagnostics.MissingConnection);
        Assert.Empty(report.Steps);
    }

    [Fact]
    public async Task Pump010_unsafe_without_grant_is_preflight_exit_code_1()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_unsafe.cs", "// @unsafe: true\nreturn Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway); // AllowUnsafeDirectAccess defaults to false

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.PreflightErrors, d => d.Code == PumpDiagnostics.UnsafeWithoutGrant);
        Assert.Empty(gateway.Slots);
    }

    // ── Exit code 2: runtime failures ───────────────────────────────────────────

    [Fact]
    public async Task Fail_verdict_yields_exit_code_2_and_rollback()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_fail.cs", "Execute(\"x\"); return Fail(\"boom\");");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);
        Assert.Single(report.Steps);
        Assert.False(report.Steps[0].Committed);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);
    }

    [Fact]
    public async Task Error_note_over_ok_return_yields_exit_code_2()
    {
        // Step returns Ok but records an Error note: effective severity is Error -> rollback -> exit 2.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_note.cs", "Execute(\"x\"); Note(\"bad\", Severity.Error); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);
        Assert.Equal(Severity.Error, report.Steps[0].EffectiveSeverity);
        Assert.False(report.Steps[0].Committed);
    }

    [Fact]
    public async Task Commit_failure_demotes_to_error_and_yields_exit_code_2()
    {
        // A step returns Ok (would normally commit), but the gateway's slot throws from Commit(). The
        // StepRunner demotes the outcome to Error (data did not land) and the engine surfaces exit 2.
        // Proves the StepRunner→engine demotion wiring end-to-end through ExecuteAsync.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_commit.cs", "Execute(\"x\"); return Ok();");
        var gateway = new FakeConnectionGateway { NextSlotThrowsOnCommit = true };
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);
        Assert.Single(report.Steps);
        Assert.Equal(Severity.Error, report.Steps[0].EffectiveSeverity);
        Assert.False(report.Steps[0].Committed);
        // The slot was opened and its (throwing) Commit was attempted exactly once; no rollback follows a
        // commit attempt (commit failure is not retried as a rollback).
        Assert.Equal(1, gateway.Slots[0].Commits);
    }

    [Fact]
    public async Task Crash_yields_exit_code_2()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_crash.cs", "throw new InvalidOperationException(\"kaboom\");");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);
        Assert.Equal(Severity.Error, report.Steps[0].EffectiveSeverity);
    }

    [Fact]
    public async Task Cancellation_rolls_back_and_propagates()
    {
        // The step opens a DB slot (Execute) THEN loops on the cancellation token — deterministic:
        // cancel only after the slot is open so the gateway records a Rollback (item 3).
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_loop.cs",
                "Execute(\"x\"); " +
                "while (true) { Cancellation.ThrowIfCancellationRequested(); } return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);
        using var cts = new CancellationTokenSource();

        // Start the run, then cancel once the slot has been opened (the Execute call records it).
        var runTask = engine.ExecuteAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), ct: cts.Token);

        // Poll until the gateway has at least one slot (the Execute inside the script opened it).
        while (gateway.Slots.Count == 0 && !runTask.IsCompleted)
            await Task.Yield();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        // The slot that was opened must have been rolled back (item 3: rollback on cancellation).
        Assert.NotEmpty(gateway.Slots);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);
    }

    // ── B1: script unreadable before the engine's single up-front read ────────────

    [Fact]
    public async Task Script_deleted_before_read_yields_exit_code_2_not_thrown_exception()
    {
        // Arrange: two steps. The engine reads every script exactly once, up front, after it reports
        // Discovered and before validation. Deleting the second file the moment Discovered fires makes that
        // single read fail; the failure must be turned into a recorded Ran/Error step (exit 2), not thrown.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_ok.cs", "return Ok();")
            .AddStep("01_20_gone.cs", "return Ok();");
        var ctx = scaffold.Discover();
        var secondFilePath = ctx.Steps[1].FilePath;

        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var phases = new List<PumpPhase>();
        // Delete the second script the moment the engine reports Discovered (just before the up-front read).
        var deleteTriggered = false;
        var progress = new DeleteOnDiscoveredProgress(phases, secondFilePath, () => deleteTriggered = true);

        // Act: must NOT throw — the IOException must be caught and turned into exit 2.
        var report = await engine.ExecuteAsync(
            ctx, EngineScaffold.Extern(), EngineScaffold.Connections(), progress,
            ct: TestContext.Current.CancellationToken);

        Assert.True(deleteTriggered, "The delete was not triggered — check progress ordering.");

        // Exit code 2 (runtime error), RunFinished emitted.
        Assert.Equal(2, report.ExitCode);
        Assert.Contains(PumpPhase.RunFinished, phases);
        Assert.Equal(2, report.Steps.Count);
        Assert.True(report.Steps[0].Ran);   // first step ran OK
        Assert.True(report.Steps[1].Ran);   // second step attempted to run (Ran=true, but errored)
        Assert.Equal(Severity.Error, report.Steps[1].EffectiveSeverity);
    }

    /// <summary>
    /// Progress sink that deletes a file the first time it sees <see cref="PumpPhase.Discovered"/>,
    /// simulating a script being removed just before the engine's single up-front read (B1).
    /// </summary>
    private sealed class DeleteOnDiscoveredProgress(
        List<PumpPhase> phases,
        string fileToDelete,
        Action onDeleted) : IProgress<PumpProgress>
    {
        private bool _deleted;

        public void Report(PumpProgress value)
        {
            phases.Add(value.Phase);
            if (!_deleted && value.Phase == PumpPhase.Discovered && File.Exists(fileToDelete))
            {
                File.Delete(fileToDelete);
                _deleted = true;
                onDeleted();
            }
        }
    }

    // ── Group-local State vs run-global Shared (closes T04.6) ────────────────────

    [Fact]
    public async Task State_is_group_local_and_shared_is_run_global()
    {
        // Group 01 writes to State and Shared; group 02 reads both. State must be invisible across groups,
        // Shared must be visible. Each step asserts via Expect so a wrong outcome surfaces as exit 2.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_write.cs", "State.Set(\"k\", 1); Shared.Set(\"s\", 9); return Ok();")
            .AddStep("02_10_read.cs",
                "Expect(!State.Has(\"k\"), \"State leaked across groups\"); " +
                "Expect(Shared.Get<int>(\"s\") == 9, \"Shared not visible\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task State_is_shared_within_a_group()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_write.cs", "State.Set(\"k\", 42); return Ok();")
            .AddStep("01_20_read.cs", "Expect(State.Get<int>(\"k\") == 42, \"State lost within group\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task LegacyGlobalState_makes_state_run_global()
    {
        // With LegacyGlobalState, State written in group 01 is visible in group 02.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_write.cs", "State.Set(\"k\", 7); return Ok();")
            .AddStep("02_10_read.cs", "Expect(State.Get<int>(\"k\") == 7, \"legacy global state not shared\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway, legacyGlobalState: true);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
    }

    // ── haltOnError ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task HaltOnError_default_stops_run_and_marks_remaining_not_run()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_fail.cs", "return Fail(\"stop here\");")  // @haltOnError defaults to true
            .AddStep("01_20_after.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);
        Assert.Equal(2, report.Steps.Count);
        Assert.True(report.Steps[0].Ran);
        Assert.False(report.Steps[1].Ran); // recorded as not-run
        Assert.Null(report.Steps[1].Result);
    }

    [Fact]
    public async Task HaltOnError_false_continues_after_error()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_fail.cs", "// @haltOnError: false\nreturn Fail(\"keep going\");")
            .AddStep("01_20_after.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode); // error still drives the exit code
        Assert.True(report.Steps[0].Ran);
        Assert.True(report.Steps[1].Ran); // the run continued
    }

    // ── Per-step timeout (deterministic via ManualTimeProvider) ──────────────────

    [Fact]
    public async Task Step_timeout_is_enforced_and_yields_exit_code_2()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_slow.cs",
                "await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, Cancellation); return Ok();");
        var gateway = new FakeConnectionGateway();
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = NewEngine(scaffold, gateway, timeout: TimeSpan.FromSeconds(5), time: time);

        var runTask = engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        // The infinite delay is observing the linked (timeout) token; advancing the manual clock fires it.
        while (!runTask.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Yield();
        }

        var report = await runTask;
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(Severity.Error, report.Steps[0].EffectiveSeverity);
    }

    // ── No second result type / RunId before validate ───────────────────────────

    [Fact]
    public async Task RunId_is_generated_before_validate_so_it_is_present_even_on_preflight_failure()
    {
        using var scaffold = new EngineScaffold().AddStep("01_10_typo.cs", "Qery(); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(FixedRunId, report.RunId); // RunId present despite the preflight abort
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void Validate_standalone_returns_report_without_executing()
    {
        using var scaffold = new EngineScaffold().AddStep("01_10_ok.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = engine.Validate(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections());

        Assert.True(report.IsValid);
        Assert.Empty(gateway.Slots);
    }

    // ── Item 4: not-run record has Severity.Success (not Error) ─────────────────

    [Fact]
    public async Task HaltOnError_not_run_record_has_Success_severity_and_null_result()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_fail.cs", "return Fail(\"stop here\");")
            .AddStep("01_20_after.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode); // exit code still driven by the step that ran
        var notRun = report.Steps[1];
        Assert.False(notRun.Ran);
        Assert.Null(notRun.Result);
        // Not-run steps carry Success — they have no severity of their own (ComputeExitCode gates on Ran).
        Assert.Equal(Severity.Success, notRun.EffectiveSeverity);
    }

    // ── Item 6: preflight-abort progress sequence ────────────────────────────────

    [Fact]
    public async Task Preflight_failure_yields_Discovered_Validated_RunFinished_with_no_step_events()
    {
        // A CS typo causes a preflight abort; progress must emit exactly Discovered, Validated, RunFinished
        // and no StepStarted or StepFinished events.
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_typo.cs", "Qery(\"select 1\"); return Ok();"); // CS0103
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);

        var phases = new List<PumpPhase>();
        var progress = new PhaseSink(phases);

        var report = await engine.ExecuteAsync(
            scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(), progress,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ExitCode);
        Assert.Equal(3, phases.Count);
        Assert.Equal(PumpPhase.Discovered, phases[0]);
        Assert.Equal(PumpPhase.Validated, phases[1]);
        Assert.Equal(PumpPhase.RunFinished, phases[2]);
        Assert.DoesNotContain(PumpPhase.StepStarted, phases);
        Assert.DoesNotContain(PumpPhase.StepFinished, phases);
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> that records each phase into a list.</summary>
    private sealed class PhaseSink(List<PumpPhase> target) : IProgress<PumpProgress>
    {
        public void Report(PumpProgress value) => target.Add(value.Phase);
    }
}
