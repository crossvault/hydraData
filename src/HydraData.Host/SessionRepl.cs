// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;

namespace HydraData.Host;

/// <summary>
/// An interactive REPL (read-eval-print loop) that drives a single, long-lived <see cref="PumpSession"/>
/// over a <see cref="TextReader"/>/<see cref="TextWriter"/> pair, making it fully testable without
/// binding to <see cref="System.Console"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a persistent session?</b> The value of interactive step execution is the ability to re-run
/// a single step while keeping an earlier step's expensive <c>State</c> (e.g. a DB lookup written by
/// step 1 survives re-running step 2). A new process per step would lose that state. The REPL holds
/// ONE <see cref="PumpSession"/> for its entire lifetime and never recreates it between commands.
/// </para>
/// <para>
/// <b>Non-TTY safe.</b> All output is plain text: no Spectre interactive elements (<c>Live</c>,
/// <c>Status</c>, animated bars) are used. The REPL works correctly when stdin/stdout are piped
/// (e.g. in tests or CI shell scripts).
/// </para>
/// <para>
/// <b>Robustness.</b> Parse errors on user input produce a friendly message; the loop continues.
/// A validation failure produces the diagnostics inline; the session (and its State) are preserved.
/// </para>
/// </remarks>
public sealed class SessionRepl
{
    private readonly PumpSession _session;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly IProgress<PumpProgress>? _progress;

    /// <summary>
    /// Initialises the REPL.
    /// </summary>
    /// <param name="session">
    /// The long-lived session to drive. Its <c>State</c> persists across every command issued in the loop.
    /// The REPL does not dispose the session; the caller owns its lifetime.
    /// </param>
    /// <param name="input">Line source — <see cref="Console.In"/> in production; a <see cref="StringReader"/> in tests.</param>
    /// <param name="output">Output sink — <see cref="Console.Out"/> in production; a <see cref="StringWriter"/> in tests.</param>
    /// <param name="progress">
    /// Optional per-step progress sink. In production a <see cref="ConsolePresenter"/> (non-TTY) is passed;
    /// tests may pass <see langword="null"/> to suppress inline progress noise.
    /// </param>
    /// <exception cref="ArgumentNullException">Any required argument is <see langword="null"/>.</exception>
    public SessionRepl(
        PumpSession session,
        TextReader input,
        TextWriter output,
        IProgress<PumpProgress>? progress = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _progress = progress;
    }

    /// <summary>
    /// Runs the interactive loop until the user issues <c>:quit</c> / <c>:q</c> or the input stream ends.
    /// </summary>
    /// <param name="ct">Cancellation token. Cancellation exits the loop cleanly (returns 0).</param>
    /// <returns><c>0</c> for a clean exit (<c>:quit</c> / EOF / cancellation); never throws for user errors.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        WritePrompt();

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // EOF — input stream exhausted (piped script finished).
            if (line is null)
                break;

            line = line.Trim();

            // Blank line: ignore, re-prompt.
            if (line.Length == 0)
            {
                WritePrompt();
                continue;
            }

            // Dispatch.
            bool quit = await HandleLineAsync(line, ct).ConfigureAwait(false);
            if (quit)
                return 0;

            WritePrompt();
        }

        return 0;
    }

    // ── Command dispatch ──────────────────────────────────────────────────────────

    /// <returns><see langword="true"/> when the loop should exit.</returns>
    private async Task<bool> HandleLineAsync(string line, CancellationToken ct)
    {
        if (line.StartsWith(':'))
            return await HandleColonCommandAsync(line, ct).ConfigureAwait(false);

        // Bare order input (e.g. "01_20") treated as ":run 01_20".
        return await HandleRunCommandAsync(line.TrimStart(), ct).ConfigureAwait(false);
    }

    private async Task<bool> HandleColonCommandAsync(string line, CancellationToken ct)
    {
        // Split on first space: ":run 01_20" → cmd=":run", rest="01_20".
        var spaceIdx = line.IndexOf(' ', StringComparison.Ordinal);
        var cmd = spaceIdx < 0 ? line : line[..spaceIdx];
        var arg = spaceIdx < 0 ? string.Empty : line[(spaceIdx + 1)..].Trim();

        switch (cmd.ToLowerInvariant())
        {
            case ":quit":
            case ":q":
                _output.WriteLine("Session closed.");
                return true;

            case ":help":
                WriteHelp();
                return false;

            case ":list":
                WriteList();
                return false;

            case ":run":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    _output.WriteLine("Usage: :run <order>  (e.g. :run 01_20)");
                    return false;
                }
                return await HandleRunCommandAsync(arg, ct).ConfigureAwait(false);

            default:
                _output.WriteLine($"Unknown command '{cmd}'. Type :help for available commands.");
                return false;
        }
    }

    private async Task<bool> HandleRunCommandAsync(string orderText, CancellationToken ct)
    {
        if (!OrderKeyParser.TryParse(orderText, out var order))
        {
            _output.WriteLine($"Cannot parse '{orderText}' as a step order. " +
                              "Expected GG_SS or GG_SS_TT (e.g. 01_20 or 01_20_01).");
            return false;
        }

        StepSessionResult result;
        try
        {
            result = await _session.RunStepAsync(order!, _progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine("Step run cancelled.");
            return false;
        }

        RenderResult(orderText, result);
        return false;
    }

    // ── Output helpers ────────────────────────────────────────────────────────────

    private void WritePrompt() => _output.Write("hydradata> ");

    private void WriteHelp()
    {
        _output.WriteLine("Commands:");
        _output.WriteLine("  :list           List all discovered steps in this session.");
        _output.WriteLine("  :run <order>    Run the step identified by <order> (e.g. :run 01_20).");
        _output.WriteLine("  <order>         Shorthand for :run <order> (e.g. 01_20).");
        _output.WriteLine("  :help           Show this help.");
        _output.WriteLine("  :quit / :q      Exit the session (exit code 0).");
        _output.WriteLine();
        _output.WriteLine("Order format: GG_SS or GG_SS_TT  (e.g. 01_20 or 01_20_01).");
        _output.WriteLine();
        _output.WriteLine("State is preserved between commands: running 01_10 then 01_20 then");
        _output.WriteLine("re-running 01_20 still sees the State written by 01_10.");
    }

    private void WriteList()
    {
        var steps = _session.KnownSteps;
        if (steps.Count == 0)
        {
            _output.WriteLine("(no steps discovered in this session)");
            return;
        }

        _output.WriteLine($"Steps ({steps.Count}):");
        foreach (var s in steps)
        {
            var sub = s.Order.SubStep is { } ss ? $"_{ss:D2}" : string.Empty;
            _output.WriteLine($"  {s.Order.Group:D2}_{s.Order.Step:D2}{sub}  {s.FileName}");
        }
    }

    private void RenderResult(string orderText, StepSessionResult result)
    {
        switch (result.Status)
        {
            case StepSessionStatus.NotFound:
                _output.WriteLine($"Step '{orderText}' not found. " +
                                  "Use :list to see discovered steps.");
                if (result.Message is not null)
                    _output.WriteLine($"  Detail: {result.Message}");
                return;

            case StepSessionStatus.NotValidated:
                _output.WriteLine($"Step '{orderText}' failed validation (not executed):");
                if (result.Message is not null)
                {
                    _output.WriteLine($"  {result.Message}");
                }
                else
                {
                    foreach (var d in result.Validation.Diagnostics)
                    {
                        var pos = d.Line > 0 ? $"({d.Line},{d.Column})" : "(file)";
                        _output.WriteLine($"  [{d.Code}] {d.ScriptName} {pos}: {d.Message}");
                    }
                }

                return;
        }

        // StepSessionStatus.Ran: validated and executed — Result is non-null by construction.
        // The property pattern binds it without a null-forgiving operator.
        if (result is not { Result: { } r })
            return;

        var verdict = $"{r.EffectiveSeverity}: {r.Result?.Message ?? "(no message)"}";
        _output.WriteLine($"Step '{orderText}' => {verdict} (committed={r.Committed})");

        if (!string.IsNullOrWhiteSpace(r.Output))
        {
            _output.WriteLine("  Output:");
            foreach (var outputLine in r.Output.Split('\n'))
            {
                var trimmed = outputLine.TrimEnd('\r');
                if (trimmed.Length > 0)
                    _output.WriteLine("    " + trimmed);
            }
        }
    }

    // ── Async read shim ───────────────────────────────────────────────────────────

    private Task<string?> ReadLineAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // TextReader.ReadLineAsync(CancellationToken) is .NET 7+; use the overload if available,
        // otherwise fall back to the synchronous path in a Task (acceptable for REPL latency).
        return _input.ReadLineAsync(ct).AsTask();
    }
}
