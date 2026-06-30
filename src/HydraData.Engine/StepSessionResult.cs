// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Classifies the outcome of a single
/// <see cref="PumpSession.RunStepAsync(StepOrder, IProgress{PumpProgress}?, CancellationToken)"/> call and is
/// the single source of truth for which payload a <see cref="StepSessionResult"/> carries.
/// </summary>
public enum StepSessionStatus
{
    /// <summary>
    /// No step matching the requested order existed in the session's <see cref="ScriptContext"/>; nothing
    /// was validated or executed. <see cref="StepSessionResult.Message"/> describes the miss.
    /// </summary>
    NotFound,

    /// <summary>
    /// A step was found but was NOT executed: either it failed re-validation (compile / PUMP001 / PUMP010 —
    /// <see cref="StepSessionResult.Validation"/> carries the diagnostics) or its file could not be read
    /// between calls (<see cref="StepSessionResult.Message"/> describes the read failure).
    /// </summary>
    NotValidated,

    /// <summary>
    /// The step was found, passed re-validation, and executed; <see cref="StepSessionResult.Result"/> holds
    /// its outcome.
    /// </summary>
    Ran,
}

/// <summary>
/// The result of a single <see cref="PumpSession.RunStepAsync(StepOrder, IProgress{PumpProgress}?, CancellationToken)"/>
/// call. <see cref="Status"/> is the single source of truth: the carried payload is exactly the one valid for
/// that status, removing the earlier <c>Found</c>/<c>Validated</c>/nullable-<c>Result</c> combinations that
/// could disagree.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>RunStepAsync</c> call re-reads and re-validates the step file from disk (so edits made between
/// calls are picked up). Construct instances via the <see cref="NotFound"/>, <see cref="Invalid"/> and
/// <see cref="Ran"/> factories rather than the primary constructor.
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <see cref="StepSessionStatus.NotFound"/> — the order was unknown; <see cref="Found"/> is
///     <see langword="false"/>, <see cref="Result"/> is <see langword="null"/>, <see cref="Message"/>
///     describes the miss.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="StepSessionStatus.NotValidated"/> — the step was found but not executed (validation
///     failed or the file was unreadable); <see cref="Result"/> is <see langword="null"/>,
///     <see cref="Validation"/> carries any failing diagnostics, and <see cref="Message"/> is set for a
///     read failure.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="StepSessionStatus.Ran"/> — the step validated and executed; <see cref="Result"/> is
///     non-<see langword="null"/>.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed record StepSessionResult
{
    // The primary constructor is private so the only way to build a result is through the factories
    // below — that makes the "payload valid for the status" invariant (e.g. Result non-null iff Ran)
    // type-enforced, not merely a convention a hand-rolled `new` could violate.
    private StepSessionResult(
        StepSessionStatus status,
        ValidationReport validation,
        StepRunResult? result,
        string? message = null)
    {
        Status = status;
        Validation = validation;
        Result = result;
        Message = message;
    }

    /// <summary>The outcome classification and single source of truth for the carried payload.</summary>
    public StepSessionStatus Status { get; }

    /// <summary>
    /// The single-step validation report for this call. Carries the error diagnostics for a validation
    /// failure; may contain warnings (no errors) on success; empty when the step was not found or unreadable
    /// (those outcomes are described by <see cref="Message"/> instead).
    /// </summary>
    public ValidationReport Validation { get; }

    /// <summary>
    /// The step's outcome when <see cref="Status"/> is <see cref="StepSessionStatus.Ran"/>; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public StepRunResult? Result { get; }

    /// <summary>
    /// A human-readable explanation for the non-script outcomes — an unknown step order or a file that could
    /// not be read between calls. <see langword="null"/> when the step was found and validated (success or
    /// script-diagnostic failure, where <see cref="Validation"/> carries the detail).
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// <see langword="true"/> when a step matching the requested order existed (any status other than
    /// <see cref="StepSessionStatus.NotFound"/>). Derived from <see cref="Status"/>.
    /// </summary>
    public bool Found => Status != StepSessionStatus.NotFound;

    /// <summary>
    /// <see langword="true"/> when the step compiled, passed the PUMP gates and was executed
    /// (<see cref="Status"/> == <see cref="StepSessionStatus.Ran"/>). Derived from <see cref="Status"/>.
    /// </summary>
    public bool Validated => Status == StepSessionStatus.Ran;

    /// <summary>Builds a <see cref="StepSessionStatus.NotFound"/> result for an unknown step order.</summary>
    /// <param name="message">A human-readable description of the miss.</param>
    public static StepSessionResult NotFound(string message) =>
        new(StepSessionStatus.NotFound, new ValidationReport([]), result: null, message: message);

    /// <summary>
    /// Builds a <see cref="StepSessionStatus.NotValidated"/> result: the step was found but not executed
    /// because validation failed (the report carries the diagnostics).
    /// </summary>
    /// <param name="validation">The failing single-step validation report.</param>
    public static StepSessionResult Invalid(ValidationReport validation) =>
        new(StepSessionStatus.NotValidated, validation, result: null);

    /// <summary>
    /// Builds a <see cref="StepSessionStatus.NotValidated"/> result for a step whose file could not be read
    /// between calls (no script diagnostics; the reason is in <paramref name="message"/>).
    /// </summary>
    /// <param name="message">A human-readable description of the read failure.</param>
    public static StepSessionResult Unreadable(string message) =>
        new(StepSessionStatus.NotValidated, new ValidationReport([]), result: null, message: message);

    /// <summary>
    /// Builds a <see cref="StepSessionStatus.Ran"/> result for a step that validated and executed.
    /// </summary>
    /// <param name="validation">The (passing) single-step validation report; may carry warnings.</param>
    /// <param name="result">The step's execution outcome.</param>
    public static StepSessionResult Ran(ValidationReport validation, StepRunResult result) =>
        new(StepSessionStatus.Ran, validation, result);
}
