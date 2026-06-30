// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// The single final result of a pump run. Carries the per-step outcomes,
/// the preflight diagnostics when validation aborted the run, and the canonical
/// <see cref="ExitCode"/>. No second result type is introduced; <see cref="PumpProgress"/> only streams
/// interim states and does not duplicate this report.
/// </summary>
public sealed class RunReport
{
    /// <summary>Initialises a run report.</summary>
    /// <param name="runId">The run identifier (generated before validation).</param>
    /// <param name="exitCode">The canonical exit code.</param>
    /// <param name="steps">The per-step results, in execution order.</param>
    /// <param name="preflightErrors">
    /// The validation diagnostics when preflight failed (exit code 1); empty when the run executed.
    /// </param>
    internal RunReport(
        Guid runId,
        int exitCode,
        IReadOnlyList<StepRunResult> steps,
        IReadOnlyList<ScriptDiagnostic> preflightErrors)
    {
        RunId = runId;
        ExitCode = exitCode;
        Steps = steps;
        PreflightErrors = preflightErrors;
    }

    /// <summary>The run identifier.</summary>
    public Guid RunId { get; }

    /// <summary>
    /// The canonical exit code:
    /// <list type="bullet">
    ///   <item><term>0</term><description>All steps <c>Ok</c>/<c>Warn</c>, no error-note, no compile/runtime error.</description></item>
    ///   <item><term>1</term><description>Validate/preflight failure (syntax/type, PUMP001, PUMP010, missing connection) — aborted before execution.</description></item>
    ///   <item><term>2</term><description>Runtime failure: a <c>Fail</c>, an error-note, a crash, or a per-step timeout — step rollback. Caller cancellation propagates as <see cref="OperationCanceledException"/> instead of producing a report.</description></item>
    /// </list>
    /// </summary>
    public int ExitCode { get; }

    /// <summary>The per-step results, in execution order. Empty when the run aborted at preflight.</summary>
    public IReadOnlyList<StepRunResult> Steps { get; }

    /// <summary>
    /// The validation diagnostics that aborted the run before execution. Empty
    /// when the run executed (whether or not a step later failed).
    /// </summary>
    public IReadOnlyList<ScriptDiagnostic> PreflightErrors { get; }
}

/// <summary>
/// Classifies how a step contributed to a <see cref="RunReport"/>. This is the
/// single source of truth for a step's run disposition: the boolean <see cref="StepRunResult.Ran"/> is
/// derived from it (<c>Status == Ran</c>). Only <see cref="Ran"/> steps can drive the exit code, so a
/// <see cref="Skipped"/> or <see cref="NotRunAfterHalt"/> step never affects it.
/// </summary>
public enum StepRunStatus
{
    /// <summary>The step was executed and produced a result (the full-run default).</summary>
    Ran,

    /// <summary>
    /// The step was skipped because its order is before the resume point of an
    /// <see cref="IPumpEngine.ExecuteFromAsync"/> run; it was never executed.
    /// </summary>
    Skipped,

    /// <summary>
    /// The step was recorded as not-run because an earlier step halted the run (<c>haltOnError</c>); it
    /// was reached but never executed.
    /// </summary>
    NotRunAfterHalt,
}

/// <summary>
/// One step's contribution to a <see cref="RunReport"/>: the script name, its result and effective
/// severity, whether it committed, the captured output and how it contributed to the run.
/// </summary>
/// <param name="ScriptName">The step's filename.</param>
/// <param name="Result">
/// The step's reported result (a synthesised <see cref="Severity.Error"/> result for a crash/timeout), or
/// <see langword="null"/> when the step did not run (see <see cref="Ran"/>).
/// </param>
/// <param name="EffectiveSeverity">
/// The maximum of the result severity and the highest note severity;
/// drives the rollback/exit-code decision.
/// </param>
/// <param name="Committed">Whether the step's slots were committed (<see langword="false"/> = rolled back / not run).</param>
/// <param name="Output">Captured stdout/stderr produced during the step (empty when not run).</param>
/// <param name="Status">
/// How the step contributed to the run, and the single source of truth for its disposition:
/// <see cref="StepRunStatus.Ran"/> (default — executed), <see cref="StepRunStatus.Skipped"/> (before a
/// resume point), or <see cref="StepRunStatus.NotRunAfterHalt"/> (reached but not executed because an
/// earlier step halted the run). Only <see cref="StepRunStatus.Ran"/> steps can affect the exit code.
/// </param>
public sealed record StepRunResult(
    string ScriptName,
    StepResult? Result,
    Severity EffectiveSeverity,
    bool Committed,
    string Output,
    StepRunStatus Status = StepRunStatus.Ran)
{
    /// <summary>
    /// <see langword="true"/> when the step actually executed (<see cref="Status"/> ==
    /// <see cref="StepRunStatus.Ran"/>); <see langword="false"/> when it was recorded as not-run (an
    /// earlier step halted the run via <c>haltOnError</c>, or it was skipped before a resume point).
    /// Derived from <see cref="Status"/> so the two can never disagree.
    /// </summary>
    public bool Ran => Status == StepRunStatus.Ran;
}
