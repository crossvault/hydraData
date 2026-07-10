// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// S1.1 — covers <see cref="PumpSession"/>: persistent per-group <c>State</c> across re-runs, stable
/// RunId/Workspace for the session lifetime, per-call re-read + re-validate (edits picked up, PUMP gates),
/// transaction-per-step against the fake gateway, group isolation vs. <c>LegacyGlobalState</c>, and the
/// unknown-order path. Runs in the console-capture collection because StepOutputCapture is process-global.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class PumpSessionTests
{
    private static readonly Guid FixedRunId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Builds a session over the scaffold's discovered context, wired with the fake gateway and a fixed RunId.</summary>
    private static PumpSession NewSession(
        EngineScaffold scaffold,
        FakeConnectionGateway gateway,
        bool legacyGlobalState = false,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null)
    {
        var options = new PumpOptions(
            scaffold.WorkspaceBase,
            PumpFolderPolicy.Empty,
            StepTimeout: timeout,
            LegacyGlobalState: legacyGlobalState);
        return new PumpSession(
            scaffold.Discover(),
            EngineScaffold.Extern(),
            EngineScaffold.Connections(),
            options,
            new FakeGuidProvider(FixedRunId),
            timeProvider ?? TimeProvider.System,
            gateway,
            logger: null);
    }

    private static StepOrder Order(int group, int step) => new(group, step, null, null);

    // ── Persistent State across re-run (the headline) ─────────────────────────

    [Fact]
    public async Task Re_running_step2_keeps_step1_state_without_re_running_step1()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        // Step 1 writes an expensive lookup into the group State AND touches the DB (Execute), so
        // the gateway records one slot per execution — used below to prove it ran exactly once.
        // Step 2 reads the State and also touches the DB, so each run opens one slot.
        scaffold.AddStep("01_10_lookup.cs", "Execute(\"probe\"); State.Set(\"lookup\", 42); return Ok();");
        scaffold.AddStep("01_20_read.cs",
            "Execute(\"probe\"); var v = State.Get<int>(\"lookup\"); Print(v.ToString()); return Ok(\"v=\" + v);");
        using var session = NewSession(scaffold, gateway);

        // step1 then step2 then RE-RUN step2 — step1 is NOT run again.
        var r1 = await session.RunStepAsync(Order(1, 10), ct: Ct);
        var r2a = await session.RunStepAsync(Order(1, 20), ct: Ct);
        var r2b = await session.RunStepAsync(Order(1, 20), ct: Ct);

        Assert.True(r1.Validated);
        Assert.Equal("v=42", r2a.Result!.Result!.Message);
        // The re-run still sees step1's State even though step1 was not executed again.
        Assert.Equal("v=42", r2b.Result!.Result!.Message);
        // Step1 ran exactly once (one slot opened for it) and step2 ran twice — proving step1 was
        // NOT silently re-executed during either of the step2 runs.
        // Slot[0] = step1 (1st run), Slot[1] = step2 (1st run), Slot[2] = step2 (re-run): 3 total.
        Assert.Equal(3, gateway.Slots.Count);
    }

    [Fact]
    public async Task Shared_state_persists_across_groups_and_calls()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_set.cs", "Shared.Set(\"k\", \"vee\"); return Ok();");
        scaffold.AddStep("02_10_get.cs", "return Ok(Shared.Get<string>(\"k\"));");
        using var session = NewSession(scaffold, gateway);

        await session.RunStepAsync(Order(1, 10), ct: Ct);
        var r = await session.RunStepAsync(Order(2, 10), ct: Ct);

        Assert.Equal("vee", r.Result!.Result!.Message);
    }

    // ── Edited step is re-read + re-validated ─────────────────────────────────

    [Fact]
    public async Task Edited_step_is_re_read_then_typo_fails_validation_then_fix_runs()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_edit.cs", "return Ok(\"first\");");
        using var session = NewSession(scaffold, gateway);

        var ok1 = await session.RunStepAsync(Order(1, 10), ct: Ct);
        Assert.Equal(StepSessionStatus.Ran, ok1.Status);
        Assert.True(ok1.Validated);
        Assert.Equal("first", ok1.Result!.Result!.Message);

        // Overwrite with a typo (undeclared identifier) — must fail validation, NOT execute, NOT crash.
        scaffold.AddStep("01_10_edit.cs", "return Okkk(\"boom\");");
        var bad = await session.RunStepAsync(Order(1, 10), ct: Ct);
        Assert.Equal(StepSessionStatus.NotValidated, bad.Status);
        Assert.False(bad.Validated);
        Assert.Null(bad.Result);
        Assert.Contains(bad.Validation.Diagnostics, d =>
            d.Severity == Severity.Error && d.Code.StartsWith("CS", StringComparison.Ordinal) && d.Line >= 1);

        // Fix it again — runs.
        scaffold.AddStep("01_10_edit.cs", "return Ok(\"fixed\");");
        var ok2 = await session.RunStepAsync(Order(1, 10), ct: Ct);
        Assert.True(ok2.Validated);
        Assert.Equal("fixed", ok2.Result!.Result!.Message);
    }

    // ── Validation gates (PUMP001 / PUMP010) ──────────────────────────────────

    [Fact]
    public async Task Pump001_nuget_directive_fails_validation_and_does_not_execute()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_nuget.cs", "#r \"nuget:Some.Pkg\"\nreturn Ok();");
        using var session = NewSession(scaffold, gateway);

        var r = await session.RunStepAsync(Order(1, 10), ct: Ct);

        Assert.False(r.Validated);
        Assert.Null(r.Result);
        Assert.Contains(r.Validation.Diagnostics, d => d.Code == PumpDiagnostics.NuGetDirective);
        Assert.Empty(gateway.Slots); // never executed → no slot opened
    }

    [Fact]
    public async Task Pump010_unsafe_without_grant_fails_validation_and_does_not_execute()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_unsafe.cs", "// @unsafe: true\nreturn Ok();");
        using var session = NewSession(scaffold, gateway); // AllowUnsafeDirectAccess defaults to false

        var r = await session.RunStepAsync(Order(1, 10), ct: Ct);

        Assert.False(r.Validated);
        Assert.Null(r.Result);
        Assert.Contains(r.Validation.Diagnostics, d => d.Code == PumpDiagnostics.UnsafeWithoutGrant);
        Assert.Empty(gateway.Slots);
    }

    // ── Transaction per step ──────────────────────────────────────────────────

    [Fact]
    public async Task Ok_step_commits_and_fail_step_rolls_back_each_re_run_independent()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_ok.cs", "Execute(\"insert\"); return Ok();");
        scaffold.AddStep("01_20_fail.cs", "Execute(\"x\"); return Fail(\"boom\");");
        using var session = NewSession(scaffold, gateway);

        var ok = await session.RunStepAsync(Order(1, 10), ct: Ct);
        Assert.True(ok.Result!.Committed);
        Assert.Equal(1, gateway.Slots[0].Commits);
        Assert.Equal(0, gateway.Slots[0].Rollbacks);

        var fail = await session.RunStepAsync(Order(1, 20), ct: Ct);
        Assert.False(fail.Result!.Committed);
        Assert.Equal(Severity.Error, fail.Result.EffectiveSeverity);
        Assert.Equal(1, gateway.Slots[1].Rollbacks);

        // Re-running the fail step is its own independent transaction (new slot, rolled back again).
        var failAgain = await session.RunStepAsync(Order(1, 20), ct: Ct);
        Assert.False(failAgain.Result!.Committed);
        Assert.Equal(1, gateway.Slots[2].Rollbacks);
    }

    // ── Stable RunId / Workspace across calls ─────────────────────────────────

    [Fact]
    public async Task RunId_and_RunDir_are_stable_across_calls()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_a.cs", "return Ok();");
        scaffold.AddStep("01_20_b.cs", "return Ok();");
        using var session = NewSession(scaffold, gateway);

        Assert.Equal(FixedRunId, session.RunId);
        var runDir = session.Workspace.RunDir;

        await session.RunStepAsync(Order(1, 10), ct: Ct);
        await session.RunStepAsync(Order(1, 20), ct: Ct);

        Assert.Equal(FixedRunId, session.RunId);
        Assert.Equal(runDir, session.Workspace.RunDir);
        Assert.Contains(FixedRunId.ToString("D"), runDir, StringComparison.Ordinal);
    }

    // ── Group isolation + LegacyGlobalState ───────────────────────────────────

    [Fact]
    public async Task Group_state_is_isolated_by_default()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_set.cs", "State.Set(\"g\", \"one\"); return Ok();");
        // Group 02 must NOT see group 01's State: Get<string> returns default (null) → message "null".
        scaffold.AddStep("02_10_read.cs",
            "var v = State.Get<string>(\"g\"); return Ok(v ?? \"null\");");
        using var session = NewSession(scaffold, gateway);

        await session.RunStepAsync(Order(1, 10), ct: Ct);
        var r = await session.RunStepAsync(Order(2, 10), ct: Ct);

        Assert.Equal("null", r.Result!.Result!.Message);
    }

    [Fact]
    public async Task Legacy_global_state_shares_state_across_groups()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_set.cs", "State.Set(\"g\", \"one\"); return Ok();");
        scaffold.AddStep("02_10_read.cs",
            "var v = State.Get<string>(\"g\"); return Ok(v ?? \"null\");");
        using var session = NewSession(scaffold, gateway, legacyGlobalState: true);

        await session.RunStepAsync(Order(1, 10), ct: Ct);
        var r = await session.RunStepAsync(Order(2, 10), ct: Ct);

        Assert.Equal("one", r.Result!.Result!.Message);
    }

    // ── Unknown order → clear result/error ────────────────────────────────────

    [Fact]
    public async Task Empty_context_with_empty_connection_directory_constructs_and_returns_empty_results()
    {
        using var scaffold = new EngineScaffold();
        var gateway = new FakeConnectionGateway();
        var options = new PumpOptions(scaffold.WorkspaceBase, PumpFolderPolicy.Empty);
        using var session = new PumpSession(
            scaffold.Discover(),
            EngineScaffold.Extern(),
            new EmptyConnectionDirectory(),
            options,
            new FakeGuidProvider(FixedRunId),
            TimeProvider.System,
            gateway,
            logger: null);

        var group = await session.RunGroupAsync(1, ct: Ct);
        var step = await session.RunStepAsync(Order(1, 10), ct: Ct);

        Assert.Empty(group);
        Assert.Equal(StepSessionStatus.NotFound, step.Status);
        Assert.Empty(gateway.Slots);
    }

    [Fact]
    public void Nonempty_context_with_empty_connection_directory_fails_eagerly_and_disposes_scope()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "return Ok();");
        var logger = new TestLogger();
        var options = new PumpOptions(scaffold.WorkspaceBase, PumpFolderPolicy.Empty);

        Assert.Throws<InvalidOperationException>(() => new PumpSession(
            scaffold.Discover(),
            EngineScaffold.Extern(),
            new EmptyConnectionDirectory(),
            options,
            new FakeGuidProvider(FixedRunId),
            TimeProvider.System,
            new FakeConnectionGateway(),
            logger));

        Assert.Equal(0, logger.ActiveScopeCount);
    }

    [Fact]
    public async Task Unknown_order_returns_clear_not_found_result()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_a.cs", "return Ok();");
        using var session = NewSession(scaffold, gateway);

        var r = await session.RunStepAsync(Order(9, 99), ct: Ct);

        Assert.Equal(StepSessionStatus.NotFound, r.Status);
        Assert.False(r.Found);
        Assert.False(r.Validated);
        Assert.Null(r.Result);
        Assert.NotNull(r.Message);
        Assert.Contains("9_99", r.Message);
        Assert.Empty(gateway.Slots);
    }

    // ── RunGroupAsync convenience + descriptor overload ───────────────────────

    [Fact]
    public async Task RunGroupAsync_runs_group_in_order_sharing_state()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_set.cs", "State.Set(\"k\", 7); return Ok();");
        scaffold.AddStep("01_20_get.cs", "return Ok(State.Get<int>(\"k\").ToString());");
        scaffold.AddStep("02_10_other.cs", "return Ok();");
        using var session = NewSession(scaffold, gateway);

        var results = await session.RunGroupAsync(1, ct: Ct);

        Assert.Equal(2, results.Count); // only the two group-01 steps
        Assert.All(results, r => Assert.True(r.Validated));
        Assert.Equal("7", results[1].Result!.Result!.Message); // step 01_20 saw 01_10's State
    }

    [Fact]
    public async Task RunStepAsync_descriptor_overload_runs_the_step()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_a.cs", "return Ok(\"hi\");");
        var ctx = scaffold.Discover();
        var step = ctx.Steps.Single();
        using var session = NewSession(scaffold, gateway);

        var r = await session.RunStepAsync(step, ct: Ct);

        Assert.True(r.Validated);
        Assert.Equal("hi", r.Result!.Result!.Message);
    }

    [Fact]
    public async Task Disposed_session_rejects_further_runs()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_a.cs", "return Ok();");
        var session = NewSession(scaffold, gateway);
        session.Dispose();
        session.Dispose(); // idempotent

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RunStepAsync(Order(1, 10), ct: Ct));
    }

    // ── New S1.1 correction tests ──────────────────────────────────────────────

    /// <summary>File deleted between calls surfaces as Found=true, Validated=false, Message set, no crash.</summary>
    [Fact]
    public async Task File_deleted_between_calls_returns_not_validated_with_message()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_gone.cs", "return Ok();");
        using var session = NewSession(scaffold, gateway);

        // Run once successfully so we know the step exists.
        var r1 = await session.RunStepAsync(Order(1, 10), ct: Ct);
        Assert.True(r1.Validated);

        // Delete the file between calls.
        var ctx = scaffold.Discover();
        var filePath = ctx.Steps.Single().FilePath;
        File.Delete(filePath);

        var r2 = await session.RunStepAsync(Order(1, 10), ct: Ct);

        Assert.Equal(StepSessionStatus.NotValidated, r2.Status);
        Assert.True(r2.Found);          // step still exists in the session's ScriptContext
        Assert.False(r2.Validated);     // but file read failed
        Assert.Null(r2.Result);
        Assert.NotNull(r2.Message);
        Assert.Contains("could not be read", r2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task File_replaced_by_directory_between_calls_returns_not_validated_with_message()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_directory.cs", "return Ok();");
        using var session = NewSession(scaffold, gateway);

        var first = await session.RunStepAsync(Order(1, 10), ct: Ct);
        Assert.True(first.Validated);

        var filePath = Assert.Single(session.KnownSteps).FilePath;
        File.Delete(filePath);
        Directory.CreateDirectory(filePath);

        var second = await session.RunStepAsync(Order(1, 10), ct: Ct);

        Assert.Equal(StepSessionStatus.NotValidated, second.Status);
        Assert.True(second.Found);
        Assert.False(second.Validated);
        Assert.Contains("could not be read", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Edit that flips @unsafe false→true between calls (engine grant off) → second call returns PUMP010,
    /// proving meta is RE-PARSED from the newly read text, not reused from the first call.
    /// </summary>
    [Fact]
    public async Task Unsafe_tag_added_between_calls_triggers_pump010_on_rerun()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_safe.cs", "return Ok(\"safe\");");
        // AllowUnsafeDirectAccess defaults to false in NewSession.
        using var session = NewSession(scaffold, gateway);

        var r1 = await session.RunStepAsync(Order(1, 10), ct: Ct);
        Assert.True(r1.Validated);

        // Overwrite with @unsafe: true — engine grant is off, so PUMP010 must fire.
        scaffold.AddStep("01_10_safe.cs", "// @unsafe: true\nreturn Ok(\"unsafe\");");
        var r2 = await session.RunStepAsync(Order(1, 10), ct: Ct);

        Assert.False(r2.Validated);
        Assert.Contains(r2.Validation.Diagnostics, d => d.Code == PumpDiagnostics.UnsafeWithoutGrant);
    }

    /// <summary>RunGroupAsync: a validation-failing step does NOT stop later steps (continuation).</summary>
    [Fact]
    public async Task RunGroupAsync_validation_failure_does_not_stop_later_steps()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_bad.cs", "#r \"nuget:Some.Pkg\"\nreturn Ok();"); // PUMP001
        scaffold.AddStep("01_20_good.cs", "return Ok(\"ran\");");
        using var session = NewSession(scaffold, gateway);

        var results = await session.RunGroupAsync(1, ct: Ct);

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Validated);    // first step failed validation
        Assert.True(results[1].Validated);     // second step still ran
        Assert.Equal("ran", results[1].Result!.Result!.Message);
    }

    /// <summary>Disposed session rejects RunGroupAsync.</summary>
    [Fact]
    public async Task Disposed_session_rejects_RunGroupAsync()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_a.cs", "return Ok();");
        var session = NewSession(scaffold, gateway);
        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RunGroupAsync(1, ct: Ct));
    }

    /// <summary>A pre-cancelled token causes RunStepAsync to surface OperationCanceledException; gate is released.</summary>
    [Fact]
    public async Task Pre_cancelled_token_surfaces_cancellation_and_gate_is_released()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        scaffold.AddStep("01_10_a.cs", "return Ok();");
        using var session = NewSession(scaffold, gateway);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // RunStepAsync (descriptor overload) should propagate OCE because WaitAsync(cancelledToken) throws.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.RunStepAsync(Order(1, 10), ct: cts.Token));

        // Gate must have been released: a subsequent call with a live token must succeed.
        var r = await session.RunStepAsync(Order(1, 10), ct: Ct);
        Assert.True(r.Validated);
    }

    [Fact]
    public async Task Mid_step_cancellation_rolls_back_rethrows_and_releases_gate()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_loop.cs",
                "Execute(\"x\"); while (true) { Cancellation.ThrowIfCancellationRequested(); } return Ok();")
            .AddStep("01_20_after.cs", "return Ok(\"after cancellation\");");
        using var session = NewSession(scaffold, gateway);
        using var cts = new CancellationTokenSource();

        var runTask = session.RunStepAsync(Order(1, 10), ct: cts.Token);
        await gateway.WaitForSlotCountAsync(1, Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);

        var after = await session.RunStepAsync(Order(1, 20), ct: Ct);
        Assert.Equal("after cancellation", after.Result!.Result!.Message);
    }

    [Fact]
    public async Task Step_timeout_uses_injected_time_and_returns_error_result()
    {
        var gateway = new FakeConnectionGateway();
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_timeout.cs",
                "Execute(\"x\"); await Task.Delay(Timeout.InfiniteTimeSpan, Cancellation); return Ok();");
        using var session = NewSession(
            scaffold,
            gateway,
            timeout: TimeSpan.FromSeconds(5),
            timeProvider: time);

        var runTask = session.RunStepAsync(Order(1, 10), ct: Ct);
        await gateway.WaitForSlotCountAsync(1, Ct);
        time.Advance(TimeSpan.FromSeconds(6));

        var result = await runTask;

        Assert.Equal(StepSessionStatus.Ran, result.Status);
        Assert.Equal(Severity.Error, result.Result!.EffectiveSeverity);
        Assert.Equal("Step timed out.", result.Result.Result!.Message);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);
    }

    [Fact]
    public async Task Concurrent_step_calls_are_serialized_by_session_gate()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_block.cs",
                "Execute(\"x\"); " +
                "var entered = Shared.Get<int>(\"entered\"); " +
                "Shared.Set(\"entered\", entered + 1); " +
                "Shared.Require<TaskCompletionSource<bool>>(\"started\").TrySetResult(true); " +
                "await Shared.Require<TaskCompletionSource<bool>>(\"release\").Task; return Ok();");
        using var session = NewSession(scaffold, gateway);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Shared.Set("started", started);
        session.Shared.Set("release", release);

        var first = session.RunStepAsync(Order(1, 10), ct: Ct);
        await started.Task.WaitAsync(Ct);
        var second = session.RunStepAsync(Order(1, 10), ct: Ct);

        Assert.False(second.IsCompleted);
        Assert.Equal(1, session.Shared.Get<int>("entered"));
        Assert.Single(gateway.Slots);

        release.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(2, session.Shared.Get<int>("entered"));
        Assert.Equal(2, gateway.Slots.Count);
    }

    [Fact]
    public async Task Unknown_group_returns_empty_without_opening_slots()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "return Ok();");
        using var session = NewSession(scaffold, gateway);

        var results = await session.RunGroupAsync(99, ct: Ct);

        Assert.Empty(results);
        Assert.Empty(gateway.Slots);
    }

    /// <summary>
    /// A step with @haltOnError:true that errors does NOT prevent a subsequent RunStepAsync
    /// (the Halt flag is ignored by the session — there is no run loop to halt).
    /// </summary>
    [Fact]
    public async Task HaltOnError_step_does_not_prevent_next_RunStepAsync()
    {
        var gateway = new FakeConnectionGateway();
        using var scaffold = new EngineScaffold();
        // @haltOnError defaults to true; Fail() produces a Severity.Error result.
        scaffold.AddStep("01_10_fail.cs", "return Fail(\"boom\");");
        scaffold.AddStep("01_20_ok.cs", "return Ok(\"after\");");
        using var session = NewSession(scaffold, gateway);

        var r1 = await session.RunStepAsync(Order(1, 10), ct: Ct);
        Assert.True(r1.Validated);
        Assert.Equal(Severity.Error, r1.Result!.EffectiveSeverity);

        // The session must still run the next step.
        var r2 = await session.RunStepAsync(Order(1, 20), ct: Ct);
        Assert.True(r2.Validated);
        Assert.Equal("after", r2.Result!.Result!.Message);
    }
}
