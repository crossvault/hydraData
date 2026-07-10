// Copyright (c) 2026 crossVault GmbH.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HydraData.Engine;

/// <summary>
/// The embedded orchestration keystone. Implements <see cref="IPumpEngine"/>:
/// validates a discovered <see cref="ScriptContext"/> compile-only, then executes its steps in order with
/// group-local <c>State</c>, run-global <c>Shared</c>, a per-step timeout, the transaction policy and
/// captured output, producing a single <see cref="RunReport"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Construction-time options.</b> <see cref="PumpOptions"/> is passed to the constructor and applies to
/// all runs of the instance; it is not a parameter of <see cref="ExecuteAsync"/>.
/// </para>
/// <para>
/// <b>RunId before validate.</b> The <c>RunId</c> is generated (via <see cref="IGuidProvider"/>) at the very
/// start of <see cref="ExecuteAsync"/>, before validation, so logs/reports correlate even when preflight
/// fails.
/// </para>
/// <para>
/// <b>State scoping.</b> <c>State</c> is group-local: a fresh <see cref="PumpState"/> is created when the
/// group number (GG) changes. <c>Shared</c> is one run-global bag. With
/// <see cref="PumpOptions.LegacyGlobalState"/> set, a single run-global <see cref="PumpState"/> is used for
/// <c>State</c> too (legacy behaviour).
/// </para>
/// <para>
/// <b>Connection selection.</b> Each step is wired with the connection directory's
/// <see cref="IConnectionDirectory.Default"/> as its default (implicit <c>CurrentConnection</c>), and with the
/// directory itself so a step can switch connections in-script via the <c>GetConnection</c> overloads and the
/// connection-targeted DB methods. A directory with no connection yields a
/// <see langword="null"/> default, so file-only steps still run and a no-arg DB call without a connection fails
/// fast at run time. If resolving <c>Default</c> throws, that is treated as a missing-connection preflight
/// error (exit code 1).
/// </para>
/// <para>
/// <b>haltOnError.</b> When a step's effective severity is <see cref="Severity.Error"/> and the step's
/// <c>@haltOnError</c> meta is <see langword="true"/> (the default), the run stops: the remaining steps are
/// recorded as not-run. With <c>@haltOnError: false</c> the run continues so later steps still execute; the
/// exit code still reflects the error. Cancellation always stops the run and propagates.
/// </para>
/// <para>
/// <b>No Spectre.</b> The engine does not reference Spectre.Console (it is Host-only). Scripts running in the
/// embedded engine therefore cannot call Spectre at all — the "no interactive Spectre" guarantee holds by
/// construction. The <c>Table</c> API renders a pure ASCII table instead.
/// </para>
/// </remarks>
public sealed class PumpEngine : IPumpEngine
{
    private readonly PumpOptions _options;
    private readonly IGuidProvider _guidProvider;
    private readonly TimeProvider _timeProvider;
    private readonly IConnectionGateway _gateway;
    private readonly ILogger _logger;
    private readonly ScriptCompiler _compiler = new();

    /// <summary>Initialises a new <see cref="PumpEngine"/>.</summary>
    /// <param name="options">Construction-time options applied to all runs.</param>
    /// <param name="guidProvider">RunId source. Defaults to <see cref="SystemGuidProvider.Instance"/>.</param>
    /// <param name="timeProvider">Per-step timeout clock. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Diagnostic logger. Defaults to <see cref="NullLogger.Instance"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public PumpEngine(
        PumpOptions options,
        IGuidProvider? guidProvider = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : this(options, guidProvider, timeProvider, gateway: null, logger)
    {
    }

    /// <summary>
    /// Internal constructor that also accepts the (internal) database gateway, used by tests with a fake
    /// gateway. The production gateway (<see cref="ConnectionGateway"/>) is used when <paramref name="gateway"/>
    /// is <see langword="null"/>.
    /// </summary>
    /// <param name="options">Construction-time options applied to all runs.</param>
    /// <param name="guidProvider">RunId source. Defaults to <see cref="SystemGuidProvider.Instance"/>.</param>
    /// <param name="timeProvider">Per-step timeout clock. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="gateway">Database gateway. Defaults to the production <see cref="ConnectionGateway"/>.</param>
    /// <param name="logger">Diagnostic logger. Defaults to <see cref="NullLogger.Instance"/>.</param>
    internal PumpEngine(
        PumpOptions options,
        IGuidProvider? guidProvider,
        TimeProvider? timeProvider,
        IConnectionGateway? gateway,
        ILogger? logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _guidProvider = guidProvider ?? SystemGuidProvider.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _gateway = gateway ?? new ConnectionGateway();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public ValidationReport Validate(ScriptContext ctx, ExternContext externCtx, IConnectionDirectory connections)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(externCtx);
        ArgumentNullException.ThrowIfNull(connections);

        var validator = new StepValidator(_options.AllowUnsafeDirectAccess, _compiler, _logger);
        return validator.Validate(ctx);
    }

    /// <inheritdoc />
    public Task<RunReport> ExecuteAsync(
        ScriptContext ctx,
        ExternContext externCtx,
        IConnectionDirectory connections,
        IProgress<PumpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(externCtx);
        ArgumentNullException.ThrowIfNull(connections);

        // Full run: execute every step (no resume point).
        return RunCoreAsync(ctx, externCtx, connections, resumeFrom: null, progress, ct);
    }

    /// <inheritdoc />
    public Task<RunReport> ExecuteFromAsync(
        ScriptContext ctx,
        ExternContext externCtx,
        IConnectionDirectory connections,
        StepOrder resumeFrom,
        IProgress<PumpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(externCtx);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(resumeFrom);

        // Resume run: validate ALL steps (preflight is whole-context), but execute only steps whose
        // order is at or after resumeFrom; earlier steps are recorded Skipped.
        return RunCoreAsync(ctx, externCtx, connections, resumeFrom, progress, ct);
    }

    /// <summary>
    /// The single shared run-core for the full run (<see cref="ExecuteAsync"/>, <paramref name="resumeFrom"/>
    /// = <see langword="null"/>) and the resume run (<see cref="ExecuteFromAsync"/>). It owns the one source
    /// of the RunId-before-validate ordering, the workspace, the validate-all preflight (incl. PUMP020), the
    /// Discovered/Validated/RunFinished progress phases, the group-<c>State</c> rollover, the transaction and
    /// haltOnError policy and the exit-code computation. The only resume-specific behaviour is the per-step
    /// gate: steps with <c>order &lt; resumeFrom</c> are recorded <see cref="StepRunStatus.Skipped"/> and not
    /// executed. With <paramref name="resumeFrom"/> = <see langword="null"/> the behaviour is byte-identical
    /// to the original full run.
    /// </summary>
    private async Task<RunReport> RunCoreAsync(
        ScriptContext ctx,
        ExternContext externCtx,
        IConnectionDirectory connections,
        StepOrder? resumeFrom,
        IProgress<PumpProgress>? progress,
        CancellationToken ct)
    {
        // RunId is generated BEFORE validation so logs/reports correlate even on preflight failure.
        var runId = _guidProvider.NewGuid();

        using var runScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["RunId"] = runId,
        });

        _logger.LogInformation("Run {RunId} starting with {StepCount} step(s).", runId, ctx.Steps.Count);
        progress?.Report(new PumpProgress(PumpPhase.Discovered));

        // The workspace (RunDir) is created up-front, after RunId, before execution.
        var workspace = new Workspace(_options.WorkspaceBase, runId, _options.Folders, _logger);

        // ── Read every script ONCE ───────────────────────────────────────────────
        // The same text feeds preflight validation AND execution, so each file is read exactly once per run
        // (mirrors PumpSession). A per-file read failure is captured here and surfaced at the step's execution
        // slot below as a Ran/Error step — never an uncaught throw. The read is not tied to the cancellation
        // token (it mirrors the old, non-cancellable preflight read); cancellation is observed during execution.
        var steps = ctx.Steps.ToArray();
        var scriptTexts = new string?[ctx.Steps.Count];
        var readErrors = new string?[ctx.Steps.Count];
        for (int i = 0; i < ctx.Steps.Count; i++)
        {
            try
            {
                var scriptText = await File.ReadAllTextAsync(ctx.Steps[i].FilePath).ConfigureAwait(false);
                scriptTexts[i] = scriptText;
                steps[i] = ctx.Steps[i] with { Meta = StepMeta.Parse(scriptText) };
            }
            catch (Exception ioEx) when (ioEx is IOException or UnauthorizedAccessException)
            {
                readErrors[i] = ioEx.Message;
                _logger.LogError("Could not read script {ScriptName} (run {RunId}): {Message}",
                    ctx.Steps[i].FileName, runId, ioEx.Message);
            }
        }

        // ── Preflight (Validate) ────────────────────────────────────────────────
        // Validate every readable step from the already-read text, routed through the shared ScriptCompiler
        // cache so a validated script's compiled delegate is reused at execution time (no second Roslyn
        // compile). A step whose file could not be read is not validated here; its read failure is recorded
        // at its execution slot below.
        var validator = new StepValidator(_options.AllowUnsafeDirectAccess, _compiler, _logger);
        var diagnostics = new List<ScriptDiagnostic>();
        for (int i = 0; i < ctx.Steps.Count; i++)
        {
            if (readErrors[i] is not null) continue;
            diagnostics.AddRange(validator.ValidateStep(steps[i], scriptTexts[i]!).Diagnostics);
        }
        var report = new ValidationReport(diagnostics.AsReadOnly());
        progress?.Report(new PumpProgress(PumpPhase.Validated));

        var preflightErrors = new List<ScriptDiagnostic>(report.Diagnostics.Where(d => d.Severity == Severity.Error));

        // Missing connection is a preflight error: in v1 every step is wired with the
        // directory's Default connection, so a directory that cannot resolve a Default while there are steps
        // to run is a misconfiguration caught before execution. A run with no steps needs no connection.
        ConnectionInfo? defaultConnection = null;
        if (preflightErrors.Count == 0 && ctx.Steps.Count > 0)
        {
            try
            {
                defaultConnection = ResolveDefaultConnection(connections);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not ThreadAbortException)
            {
                // Any non-fatal exception from IConnectionDirectory.Default is wrapped as PUMP020 so
                // the caller always sees a clean preflight error rather than a raw exception escaping
                // ExecuteAsync. The expected cases are InvalidOperationException (no Default configured)
                // and ArgumentException, but a poorly-behaved directory may throw anything.
                preflightErrors.Add(new ScriptDiagnostic(
                    ScriptName: string.Empty,
                    Line: 0,
                    Column: 0,
                    Code: PumpDiagnostics.MissingConnection,
                    Severity: Severity.Error,
                    Message: $"Preflight: the default connection could not be resolved: {ex.Message}"));
            }
        }

        if (preflightErrors.Count > 0)
        {
            _logger.LogError("Run {RunId} aborted at preflight with {ErrorCount} error(s); no step executed.",
                runId, preflightErrors.Count);
            progress?.Report(new PumpProgress(PumpPhase.RunFinished));
            return new RunReport(runId, exitCode: 1, steps: [], preflightErrors: preflightErrors);
        }

        // ── Execute ──────────────────────────────────────────────────────────────
        var shared = new PumpState();
        // Group-local State: a fresh bag per group; or one run-global bag in legacy mode.
        // INVARIANT: ctx.Steps are globally sorted by (Group, Step) — guaranteed by DiscoveryService.
        // The engine detects group changes by a strict monotonic increase; it does not re-sort here.
        var globalState = _options.LegacyGlobalState ? new PumpState() : null;
        PumpState? groupState = null;
        int? currentGroup = null;

        var results = new List<StepRunResult>(steps.Length);
        bool halted = false;

        // Single source of truth for the per-step tx/capture/state wiring (S1.0), shared with the
        // future step-session. The engine still owns the loop, group-State rollover, halt control,
        // exit code, and the Discovered/Validated/RunFinished progress phases.
        var executor = new StepExecutor(_options, _compiler, _gateway, _timeProvider, workspace, _logger);

        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];

            // Resume gate: a step before the resume point is recorded Skipped and never runs.
            // EffectiveSeverity is Success because the step has no severity of its own; ComputeExitCode
            // gates on Status==Ran, so a Skipped step never affects the exit code. For the full run
            // (resumeFrom == null) this branch is never taken — behaviour is unchanged.
            if (resumeFrom is not null && step.Order.CompareTo(resumeFrom) < 0)
            {
                results.Add(new StepRunResult(
                    step.FileName, Result: null, Severity.Success, Committed: false, Output: string.Empty,
                    Status: StepRunStatus.Skipped));
                continue;
            }

            if (halted)
            {
                // An earlier Error step with haltOnError stopped the run: record the rest as not-run.
                // EffectiveSeverity is Success (not Error) because the step did not run — it has no
                // severity of its own. ComputeExitCode gates on Status==Ran, so Success here does not
                // affect the exit code (which is already driven by the step that triggered the halt).
                results.Add(new StepRunResult(
                    step.FileName, Result: null, Severity.Success, Committed: false, Output: string.Empty,
                    Status: StepRunStatus.NotRunAfterHalt));
                continue;
            }

            // Group-local State: roll a new bag when the group changes (unless legacy global state).
            if (_options.LegacyGlobalState)
            {
                groupState = globalState!;
            }
            else if (currentGroup != step.Order.Group || groupState is null)
            {
                groupState = new PumpState();
                currentGroup = step.Order.Group;
            }

            // A file that could not be read in the single up-front pass is recorded here as a Ran/Error step
            // (never an uncaught throw), so the exit-code contract and the "RunFinished always emitted"
            // guarantee (B1) hold. This is reached only for executing steps — a read failure on a Skipped
            // (resume gate) or NotRunAfterHalt step is ignored above. The executor itself never reads files.
            if (readErrors[i] is { } readError)
            {
                // Mirror the per-step progress shape of a step that started: StepStarted then StepFinished
                // with the read-failure result. No StepOutput is reported — the file was never read.
                progress?.Report(new PumpProgress(PumpPhase.StepStarted, ScriptName: step.FileName));
                progress?.Report(new PumpProgress(PumpPhase.StepFinished, ScriptName: step.FileName,
                    Result: StepResult.Fail($"Script file could not be read: {readError}")));
                results.Add(new StepRunResult(
                    step.FileName,
                    StepResult.Fail($"Script file could not be read: {readError}"),
                    Severity.Error,
                    Committed: false,
                    Output: string.Empty));
                halted = step.Meta.HaltOnError;
                continue;
            }

            StepRunResult result;
            bool halt;
            try
            {
                (result, halt) = await executor.RunOneAsync(
                    step,
                    scriptTexts[i]!,
                    groupState,
                    shared,
                    externCtx,
                    defaultConnection,
                    connections,
                    progress,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller cancellation: the step already rolled back. Record it and re-throw (exit 2 semantics
                // would apply, but the documented policy is to propagate so the host sees the cancellation).
                _logger.LogError("Run {RunId} cancelled during step {ScriptName}; rolled back.", runId, step.FileName);
                results.Add(new StepRunResult(
                    step.FileName,
                    StepResult.Fail("Step cancelled by caller."),
                    Severity.Error,
                    Committed: false,
                    Output: string.Empty));
                progress?.Report(new PumpProgress(PumpPhase.RunFinished));
                throw;
            }

            results.Add(result);
            halted = halt;
        }

        var exitCode = ComputeExitCode(results);
        _logger.LogInformation("Run {RunId} finished with exit code {ExitCode}.", runId, exitCode);
        progress?.Report(new PumpProgress(PumpPhase.RunFinished));

        return new RunReport(runId, exitCode, results, preflightErrors: []);
    }

    /// <summary>
    /// Resolves the step's default connection: the directory's <see cref="IConnectionDirectory.Default"/>.
    /// Throws when no connection is configured so the caller can record a missing-connection preflight error.
    /// </summary>
    private static ConnectionInfo ResolveDefaultConnection(IConnectionDirectory connections) =>
        ConnectionInfo.AsConnectionInfo(connections.Default);

    /// <summary>
    /// Computes the canonical exit code from the per-step results:
    /// 2 when any step that actually ran reached <see cref="Severity.Error"/>; otherwise 0. Only
    /// <see cref="StepRunStatus.Ran"/> steps count — <see cref="StepRunStatus.Skipped"/> (before a resume
    /// point, design contract) and <see cref="StepRunStatus.NotRunAfterHalt"/> steps never make the run a failure.
    /// Exit code 1 is decided earlier (preflight) and never reaches this method.
    /// </summary>
    private static int ComputeExitCode(IReadOnlyList<StepRunResult> results)
    {
        foreach (var r in results)
        {
            if (r.Status == StepRunStatus.Ran && r.EffectiveSeverity >= Severity.Error)
                return 2;
        }

        return 0;
    }
}
