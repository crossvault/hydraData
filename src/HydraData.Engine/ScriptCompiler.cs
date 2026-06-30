// Copyright (c) 2026 crossVault GmbH.

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

// Roslyn DiagnosticSeverity is used internally only; the public surface exposes the engine's Severity.
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace HydraData.Engine;

/// <summary>
/// Thrown when a script fails to compile. Carries structured diagnostics so callers (validation,
/// the runner) can surface precise errors without a dependency on Microsoft.CodeAnalysis.
/// </summary>
public sealed class ScriptCompileException : Exception
{
    /// <summary>Initializes a new instance with the failing diagnostics.</summary>
    /// <param name="message">A summary message.</param>
    /// <param name="diagnostics">The mapped compilation diagnostics (errors and warnings).</param>
    public ScriptCompileException(string message, IReadOnlyList<ScriptDiagnostic> diagnostics)
        : base(message) => Diagnostics = diagnostics;

    /// <summary>The compilation diagnostics that caused the failure.</summary>
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Compiles step scripts into reusable <see cref="ScriptRunner{TResult}"/> delegates and caches them
/// per unique script text. Each distinct text is compiled exactly once;
/// repeated runs of the same text hit the cache. The cache is instance-local for test isolation
///.
/// </summary>
public sealed class ScriptCompiler
{
    // A cached compiled script: the executable runner plus the diagnostics (warnings/info) produced by a
    // successful compile. Validation reads the diagnostics; execution reads the runner — both off the same
    // single compile.
    private sealed record Compiled(ScriptRunner<StepResult> Runner, IReadOnlyList<ScriptDiagnostic> Diagnostics);

    // The script text IS the cache key (ordinal comparison) — a ConcurrentDictionary already compares keys by
    // value, so there is no need to hash the text into a separate key.
    // ConcurrentDictionary.GetOrAdd(key, factory) may invoke the factory more than once under concurrent
    // access for the same key. Wrapping the value in Lazy<T> ensures the compile body runs at most once
    // per key even when N threads race on the same script text simultaneously.
    private readonly ConcurrentDictionary<string, Lazy<Compiled>> _cache = new(StringComparer.Ordinal);
    private int _compileCount;

    /// <summary>
    /// The number of distinct compilations performed by this instance. Increments once per script text
    /// that is compiled; a cache hit does not increment it (observable for tests, T02.4).
    /// </summary>
    public int CompileCount => Volatile.Read(ref _compileCount);

    /// <summary>
    /// Returns the cached runner for <paramref name="code"/>, compiling and caching it on first use.
    /// </summary>
    /// <param name="code">The script source text.</param>
    /// <returns>A compiled runner producing a <see cref="StepResult"/>.</returns>
    /// <exception cref="ScriptCompileException">Compilation produced errors.</exception>
    public ScriptRunner<StepResult> GetRunner(string code) => GetOrCompile(code).Runner;

    /// <summary>
    /// Compiles <paramref name="code"/> through the shared cache — so the delegate produced here is the same
    /// one <see cref="GetRunner"/> returns at execution time (each distinct text is Roslyn-compiled exactly
    /// once per run) — and returns the non-error diagnostics (warnings/info). A compile error throws
    /// <see cref="ScriptCompileException"/> carrying the full error+warning diagnostics, exactly like
    /// <see cref="GetRunner"/>. Used by <see cref="StepValidator"/> so validation and execution share one compile.
    /// </summary>
    /// <param name="code">The script source text.</param>
    /// <returns>The warning/info diagnostics produced by a successful compile (empty for a clean script).</returns>
    /// <exception cref="ScriptCompileException">Compilation produced errors.</exception>
    internal IReadOnlyList<ScriptDiagnostic> GetDiagnostics(string code) => GetOrCompile(code).Diagnostics;

    private Compiled GetOrCompile(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        // LazyThreadSafetyMode.ExecutionAndPublication ensures exactly one thread runs the factory
        // body per key even under high concurrency; all other threads block then share the result.
        var lazy = _cache.GetOrAdd(code,
            c => new Lazy<Compiled>(() => Compile(c), LazyThreadSafetyMode.ExecutionAndPublication));

        // If the Lazy factory threw (compile error), propagate the exception to the caller and do NOT
        // leave a poisoned Lazy in the cache — remove the entry so the next caller gets a fresh attempt.
        try
        {
            return lazy.Value;
        }
        catch
        {
            _cache.TryRemove(code, out _);
            throw;
        }
    }

    private Compiled Compile(string code)
    {
        var script = CSharpScript.Create<StepResult>(code, ScriptHost.Options, typeof(PumpContext));

        var roslynDiagnostics = script.Compile();
        var mapped = roslynDiagnostics.Select(d => MapDiagnostic(d, scriptName: string.Empty)).ToList().AsReadOnly();
        var errors = roslynDiagnostics.Where(d => d.Severity == RoslynSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            throw new ScriptCompileException(
                $"Script compilation failed with {errors.Length} error(s): {errors[0].GetMessage()}",
                mapped);
        }

        var runner = script.CreateDelegate();
        Interlocked.Increment(ref _compileCount);
        return new Compiled(runner, mapped);
    }

    /// <summary>
    /// Maps a Roslyn <see cref="Diagnostic"/> to a <see cref="ScriptDiagnostic"/>.
    /// Positions are converted from 0-based (Roslyn) to 1-based; diagnostics without a source
    /// location are placed at line 0, column 0 (file-level sentinel).
    /// </summary>
    internal static ScriptDiagnostic MapDiagnostic(Diagnostic d, string scriptName)
    {
        int line, column;
        if (d.Location.IsInSource)
        {
            var span = d.Location.GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            column = span.StartLinePosition.Character + 1;
        }
        else
        {
            line = 0;
            column = 0;
        }

        var severity = d.Severity switch
        {
            RoslynSeverity.Error => Severity.Error,
            RoslynSeverity.Warning => Severity.Warning,
            _ => Severity.Success,
        };

        return new ScriptDiagnostic(
            ScriptName: scriptName,
            Line: line,
            Column: column,
            Code: d.Id,
            Severity: severity,
            Message: d.GetMessage());
    }
}
