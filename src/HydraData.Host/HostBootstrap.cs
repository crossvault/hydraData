// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Microsoft.Extensions.Logging;

namespace HydraData.Host;

/// <summary>
/// The host composition root: loads configuration, builds the concrete logging providers (T07.4: console +
/// file), the connection directory and the engine, runs retention, and executes a single run. Kept thin so
/// the testable units (<see cref="PumpOptionsMapper"/>, <see cref="PumpRunner"/>, <see cref="RunDirRetention"/>,
/// <see cref="ConsolePresenter"/>) carry the logic.
/// </summary>
public static class HostBootstrap
{
    /// <summary>
    /// Runs the host end-to-end from the working directory <paramref name="baseDirectory"/>:
    /// load <c>appsettings.json</c> + <c>connections.xml</c>, run, return the process exit code (0/1/2).
    /// The exit-code contract is total:
    /// <list type="bullet">
    ///   <item><description><c>0</c> – success.</description></item>
    ///   <item><description><c>1</c> – configuration/preflight failure (the run never started).</description></item>
    ///   <item><description><c>2</c> – cancelled (Ctrl-C / token already cancelled before or during the run).</description></item>
    /// </list>
    /// Any other exception that escapes the engine or configuration layer is mapped to exit code <c>1</c>
    /// with a single-line stderr message (no raw stack trace exposed to the scheduler).
    /// </summary>
    /// <param name="baseDirectory">The working/content directory holding <c>appsettings.json</c>.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(string baseDirectory, CancellationToken ct = default) =>
        RunAsync(baseDirectory, resumeFrom: null, ct);

    /// <summary>
    /// Resumes a batch run from <paramref name="resumeFrom"/>:
    /// loads the same configuration, writes the same <c>host.log</c>, and applies the same exit-code contract
    /// as <see cref="RunAsync(string, CancellationToken)"/>, but executes only steps whose order is at or after
    /// <paramref name="resumeFrom"/> (earlier steps are skipped, validation still covers the whole context).
    /// </summary>
    /// <param name="baseDirectory">The working/content directory holding <c>appsettings.json</c>.</param>
    /// <param name="resumeFrom">The order to resume from; steps with <c>order &gt;= resumeFrom</c> execute.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The process exit code (0/1/2).</returns>
    public static Task<int> RunResumeAsync(string baseDirectory, StepOrder resumeFrom, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resumeFrom);
        return RunAsync(baseDirectory, resumeFrom, ct);
    }

    // Shared bootstrap core for full-run and resume mode. resumeFrom == null ⇒ full run (ExecuteAsync);
    // resumeFrom != null ⇒ resume run (ExecuteFromAsync). Everything else (config load, RunId/host.log,
    // connections, retention, exit-code mapping) is identical.
    private static async Task<int> RunAsync(string baseDirectory, StepOrder? resumeFrom, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        // [B2] ALL configuration loading (appsettings.json read/bind + ToPumpOptions + path resolution) runs
        // INSIDE the outer try so a missing or malformed appsettings.json is caught and mapped to exit 1,
        // never escaping to Program.cs as an unguarded exception.
        try
        {
            ct.ThrowIfCancellationRequested();

            // Call ToPumpOptions once and reuse the result for both workspaceBase and the engine options.
            // HostConfigLoader.Load also parses the connection registry and retains its warnings.
            var config = HostConfigLoader.Load(baseDirectory);
            var options = config.Options;
            var workspaceBase = options.WorkspaceBase;

            // Generate a RunId here so the host log can be written INSIDE the run's RunDir.
            // RunDirRetention then reaps the log together with the run directory — no unbounded growth.
            // The same RunId is handed to the engine via a FixedGuidProvider so the RunDir used by
            // the engine matches the directory containing host.log.
            var runId = Guid.NewGuid();
            var runDir = Path.Combine(workspaceBase, runId.ToString("D"));
            var logPath = Path.Combine(runDir, "host.log");

            // LoggerFactory does not own provider instances passed to AddProvider; dispose this explicitly
            // after the factory so host.log is flushed/unlocked before RunAsync returns.
            using var fileLoggerProvider = new FileLoggerProvider(logPath);
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                // Console provider for stdout (CI/scheduler capture). It writes to stderr-free simple lines; the
                // ConsolePresenter handles the human-facing progress/summary separately.
                builder.AddSimpleConsole(o => o.SingleLine = true);
                // File sink inside the run directory so RunDirRetention reaps it with the run.
                builder.AddProvider(fileLoggerProvider);
            });

            var logger = loggerFactory.CreateLogger("HydraData.Host");

            // Map every failure after logger creation while the factory and file sink are still alive.
            try
            {
                foreach (var warning in config.Warnings)
                    logger.LogWarning("connections.xml: {Warning}", warning.Message);

                var connections = new ConnectionDirectory(config.Registry);

                // Retention runs before the new run so an old, full workspace is trimmed first.
                try
                {
                    new RunDirRetention(logger).CleanOlderThanDays(workspaceBase, config.Settings.RunDirRetentionDays);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Retention is best-effort housekeeping; never let it block the run.
                    logger.LogWarning(ex, "Retention pass failed; continuing with the run.");
                }

                var engine = new PumpEngine(options, guidProvider: new FixedGuidProvider(runId), logger: logger);
                var discovery = new DiscoveryService(new LoaderOptions
                {
                    // Derive legacy flags from the already-resolved PumpOptions, not by re-reading settings.
                    LegacyGroupBySlug = options.LegacyGroupBySlug,
                    LegacyGlobalState = options.LegacyGlobalState,
                });
                var presenter = new ConsolePresenter();

                // ExternContext is intentionally minimal in v1: no values are sourced from the CLI yet. Hosts that
                // need batch-date/tenant inputs add them here from configuration or args.
                var externCtx = ExternContext.FromValues(new Dictionary<string, object?>());

                var runner = new PumpRunner(engine, discovery, connections, presenter, logger);

                return resumeFrom is { } from
                    ? await runner.RunResumeAsync(config.ScriptFolders, externCtx, from, ct).ConfigureAwait(false)
                    : await runner.RunAsync(config.ScriptFolders, externCtx, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == ct || ct.IsCancellationRequested)
            {
                await Console.Error.WriteLineAsync("Run cancelled.").ConfigureAwait(false);
                return 2;
            }
            catch (Exception ex)
            {
                return await HostExit.MapConfigExceptionAsync(ex, logger, Console.Error).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == ct || ct.IsCancellationRequested)
        {
            // Token was cancelled before/during config loading (e.g. pre-cancelled token passed in).
            await Console.Error.WriteLineAsync("Run cancelled.").ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex)
        {
            return await HostExit.MapConfigExceptionAsync(ex, logger: null, Console.Error).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An <see cref="IGuidProvider"/> that always returns a pre-determined <see cref="Guid"/>. Used to
    /// align the engine's <c>RunId</c> (and therefore its <c>RunDir</c>) with the host log path computed
    /// before the engine is constructed, so that <c>host.log</c> lands inside the run directory and is
    /// reaped by <see cref="RunDirRetention"/> along with the rest of the run's output.
    /// </summary>
    private sealed class FixedGuidProvider(Guid value) : IGuidProvider
    {
        /// <inheritdoc />
        public Guid NewGuid() => value;
    }
}
