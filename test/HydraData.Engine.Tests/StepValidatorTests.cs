// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T09.2 — <see cref="StepValidator"/> compile-only validation: Roslyn diagnostics mapped to
/// 1-based positions, PUMP001/PUMP010 detection, clean-script happy path, warnings-only
/// leaves <c>IsValid</c> true, and PUMP codes originate from <see cref="PumpDiagnostics"/> consts.
/// </summary>
public sealed class StepValidatorTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a temporary directory containing exactly one step file and returns a
    /// <see cref="ScriptContext"/> built from it. The caller provides the script text.
    /// </summary>
    private ScriptContext MakeContext(string scriptText, string fileName = "01_10_step.cs", bool unsafe_ = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hydradata-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var header = unsafe_
            ? $"// @unsafe: true\n{scriptText}"
            : scriptText;

        File.WriteAllText(Path.Combine(dir, fileName), header);
        return new DiscoveryService().Discover([dir]);
    }

    // ── clean script → IsValid true ───────────────────────────────────────────

    [Fact]
    public void Clean_script_IsValid_true_no_diagnostics()
    {
        var ctx = MakeContext("return Ok(\"all good\");");
        var report = new StepValidator().Validate(ctx);

        Assert.True(report.IsValid);
        Assert.Empty(report.Diagnostics);
    }

    // ── Roslyn typo → 1-based position + CS code + script name ───────────────

    [Fact]
    public void Typo_produces_error_diagnostic_with_1based_position()
    {
        // Exact layout (1-indexed):
        //   line 1: "var x = 1;"
        //   line 2: "Qery(\"x\");"   ← error at col 0 (0-based) → col 1 (1-based)
        const string scriptText =
            "var x = 1;\n" +
            "Qery(\"x\");";

        var ctx = MakeContext(scriptText, "01_20_typo.cs");
        var report = new StepValidator().Validate(ctx);

        Assert.False(report.IsValid);
        // Use First — there may be more than one CS diagnostic (e.g. CS0103 + CS1002).
        var diag = report.Diagnostics.First(d => d.Code.StartsWith("CS", StringComparison.Ordinal));

        // Script name is propagated.
        Assert.Equal("01_20_typo.cs", diag.ScriptName);

        // Roslyn reports line 1 (0-based) for the second line → 1-based line 2.
        Assert.Equal(2, diag.Line);

        // Roslyn reports col 0 (0-based) for the start of "Qery" → 1-based col 1.
        Assert.Equal(1, diag.Column);

        // Error code is a CS code.
        Assert.Matches(@"^CS\d{4}$", diag.Code);

        // Severity is Error.
        Assert.Equal(Severity.Error, diag.Severity);

        // Message is non-empty.
        Assert.NotEmpty(diag.Message);
    }

    [Fact]
    public void Single_line_typo_reports_correct_line_and_column()
    {
        // Error on the very first line, column 0 (0-based) → line 1, column 1 (1-based).
        const string scriptText = "Qery(\"x\");";
        var ctx = MakeContext(scriptText, "01_10_typo.cs");
        var report = new StepValidator().Validate(ctx);

        var errorDiag = report.Diagnostics
            .First(d => d.Code.StartsWith("CS", StringComparison.Ordinal));

        Assert.Equal(1, errorDiag.Line);
        Assert.Equal(1, errorDiag.Column);
    }

    [Fact]
    public void NonZero_column_typo_reports_correct_column()
    {
        // `return Qery("x");` — the identifier "Qery" starts at column 7 (0-based) → col 8 (1-based).
        const string scriptText = "return Qery(\"x\");";
        var ctx = MakeContext(scriptText, "01_10_col.cs");
        var report = new StepValidator().Validate(ctx);

        var errorDiag = report.Diagnostics
            .First(d => d.Code.StartsWith("CS", StringComparison.Ordinal));

        Assert.Equal(1, errorDiag.Line);
        // "return " is 7 characters; Roslyn col 7 (0-based) → col 8 (1-based).
        Assert.Equal(8, errorDiag.Column);
    }

    [Fact]
    public void Script_name_is_bare_filename_not_full_path()
    {
        var ctx = MakeContext("Qery(\"x\");", "01_10_myfile.cs");
        var report = new StepValidator().Validate(ctx);

        Assert.All(report.Diagnostics, d =>
            Assert.Equal("01_10_myfile.cs", d.ScriptName));
    }

    // ── PUMP001: #r "nuget:" not allowed ─────────────────────────────────────

    [Fact]
    public void PUMP001_produced_for_nuget_directive()
    {
        // The const is the single source — test against PumpDiagnostics.NuGetDirective.
        // Roslyn compile is skipped when PUMP001 fires → exactly ONE diagnostic total.
        const string scriptText = "#r \"nuget:Newtonsoft.Json\"\nreturn Ok();";
        var ctx = MakeContext(scriptText);
        var report = new StepValidator().Validate(ctx);

        Assert.Single(report.Diagnostics);
        var pump001 = report.Diagnostics[0];
        Assert.Equal(PumpDiagnostics.NuGetDirective, pump001.Code);
        Assert.Equal(Severity.Error, pump001.Severity);
        Assert.False(report.IsValid);
    }

    [Theory]
    [InlineData("#r \"nuget:Pkg\"")]                  // standard form
    [InlineData("#r \"nuget: Pkg\"")]                  // space after colon
    [InlineData("#r\"nuget:Pkg\"")]                    // no space between #r and quote
    [InlineData("#R \"nuget:x\"")]                     // uppercase #R
    public void PUMP001_produced_for_nuget_directive_variants(string directiveLine)
    {
        var scriptText = directiveLine + "\nreturn Ok();";
        var ctx = MakeContext(scriptText);
        var report = new StepValidator().Validate(ctx);

        // Exactly one diagnostic — PUMP001 only, no duplicate Roslyn error.
        Assert.Single(report.Diagnostics);
        Assert.Equal(PumpDiagnostics.NuGetDirective, report.Diagnostics[0].Code);
        Assert.False(report.IsValid);
    }

    [Fact]
    public void PUMP001_NOT_produced_when_nuget_in_comment()
    {
        // A commented-out directive must NOT trigger PUMP001; compile proceeds normally.
        const string scriptText = "// #r \"nuget:Foo\"\nreturn Ok();";
        var ctx = MakeContext(scriptText);
        var report = new StepValidator().Validate(ctx);

        Assert.DoesNotContain(report.Diagnostics,
            d => d.Code == PumpDiagnostics.NuGetDirective);
        // The script itself is valid — IsValid must be true.
        Assert.True(report.IsValid);
    }

    [Fact]
    public void PUMP001_NOT_produced_when_nuget_in_string_literal()
    {
        // A nuget reference inside a string literal must NOT trigger PUMP001.
        const string scriptText = "var s = \"#r \\\"nuget:Foo\\\"\";\nreturn Ok();";
        var ctx = MakeContext(scriptText);
        var report = new StepValidator().Validate(ctx);

        Assert.DoesNotContain(report.Diagnostics,
            d => d.Code == PumpDiagnostics.NuGetDirective);
        Assert.True(report.IsValid);
    }

    [Fact]
    public void PUMP001_code_comes_from_PumpDiagnostics_const()
    {
        // Ensures no magic string — the code value must equal the const.
        Assert.Equal("PUMP001", PumpDiagnostics.NuGetDirective);
    }

    [Fact]
    public void No_PUMP001_when_no_nuget_directive()
    {
        var ctx = MakeContext("return Ok();");
        var report = new StepValidator().Validate(ctx);

        Assert.DoesNotContain(report.Diagnostics,
            d => d.Code == PumpDiagnostics.NuGetDirective);
    }

    // ── PUMP010: @unsafe: true without engine grant ──────────────────────────

    [Fact]
    public void PUMP010_produced_when_unsafe_meta_set_without_grant()
    {
        // Script has @unsafe: true; validator created WITHOUT AllowUnsafeDirectAccess.
        var ctx = MakeContext("return Ok();", unsafe_: true);
        var report = new StepValidator(allowUnsafeDirectAccess: false).Validate(ctx);

        var pump010 = report.Diagnostics.SingleOrDefault(d => d.Code == PumpDiagnostics.UnsafeWithoutGrant);
        Assert.NotNull(pump010);
        Assert.Equal(Severity.Error, pump010!.Severity);
        Assert.False(report.IsValid);
    }

    [Fact]
    public void PUMP010_NOT_produced_when_engine_grants_unsafe()
    {
        // Script has @unsafe: true; validator created WITH AllowUnsafeDirectAccess = true.
        var ctx = MakeContext("return Ok();", unsafe_: true);
        var report = new StepValidator(allowUnsafeDirectAccess: true).Validate(ctx);

        Assert.DoesNotContain(report.Diagnostics,
            d => d.Code == PumpDiagnostics.UnsafeWithoutGrant);
    }

    [Fact]
    public void PUMP010_NOT_produced_when_unsafe_meta_is_false()
    {
        // Script does NOT have @unsafe: true; no PUMP010 regardless of engine flag.
        var ctx = MakeContext("return Ok();", unsafe_: false);
        var report = new StepValidator(allowUnsafeDirectAccess: false).Validate(ctx);

        Assert.DoesNotContain(report.Diagnostics,
            d => d.Code == PumpDiagnostics.UnsafeWithoutGrant);
    }

    [Fact]
    public void PUMP010_code_comes_from_PumpDiagnostics_const()
    {
        Assert.Equal("PUMP010", PumpDiagnostics.UnsafeWithoutGrant);
    }

    // ── PUMP000: CompileSetupGuard (internal fail-safe) ───────────────────────────

    [Fact]
    public void PUMP000_CompileSetupGuard_code_comes_from_PumpDiagnostics_const()
    {
        // Single source for the internal compile-setup fail-safe code (mirrors the PUMP001/PUMP010 const
        // tests). The runtime emission path in StepValidator.CompileScript is the catch-all that fires only
        // when CSharpScript.Create/Compile itself throws an UNEXPECTED exception (e.g. a misconfigured host
        // or missing Roslyn assemblies) — there is no deterministic, side-effect-free way to force Roslyn's
        // scripting setup to throw from a test without reflection hacks or breaking the loaded Roslyn
        // assemblies for the whole test host, so the runtime path is asserted only via this const guard.
        // The behaviour (Error severity, IsValid=false) is exercised structurally by the ValidationReport
        // tests below: any Error-severity diagnostic — PUMP000 included — flips IsValid to false.
        Assert.Equal("PUMP000", PumpDiagnostics.CompileSetupGuard);
    }

    [Fact]
    public void PUMP000_error_diagnostic_makes_report_invalid()
    {
        // Structural proof that a PUMP000 (CompileSetupGuard) Error diagnostic — were the catch-all to fire —
        // is fatal: IsValid is false. Uses the same const so there is no magic string.
        var pump000 = new ScriptDiagnostic(
            "01_10_step.cs", 0, 0, PumpDiagnostics.CompileSetupGuard, Severity.Error,
            "Internal error during compilation setup.");
        var report = new ValidationReport([pump000]);

        Assert.False(report.IsValid);
        Assert.Equal("PUMP000", Assert.Single(report.Diagnostics).Code);
    }

    // ── warnings only → IsValid stays true ───────────────────────────────────

    [Fact]
    public void Warning_only_diagnostics_keep_IsValid_true()
    {
        // CS8602 "Dereference of a possibly null reference" is a deterministic Roslyn warning
        // produced by CSharpScript.Compile when nullable analysis is enabled. IsValid must be
        // true because warnings do not constitute errors, and the warning must be present.
        const string scriptText =
            "#nullable enable\n" +
            "string? s = null;\n" +
            "var _ = s.Length;\n" +
            "return Ok();";
        var ctx = MakeContext(scriptText);
        var report = new StepValidator().Validate(ctx);

        // Unconditional assertions — no conditional guard.
        Assert.True(report.IsValid);
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning);
    }

    [Fact]
    public void IsValid_false_only_when_error_severity_present()
    {
        // A warning diagnostic should not flip IsValid.
        var warningOnly = new ScriptDiagnostic(
            "01_10_step.cs", 1, 1, "CS8321", Severity.Warning, "unused");
        var report = new ValidationReport([warningOnly]);

        Assert.True(report.IsValid);
        Assert.Single(report.Diagnostics);
    }

    [Fact]
    public void IsValid_false_when_error_diagnostic_present()
    {
        var error = new ScriptDiagnostic(
            "01_10_step.cs", 1, 1, "CS0103", Severity.Error, "name not found");
        var report = new ValidationReport([error]);

        Assert.False(report.IsValid);
    }

    // ── multiple scripts: each gets its own diagnostics ───────────────────────

    [Fact]
    public void Multiple_scripts_each_produce_diagnostics_with_correct_script_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hydradata-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        File.WriteAllText(Path.Combine(dir, "01_10_good.cs"), "return Ok();");
        File.WriteAllText(Path.Combine(dir, "01_20_bad.cs"), "Qery(\"x\");");

        var ctx = new DiscoveryService().Discover([dir]);
        var report = new StepValidator().Validate(ctx);

        Assert.False(report.IsValid);
        // All error diagnostics should name the bad script.
        var errDiags = report.Diagnostics.Where(d => d.Severity == Severity.Error).ToList();
        Assert.All(errDiags, d => Assert.Equal("01_20_bad.cs", d.ScriptName));
        // The good script produced no errors.
        Assert.DoesNotContain(report.Diagnostics,
            d => d.ScriptName == "01_10_good.cs" && d.Severity == Severity.Error);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
