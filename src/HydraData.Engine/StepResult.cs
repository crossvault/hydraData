// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// The result a step reports back to the engine: a <see cref="Severity"/>, a human-readable
/// message and optional structured details.
/// </summary>
/// <param name="Severity">The outcome severity.</param>
/// <param name="Message">A human-readable message describing the outcome.</param>
/// <param name="Details">Optional structured details (e.g. counts, offending rows).</param>
public sealed record StepResult(Severity Severity, string Message, object? Details = null)
{
    /// <summary>Creates a success result.</summary>
    public static StepResult Ok(string message = "OK", object? details = null) =>
        new(Severity.Success, message, details);

    /// <summary>Creates a warning result (the transaction still commits).</summary>
    public static StepResult Warn(string message, object? details = null) =>
        new(Severity.Warning, message, details);

    /// <summary>Creates an error result (the transaction is rolled back).</summary>
    public static StepResult Fail(string message, object? details = null) =>
        new(Severity.Error, message, details);
}
