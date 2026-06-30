// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// The phase a <see cref="PumpProgress"/> message reports. Reported
/// synchronously and in order via <see cref="IProgress{T}"/>; granularity is deliberately step-level
/// (v1), extensible by adding enum members (YAGNI).
/// </summary>
public enum PumpPhase
{
    /// <summary>Discovery completed; the script context is known. Reported once at the start of a run.</summary>
    Discovered,

    /// <summary>Preflight validation completed. Reported once after <c>Validate</c>.</summary>
    Validated,

    /// <summary>A step is about to run. <see cref="PumpProgress.ScriptName"/> identifies it.</summary>
    StepStarted,

    /// <summary>A step produced captured output. <see cref="PumpProgress.Message"/> carries the text.</summary>
    StepOutput,

    /// <summary>A step finished. <see cref="PumpProgress.Result"/> carries its result.</summary>
    StepFinished,

    /// <summary>The whole run finished. Reported once at the end.</summary>
    RunFinished,
}

/// <summary>
/// An interim run state streamed via <see cref="IProgress{T}"/> during <see cref="IPumpEngine.ExecuteAsync"/>
///. Progress is reported synchronously because order matters; it provides
/// interim states only and does <em>not</em> duplicate the final <see cref="RunReport"/>.
/// </summary>
/// <param name="Phase">The phase this message reports.</param>
/// <param name="ScriptName">
/// The step's filename, set for the per-step phases (<see cref="PumpPhase.StepStarted"/>,
/// <see cref="PumpPhase.StepOutput"/>, <see cref="PumpPhase.StepFinished"/>); otherwise <see langword="null"/>.
/// </param>
/// <param name="Result">
/// The step's result; set only at <see cref="PumpPhase.StepFinished"/>, otherwise <see langword="null"/>.
/// </param>
/// <param name="Message">
/// Free-text payload; set at <see cref="PumpPhase.StepOutput"/> (captured output), otherwise <see langword="null"/>.
/// </param>
public sealed record PumpProgress(
    PumpPhase Phase,
    string? ScriptName = null,
    StepResult? Result = null,
    string? Message = null);
