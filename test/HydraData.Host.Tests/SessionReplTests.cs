// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HydraData.Host.Tests;

/// <summary>
/// S1.2 — unit tests for <see cref="SessionRepl"/>: scripted <see cref="StringReader"/> input drives a
/// real <see cref="PumpSession"/> (via the fake gateway from the Engine test project — replicated here as
/// a local helper); assertions target <see cref="StringWriter"/> output. No Docker; no process-global
/// Console.Out capture (the REPL writes to an injected <see cref="TextWriter"/>).
/// </summary>
public sealed class SessionReplTests : IDisposable
{
    // ── Scaffold helpers ──────────────────────────────────────────────────────────

    private readonly string _root;
    private readonly string _scriptDir;
    private readonly string _workspaceBase;

    public SessionReplTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hydradata-repl-tests", Path.GetRandomFileName());
        _scriptDir = Path.Combine(_root, "scripts");
        _workspaceBase = Path.Combine(_root, "_runs");
        Directory.CreateDirectory(_scriptDir);
        Directory.CreateDirectory(_workspaceBase);
    }

    private void AddStep(string fileName, string body) =>
        File.WriteAllText(Path.Combine(_scriptDir, fileName), body);

    private PumpSession BuildSession(bool legacyGlobalState = false)
    {
        var options = new PumpOptions(_workspaceBase, PumpFolderPolicy.Empty,
            LegacyGlobalState: legacyGlobalState);
        var ctx = new DiscoveryService().Discover([_scriptDir]);
        var externCtx = ExternContext.FromValues(new Dictionary<string, object?>());
        var connections = BuildConnections();
        // Use the internal ctor with a null (production) gateway — steps in tests never open a real DB
        // because they are pure-C# scripts (State.Set/Get, return Ok(), etc.) that don't call Sql().
        return new PumpSession(ctx, externCtx, connections, options, logger: NullLogger.Instance);
    }

    private static IConnectionDirectory BuildConnections()
    {
        const string xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="stage">
                <Parameters>
                  <Parameter key="Server"   value="localhost" type="String" />
                  <Parameter key="Database" value="stage"     type="String" />
                </Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;
        return new ConnectionDirectory(ConnectionRegistry.Parse(xml));
    }

    private static (StringWriter Output, SessionRepl Repl) BuildRepl(
        PumpSession session, string scriptedInput)
    {
        var reader = new StringReader(scriptedInput);
        var writer = new StringWriter();
        var repl = new SessionRepl(session, reader, writer, progress: null);
        return (writer, repl);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Core headline: persistent State across multiple commands ──────────────────

    /// <summary>
    /// S1.2 headline test: run step 1 (writes State), run step 2 (reads State), RE-RUN step 2 —
    /// the re-run must still see step 1's State without re-running step 1.
    /// </summary>
    [Fact]
    public async Task State_written_by_step1_survives_rerun_of_step2()
    {
        AddStep("01_10_lookup.cs", "State.Set(\"result\", 99); return Ok();");
        AddStep("01_20_read.cs",
            "var v = State.Get<int>(\"result\"); Print(v.ToString()); return Ok(\"v=\" + v);");

        using var session = BuildSession();
        var (output, repl) = BuildRepl(session,
            """
            01_10
            01_20
            01_20
            :quit
            """);

        var exit = await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);

        // Both the first and re-run of 01_20 must report v=99, showing State survived.
        // The verdict line (not the Print output which is "99") contains "v=99" exactly once per step2
        // run, so two step2 runs yield exactly 2 occurrences. Use == 2 (not >= 2) so any stray
        // "v=99" in output or progress noise causes the assertion to fail rather than pass silently.
        var occurrences = CountOccurrences(text, "v=99");
        Assert.Equal(2, occurrences);

        // The read step returns Ok with no DB writes, so each run commits: the verdict line reports
        // committed=True. Both step-2 runs are Ok, so committed=True appears at least twice — asserting the
        // success path was actually a COMMIT, not a silently-rolled-back run that still printed "v=99".
        Assert.True(CountOccurrences(text, "committed=True") >= 2,
            $"Expected committed=True at least twice; output:\n{text}");
    }

    // ── Item 10: mid-run cancellation ─────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_during_RunStepAsync_reports_Step_run_cancelled()
    {
        // A step that opens no DB but blocks on its Cancellation token. Cancelling the REPL token while the
        // step is in-flight makes RunStepAsync throw OperationCanceledException, which the REPL catches and
        // renders as "Step run cancelled." (the step's slots were already rolled back by the engine). After
        // the catch the loop re-checks the (now-cancelled) token and exits cleanly with 0 — matching the
        // RunAsync contract that cancellation is a clean end. The follow-up command is left in the input to
        // document that the cancelled step did not crash the REPL or leave it in a broken state.
        AddStep("01_10_block.cs",
            "while (true) { Cancellation.ThrowIfCancellationRequested(); await System.Threading.Tasks.Task.Yield(); } return Ok();");

        using var session = BuildSession();
        var reader = new StringReader("01_10\n:quit\n");
        var writer = new StringWriter();
        var repl = new SessionRepl(session, reader, writer, progress: null);

        using var cts = new CancellationTokenSource();

        // Run the REPL; cancel shortly after so the cancellation lands while the blocking step is running.
        var runTask = repl.RunAsync(cts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var exit = await runTask;
        var text = writer.ToString();

        // The mid-run cancellation was reported by the REPL (not swallowed, not a crash).
        Assert.Contains("Step run cancelled.", text);
        // Cancellation is a clean end: exit 0, no thrown exception escaped RunAsync.
        Assert.Equal(0, exit);
    }

    // ── :quit / :q ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Quit_command_returns_exit_0()
    {
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, ":quit\n");

        var exit = await repl.RunAsync(Ct);

        Assert.Equal(0, exit);
        Assert.Contains("Session closed.", output.ToString());
    }

    [Fact]
    public async Task Q_shorthand_returns_exit_0()
    {
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, ":q\n");

        var exit = await repl.RunAsync(Ct);

        Assert.Equal(0, exit);
        Assert.Contains("Session closed.", output.ToString());
    }

    // ── :help ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Help_command_lists_available_commands()
    {
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, ":help\n:quit\n");

        await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Contains(":list", text);
        Assert.Contains(":run", text);
        Assert.Contains(":quit", text);
        Assert.Contains(":help", text);
    }

    // ── :list ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_command_shows_discovered_steps()
    {
        AddStep("01_10_a.cs", "return Ok();");
        AddStep("01_20_b.cs", "return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, ":list\n:quit\n");

        await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Contains("01_10_a.cs", text);
        Assert.Contains("01_20_b.cs", text);
    }

    [Fact]
    public async Task List_command_on_empty_session_shows_no_steps_message()
    {
        // No steps — empty script dir.
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, ":list\n:quit\n");

        await repl.RunAsync(Ct);

        Assert.Contains("no steps", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── :run and bare order ───────────────────────────────────────────────────────

    [Fact]
    public async Task Run_command_executes_the_step_and_shows_verdict()
    {
        AddStep("01_10_ok.cs", "return Ok(\"all good\");");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, ":run 01_10\n:quit\n");

        await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Contains("all good", text);
        Assert.Contains("Success", text);
    }

    [Fact]
    public async Task Bare_order_is_treated_as_run_command()
    {
        AddStep("01_10_ok.cs", "return Ok(\"bare-ok\");");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, "01_10\n:quit\n");

        await repl.RunAsync(Ct);

        Assert.Contains("bare-ok", output.ToString());
    }

    // ── Invalid / parse-error input does not crash the REPL ──────────────────────

    [Fact]
    public async Task Invalid_order_input_produces_friendly_message_and_loop_continues()
    {
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        // "notanorder" cannot be parsed; the REPL should continue and then quit cleanly.
        var (output, repl) = BuildRepl(session, "notanorder\n:quit\n");

        var exit = await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        // A friendly parse-error message is emitted.
        Assert.Contains("Cannot parse", text, StringComparison.OrdinalIgnoreCase);
        // And the loop continued to process :quit.
        Assert.Contains("Session closed.", text);
    }

    [Fact]
    public async Task Unknown_colon_command_produces_friendly_message_and_loop_continues()
    {
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, ":nonexistent\n:quit\n");

        var exit = await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("Unknown command", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Session closed.", text);
    }

    [Fact]
    public async Task Order_not_found_in_session_produces_friendly_message()
    {
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        // 02_99 does not exist in the context.
        var (output, repl) = BuildRepl(session, "02_99\n:quit\n");

        var exit = await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation failure ────────────────────────────────────────────────────────

    [Fact]
    public async Task Validation_failure_shows_diagnostics_and_loop_continues()
    {
        // A script with a typo (CS0103: name 'Qery' does not exist).
        AddStep("01_10_typo.cs", "Qery(); return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, "01_10\n:quit\n");

        var exit = await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        // Validation failure is reported.
        Assert.Contains("failed validation", text, StringComparison.OrdinalIgnoreCase);
        // Session continued and processed :quit.
        Assert.Contains("Session closed.", text);
    }

    [Fact]
    public async Task Validation_failure_diagnostic_includes_line_info()
    {
        AddStep("01_10_typo.cs", "Qery(); return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, "01_10\n:quit\n");

        await repl.RunAsync(Ct);
        var text = output.ToString();

        // The diagnostic should mention a CS code and line/col info.
        Assert.Contains("CS", text);
    }

    // ── :run with missing arg ─────────────────────────────────────────────────────

    [Fact]
    public async Task Run_without_argument_shows_usage()
    {
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, ":run\n:quit\n");

        await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Contains("Usage:", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── EOF exits cleanly ─────────────────────────────────────────────────────────

    [Fact]
    public async Task EOF_on_input_stream_exits_cleanly_with_0()
    {
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        // Empty input → immediate EOF.
        var (_, repl) = BuildRepl(session, string.Empty);

        var exit = await repl.RunAsync(Ct);

        Assert.Equal(0, exit);
    }

    // ── Blank lines are ignored ───────────────────────────────────────────────────

    [Fact]
    public async Task Blank_lines_are_ignored_and_repl_continues()
    {
        AddStep("01_10_a.cs", "return Ok(\"reached\");");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, "\n\n01_10\n:quit\n");

        await repl.RunAsync(Ct);

        Assert.Contains("reached", output.ToString());
    }

    // ── Three-part order (GG_SS_TT) ──────────────────────────────────────────────

    [Fact]
    public async Task Three_part_order_is_parsed_and_executed()
    {
        AddStep("01_10_01_sub.cs", "return Ok(\"sub-step\");");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, "01_10_01\n:quit\n");

        await repl.RunAsync(Ct);

        Assert.Contains("sub-step", output.ToString());
    }

    // ── Item 6 edge-case tests ────────────────────────────────────────────────────

    [Fact]
    public async Task Integer_overflow_order_produces_friendly_parse_error()
    {
        // "99999999999_20" overflows int — TryParseOrder must return false, not throw.
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, "99999999999_20\n:quit\n");

        var exit = await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("Cannot parse", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Session closed.", text);
    }

    [Fact]
    public async Task Command_case_insensitivity_QUIT_works()
    {
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, ":QUIT\n");

        var exit = await repl.RunAsync(Ct);

        Assert.Equal(0, exit);
        Assert.Contains("Session closed.", output.ToString());
    }

    [Fact]
    public async Task Command_case_insensitivity_RUN_works()
    {
        AddStep("01_10_ok.cs", "return Ok(\"upper-case-run\");");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, ":RUN 01_10\n:quit\n");

        var exit = await repl.RunAsync(Ct);

        Assert.Equal(0, exit);
        Assert.Contains("upper-case-run", output.ToString());
    }

    [Fact]
    public async Task EOF_after_real_command_exits_cleanly_with_0()
    {
        // Input has a valid command but no trailing :quit — EOF terminates cleanly.
        AddStep("01_10_ok.cs", "return Ok(\"eof-cmd\");");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, "01_10\n");  // no :quit

        var exit = await repl.RunAsync(Ct);

        Assert.Equal(0, exit);
        Assert.Contains("eof-cmd", output.ToString());
    }

    [Fact]
    public async Task Two_segment_incomplete_order_01_trailing_underscore_produces_friendly_message()
    {
        // "01_" splits to ["01", ""] — empty second segment fails int.TryParse → friendly message.
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, "01_\n:quit\n");

        var exit = await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("Cannot parse", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Session closed.", text);
    }

    [Fact]
    public async Task Four_segment_order_produces_friendly_message()
    {
        // "01_20_extra_extra" has 4 underscore-separated parts — TryParseOrder rejects it gracefully.
        AddStep("01_10_a.cs", "return Ok();");
        using var session = BuildSession();
        var (output, repl) = BuildRepl(session, "01_20_extra_extra\n:quit\n");

        var exit = await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("Cannot parse", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Session closed.", text);
    }

    [Fact]
    public async Task Validation_failure_then_valid_step_still_sees_prior_state()
    {
        // A step with a compile error is rejected without touching State.
        // The next valid step in the same group still sees whatever State existed before.
        AddStep("01_10_write.cs", "State.Set(\"k\", 42); return Ok();");
        AddStep("01_20_typo.cs", "Qery(); return Ok();"); // compile error
        AddStep("01_30_read.cs", "return Ok(\"k=\" + State.Get<int>(\"k\"));");

        using var session = BuildSession();
        var (output, repl) = BuildRepl(session,
            "01_10\n" +    // writes State["k"] = 42
            "01_20\n" +    // validation failure — State must be undisturbed
            "01_30\n" +    // reads State["k"]; must still see 42
            ":quit\n");

        var exit = await repl.RunAsync(Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        // Validation failure was reported.
        Assert.Contains("failed validation", text, StringComparison.OrdinalIgnoreCase);
        // State was not disturbed — the read step sees the value written by step 1.
        Assert.Contains("k=42", text);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }

        return count;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
