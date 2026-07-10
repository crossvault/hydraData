// Copyright (c) 2026 crossVault GmbH.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HydraData.Engine;

/// <summary>
/// A stateful, embeddable session that runs and re-runs <em>individual</em> steps of a discovered
/// <see cref="ScriptContext"/> while sharing persistent <c>State</c> across calls — so an expensive lookup
/// performed by one step survives the re-run of another (the authoring/dev step-session, task S1.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Sequential execution.</b> A session runs ONE step at a time; concurrent calls to
/// <see cref="RunStepAsync(StepOrder, IProgress{PumpProgress}?, CancellationToken)"/> or
/// <see cref="RunGroupAsync"/> serialize via an internal gate (<c>SemaphoreSlim(1,1)</c>).
/// This is consistent with the no-parallelism constraint of <see cref="IPumpEngine"/>:
/// <see cref="StepOutputCapture"/> redirects <c>Console.Out/Error</c> process-globally; two
/// concurrent captures would corrupt output or deadlock.
/// </para>
/// <para>
/// <b>Stable run identity.</b> The <see cref="RunId"/> is generated once (via <see cref="IGuidProvider"/>)
/// at construction and the single <see cref="Workspace"/> is built once; both are stable for the session's
/// lifetime. This is the point of the session: the <c>RunDir</c> (and any expensive DuckDB extract written
/// into <c>duck/</c>/<c>tmp/</c>) persists across re-runs, mirroring how <see cref="PumpEngine"/> creates
/// the workspace once per run.
/// </para>
/// <para>
/// <b>Persistent state.</b> <see cref="Shared"/> is one run-global <see cref="PumpState"/>. A per-group map
/// (<c>GG → PumpState</c>) is created lazily on first use of a group and then REUSED across calls, so
/// re-running step 2 of group 01 still sees the <c>State</c> that step 1 of group 01 wrote, without
/// re-running step 1. With <see cref="PumpOptions.LegacyGlobalState"/> set, every step shares one
/// run-global <c>State</c> bag instead (consistent with the batch engine).
/// </para>
/// <para>
/// <b>Re-read + re-validate per call.</b> Every <see cref="RunStepAsync(StepOrder, IProgress{PumpProgress}?, CancellationToken)"/>
/// re-reads the step file from disk exactly ONCE, re-parses its <see cref="StepMeta"/> from that text, and
/// passes the same text to <see cref="StepValidator"/> for compilation (PUMP001 + PUMP010 + Roslyn) and
/// then to <see cref="StepExecutor.RunOneAsync"/> for execution — so the file is read exactly once per
/// call. This single-read approach avoids TOCTOU discrepancies: meta, compile, and execute all see the
/// same bytes.
/// Validation and execution share one <see cref="ScriptCompiler"/> cache, so a step that validates cleanly
/// is Roslyn-compiled once per call and the compiled delegate is reused for execution.
/// A failed validation returns a <see cref="StepSessionResult"/> with
/// <see cref="StepSessionResult.Validated"/> = <see langword="false"/> and the step is NOT executed.
/// </para>
/// <para>
/// <b>Transaction per step.</b> The session reuses <see cref="StepExecutor"/> — the single source of truth
/// for the per-step transaction/capture/state wiring — so each <c>RunStepAsync</c> opens, commits or rolls
/// back, and disposes its own DB slots. There is no long-lived DB slot across calls; re-running a writing
/// step is therefore its own independent transaction, and idempotency of writing steps is the script's
/// responsibility. The session ignores the executor's <c>Halt</c> flag (there is
/// no run loop to halt — this is single-step execution).
/// </para>
/// <para>
/// <b>Lifecycle.</b> The session does not auto-clean its <c>RunDir</c> — run-directory retention is the
/// Host's job. <see cref="Dispose"/> is provided for symmetry and disposes the
/// run-level logging scope and the internal gate; it does not delete the workspace.
/// </para>
/// <para>
/// <b>Single-owner lifecycle (dispose safety).</b> <see cref="Dispose"/> must not race with an in-flight
/// <see cref="RunStepAsync(StepDescriptor, IProgress{PumpProgress}?, CancellationToken)"/> or
/// <see cref="RunGroupAsync"/> call. The session is designed for single-owner use: one caller drives the
/// run loop (e.g. <c>SessionBootstrap</c> / <c>SessionRepl</c>) and disposes the session only after the
/// run loop ends — never concurrently with an active step execution.
/// </para>
/// </remarks>
public sealed class PumpSession : IDisposable
{
    private readonly ScriptContext _ctx;
    private readonly ExternContext _externCtx;
    private readonly IConnectionDirectory _connections;
    private readonly PumpOptions _options;
    private readonly ScriptCompiler _compiler;
    private readonly StepValidator _validator;
    private readonly StepExecutor _executor;
    private readonly ILogger _logger;
    private readonly IDisposable? _runScope;

    // Serializes RunStepAsync/RunGroupAsync: one step at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Persistent per-group State (GG → PumpState), created lazily and reused for the session's lifetime.
    // In legacy mode a single run-global bag is used for every group.
    private readonly Dictionary<int, PumpState> _groupState = [];
    private readonly PumpState? _legacyGlobalState;

    private readonly ConnectionInfo? _defaultConnection;
    private volatile bool _disposed;

    /// <summary>
    /// Initialises a step session over a discovered <see cref="ScriptContext"/>.
    /// </summary>
    /// <param name="ctx">The discovered scripts the session can run steps from.</param>
    /// <param name="externCtx">The read-only host context passed to every step.</param>
    /// <param name="connections">The connection directory; its <c>Default</c> is resolved once.</param>
    /// <param name="options">Construction-time options applied to every step run.</param>
    /// <param name="guidProvider">RunId source. Defaults to <see cref="SystemGuidProvider.Instance"/>.</param>
    /// <param name="timeProvider">Per-step timeout clock. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Diagnostic logger. Defaults to <see cref="NullLogger.Instance"/>.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public PumpSession(
        ScriptContext ctx,
        ExternContext externCtx,
        IConnectionDirectory connections,
        PumpOptions options,
        IGuidProvider? guidProvider = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : this(ctx, externCtx, connections, options, guidProvider, timeProvider, gateway: null, logger)
    {
    }

    /// <summary>
    /// Internal constructor that also accepts the (internal) database gateway, used by tests with a fake
    /// gateway. The production gateway (<see cref="ConnectionGateway"/>) is used when <paramref name="gateway"/>
    /// is <see langword="null"/>.
    /// </summary>
    /// <param name="ctx">The discovered scripts the session can run steps from.</param>
    /// <param name="externCtx">The read-only host context passed to every step.</param>
    /// <param name="connections">The connection directory; its <c>Default</c> is resolved once.</param>
    /// <param name="options">Construction-time options applied to every step run.</param>
    /// <param name="guidProvider">RunId source. Defaults to <see cref="SystemGuidProvider.Instance"/>.</param>
    /// <param name="timeProvider">Per-step timeout clock. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="gateway">Database gateway. Defaults to the production <see cref="ConnectionGateway"/>.</param>
    /// <param name="logger">Diagnostic logger. Defaults to <see cref="NullLogger.Instance"/>.</param>
    internal PumpSession(
        ScriptContext ctx,
        ExternContext externCtx,
        IConnectionDirectory connections,
        PumpOptions options,
        IGuidProvider? guidProvider,
        TimeProvider? timeProvider,
        IConnectionGateway? gateway,
        ILogger? logger)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _externCtx = externCtx ?? throw new ArgumentNullException(nameof(externCtx));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        var guids = guidProvider ?? SystemGuidProvider.Instance;
        var clock = timeProvider ?? TimeProvider.System;
        var gw = gateway ?? new ConnectionGateway();
        _logger = logger ?? NullLogger.Instance;
        _compiler = new ScriptCompiler();

        // RunId once, then ONE workspace — both stable for the session lifetime so the RunDir (and any
        // expensive DuckDB extract) persists across re-runs (mirrors PumpEngine's up-front creation).
        RunId = guids.NewGuid();
        try
        {
            _runScope = _logger.BeginScope(new Dictionary<string, object> { ["RunId"] = RunId });
            Workspace = new Workspace(_options.WorkspaceBase, RunId, _options.Folders, _logger);

            _validator = new StepValidator(_options.AllowUnsafeDirectAccess, _compiler, _logger);
            _executor = new StepExecutor(_options, _compiler, gw, clock, Workspace, _logger);

            _legacyGlobalState = _options.LegacyGlobalState ? new PumpState() : null;

            // File-only sessions (zero steps) skip connection resolution entirely — no connection is needed.
            // A directory without any configured connection AND at least one step throws eagerly here so the
            // caller sees the misconfiguration at construction time rather than on the first step run.
            if (_ctx.Steps.Count > 0)
                _defaultConnection = ConnectionInfo.AsConnectionInfo(_connections.Default);

            _logger.LogInformation("Step session {RunId} opened over {StepCount} step(s).", RunId, _ctx.Steps.Count);
        }
        catch
        {
            _runScope?.Dispose();
            _gate.Dispose();
            throw;
        }
    }

    /// <summary>The session run identifier, generated once at construction and stable for its lifetime.</summary>
    public Guid RunId { get; }

    /// <summary>The single per-session filesystem sandbox (its <c>RunDir</c> persists across re-runs).</summary>
    public Workspace Workspace { get; }

    /// <summary>The run-global state bag shared by every step in the session.</summary>
    public PumpState Shared { get; } = new();

    /// <summary>
    /// All steps discovered and available in this session, in sort order (GG, SS, TT). Provided so
    /// interactive hosts (e.g. <c>SessionRepl :list</c>) can enumerate steps without accessing the
    /// internal <see cref="ScriptContext"/>.
    /// </summary>
    public IReadOnlyList<StepDescriptor> KnownSteps => _ctx.Steps;

    /// <summary>
    /// Runs the step identified by <paramref name="order"/> against the living group <c>State</c> and the
    /// session <see cref="Shared"/> bag. The step file is re-read and re-validated first; on a validation
    /// failure the step is not executed.
    /// </summary>
    /// <remarks>
    /// Concurrent calls serialize via an internal gate: only one step runs at a time.
    /// </remarks>
    /// <param name="order">The full order (Group/Step/SubStep) identifying the step.</param>
    /// <param name="progress">Optional progress sink for the per-step phases.</param>
    /// <param name="ct">The caller cancellation token.</param>
    /// <returns>The validation outcome and, when valid, the step's <see cref="StepRunResult"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="order"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public Task<StepSessionResult> RunStepAsync(
        StepOrder order,
        IProgress<PumpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var step = _ctx.Steps.FirstOrDefault(s => s.Order.CompareTo(order) == 0);
        if (step is null)
        {
            _logger.LogWarning("Step session {RunId}: no step matches order {Group}_{Step}_{SubStep}.",
                RunId, order.Group, order.Step, order.SubStep);
            var message = $"No step matches order {order.Group}_{order.Step}" +
                          (order.SubStep is { } sub ? $"_{sub}" : string.Empty) + " in this session.";
            return Task.FromResult(StepSessionResult.NotFound(message));
        }

        return RunStepAsync(step, progress, ct);
    }

    /// <summary>
    /// Runs the given <paramref name="step"/> directly (convenience overload). The step file is re-read and
    /// re-validated first; on a validation failure the step is not executed.
    /// </summary>
    /// <remarks>
    /// Concurrent calls serialize via an internal gate: only one step runs at a time.
    /// </remarks>
    /// <param name="step">The step to run. Its group selects the persistent group <c>State</c> bag.</param>
    /// <param name="progress">Optional progress sink for the per-step phases.</param>
    /// <param name="ct">The caller cancellation token.</param>
    /// <returns>The validation outcome and, when valid, the step's <see cref="StepRunResult"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="step"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public async Task<StepSessionResult> RunStepAsync(
        StepDescriptor step,
        IProgress<PumpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunStepCoreAsync(step, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs every step of <paramref name="group"/> in order, sharing that group's persistent <c>State</c>
    /// bag (a convenience over repeated <see cref="RunStepAsync(StepOrder, IProgress{PumpProgress}?, CancellationToken)"/>
    /// calls). Each step is independently re-read, re-validated and run; a step that fails validation is
    /// included in the results (not executed) and does NOT stop the remaining steps.
    /// </summary>
    /// <remarks>
    /// Concurrent calls serialize via an internal gate: the group runs as an uninterruptible
    /// sequence — no other <c>RunStepAsync</c> or <c>RunGroupAsync</c> call can interleave.
    /// The gate is acquired ONCE for the entire group (not once per step), so no other caller can
    /// interleave between the steps. <see cref="RunStepCoreAsync"/> is called directly (no re-entry
    /// into the public gated method — <see cref="SemaphoreSlim"/> is non-reentrant and would deadlock).
    /// </remarks>
    /// <param name="group">The group number (GG) whose steps to run.</param>
    /// <param name="progress">Optional progress sink for the per-step phases.</param>
    /// <param name="ct">The caller cancellation token.</param>
    /// <returns>One <see cref="StepSessionResult"/> per step in the group, in order; empty when the group has no steps.</returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public async Task<IReadOnlyList<StepSessionResult>> RunGroupAsync(
        int group,
        IProgress<PumpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Acquire the gate ONCE for the entire group so no concurrent RunStepAsync can interleave
        // between steps (making the group truly uninterruptible as documented).
        // Call RunStepCoreAsync directly — NOT the public RunStepAsync — because SemaphoreSlim(1,1)
        // is non-reentrant and calling the gated method while already holding the gate would deadlock.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // ctx.Steps are globally sorted by (Group, Step, SubStep) — preserve that order within the group.
            var steps = _ctx.Steps.Where(s => s.Order.Group == group).ToList();
            var results = new List<StepSessionResult>(steps.Count);
            foreach (var step in steps)
                results.Add(await RunStepCoreAsync(step, progress, ct).ConfigureAwait(false));
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The gate-free inner body of a single step run: re-reads the file from disk exactly ONCE, re-parses
    /// meta, validates (PUMP001 + PUMP010 + Roslyn), and executes via <see cref="StepExecutor.RunOneAsync"/>
    /// — passing the already-read text so the executor does not read the file a second time.
    /// </summary>
    /// <remarks>
    /// Called by the public gated <see cref="RunStepAsync(StepDescriptor, IProgress{PumpProgress}?, CancellationToken)"/>
    /// (gate acquired before entry) and by <see cref="RunGroupAsync"/> (gate held for the whole group).
    /// Must only be called while the caller holds <c>_gate</c>.
    /// </remarks>
    private async Task<StepSessionResult> RunStepCoreAsync(
        StepDescriptor step,
        IProgress<PumpProgress>? progress,
        CancellationToken ct)
    {
        // Re-read from disk ONCE: the same text is used for meta-parse, validation compile, and
        // execution — avoiding any TOCTOU discrepancy and ensuring the file is read exactly once per call.
        StepDescriptor fresh;
        string scriptText;
        try
        {
            scriptText = await File.ReadAllTextAsync(step.FilePath, ct).ConfigureAwait(false);
            fresh = step with { Meta = StepMeta.Parse(scriptText) };
        }
        catch (Exception ioEx) when (ioEx is IOException or UnauthorizedAccessException)
        {
            // File deleted/locked between calls: surface as a clear result rather than crashing.
            _logger.LogError("Step session {RunId}: could not read {ScriptName}: {Message}.",
                RunId, step.FileName, ioEx.Message);
            return StepSessionResult.Unreadable($"Step file could not be read: {ioEx.Message}");
        }

        // Single-step validate: PUMP001 + PUMP010 + Roslyn compile using the already-read text.
        // The compile is routed through the shared ScriptCompiler cache, so a clean script is compiled
        // once here and the executor below reuses that delegate (no second Roslyn compile).
        var report = _validator.ValidateStep(fresh, scriptText);

        if (!report.IsValid)
        {
            _logger.LogWarning("Step session {RunId}: {ScriptName} failed validation; not executed.",
                RunId, fresh.FileName);
            return StepSessionResult.Invalid(report);
        }

        var groupState = GroupStateFor(fresh.Order.Group);

        // Reuse the shared per-step seam (S1.0). Pass the already-read scriptText so the executor
        // does not perform a second file read (true single-read, no TOCTOU between validate and execute).
        // The session ignores the Halt flag: there is no run loop.
        var (result, _) = await _executor.RunOneAsync(
            fresh,
            scriptText,
            groupState,
            Shared,
            _externCtx,
            _defaultConnection,
            _connections,
            progress,
            ct).ConfigureAwait(false);

        return StepSessionResult.Ran(report, result);
    }

    /// <summary>Returns the persistent group <c>State</c> bag for <paramref name="group"/>, creating it on first use.</summary>
    private PumpState GroupStateFor(int group)
    {
        if (_legacyGlobalState is not null)
            return _legacyGlobalState;

        if (!_groupState.TryGetValue(group, out var state))
        {
            state = new PumpState();
            _groupState[group] = state;
        }

        return state;
    }

    /// <summary>
    /// Releases the run-level logging scope and the internal gate. The <c>RunDir</c> is intentionally NOT
    /// deleted — run-directory retention is the Host's responsibility.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _runScope?.Dispose();
        _gate.Dispose();
    }
}
