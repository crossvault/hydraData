// Copyright (c) 2026 crossVault GmbH.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HydraData.Engine;

/// <summary>
/// The single source of truth for executing ONE already-loaded step: it opens the step's logging scope,
/// reports the per-step progress (<see cref="PumpPhase.StepStarted"/>/<see cref="PumpPhase.StepOutput"/>/
/// <see cref="PumpPhase.StepFinished"/>), builds the workspace-backed <see cref="ScriptIo"/> and the
/// <see cref="PumpContext"/>, runs the supplied script text through a <see cref="StepRunner"/> and maps
/// the outcome to a <see cref="StepRunResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// This seam exists so the batch <see cref="PumpEngine"/> and the step-session share exactly one
/// implementation of the per-step transaction/capture/state wiring (task S1.0). It is deliberately a thin
/// extraction: it changes no behaviour. The caller still owns the run-level concerns — RunId/Workspace
/// creation, validation/preflight, group-<c>State</c> rollover, the <c>Discovered</c>/<c>Validated</c>/
/// <c>RunFinished</c> progress phases, the exit-code computation and the halt loop control.
/// </para>
/// <para>
/// <b>One loaded-step model.</b> This executor NEVER reads files: the script text is supplied by the
/// caller. Reading the step file (and the B1 handling of a file that became unreadable between validate
/// and its execution slot — surfaced as a step <see cref="Severity.Error"/> so the run still finishes)
/// is the caller's responsibility (<see cref="PumpEngine"/>'s execute loop and <see cref="PumpSession"/>).
/// </para>
/// <para>
/// <b>Cancellation.</b> A caller <see cref="OperationCanceledException"/> (and a step that throws it after
/// the runner rolled back) propagates out of <see cref="RunOneAsync"/> unchanged so the caller can record
/// the cancellation, emit <see cref="PumpPhase.RunFinished"/> and rethrow — preserving the existing
/// exit-2/propagation policy.
/// </para>
/// </remarks>
internal sealed class StepExecutor
{
    private readonly PumpOptions _options;
    private readonly ScriptCompiler _compiler;
    private readonly IConnectionGateway _gateway;
    private readonly TimeProvider _timeProvider;
    private readonly Workspace _workspace;
    private readonly ILogger _logger;

    /// <summary>Initialises a step executor bound to one run's options, compiler, gateway and workspace.</summary>
    /// <param name="options">The run options (timeout, unsafe flag, folders).</param>
    /// <param name="compiler">The shared compile cache.</param>
    /// <param name="gateway">The database gateway passed to each step.</param>
    /// <param name="timeProvider">The per-step timeout clock.</param>
    /// <param name="workspace">The run's filesystem sandbox.</param>
    /// <param name="logger">Diagnostic logger; the per-step scope is opened here.</param>
    internal StepExecutor(
        PumpOptions options,
        ScriptCompiler compiler,
        IConnectionGateway gateway,
        TimeProvider timeProvider,
        Workspace workspace,
        ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Runs a single already-loaded step: opens the step scope, reports
    /// <see cref="PumpPhase.StepStarted"/>, runs the supplied <paramref name="scriptText"/>, reports
    /// <see cref="PumpPhase.StepOutput"/> (when there is output) and <see cref="PumpPhase.StepFinished"/>,
    /// and returns the recorded result together with whether the run should halt (an
    /// <see cref="Severity.Error"/> step that declares <c>@haltOnError</c>).
    /// </summary>
    /// <param name="step">The step to run.</param>
    /// <param name="scriptText">
    /// The step's script text, already read (and validated) by the caller. This executor never reads
    /// files; reading and the B1 file-unreadable handling live in the caller.
    /// </param>
    /// <param name="groupState">The group-local state bag for this step's group.</param>
    /// <param name="shared">The run-global state bag.</param>
    /// <param name="externCtx">The read-only host context.</param>
    /// <param name="defaultConnection">The run's default connection (implicit <c>CurrentConnection</c>), or null.</param>
    /// <param name="connections">The connection directory backing in-script connection switching.</param>
    /// <param name="progress">Optional progress sink for the per-step phases.</param>
    /// <param name="ct">The caller cancellation token.</param>
    /// <returns>The step's <see cref="StepRunResult"/> and whether the run should halt after it.</returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled (slots rolled back first); propagated for the caller to record.</exception>
    internal async Task<(StepRunResult Result, bool Halt)> RunOneAsync(
        StepDescriptor step,
        string scriptText,
        PumpState groupState,
        PumpState shared,
        ExternContext externCtx,
        ConnectionInfo? defaultConnection,
        IConnectionDirectory connections,
        IProgress<PumpProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(scriptText);

        using var stepScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ScriptName"] = step.FileName,
            ["Order"] = $"{step.Order.Group}_{step.Order.Step}",
        });

        _logger.LogInformation("Step {ScriptName} starting (run {RunId}).", step.FileName, _workspace.RunId);
        progress?.Report(new PumpProgress(PumpPhase.StepStarted, ScriptName: step.FileName));

        // Real workspace-backed IO honouring the engine flag and this step's @unsafe meta.
        var io = new ScriptIo(_workspace, _options.AllowUnsafeDirectAccess, step.Meta.Unsafe);
        var stepRunner = new StepRunner(_compiler, _gateway, io, _timeProvider, _logger);

        var outcome = await stepRunner.RunAsync(
            scriptText,
            groupState,
            shared,
            externCtx,
            defaultConnection,
            unsafeAllowed: step.Meta.Unsafe && _options.AllowUnsafeDirectAccess,
            stepTimeout: _options.StepTimeout,
            logger: _logger,
            connections: connections,
            ct: ct).ConfigureAwait(false);

        // Surface captured output as a StepOutput progress message (interim state only).
        if (!string.IsNullOrEmpty(outcome.Output))
            progress?.Report(new PumpProgress(PumpPhase.StepOutput, ScriptName: step.FileName, Message: outcome.Output));

        var result = new StepRunResult(
            step.FileName,
            outcome.Result,
            outcome.EffectiveSeverity,
            outcome.Committed,
            outcome.Output);

        // Result is set only at StepFinished.
        progress?.Report(new PumpProgress(PumpPhase.StepFinished, ScriptName: step.FileName, Result: outcome.Result));

        bool halt = false;
        if (outcome.EffectiveSeverity >= Severity.Error && step.Meta.HaltOnError)
        {
            _logger.LogError("Step {ScriptName} (run {RunId}) errored and declares haltOnError; halting the run.", step.FileName, _workspace.RunId);
            halt = true;
        }

        return (result, halt);
    }
}
