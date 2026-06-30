// Copyright (c) 2026 crossVault GmbH.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HydraData.Engine;

/// <summary>
/// Performs compile-only validation of all scripts in a <see cref="ScriptContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// No script is executed; only compilation diagnostics are collected and mapped to
/// <see cref="ScriptDiagnostic"/> records.
/// </para>
/// <para>
/// Roslyn positions are 0-based; this validator adds 1 to both line and column when
/// constructing <see cref="ScriptDiagnostic"/> values (documented found-gap correction,
/// runtime contract). PUMP-specific diagnostics are placed at line 0, column 0.
/// Roslyn diagnostics whose location is not in source are also placed at line 0, column 0.
/// </para>
/// <para>
/// Two engine-owned diagnostics are detected:
/// <list type="bullet">
///   <item>
///     <term><see cref="PumpDiagnostics.NuGetDirective"/> (PUMP001)</term>
///     <description>
///     The script contains a line-anchored <c>#r "nuget:"</c> directive (the trimmed line
///     starts with <c>#r</c> and references nuget). A directive inside a comment or string
///     literal does NOT trigger this. When PUMP001 fires, the Roslyn compile step is skipped
///     for that script so there is exactly one diagnostic.
///     </description>
///   </item>
///   <item>
///     <term><see cref="PumpDiagnostics.UnsafeWithoutGrant"/> (PUMP010)</term>
///     <description>
///     The step meta declares <c>@unsafe: true</c> but the engine was not created with
///     <c>AllowUnsafeDirectAccess = true</c>.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class StepValidator
{
    // Matches a line whose trimmed content starts with #r (case-insensitive) followed by
    // optional whitespace, a double-quote, optional whitespace, "nuget" and a colon.
    // Examples matched: `#r "nuget:Pkg"`, `#r"nuget:Pkg"`, `#R "nuget : x"`, ` #r "nuget:Pkg"`.
    // A C# comment `// #r "nuget:Pkg"` is NOT matched because the trim starts with `//`.
    // A string literal containing #r "nuget:" is NOT matched because the line itself does not
    // start (after trimming) with #r.
    private static readonly Regex NuGetDirectivePattern =
        new(@"^\s*#r\s*""\s*nuget\s*:", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly bool _allowUnsafeDirectAccess;
    private readonly ScriptCompiler _compiler;
    private readonly ILogger _logger;

    /// <summary>
    /// Initialises a new <see cref="StepValidator"/>.
    /// </summary>
    /// <param name="allowUnsafeDirectAccess">
    /// When <see langword="true"/> the engine has granted unsafe access; steps with
    /// <c>@unsafe: true</c> meta will NOT produce a PUMP010 diagnostic.
    /// When <see langword="false"/> (the default), any step with <c>@unsafe: true</c> meta
    /// produces a PUMP010 error-severity diagnostic.
    /// </param>
    /// <param name="compiler">
    /// The shared <see cref="ScriptCompiler"/> whose cache the validation compile populates, so the compiled
    /// delegate is reused at execution time (no second Roslyn compile per run). Defaults to a private
    /// instance for stand-alone validation (no execution to share with).
    /// </param>
    /// <param name="logger">
    /// Diagnostic logger for the compile pass (Debug per script, Error on a failed validation).
    /// Defaults to <see cref="NullLogger.Instance"/>.
    /// </param>
    public StepValidator(bool allowUnsafeDirectAccess = false, ScriptCompiler? compiler = null, ILogger? logger = null)
    {
        _allowUnsafeDirectAccess = allowUnsafeDirectAccess;
        _compiler = compiler ?? new ScriptCompiler();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Validates all scripts in <paramref name="context"/> by compiling each one without
    /// executing it. Each step's file is read from disk within this call.
    /// </summary>
    /// <param name="context">The discovery context to validate.</param>
    /// <returns>A <see cref="ValidationReport"/> with all collected diagnostics.</returns>
    public ValidationReport Validate(ScriptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<ScriptDiagnostic>();

        foreach (var step in context.Steps)
        {
            _logger.LogDebug("Compiling step {ScriptName} for validation.", step.FileName);
            var scriptText = File.ReadAllText(step.FilePath);
            CollectStepDiagnostics(step, scriptText, diagnostics);
        }

        return BuildReport(diagnostics);
    }

    /// <summary>
    /// Validates a single step using already-read script text and RETURNS its report (avoids a second file
    /// read when the caller has already read the file for meta-parsing, preventing TOCTOU discrepancies
    /// between meta-parse and compile).
    /// </summary>
    /// <remarks>
    /// The Roslyn compile is routed through the shared <see cref="ScriptCompiler"/> cache, so a script that
    /// validates cleanly is compiled once and that delegate is reused at execution time.
    /// <see cref="PumpSession"/> uses this so that the meta-parse, the validation compile, and the execution
    /// compile all operate on the same bytes read from disk in a single call.
    /// </remarks>
    /// <param name="step">The step descriptor (its <see cref="StepDescriptor.Meta"/> is used for PUMP010).</param>
    /// <param name="scriptText">The script text already read from disk.</param>
    /// <returns>A <see cref="ValidationReport"/> with the diagnostics collected for this single step.</returns>
    internal ValidationReport ValidateStep(StepDescriptor step, string scriptText)
    {
        var diagnostics = new List<ScriptDiagnostic>();
        CollectStepDiagnostics(step, scriptText, diagnostics);
        return BuildReport(diagnostics);
    }

    /// <summary>
    /// Collects the diagnostics for one step (PUMP001 + PUMP010 + Roslyn compile) into
    /// <paramref name="diagnostics"/>. Shared by the batch <see cref="Validate"/> pass (one list across all
    /// steps) and the pure single-step <see cref="ValidateStep(StepDescriptor, string)"/>.
    /// </summary>
    private void CollectStepDiagnostics(StepDescriptor step, string scriptText, List<ScriptDiagnostic> diagnostics)
    {
        // PUMP001: line-anchored #r "nuget:" directive is not allowed.
        // When detected, Roslyn compilation is skipped for this script so there is
        // exactly one diagnostic (no duplicate Roslyn error from the failed directive).
        var hasPump001 = ContainsNuGetDirective(scriptText);
        if (hasPump001)
        {
            diagnostics.Add(new ScriptDiagnostic(
                ScriptName: step.FileName,
                Line: 0,
                Column: 0,
                Code: PumpDiagnostics.NuGetDirective,
                Severity: Severity.Error,
                Message: "Runtime NuGet resolution is not supported (#r \"nuget:\"). " +
                         "Add the package as a project reference instead (PUMP001)."));
        }

        // PUMP010: @unsafe: true without engine grant.
        if (step.Meta.Unsafe && !_allowUnsafeDirectAccess)
        {
            diagnostics.Add(new ScriptDiagnostic(
                ScriptName: step.FileName,
                Line: 0,
                Column: 0,
                Code: PumpDiagnostics.UnsafeWithoutGrant,
                Severity: Severity.Error,
                Message: "Step declares '@unsafe: true' but the engine was not created with " +
                         "AllowUnsafeDirectAccess = true. Both are required (PUMP010)."));
        }

        // Roslyn compile-only: skipped when PUMP001 was raised (the script cannot be
        // compiled without the package, and we have already recorded one diagnostic).
        if (!hasPump001)
        {
            CompileViaSharedCache(step.FileName, scriptText, diagnostics);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="scriptText"/> contains a
    /// line-anchored <c>#r "nuget:"</c> directive. The match is line-anchored: only a line
    /// whose trimmed content starts with <c>#r</c> (case-insensitive) qualifies. A directive
    /// inside a C# comment (<c>// #r "nuget:…"</c>) or inside a string literal is not matched.
    /// </summary>
    private static bool ContainsNuGetDirective(string scriptText) =>
        NuGetDirectivePattern.IsMatch(scriptText);

    /// <summary>
    /// Compiles the script text through the shared <see cref="ScriptCompiler"/> cache and appends its
    /// diagnostics to <paramref name="diagnostics"/>: warning/info diagnostics on a successful compile, or the
    /// full error+warning set on a compile failure (carried by <see cref="ScriptCompileException"/>). Routing
    /// through the shared compiler means a script that validates cleanly is Roslyn-compiled exactly once per
    /// run and that delegate is reused at execution time. An internal PUMP000 error diagnostic is appended
    /// when the compilation setup itself throws (so a misconfigured host cannot masquerade as a clean result).
    /// </summary>
    private void CompileViaSharedCache(string scriptName, string scriptText, List<ScriptDiagnostic> diagnostics)
    {
        try
        {
            // Success: surface warnings/info exactly as before. The compiled delegate is now cached for
            // execution, so this is the only Roslyn compile of this text for the whole run.
            foreach (var d in _compiler.GetDiagnostics(scriptText))
                diagnostics.Add(d with { ScriptName = scriptName });
        }
        catch (ScriptCompileException ex)
        {
            // Compile errors: the exception carries the same error+warning diagnostics validation produced
            // before (mapped with an empty script name) — re-stamp them with this step's file name.
            foreach (var d in ex.Diagnostics)
                diagnostics.Add(d with { ScriptName = scriptName });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Unexpected exception during compilation setup — emit an internal error diagnostic
            // so the caller sees IsValid=false rather than a silent empty result.
            diagnostics.Add(new ScriptDiagnostic(
                ScriptName: scriptName,
                Line: 0,
                Column: 0,
                Code: PumpDiagnostics.CompileSetupGuard,
                Severity: Severity.Error,
                Message: $"Internal error during compilation setup: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>Builds the final report and logs when validation failed.</summary>
    private ValidationReport BuildReport(List<ScriptDiagnostic> diagnostics)
    {
        var report = new ValidationReport(diagnostics.AsReadOnly());
        if (!report.IsValid)
            _logger.LogError("Validation failed with {ErrorCount} error diagnostic(s).",
                diagnostics.Count(d => d.Severity == Severity.Error));
        return report;
    }
}
