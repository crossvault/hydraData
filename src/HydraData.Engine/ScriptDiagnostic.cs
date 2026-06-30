// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// A structured diagnostic produced during script validation.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn positions are 0-based; all positions stored here are <b>1-based</b>
/// (add 1 to the Roslyn line and column when constructing this record).
/// PUMP-codes (e.g. <see cref="PumpDiagnostics.NuGetDirective"/>) are placed at
/// line 0, column 0 to indicate a file-level (not position-specific) diagnostic.
/// </para>
/// <para>
/// <b>File-level sentinel:</b> <c>Line == 0 &amp;&amp; Column == 0</c> means the diagnostic
/// applies to the whole script file rather than to a specific source location.
/// This convention is used for all PUMP-code diagnostics. Roslyn diagnostics whose
/// location is not in source (e.g. assembly-level errors) are also mapped to 0/0.
/// </para>
/// </remarks>
/// <param name="ScriptName">
/// The bare filename of the script (e.g. <c>01_20_validieren.cs</c>).
/// </param>
/// <param name="Line">
/// 1-based line number. Use <c>0</c> for diagnostics that are not tied to a specific line
/// (e.g. PUMP001, PUMP010).
/// </param>
/// <param name="Column">
/// 1-based column number. Use <c>0</c> for diagnostics that are not tied to a specific column.
/// </param>
/// <param name="Code">
/// The diagnostic code: a Roslyn CS-code (e.g. <c>CS0103</c>) or a PUMP-code
/// (e.g. <see cref="PumpDiagnostics.NuGetDirective"/>).
/// </param>
/// <param name="Severity">
/// The severity of the diagnostic. Only <see cref="Severity.Error"/> makes
/// <see cref="ValidationReport.IsValid"/> false; warnings do not.
/// </param>
/// <param name="Message">Human-readable explanation of the diagnostic.</param>
public sealed record ScriptDiagnostic(
    string ScriptName,
    int Line,
    int Column,
    string Code,
    Severity Severity,
    string Message);
