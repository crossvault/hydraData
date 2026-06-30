// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HydraData.Host;

/// <summary>
/// Bootstraps the interactive <c>session</c> host mode: loads <c>appsettings.json</c> and
/// <c>connections.xml</c> from a base directory (the same configuration as the full-run mode), builds a
/// <see cref="PumpSession"/>, and hands it to a <see cref="SessionRepl"/>. Returns the REPL's exit code.
/// </summary>
/// <remarks>
/// <para>
/// The session exit-code contract differs from the batch <see cref="HostBootstrap.RunAsync"/> contract
/// because the session is an interactive (REPL) tool, not a batch job:
/// <list type="bullet">
///   <item><description><c>0</c> — clean session end: <c>:quit</c>, EOF, or cancellation with any
///     in-flight step already rolled back by the engine. Cancellation is a normal interactive end,
///     not a batch pass/fail signal.</description></item>
///   <item><description><c>1</c> — configuration failure (missing or malformed <c>appsettings.json</c> /
///     <c>connections.xml</c> / script folder); session never started.</description></item>
/// </list>
/// There is no exit-code <c>2</c> in session mode. Contrast with the batch host
/// (<see cref="HostBootstrap.RunAsync"/>) which uses <c>2</c> for cancellation.
/// </para>
/// <para>
/// Configuration loading reuses <see cref="HostConfigLoader"/> and the same
/// <see cref="PumpSettings"/> / <see cref="ConnectionRegistry"/> path as the batch run. The session does
/// NOT run retention (it is a dev tool, not a production batch), and does NOT write a <c>host.log</c>
/// (the session workspace is ephemeral).
/// </para>
/// </remarks>
public static class SessionBootstrap
{
    /// <summary>
    /// Runs the interactive session mode.
    /// </summary>
    /// <param name="baseDirectory">
    /// The working/content directory holding <c>appsettings.json</c> (same semantics as
    /// <see cref="HostBootstrap.RunAsync"/>).
    /// </param>
    /// <param name="input">The input reader for the REPL; pass <see langword="null"/> to use <see cref="Console.In"/>.</param>
    /// <param name="output">The output writer for the REPL; pass <see langword="null"/> to use <see cref="Console.Out"/>.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The process exit code (0 or 1 — there is no exit 2 in session mode).</returns>
    public static async Task<int> RunAsync(
        string baseDirectory,
        TextReader? input = null,
        TextWriter? output = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var actualInput = input ?? Console.In;
        var actualOutput = output ?? Console.Out;

        ILogger? logger = null;
        try
        {
            // Minimal console logger for the session (no file sink — the session workspace is transient).
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Warning);
                builder.AddSimpleConsole(o => o.SingleLine = true);
            });
            logger = loggerFactory.CreateLogger("HydraData.Host.Session");

            var config = HostConfigLoader.Load(baseDirectory, logger);
            var options = config.Options;

            var discovery = new DiscoveryService(new LoaderOptions
            {
                LegacyGroupBySlug = options.LegacyGroupBySlug,
                LegacyGlobalState = options.LegacyGlobalState,
            });
            var ctx = discovery.Discover(config.ScriptFolders);

            var externCtx = ExternContext.FromValues(new Dictionary<string, object?>());

            // The session is the unit of state: one long-lived PumpSession for the entire REPL lifetime.
            using var session = new PumpSession(ctx, externCtx, config.Connections, options, logger: logger);

            // Non-TTY progress sink: plain text, no Spectre interactive elements.
            var presenter = new ConsolePresenter(actualOutput, isInteractive: false);

            actualOutput.WriteLine($"hydradata session — {ctx.Steps.Count} step(s) discovered. " +
                                   "Type :help for commands, :quit to exit.");

            var repl = new SessionRepl(session, actualInput, actualOutput, presenter);

            // If the token is already cancelled before we enter the REPL, treat as a clean no-op end (exit 0).
            // Session mode does not use exit 2; see class-level remarks.
            if (ct.IsCancellationRequested)
                return 0;

            return await repl.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == ct || ct.IsCancellationRequested)
        {
            // Cancellation at any point (config load, pre-REPL, or REPL body) is a clean end in session
            // mode — the engine already rolled back any in-flight step. Exit 0, not 2.
            await actualOutput.WriteLineAsync("Session cancelled.").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            return await HostExit.MapConfigExceptionAsync(ex, logger, actualOutput).ConfigureAwait(false);
        }
    }
}
