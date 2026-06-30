// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Spectre.Console;

namespace HydraData.Host;

/// <summary>
/// Streams <see cref="PumpProgress"/> to the console and renders a run summary. Honours runtime contract
/// section 19 / the production checklist "Spectre nur bei TTY": at a real terminal it may render a Spectre
/// table; when stdout is redirected (CI/scheduler) it emits plain, fully-capturable text and never uses
/// Spectre's interactive surface (Live/Status/animated bars/prompts).
/// </summary>
/// <remarks>
/// TTY detection is <see cref="System.Console.IsOutputRedirected"/>. The presenter implements
/// <see cref="IProgress{T}"/> directly so it can be passed straight to
/// <see cref="IPumpEngine.ExecuteAsync"/>; reporting is synchronous (order matters).
/// </remarks>
public sealed class ConsolePresenter : IProgress<PumpProgress>
{
    private readonly TextWriter _out;
    private readonly bool _isTty;

    /// <summary>Creates a presenter.</summary>
    /// <param name="output">Where plain text is written.</param>
    /// <param name="isInteractive">
    /// Whether a real terminal is attached. Defaults to <c>!System.Console.IsOutputRedirected</c>; tests pass
    /// <see langword="false"/> to assert the non-TTY path.
    /// </param>
    public ConsolePresenter(TextWriter? output = null, bool? isInteractive = null)
    {
        _out = output ?? Console.Out;
        _isTty = isInteractive ?? !Console.IsOutputRedirected;
    }

    /// <summary>Whether the presenter considers itself attached to an interactive terminal.</summary>
    public bool IsInteractive => _isTty;

    /// <inheritdoc />
    public void Report(PumpProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Step progress is streamed as plain text in BOTH modes: it is a running log, not interactive UI, so
        // it stays fully capturable. Spectre is only used for the optional end-of-run summary table at a TTY.
        switch (value.Phase)
        {
            case PumpPhase.Discovered:
                _out.WriteLine("Discovery complete.");
                break;
            case PumpPhase.Validated:
                _out.WriteLine("Validation complete.");
                break;
            case PumpPhase.StepStarted:
                _out.WriteLine($"  > {value.ScriptName}");
                break;
            case PumpPhase.StepOutput:
                if (!string.IsNullOrEmpty(value.Message))
                    _out.WriteLine(Indent(value.Message));
                break;
            case PumpPhase.StepFinished:
                _out.WriteLine($"  < {value.ScriptName}: {value.Result?.Severity.ToString() ?? "n/a"}");
                break;
            case PumpPhase.RunFinished:
                _out.WriteLine("Run finished.");
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Renders the final run summary. At a TTY a Spectre table is drawn; otherwise a plain text block is
    /// written (no Spectre). Either way the host's exit code comes from <see cref="RunReport.ExitCode"/>,
    /// not from this output.
    /// </summary>
    /// <param name="report">The completed run report.</param>
    public void RenderSummary(RunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (_isTty)
        {
            RenderSpectreSummary(report);
            return;
        }

        RenderPlainSummary(report);
    }

    private void RenderPlainSummary(RunReport report)
    {
        _out.WriteLine();
        _out.WriteLine($"Run {report.RunId} -> exit code {report.ExitCode}");

        if (report.PreflightErrors.Count > 0)
        {
            _out.WriteLine($"Preflight errors ({report.PreflightErrors.Count}):");
            foreach (var d in report.PreflightErrors)
                _out.WriteLine($"  [{d.Code}] {d.ScriptName} ({d.Line},{d.Column}): {d.Message}");
            return;
        }

        _out.WriteLine($"Steps ({report.Steps.Count}):");
        foreach (var s in report.Steps)
        {
            var verdict = s.Ran ? s.EffectiveSeverity.ToString() : "not-run";
            _out.WriteLine($"  {s.ScriptName}: {verdict} (committed={s.Committed})");
        }
    }

    private static void RenderSpectreSummary(RunReport report)
    {
        // Static (non-interactive) Spectre rendering only: AnsiConsole.Write(table) prints once. No Live,
        // Status, Progress or Prompt is used, consistent with "Spectre nur bei TTY" being a presentation
        // nicety, not an interactive dependency.
        if (report.PreflightErrors.Count > 0)
        {
            var errors = new Table().AddColumns("Code", "Script", "Pos", "Message");
            foreach (var d in report.PreflightErrors)
                errors.AddRow(d.Code, d.ScriptName, $"{d.Line},{d.Column}", Markup.Escape(d.Message));
            AnsiConsole.Write(errors);
            AnsiConsole.MarkupLine($"Run [bold]{report.RunId}[/] -> exit code [bold]{report.ExitCode}[/]");
            return;
        }

        var table = new Table().AddColumns("Step", "Verdict", "Committed");
        foreach (var s in report.Steps)
        {
            var verdict = s.Ran ? s.EffectiveSeverity.ToString() : "not-run";
            table.AddRow(Markup.Escape(s.ScriptName), verdict, s.Committed ? "yes" : "no");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Run [bold]{report.RunId}[/] -> exit code [bold]{report.ExitCode}[/]");
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => "    " + l.TrimEnd('\r')));
}
