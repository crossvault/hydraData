// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Host.Tests;

/// <summary>
/// S1.2 — wiring tests for <see cref="SessionBootstrap"/>: configuration loading, dispatch to the REPL,
/// exit-code contract. Mirrors the structure of <see cref="HostBootstrapTests"/> for the session mode.
/// </summary>
public sealed class SessionBootstrapTests : IDisposable
{
    private readonly string _baseDir;

    public SessionBootstrapTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "hydradata-session-bootstrap", Path.GetRandomFileName());
        Directory.CreateDirectory(_baseDir);
    }

    private void WriteAppSettings() =>
        File.WriteAllText(Path.Combine(_baseDir, "appsettings.json"), """
            {
              "Pump": {
                "WorkspaceBase": "./_runs",
                "AllowUnsafeDirectAccess": false,
                "ReadAllowlist": [ "./input" ],
                "WriteAllowlist": [ "./output" ],
                "StepTimeoutSeconds": 120,
                "RunDirRetentionDays": 14,
                "ScriptFolders": [ "./scripts" ],
                "ConnectionsFile": "./connections.xml"
              }
            }
            """);

    private void WriteConnections() =>
        File.WriteAllText(Path.Combine(_baseDir, "connections.xml"), HostScaffold.ValidConnectionsXml);

    private void AddScript(string fileName, string body)
    {
        var scriptDir = Path.Combine(_baseDir, "scripts");
        Directory.CreateDirectory(scriptDir);
        File.WriteAllText(Path.Combine(scriptDir, fileName), body);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Happy path: REPL exits cleanly ───────────────────────────────────────────

    [Fact]
    public async Task Happy_path_session_runs_step_and_returns_exit_0()
    {
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_a.cs", "return Ok(\"hello-session\");");

        var input = new StringReader("01_10\n:quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("hello-session", text);
    }

    [Fact]
    public async Task Session_exits_cleanly_on_quit_without_running_any_steps()
    {
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_a.cs", "return Ok();");

        var input = new StringReader(":quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);

        Assert.Equal(0, exit);
        Assert.Contains("Session closed.", output.ToString());
    }

    [Fact]
    public async Task Session_shows_discovered_step_count_on_start()
    {
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_a.cs", "return Ok();");
        AddScript("01_20_b.cs", "return Ok();");

        var input = new StringReader(":quit\n");
        var output = new StringWriter();

        await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);
        var text = output.ToString();

        // Header line mentions how many steps were discovered.
        Assert.Contains("2 step(s)", text);
    }

    // ── Configuration failure → exit 1 ───────────────────────────────────────────

    [Fact]
    public async Task Missing_appsettings_json_maps_to_exit_1()
    {
        // No appsettings.json written.
        var input = new StringReader(":quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Missing_connections_file_maps_to_exit_1()
    {
        WriteAppSettings();
        AddScript("01_10_a.cs", "return Ok();");
        // No connections.xml.

        var input = new StringReader(":quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Missing_script_folder_maps_to_exit_1()
    {
        WriteAppSettings();
        WriteConnections();
        // No ./scripts folder created.

        var input = new StringReader(":quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Malformed_appsettings_json_maps_to_exit_1()
    {
        File.WriteAllText(Path.Combine(_baseDir, "appsettings.json"), "{ not valid json }}}");

        var input = new StringReader(":quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);

        Assert.Equal(1, exit);
    }

    // ── Cancellation → exit 0 (session mode: clean end, not batch pass/fail) ────

    [Fact]
    public async Task Pre_cancelled_token_returns_exit_0()
    {
        // Session mode treats cancellation as a clean interactive end (exit 0), NOT as exit 2.
        // Contrast with HostBootstrap which uses exit 2 for cancellation (batch mode).
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_a.cs", "return Ok();");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var input = new StringReader(":quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, cts.Token);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Step_runs_successfully_then_quit_returns_exit_0()
    {
        // Tests the run-then-quit path: one step runs to completion, then :quit ends the session.
        // Cancellation is NOT exercised here — this is the clean sequential run path.
        // For genuine mid-run cancellation behaviour see the engine-level cancellation tests
        // (PumpSessionTests.Pre_cancelled_token_surfaces_cancellation_and_gate_is_released).
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_a.cs", "return Ok(\"step-ran\");");

        var input = new StringReader("01_10\n:quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);

        Assert.Equal(0, exit);
        // The step ran successfully.
        Assert.Contains("step-ran", output.ToString());
    }

    [Fact]
    public async Task Failing_step_in_session_still_exits_0_and_renders_the_failure()
    {
        // Session contract (distinct from batch): a step returning Fail does NOT make the session exit 2.
        // The session is an interactive dev tool — it renders the failed verdict and stays at exit 0 on
        // a clean :quit. Contrast HostBootstrapTests.Runtime_fail_returns_exit_2 (batch mode).
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_fail.cs", "return Fail(\"boom\");");

        var input = new StringReader("01_10\n:quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        // The failure verdict is rendered (severity Error + the message + committed=False).
        Assert.Contains("Error", text, StringComparison.Ordinal);
        Assert.Contains("boom", text, StringComparison.Ordinal);
        Assert.Contains("committed=False", text, StringComparison.Ordinal);
    }

    // ── State persistence across commands in one session ─────────────────────────

    [Fact]
    public async Task State_from_step1_is_visible_to_rerun_of_step2_via_session_bootstrap()
    {
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_write.cs", "State.Set(\"x\", 7); return Ok();");
        AddScript("01_20_read.cs", "return Ok(\"x=\" + State.Get<int>(\"x\"));");

        var input = new StringReader("01_10\n01_20\n01_20\n:quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        // "x=7" should appear at least twice (first run + re-run of step 2).
        var count = CountOccurrences(text, "x=7");
        Assert.True(count >= 2, $"Expected 'x=7' at least twice; output:\n{text}");
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
            if (Directory.Exists(_baseDir))
                Directory.Delete(_baseDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
