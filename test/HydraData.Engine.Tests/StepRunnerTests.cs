// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T02.6 / T02.6a: the transaction policy table, mixed-outcome rollback, per-step timeout
/// (deterministic via a manual time provider) and caller cancellation. Uses fake DB seams.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public class StepRunnerTests
{
    private static (StepRunner runner, FakeConnectionGateway gateway) NewRunner(TimeProvider? time = null)
    {
        var gateway = new FakeConnectionGateway();
        var runner = new StepRunner(new ScriptCompiler(), gateway, io: null, timeProvider: time);
        return (runner, gateway);
    }

    // The runner's RunAsync takes a CancellationToken; callers pass one explicitly (ambient token,
    // or a controllable one for the cancellation test).
    private static Task<StepOutcome> Run(
        StepRunner runner,
        string code,
        CancellationToken ct,
        TimeSpan? timeout = null) =>
        runner.RunAsync(
            code,
            new PumpState(),
            new PumpState(),
            ExternContext.FromValues(new Dictionary<string, object?>()),
            PumpContextFactory.DefaultConnection,
            unsafeAllowed: false,
            stepTimeout: timeout,
            logger: null,
            ct: ct);

    // ── Policy row 1: Ok => Commit ──────────────────────────────────────────────
    [Fact]
    public async Task Ok_commits()
    {
        var (runner, gateway) = NewRunner();
        var outcome = await Run(runner, "Execute(\"x\"); return Ok();", TestContext.Current.CancellationToken);

        Assert.True(outcome.Committed);
        Assert.Equal(Severity.Success, outcome.EffectiveSeverity);
        Assert.Equal(1, gateway.Slots[0].Commits);
        Assert.Equal(0, gateway.Slots[0].Rollbacks);
    }

    // ── Policy row 2: Warn => Commit ────────────────────────────────────────────
    [Fact]
    public async Task Warn_commits()
    {
        var (runner, gateway) = NewRunner();
        var outcome = await Run(runner, "Execute(\"x\"); return Warn(\"w\");", TestContext.Current.CancellationToken);

        Assert.True(outcome.Committed);
        Assert.Equal(Severity.Warning, outcome.EffectiveSeverity);
        Assert.Equal(1, gateway.Slots[0].Commits);
    }

    [Fact]
    public async Task Public_step_verdict_with_warning_commits()
    {
        var (runner, gateway) = NewRunner();
        var outcome = await Run(
            runner,
            "Execute(\"x\"); throw new StepVerdict(StepResult.Warn(\"partial\"));",
            TestContext.Current.CancellationToken);

        Assert.Equal(Severity.Warning, outcome.EffectiveSeverity);
        Assert.Equal("partial", outcome.Result.Message);
        Assert.True(outcome.Committed);
        Assert.Equal(1, gateway.Slots[0].Commits);
        Assert.Equal(0, gateway.Slots[0].Rollbacks);
    }

    // ── Policy row 3: Fail => Rollback ──────────────────────────────────────────
    [Fact]
    public async Task Fail_rolls_back()
    {
        var (runner, gateway) = NewRunner();
        var outcome = await Run(runner, "Execute(\"x\"); return Fail(\"f\");", TestContext.Current.CancellationToken);

        Assert.False(outcome.Committed);
        Assert.Equal(Severity.Error, outcome.EffectiveSeverity);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);
        Assert.Equal(0, gateway.Slots[0].Commits);
    }

    // ── Policy row 4: Crash (Expect false / throw) => Rollback ───────────────────
    [Fact]
    public async Task Expect_false_rolls_back_with_fail_message()
    {
        var (runner, gateway) = NewRunner();
        var outcome = await Run(runner, "Execute(\"x\"); Expect(false, \"nope\"); return Ok();", TestContext.Current.CancellationToken);

        Assert.False(outcome.Committed);
        Assert.Equal(Severity.Error, outcome.Result.Severity);
        Assert.Equal("nope", outcome.Result.Message);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);
    }

    [Fact]
    public async Task Thrown_exception_rolls_back()
    {
        var (runner, gateway) = NewRunner();
        var outcome = await Run(runner, "Execute(\"x\"); throw new System.Exception(\"kaboom\");", TestContext.Current.CancellationToken);

        Assert.False(outcome.Committed);
        Assert.Equal(Severity.Error, outcome.Result.Severity);
        Assert.Contains("kaboom", outcome.Result.Message, StringComparison.Ordinal);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);
    }

    [Fact]
    public async Task Missing_return_becomes_clear_error_and_rolls_back()
    {
        var (runner, gateway) = NewRunner();

        var outcome = await Run(runner, "Execute(\"x\");", TestContext.Current.CancellationToken);

        Assert.Equal(Severity.Error, outcome.EffectiveSeverity);
        Assert.Contains("Step returned no result", outcome.Result.Message, StringComparison.Ordinal);
        Assert.Contains("return Ok();", outcome.Result.Message, StringComparison.Ordinal);
        Assert.False(outcome.Committed);
        Assert.Equal(0, gateway.Slots[0].Commits);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);
        Assert.True(gateway.Slots[0].Disposed);
    }

    // ── Command-timeout propagation (FIX 1): StepTimeout reaches the DB seam ──────
    [Fact]
    public async Task Step_timeout_is_propagated_to_the_db_seam_as_command_timeout()
    {
        var (runner, gateway) = NewRunner();
        // A DB call must occur so a slot is actually opened (Execute opens/reuses the slot).
        await Run(runner, "Execute(\"x\"); return Ok();", TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromSeconds(30));

        // The gateway recorded the command timeout passed when the slot was opened: 30s timeout => 30.
        Assert.Equal(30, Assert.Single(gateway.CommandTimeouts));
    }

    [Fact]
    public async Task Sub_second_step_timeout_rounds_up_to_one_second_command_timeout()
    {
        var (runner, gateway) = NewRunner();
        await Run(runner, "Execute(\"x\"); return Ok();", TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromMilliseconds(250));

        // A sub-second timeout must never become a 0 ("infinite") command timeout: rounds up to 1.
        Assert.Equal(1, Assert.Single(gateway.CommandTimeouts));
    }

    [Fact]
    public async Task No_step_timeout_leaves_command_timeout_unset()
    {
        var (runner, gateway) = NewRunner();
        await Run(runner, "Execute(\"x\"); return Ok();", TestContext.Current.CancellationToken,
            timeout: null);

        // No step timeout => no override; the provider default command timeout applies.
        Assert.Null(Assert.Single(gateway.CommandTimeouts));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, 1)] // 0 must never mean "infinite" command timeout: the min-1 guard maps it to 1.
    [InlineData(0.25, 1)]
    [InlineData(1.0, 1)]
    [InlineData(1.2, 2)]
    [InlineData(30.0, 30)]
    public void CommandTimeoutSeconds_maps_step_timeout_to_seconds(double? totalSeconds, int? expected)
    {
        var stepTimeout = totalSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        Assert.Equal(expected, StepRunner.CommandTimeoutSeconds(stepTimeout));
    }

    // ── T02.6a: Error note over an Ok return => Rollback ─────────────────────────
    [Fact]
    public async Task Error_note_forces_rollback_even_when_returning_ok()
    {
        var (runner, gateway) = NewRunner();
        var outcome = await Run(runner, "Execute(\"x\"); Note(\"bad\", Severity.Error); return Ok();", TestContext.Current.CancellationToken);

        Assert.False(outcome.Committed);
        Assert.Equal(Severity.Error, outcome.EffectiveSeverity);
        Assert.Equal(Severity.Success, outcome.Result.Severity); // the returned result is still Ok
        Assert.Equal(1, gateway.Slots[0].Rollbacks);
    }

    [Fact]
    public async Task Warning_note_over_ok_still_commits()
    {
        var (runner, gateway) = NewRunner();
        var outcome = await Run(runner, "Execute(\"x\"); Note(\"careful\", Severity.Warning); return Ok();", TestContext.Current.CancellationToken);

        Assert.True(outcome.Committed);
        Assert.Equal(Severity.Warning, outcome.EffectiveSeverity);
        Assert.Equal(1, gateway.Slots[0].Commits);
    }

    // ── Output capture is reported on the outcome ────────────────────────────────
    [Fact]
    public async Task Output_is_captured_on_the_outcome()
    {
        var (runner, _) = NewRunner();
        var outcome = await Run(runner, "Print(\"hello-step\"); return Ok();", TestContext.Current.CancellationToken);

        Assert.Contains("hello-step", outcome.Output, StringComparison.Ordinal);
    }

    // ── Policy row 5: per-step timeout => Rollback (deterministic) ────────────────
    [Fact]
    public async Task Timeout_rolls_back_deterministically()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var (runner, gateway) = NewRunner(time);

        // Script touches the DB then waits on its cancellation token. The timeout fires only when the
        // manual clock is advanced past the timeout, so the test is deterministic (no wall-clock race).
        const string code =
            "Execute(\"x\"); await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, Cancellation); return Ok();";

        var runTask = Run(runner, code, TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(5));

        // Wait deterministically until the script has opened its slot (i.e. is past the Execute call
        // and is now waiting on the infinite Delay), then advance the clock past the timeout.
        await gateway.WaitForSlotCountAsync(1, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(6));

        var outcome = await runTask;

        Assert.False(outcome.Committed);
        Assert.Equal("Step timed out.", outcome.Result.Message);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);
    }

    // ── Cancellation => Rollback + rethrow ───────────────────────────────────────
    [Fact]
    public async Task Caller_cancellation_rolls_back_and_rethrows()
    {
        var (runner, gateway) = NewRunner();
        using var cts = new CancellationTokenSource();

        const string code =
            "Execute(\"x\"); await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, Cancellation); return Ok();";

        var runTask = Run(runner, code, cts.Token);

        // Wait deterministically for the slot to open (script is past Execute and waiting on Delay).
        await gateway.WaitForSlotCountAsync(1, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);

        Assert.Equal(1, gateway.Slots[0].Rollbacks);
        Assert.Equal(0, gateway.Slots[0].Commits);
    }

    [Fact]
    public async Task Caller_cancellation_takes_priority_when_timeout_is_also_signalled()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var (runner, gateway) = NewRunner(time);
        using var cts = new CancellationTokenSource();

        const string code =
            "Execute(\"x\"); await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, Cancellation); return Ok();";

        var runTask = Run(runner, code, cts.Token, timeout: TimeSpan.FromSeconds(5));

        await gateway.WaitForSlotCountAsync(1, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(6));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);
        Assert.Equal(0, gateway.Slots[0].Commits);
        Assert.Equal(1, gateway.Slots[0].Rollbacks);
        Assert.True(gateway.Slots[0].Disposed);
    }

    // ── Finalize exception handling (correction 1) ──────────────────────────────
    [Fact]
    public async Task Commit_failure_returns_error_outcome_and_disposes_slot()
    {
        // Arrange: a gateway whose slot throws from Commit().
        var gateway = new FakeConnectionGateway { NextSlotThrowsOnCommit = true };
        var runner = new StepRunner(new ScriptCompiler(), gateway, io: null, timeProvider: null);

        // The script returns Ok → the runner would normally commit; the commit throws.
        var outcome = await runner.RunAsync(
            "Execute(\"x\"); return Ok();",
            new PumpState(),
            new PumpState(),
            ExternContext.FromValues(new Dictionary<string, object?>()),
            PumpContextFactory.DefaultConnection,
            unsafeAllowed: false,
            stepTimeout: null,
            logger: null,
            ct: TestContext.Current.CancellationToken);

        // The outcome must be an Error (data did not land), not an escaping exception.
        Assert.Equal(Severity.Error, outcome.EffectiveSeverity);
        Assert.False(outcome.Committed);
        Assert.Contains("Commit failed", outcome.Result.Message, StringComparison.Ordinal);

        // The slot must still be disposed despite the commit failure.
        Assert.True(gateway.Slots[0].Disposed);
    }

    // ── Connection switching: in-script fan-out ──────

    [Fact]
    public async Task In_script_connection_switch_commits_both_slots()
    {
        var (runner, gateway) = NewRunner();

        // The script writes to the default (MSSQL) connection, then switches to PGSQL and writes there too.
        const string code =
            "Execute(\"insert into a values (1)\"); " +
            "var pg = GetConnection(CurrentConnection.Name, CurrentConnection.DbType, \"pgsql\"); " +
            "Execute(pg, \"insert into b values (1)\"); return Ok();";

        var outcome = await RunWithDirectory(runner, code, TestContext.Current.CancellationToken);

        Assert.True(outcome.Committed, outcome.Result.Message);
        Assert.Equal(2, gateway.Slots.Count);
        Assert.Equal(1, gateway.SlotFor("mssql|stage").Commits);
        Assert.Equal(1, gateway.SlotFor("pgsql|stage").Commits);
        Assert.Equal(0, gateway.SlotFor("mssql|stage").Rollbacks);
        Assert.Equal(0, gateway.SlotFor("pgsql|stage").Rollbacks);
    }

    [Fact]
    public async Task In_script_connection_switch_rolls_back_both_slots_on_fail()
    {
        var (runner, gateway) = NewRunner();

        const string code =
            "Execute(\"insert into a values (1)\"); " +
            "var pg = GetConnection(CurrentConnection.Name, CurrentConnection.DbType, \"pgsql\"); " +
            "Execute(pg, \"insert into b values (1)\"); return Fail(\"abort\");";

        var outcome = await RunWithDirectory(runner, code, TestContext.Current.CancellationToken);

        Assert.False(outcome.Committed);
        Assert.Equal(2, gateway.Slots.Count);
        Assert.Equal(1, gateway.SlotFor("mssql|stage").Rollbacks);
        Assert.Equal(1, gateway.SlotFor("pgsql|stage").Rollbacks);
        Assert.Equal(0, gateway.SlotFor("mssql|stage").Commits);
        Assert.Equal(0, gateway.SlotFor("pgsql|stage").Commits);
    }

    // A directory with both MSSQL and PGSQL entries named "stage" (ids mssql|stage and pgsql|stage).
    private static IConnectionDirectory TwoSystemDirectory() =>
        new ConnectionDirectory(ConnectionRegistry.Parse(
            """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="stage">
                <Parameters><Parameter key="Server" value="db01" type="String" /></Parameters>
              </ConnectionString>
              <ConnectionString targetSystem="PGSQL" name="stage">
                <Parameters><Parameter key="Host" value="db02" type="String" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """));

    private static Task<StepOutcome> RunWithDirectory(StepRunner runner, string code, CancellationToken ct) =>
        runner.RunAsync(
            code,
            new PumpState(),
            new PumpState(),
            ExternContext.FromValues(new Dictionary<string, object?>()),
            PumpContextFactory.DefaultConnection,
            unsafeAllowed: false,
            stepTimeout: null,
            logger: null,
            connections: TwoSystemDirectory(),
            ct: ct);

    // ── Partial-commit: two slots open, first throws on Commit ──

    [Fact]
    public async Task Partial_commit_first_slot_throws_returns_error_with_partial_commit_message_and_both_disposed()
    {
        // Arm the FIRST slot to throw on Commit; the second slot commits normally.
        // FinishAll fans-out over all slots in iteration order, collecting errors — both slots
        // are Disposed even when the first Commit throws (A5: Dispose guard).
        var gateway = new FakeConnectionGateway { NextSlotThrowsOnCommit = true };
        var runner = new StepRunner(new ScriptCompiler(), gateway, io: null, timeProvider: null);

        var outcome = await runner.RunAsync(
            "Execute(\"x\"); " +
            "Execute(GetConnection(CurrentConnection.Name, CurrentConnection.DbType, \"pgsql\"), \"y\"); " +
            "return Ok();",
            new PumpState(),
            new PumpState(),
            ExternContext.FromValues(new Dictionary<string, object?>()),
            PumpContextFactory.DefaultConnection,
            unsafeAllowed: false,
            stepTimeout: null,
            logger: null,
            connections: TwoSystemDirectory(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(Severity.Error, outcome.EffectiveSeverity);
        Assert.False(outcome.Committed);
        // The message must include the partial-commit caveat (A1).
        Assert.Contains("partial commit possible", outcome.Result.Message, StringComparison.OrdinalIgnoreCase);
        // Both slots must have been disposed despite the commit failure (A5).
        Assert.Equal(2, gateway.Slots.Count);
        Assert.True(gateway.Slots[0].Disposed);
        Assert.True(gateway.Slots[1].Disposed);
    }

    // ── Rollback-failure (non-cancellation): original failure reason preserved ──

    [Fact]
    public async Task Rollback_failure_preserves_original_failure_reason_in_message()
    {
        var gateway = new FakeConnectionGateway { NextSlotThrowsOnRollback = true };
        var runner = new StepRunner(new ScriptCompiler(), gateway, io: null, timeProvider: null);

        var outcome = await runner.RunAsync(
            "Execute(\"x\"); return Fail(\"original-reason\");",
            new PumpState(),
            new PumpState(),
            ExternContext.FromValues(new Dictionary<string, object?>()),
            PumpContextFactory.DefaultConnection,
            unsafeAllowed: false,
            stepTimeout: null,
            logger: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(Severity.Error, outcome.EffectiveSeverity);
        Assert.False(outcome.Committed);
        // Both the rollback failure and the original failure reason must appear in the message.
        Assert.Contains("Rollback failed", outcome.Result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("original-reason", outcome.Result.Message, StringComparison.Ordinal);
        Assert.True(gateway.Slots[0].Disposed);
    }

    // ── Rollback-failure during caller cancellation: OCE still rethrown ──────────

    [Fact]
    public async Task Rollback_failure_during_caller_cancellation_still_rethrows_OperationCanceledException()
    {
        var gateway = new FakeConnectionGateway { NextSlotThrowsOnRollback = true };
        var runner = new StepRunner(new ScriptCompiler(), gateway, io: null, timeProvider: null);
        using var cts = new CancellationTokenSource();

        const string code =
            "Execute(\"x\"); await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, Cancellation); return Ok();";

        var runTask = runner.RunAsync(
            code,
            new PumpState(),
            new PumpState(),
            ExternContext.FromValues(new Dictionary<string, object?>()),
            PumpContextFactory.DefaultConnection,
            unsafeAllowed: false,
            stepTimeout: null,
            logger: null,
            ct: cts.Token);

        // Wait deterministically for the slot to open before cancelling.
        await gateway.WaitForSlotCountAsync(1, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        // The rollback throws, but the suppress path means OCE is still rethrown.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);
    }

    // ── Two-slot fan-out: crash (exception) → both slots rolled back ─────────────

    [Fact]
    public async Task Two_slot_crash_rolls_back_both_slots()
    {
        var (runner, gateway) = NewRunner();

        const string code =
            "Execute(\"x\"); " +
            "Execute(GetConnection(CurrentConnection.Name, CurrentConnection.DbType, \"pgsql\"), \"y\"); " +
            "throw new System.Exception(\"kaboom\");";

        var outcome = await RunWithDirectory(runner, code, TestContext.Current.CancellationToken);

        Assert.False(outcome.Committed);
        Assert.Equal(Severity.Error, outcome.EffectiveSeverity);
        Assert.Equal(2, gateway.Slots.Count);
        Assert.Equal(1, gateway.SlotFor("mssql|stage").Rollbacks);
        Assert.Equal(1, gateway.SlotFor("pgsql|stage").Rollbacks);
        Assert.Equal(0, gateway.SlotFor("mssql|stage").Commits);
        Assert.Equal(0, gateway.SlotFor("pgsql|stage").Commits);
    }

    // ── Two-slot fan-out: Error-note over Ok → both slots rolled back ────────────

    [Fact]
    public async Task Two_slot_error_note_over_ok_rolls_back_both_slots()
    {
        var (runner, gateway) = NewRunner();

        const string code =
            "Execute(\"x\"); " +
            "Execute(GetConnection(CurrentConnection.Name, CurrentConnection.DbType, \"pgsql\"), \"y\"); " +
            "Note(\"bad\", Severity.Error); return Ok();";

        var outcome = await RunWithDirectory(runner, code, TestContext.Current.CancellationToken);

        Assert.False(outcome.Committed);
        Assert.Equal(Severity.Error, outcome.EffectiveSeverity);
        Assert.Equal(2, gateway.Slots.Count);
        Assert.Equal(1, gateway.SlotFor("mssql|stage").Rollbacks);
        Assert.Equal(1, gateway.SlotFor("pgsql|stage").Rollbacks);
        Assert.Equal(0, gateway.SlotFor("mssql|stage").Commits);
        Assert.Equal(0, gateway.SlotFor("pgsql|stage").Commits);
    }

    // ── Two-slot fan-out: timeout → both slots rolled back ───────────────────────

    [Fact]
    public async Task Two_slot_timeout_rolls_back_both_slots()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var (runner, gateway) = NewRunner(time);

        const string code =
            "Execute(\"x\"); " +
            "Execute(GetConnection(CurrentConnection.Name, CurrentConnection.DbType, \"pgsql\"), \"y\"); " +
            "await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, Cancellation); return Ok();";

        var runTask = runner.RunAsync(
            code,
            new PumpState(),
            new PumpState(),
            ExternContext.FromValues(new Dictionary<string, object?>()),
            PumpContextFactory.DefaultConnection,
            unsafeAllowed: false,
            stepTimeout: TimeSpan.FromSeconds(5),
            logger: null,
            connections: TwoSystemDirectory(),
            ct: TestContext.Current.CancellationToken);

        // Wait deterministically until both slots are opened, then fire the timeout.
        await gateway.WaitForSlotCountAsync(2, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(6));

        var outcome = await runTask;

        Assert.False(outcome.Committed);
        Assert.Equal("Step timed out.", outcome.Result.Message);
        Assert.Equal(2, gateway.Slots.Count);
        Assert.Equal(1, gateway.SlotFor("mssql|stage").Rollbacks);
        Assert.Equal(1, gateway.SlotFor("pgsql|stage").Rollbacks);
    }

    // ── ScriptCompileException: diagnostic codes survive in the failure message ────

    [Fact]
    public async Task Compile_failure_surfaces_diagnostic_codes_in_message()
    {
        // A script with an undefined identifier produces ScriptCompileException carrying CS-codes.
        // StepRunner must special-case this before the generic catch so the codes appear in the message.
        var (runner, _) = NewRunner();
        var outcome = await Run(
            runner,
            "return UndefinedSymbolXyz();", // CS0103: name not found
            TestContext.Current.CancellationToken);

        Assert.Equal(Severity.Error, outcome.Result.Severity);
        Assert.False(outcome.Committed);
        // The message must contain "Step compile failed" (not the generic "Step crashed").
        Assert.Contains("Step compile failed", outcome.Result.Message, StringComparison.Ordinal);
        // A real CS error code (CS####) must appear so the operator can look it up — match the exact
        // shape rather than the bare substring "CS" (which a broken/garbled message could satisfy).
        Assert.Matches(@"CS\d{4}", outcome.Result.Message);
    }

    // ── Two-slot fan-out: caller cancellation → both rolled back + OCE rethrown ──

    [Fact]
    public async Task Two_slot_caller_cancellation_rolls_back_both_slots_and_rethrows()
    {
        var (runner, gateway) = NewRunner();
        using var cts = new CancellationTokenSource();

        const string code =
            "Execute(\"x\"); " +
            "Execute(GetConnection(CurrentConnection.Name, CurrentConnection.DbType, \"pgsql\"), \"y\"); " +
            "await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, Cancellation); return Ok();";

        var runTask = RunWithDirectory(runner, code, cts.Token);

        // Wait deterministically until both slots are opened before cancelling.
        await gateway.WaitForSlotCountAsync(2, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);

        Assert.Equal(2, gateway.Slots.Count);
        Assert.Equal(1, gateway.SlotFor("mssql|stage").Rollbacks);
        Assert.Equal(1, gateway.SlotFor("pgsql|stage").Rollbacks);
        Assert.Equal(0, gateway.SlotFor("mssql|stage").Commits);
        Assert.Equal(0, gateway.SlotFor("pgsql|stage").Commits);
    }
}
