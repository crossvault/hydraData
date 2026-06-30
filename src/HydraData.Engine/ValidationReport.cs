// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// The result of a <see cref="StepValidator"/> compile-only validation pass over a
/// <see cref="ScriptContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsValid"/> is <see langword="true"/> when no <see cref="ScriptDiagnostic"/>
/// with <see cref="Severity.Error"/> severity is present. Warning-level diagnostics
/// do not affect <see cref="IsValid"/>.
/// </para>
/// </remarks>
public sealed class ValidationReport
{
    /// <summary>
    /// <see langword="true"/> when no error-severity diagnostic was produced; warnings do not
    /// affect this value.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// All diagnostics (errors and warnings) produced during the validation pass, ordered by
    /// script name and then by line.
    /// </summary>
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; }

    internal ValidationReport(IReadOnlyList<ScriptDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
        IsValid = diagnostics.All(d => d.Severity != Severity.Error);
    }
}
