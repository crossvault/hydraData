// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Microsoft.Extensions.Logging;

namespace HydraData.Host;

/// <summary>
/// Orchestrates a single host run: discovers scripts, executes them through an <see cref="IPumpEngine"/> and
/// maps the resulting <see cref="RunReport.ExitCode"/> to the process exit code.
/// </summary>
/// <remarks>
/// The runner takes its collaborators (engine, connection directory, presenter) as constructor arguments so
/// it can be driven with a stub engine and fake connections in tests. The host composition root
/// (<see cref="HostBootstrap"/>) builds the production collaborators from configuration.
/// </remarks>
public sealed class PumpRunner
{
    private readonly IPumpEngine _engine;
    private readonly DiscoveryService _discovery;
    private readonly IConnectionDirectory _connections;
    private readonly ConsolePresenter _presenter;
    private readonly ILogger _logger;

    /// <summary>Creates a runner.</summary>
    /// <param name="engine">The pump engine (real or stub).</param>
    /// <param name="discovery">The discovery service (already configured with the loader options).</param>
    /// <param name="connections">The resolved connection directory.</param>
    /// <param name="presenter">The console presenter (also the progress sink).</param>
    /// <param name="logger">Diagnostic logger.</param>
    public PumpRunner(
        IPumpEngine engine,
        DiscoveryService discovery,
        IConnectionDirectory connections,
        ConsolePresenter presenter,
        ILogger logger)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs the pipeline for the given script folders and returns the process exit code:
    /// <c>0</c> success, <c>1</c> preflight/validation failure, <c>2</c> runtime failure (runtime contract
    /// section 12). The exit code is taken verbatim from <see cref="RunReport.ExitCode"/>.
    /// </summary>
    /// <param name="scriptFolders">Absolute, ordered script folders to discover and run.</param>
    /// <param name="externCtx">Read-only host context for the run.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The process exit code (0/1/2).</returns>
    public async Task<int> RunAsync(
        IReadOnlyList<string> scriptFolders,
        ExternContext externCtx,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scriptFolders);
        ArgumentNullException.ThrowIfNull(externCtx);

        var ctx = _discovery.Discover(scriptFolders);
        _logger.LogInformation("Discovered {StepCount} step(s) in {GroupCount} group(s).",
            ctx.Steps.Count, ctx.Groups.Count);

        foreach (var w in ctx.Warnings)
            _logger.LogWarning("Discovery warning: {Warning}", w);

        var report = await _engine.ExecuteAsync(ctx, externCtx, _connections, _presenter, ct).ConfigureAwait(false);

        return Finish(report);
    }

    /// <summary>
    /// Resumes the pipeline from <paramref name="resumeFrom"/>: validates the whole context but executes only
    /// steps whose order is at or after <paramref name="resumeFrom"/>.
    /// The exit-code contract is identical to <see cref="RunAsync"/> (0/1/2, taken verbatim from
    /// <see cref="RunReport.ExitCode"/>).
    /// </summary>
    /// <param name="scriptFolders">Absolute, ordered script folders to discover and run.</param>
    /// <param name="externCtx">Read-only host context for the run.</param>
    /// <param name="resumeFrom">The order to resume from; steps with <c>order &gt;= resumeFrom</c> execute.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The process exit code (0/1/2).</returns>
    public async Task<int> RunResumeAsync(
        IReadOnlyList<string> scriptFolders,
        ExternContext externCtx,
        StepOrder resumeFrom,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scriptFolders);
        ArgumentNullException.ThrowIfNull(externCtx);
        ArgumentNullException.ThrowIfNull(resumeFrom);

        var ctx = _discovery.Discover(scriptFolders);
        _logger.LogInformation(
            "Resuming from {ResumeFrom}: discovered {StepCount} step(s) in {GroupCount} group(s).",
            resumeFrom, ctx.Steps.Count, ctx.Groups.Count);

        foreach (var w in ctx.Warnings)
            _logger.LogWarning("Discovery warning: {Warning}", w);

        var report = await _engine
            .ExecuteFromAsync(ctx, externCtx, _connections, resumeFrom, _presenter, ct)
            .ConfigureAwait(false);

        return Finish(report);
    }

    private int Finish(RunReport report)
    {
        _presenter.RenderSummary(report);
        _logger.LogInformation("Run {RunId} completed with exit code {ExitCode}.", report.RunId, report.ExitCode);

        return report.ExitCode;
    }
}
