// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// The embedded Phase-B surface: compile-only validation plus orchestrated
/// execution of a discovered <see cref="ScriptContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// The signature is fixed by runtime contract
/// (<see cref="PumpOptions"/>) are supplied to the implementation's constructor, not to
/// <see cref="ExecuteAsync"/>.
/// </para>
/// <para>
/// <b>Sequential execution.</b> Steps run sequentially; this interface is not designed for
/// concurrent execution. <see cref="StepOutputCapture"/> redirects <c>Console.Out/Error</c>
/// process-globally; two concurrent step executions would corrupt output or deadlock. A single
/// <c>SemaphoreSlim</c> in <see cref="StepOutputCapture"/> serializes access within a process.
/// </para>
/// </remarks>
public interface IPumpEngine
{
    /// <summary>
    /// Compiles all scripts in <paramref name="ctx"/> without executing them and checks the engine-owned
    /// diagnostics (PUMP001, PUMP010). Warnings do not make the report invalid.
    /// PUMP020 (missing connection) is checked in <see cref="ExecuteAsync"/>'s preflight, not here.
    /// </summary>
    /// <param name="ctx">The discovered script context.</param>
    /// <param name="externCtx">The read-only host context for the run.</param>
    /// <param name="connections">The connection directory used to resolve targets.</param>
    /// <returns>A <see cref="ValidationReport"/> with all collected diagnostics.</returns>
    ValidationReport Validate(ScriptContext ctx, ExternContext externCtx, IConnectionDirectory connections);

    /// <summary>
    /// Validates and then executes the run: generates the <c>RunId</c> before validation, creates the
    /// workspace, iterates steps in context order with group-local <c>State</c> and run-global
    /// <c>Shared</c>, applies the per-step timeout and transaction policy, and reports interim states via
    /// <paramref name="progress"/>. On preflight failure the run aborts
    /// before any step runs and the returned report carries exit code 1 and the diagnostics.
    /// </summary>
    /// <param name="ctx">The discovered script context.</param>
    /// <param name="externCtx">The read-only host context for the run.</param>
    /// <param name="connections">The connection directory used to resolve targets.</param>
    /// <param name="progress">Optional synchronous progress sink (order matters).</param>
    /// <param name="ct">Caller cancellation token; cancellation rolls back and propagates as <see cref="OperationCanceledException"/> (no <see cref="RunReport"/> is returned — the caller sees the exception, not exit code 2).</param>
    /// <returns>The single final <see cref="RunReport"/> (with <see cref="RunReport.ExitCode"/>).</returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled; slots are rolled back before the exception propagates.</exception>
    Task<RunReport> ExecuteAsync(
        ScriptContext ctx,
        ExternContext externCtx,
        IConnectionDirectory connections,
        IProgress<PumpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resumes a run from <paramref name="resumeFrom"/>: a batch run that validates and executes exactly as
    /// <see cref="ExecuteAsync"/>, except only steps whose order is at or after <paramref name="resumeFrom"/>
    /// are executed (segment-wise numeric order, <see cref="StepOrder.CompareTo"/>). Earlier steps are
    /// recorded as <see cref="StepRunStatus.Skipped"/> and never run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Validate-all, execute-from.</b> Preflight still compiles and audits <b>all</b> steps, so a compile
    /// error, PUMP001 or PUMP010 in any step — even a skipped one — aborts the run with exit code 1 and no
    /// execution, preserving preflight's whole-context guarantee.
    /// </para>
    /// <para>
    /// <b>No in-memory state carry.</b> Skipped steps do not run, so their in-memory <c>State</c> is not
    /// present. The recovery model is idempotent scripts / already-committed DB state (runtime contract
    /// section 8). To preserve in-memory state across a step fix, use the in-process step-session instead.
    /// Group-local <c>State</c> applies only to executed steps (the group rolls over as in the full run,
    /// starting at the first executed step's group); <c>Shared</c> remains run-global.
    /// </para>
    /// <para>
    /// <b>Edge.</b> A <paramref name="resumeFrom"/> past every step yields an empty execution: all steps are
    /// <see cref="StepRunStatus.Skipped"/>, the exit code is 0 and <see cref="PumpPhase.RunFinished"/> is
    /// still emitted.
    /// </para>
    /// </remarks>
    /// <param name="ctx">The discovered script context.</param>
    /// <param name="externCtx">The read-only host context for the run.</param>
    /// <param name="connections">The connection directory used to resolve targets.</param>
    /// <param name="resumeFrom">The order to resume from; steps with <c>order &gt;= resumeFrom</c> execute.</param>
    /// <param name="progress">Optional synchronous progress sink (order matters).</param>
    /// <param name="ct">Caller cancellation token; cancellation rolls back and propagates as <see cref="OperationCanceledException"/> (no <see cref="RunReport"/> is returned).</param>
    /// <returns>The single final <see cref="RunReport"/> (with <see cref="RunReport.ExitCode"/>).</returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled; slots are rolled back before the exception propagates.</exception>
    Task<RunReport> ExecuteFromAsync(
        ScriptContext ctx,
        ExternContext externCtx,
        IConnectionDirectory connections,
        StepOrder resumeFrom,
        IProgress<PumpProgress>? progress = null,
        CancellationToken ct = default);
}
