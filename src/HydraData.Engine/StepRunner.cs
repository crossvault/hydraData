// Copyright (c) 2026 crossVault GmbH.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HydraData.Engine;

/// <summary>
/// The outcome of running one step through the <see cref="StepRunner"/>.
/// </summary>
/// <param name="Result">
/// The step's reported result. When the step crashed or was cancelled, this is a synthesized
/// <see cref="Severity.Error"/> result describing the failure.
/// </param>
/// <param name="EffectiveSeverity">
/// The maximum of the result severity and the highest note severity (runtime contract,
/// T02.6a). Drives the transaction decision.
/// </param>
/// <param name="Committed">Whether the step's slots were committed (<see langword="false"/> = rolled back).</param>
/// <param name="Notes">Notes recorded during the step.</param>
/// <param name="Output">Captured stdout/stderr produced during the step.</param>
public sealed record StepOutcome(
    StepResult Result,
    Severity EffectiveSeverity,
    bool Committed,
    IReadOnlyList<Note> Notes,
    string Output);

/// <summary>
/// Runs a single step: captures its output, executes the compiled script against a
/// <see cref="PumpContext"/>, then applies the transaction policy. One
/// transaction per target connection per step; no cross-DB atomicity.
/// </summary>
/// <remarks>
/// Policy:
/// <list type="bullet">
/// <item>Effective severity ≤ Warning ⇒ commit all slots.</item>
/// <item>Effective severity == Error (including an Error note over an Ok/Warn return) ⇒ rollback.</item>
/// <item>Any exception (crash, compile error, <see cref="StepVerdict"/>) ⇒ rollback.</item>
/// <item>Cancellation ⇒ rollback and rethrow <see cref="OperationCanceledException"/>.</item>
/// </list>
/// The per-step timeout uses the injected <see cref="TimeProvider"/>, so it is deterministically
/// testable via a fake provider (T02.6).
/// </remarks>
public sealed class StepRunner
{
    private readonly ScriptCompiler _compiler;
    private readonly IConnectionGateway _gateway;
    private readonly IScriptIo? _io;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Initializes a new <see cref="StepRunner"/>.</summary>
    /// <param name="compiler">The compile cache used to obtain the script runner.</param>
    /// <param name="gateway">The database gateway passed to each step's context.</param>
    /// <param name="io">Optional file/CSV/Excel/DuckDB seam (cluster 05); a not-wired stub is used when null.</param>
    /// <param name="timeProvider">Time source for the per-step timeout. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Diagnostic logger for step start/verdict/timing. Defaults to <see cref="NullLogger.Instance"/>.</param>
    internal StepRunner(
        ScriptCompiler compiler,
        IConnectionGateway gateway,
        IScriptIo? io = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _io = io;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Runs the step described by <paramref name="code"/>.</summary>
    /// <param name="code">The script source text.</param>
    /// <param name="state">Group-local state bag.</param>
    /// <param name="shared">Run-global state bag.</param>
    /// <param name="ctx">Read-only host context.</param>
    /// <param name="connection">
    /// The connection the step's DB methods target (the implicit <c>CurrentConnection</c>). May be
    /// <see langword="null"/> for steps that do no database access.
    /// </param>
    /// <param name="unsafeAllowed">Whether the step declared <c>@unsafe</c> (gates <c>Raw</c>).</param>
    /// <param name="stepTimeout">Optional per-step timeout. <see langword="null"/> disables it.</param>
    /// <param name="logger">Optional logger for the step's <c>Log</c> calls.</param>
    /// <param name="connections">
    /// Optional connection directory backing the step's <c>GetConnection</c> overloads (connection
    /// switching, runtime contract). <see langword="null"/> when the run targets only the
    /// default connection.
    /// </param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The step outcome (result, effective severity, commit decision, notes, output).</returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled (slots rolled back first).</exception>
    public async Task<StepOutcome> RunAsync(
        string code,
        PumpState state,
        PumpState shared,
        ExternContext ctx,
        ConnectionInfo? connection,
        bool unsafeAllowed,
        TimeSpan? stepTimeout = null,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        IConnectionDirectory? connections = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        // The script's Log() uses the per-call logger when supplied; the runner's own diagnostics use the
        // same logger so engine-scoped run/step scopes apply, falling back to the ctor logger then Null.
        var log = logger ?? _logger;
        var context = new PumpContext(
            state, shared, ctx, _gateway, connection, unsafeAllowed, _io, logger ?? _logger, connections,
            commandTimeoutSeconds: CommandTimeoutSeconds(stepTimeout));

        await using var capture = await StepOutputCapture.StartAsync(ct).ConfigureAwait(false);

        using var timeoutCts = stepTimeout is { } timeout
            ? new CancellationTokenSource(timeout, _timeProvider)
            : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        context.Cancellation = linked.Token;

        StepResult result;
        bool cancelledByCaller = false;
        try
        {
            // The compiled runner returns the script's StepResult (or throws StepVerdict/any exception).
            var runner = _compiler.GetRunner(code);
            result = await runner(context, linked.Token).ConfigureAwait(false);
        }
        // Caller cancellation is caught first because both caller and timeout may be signalled at
        // the same time (e.g. a very short timeout fired just as the caller cancelled). Caller cancel
        // takes precedence: it must rethrow so the orchestrator can distinguish it from a mere timeout.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation: rollback, then rethrow.
            cancelledByCaller = true;
            result = StepResult.Fail("Step cancelled by caller.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Per-step timeout: rollback, treated as a step failure (not a caller cancellation).
            result = StepResult.Fail("Step timed out.");
        }
        catch (StepVerdict verdict)
        {
            result = verdict.Result;
        }
        catch (ScriptCompileException ex)
        {
            // Surface structured diagnostic codes so operators can act on CS-errors without digging
            // through the raw message. Format: "Step compile failed: CS0103, CS0246 — <summary>".
            var codes = string.Join(", ", ex.Diagnostics
                .Where(d => d.Severity == Severity.Error)
                .Select(d => d.Code)
                .Distinct(StringComparer.Ordinal));
            var codePrefix = codes.Length > 0 ? $"{codes} — " : string.Empty;
            result = StepResult.Fail($"Step compile failed: {codePrefix}{ex.Message}", ex);
        }
        catch (Exception ex)
        {
            result = StepResult.Fail($"Step crashed: {ex.Message}", ex);
        }

        var effective = MaxSeverity(result.Severity, context.Notes);
        var commit = effective <= Severity.Warning && !cancelledByCaller;

        // Wrap finalize so that a DB commit/rollback failure does not escape as a raw AggregateException.
        // Cancellation is never converted into a finalize Error — we rethrow it below after outcome is built.
        // Commit/Rollback fan out over every open slot: a step that switched connections in-script opens one
        // slot per target connection, and all are finalised together.
        if (commit)
        {
            try
            {
                context.CommitAll();
            }
            catch (Exception ex) when (!cancelledByCaller)
            {
                // Commit failed: data did not land. Demote to error outcome.
                // PARTIAL-COMMIT NOTE: CommitAll fans out over every open slot in order. If the
                // first slot committed before the second threw, those writes are already durable — there
                // is no cross-DB atomicity and no way to un-commit a committed connection. The operator
                // must re-run the (idempotent) script; it will skip the already-applied side and apply
                // the missing side. surface the partial-commit state in reconciliation logic.
                result = StepResult.Fail(
                    $"Commit failed (partial commit possible — no cross-DB atomicity): {ex.Message}", ex);
                effective = Severity.Error;
                commit = false;
            }
        }
        else
        {
            try
            {
                context.RollbackAll();
            }
            catch (Exception rbEx) when (!cancelledByCaller)
            {
                // Rollback failed (non-cancellation path): surface as error, but preserve any original
                // failure reason in the message so the caller sees both the step failure and the rollback failure.
                var originalMessage = result.Message;
                result = StepResult.Fail(
                    string.IsNullOrEmpty(originalMessage) || originalMessage == "Step cancelled by caller."
                        ? $"Rollback failed: {rbEx.Message}"
                        : $"Rollback failed: {rbEx.Message} (original: {originalMessage})",
                    rbEx);
                effective = Severity.Error;
            }
            catch (Exception) when (cancelledByCaller)
            {
                // Rollback failed during caller cancellation: suppress the rollback exception so the
                // OperationCanceledException is still rethrown below (caller cancel takes precedence).
            }
        }

        var outcome = new StepOutcome(result, effective, commit, [.. context.Notes], capture.Output);

        // Verdict -> level: Ok/Warn -> Information; Error/crash/timeout/rollback -> Error.
        // The message is a fixed template (no ConnectionString / secret is ever logged).
        switch (effective)
        {
            case Severity.Error:
                log.LogError("Step finished with verdict {Verdict} (committed={Committed}): {Message}",
                    effective, commit, result.Message);
                break;
            case Severity.Warning:
                log.LogWarning("Step finished with verdict {Verdict}: {Message}", effective, result.Message);
                break;
            default:
                log.LogInformation("Step finished with verdict {Verdict}.", effective);
                break;
        }

        if (cancelledByCaller)
            ct.ThrowIfCancellationRequested();

        return outcome;
    }

    /// <summary>
    /// Maps the per-step timeout to a Dapper/bulk <c>commandTimeout</c> in seconds. A long server-side
    /// query is then bounded by the step timeout via <c>CommandTimeout</c> as well as by the linked
    /// cancellation token. The seconds value is the step timeout rounded UP
    /// to a whole second (minimum 1, so a sub-second timeout never becomes a 0 = "infinite" command
    /// timeout); <see langword="null"/> (no step timeout) means no override — the provider default
    /// command timeout applies.
    /// </summary>
    /// <param name="stepTimeout">The per-step timeout, or <see langword="null"/> when disabled.</param>
    /// <returns>The command timeout in seconds, or <see langword="null"/> for no override.</returns>
    internal static int? CommandTimeoutSeconds(TimeSpan? stepTimeout)
    {
        if (stepTimeout is not { } timeout) return null;
        var seconds = (int)Math.Ceiling(timeout.TotalSeconds);
        return seconds < 1 ? 1 : seconds;
    }

    private static Severity MaxSeverity(Severity resultSeverity, IReadOnlyList<Note> notes)
    {
        var max = resultSeverity;
        foreach (var note in notes)
            if (note.Severity > max) max = note.Severity;
        return max;
    }
}
