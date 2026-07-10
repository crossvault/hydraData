// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HydraData.Host.Tests;

/// <summary>
/// Non-TTY behaviour: a redirected/CI presenter streams
/// plain, capturable text and renders a plain summary — no Spectre interactive surface.
/// </summary>
public class ConsolePresenterTests
{
    [Fact]
    public void Non_tty_presenter_reports_itself_non_interactive()
    {
        var presenter = new ConsolePresenter(new StringWriter(), isInteractive: false);

        Assert.False(presenter.IsInteractive);
    }

    [Fact]
    public void Streams_step_progress_as_plain_text()
    {
        var sw = new StringWriter();
        var presenter = new ConsolePresenter(sw, isInteractive: false);

        presenter.Report(new PumpProgress(PumpPhase.Discovered));
        presenter.Report(new PumpProgress(PumpPhase.Validated));
        presenter.Report(new PumpProgress(PumpPhase.StepStarted, ScriptName: "01_10_a.cs"));
        presenter.Report(new PumpProgress(PumpPhase.StepOutput, ScriptName: "01_10_a.cs", Message: "hello"));
        presenter.Report(new PumpProgress(PumpPhase.StepFinished, ScriptName: "01_10_a.cs", Result: StepResult.Ok()));
        presenter.Report(new PumpProgress(PumpPhase.RunFinished));

        var text = sw.ToString();
        Assert.Contains("Discovery complete.", text);
        Assert.Contains("Validation complete.", text);
        Assert.Contains("01_10_a.cs", text);
        Assert.Contains("hello", text);
        Assert.Contains("Success", text);
        Assert.Contains("Run finished.", text);
    }

    [Fact]
    public void Defaults_interactivity_from_console_redirection_state()
    {
        // Under the test host stdout is redirected, so the default presenter must report non-interactive,
        // proving the run never engages Spectre's interactive surface in CI.
        var presenter = new ConsolePresenter(new StringWriter());

        Assert.Equal(!Console.IsOutputRedirected, presenter.IsInteractive);
    }

    /// <summary>
    /// Drives a real validation-failure run so the presenter's RenderSummary receives a RunReport with
    /// non-empty PreflightErrors. Confirms the non-TTY path emits the preflight diagnostics as capturable
    /// plain text (no Spectre markup) — runtime contract
    /// </summary>
    [Fact]
    public async Task RenderSummary_preflight_errors_emit_as_plain_text_on_non_tty()
    {
        using var scaffold = new HostScaffold()
            .AddStep("01_10_typo.cs", "Qery(); return Ok();"); // CS0103 → preflight error

        var exit = await scaffold.RunAsync(out var sw, TestContext.Current.CancellationToken);
        var text = sw.ToString();

        Assert.Equal(1, exit);
        // The non-TTY summary must include preflight-error details (code + script name + message).
        Assert.Contains("Preflight errors", text);
        Assert.Contains("01_10_typo.cs", text);
        // Ensure no Spectre markup control sequences leaked into the capturable output.
        Assert.DoesNotContain("[bold]", text);
    }

    /// <summary>
    /// Drives a real run that ends in a runtime failure so the non-TTY summary renders a mix of severities:
    /// a WARN step that commits and a FAIL step that rolls back. Asserts the plain summary reflects
    /// Warning/Error per-step verdicts AND committed=True for the warning vs committed=False for the
    /// rolled-back failing step — not just the all-Ok happy path.
    /// </summary>
    [Fact]
    public async Task RenderSummary_renders_warn_committed_and_fail_rolledback_on_non_tty()
    {
        using var scaffold = new HostScaffold()
            // WARN commits; the subsequent FAIL rolls back (haltOnError default true halts after it, but it
            // is the last step so the run ends there with exit 2).
            .AddStep("01_10_warn.cs", "return Warn(\"heads up\");")
            .AddStep("01_20_fail.cs", "return Fail(\"boom\");");

        var exit = await scaffold.RunAsync(out var sw, TestContext.Current.CancellationToken);
        var text = sw.ToString();

        Assert.Equal(2, exit); // a Fail drives the runtime exit code

        // Plain summary lines are "<script>: <Verdict> (committed=<bool>)".
        Assert.Contains("01_10_warn.cs: Warning (committed=True)", text);
        Assert.Contains("01_20_fail.cs: Error (committed=False)", text);
        // No Spectre markup leaked into the capturable output.
        Assert.DoesNotContain("[bold]", text);
    }

    [Fact]
    public async Task Tty_summary_captures_bracket_slug_in_preflight_errors()
    {
        const string scriptName = "01_10_[kunden]_x.cs";
        using var scaffold = new HostScaffold()
            .AddStep(scriptName, "Qery(); return Ok();");
        var report = await ExecuteWithoutPresenterAsync(scaffold);
        var output = new StringWriter();
        var presenter = new ConsolePresenter(output, isInteractive: true);

        var exception = Record.Exception(() => presenter.RenderSummary(report));

        Assert.NotEmpty(report.PreflightErrors);
        Assert.Null(exception);
        Assert.Contains(scriptName, output.ToString());
    }

    [Fact]
    public async Task Tty_summary_captures_bracket_slug_in_steps()
    {
        const string scriptName = "01_10_[kunden]_x.cs";
        using var scaffold = new HostScaffold()
            .AddStep(scriptName, "return Ok();");
        var report = await ExecuteWithoutPresenterAsync(scaffold);
        var output = new StringWriter();
        var presenter = new ConsolePresenter(output, isInteractive: true);

        var exception = Record.Exception(() => presenter.RenderSummary(report));

        Assert.Single(report.Steps);
        Assert.Null(exception);
        Assert.Contains(scriptName, output.ToString());
    }

    private static Task<RunReport> ExecuteWithoutPresenterAsync(HostScaffold scaffold)
    {
        var engine = new PumpEngine(
            new PumpOptions(scaffold.WorkspaceBase, PumpFolderPolicy.Empty),
            logger: NullLogger.Instance);
        var context = new DiscoveryService().Discover([scaffold.ScriptDir]);
        return engine.ExecuteAsync(
            context,
            ExternContext.FromValues(new Dictionary<string, object?>()),
            scaffold.Connections(),
            ct: TestContext.Current.CancellationToken);
    }
}
