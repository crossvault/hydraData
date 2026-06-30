// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T09.5 — <see cref="PumpProgress"/> phase ordering and the rule that <see cref="PumpProgress.Result"/>
/// is set only at <see cref="PumpPhase.StepFinished"/>.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class PumpProgressTests
{
    private sealed class Recorder : IProgress<PumpProgress>
    {
        public List<PumpProgress> Items { get; } = [];

        // Engine reports synchronously, so appending here preserves order.
        public void Report(PumpProgress value) => Items.Add(value);
    }

    private static PumpEngine NewEngine(EngineScaffold scaffold, FakeConnectionGateway gateway)
    {
        var options = new PumpOptions(scaffold.WorkspaceBase, PumpFolderPolicy.Empty);
        return new PumpEngine(options, new FakeGuidProvider(Guid.NewGuid()), timeProvider: null, gateway, logger: null);
    }

    [Fact]
    public async Task Phases_are_reported_in_order_per_step()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "Print(\"hello from a\"); return Ok();")
            .AddStep("01_20_b.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);
        var recorder = new Recorder();

        await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            recorder, TestContext.Current.CancellationToken);

        var phases = recorder.Items.Select(p => p.Phase).ToList();

        // Overall shape: Discovered, Validated, (per step ...), RunFinished.
        Assert.Equal(PumpPhase.Discovered, phases[0]);
        Assert.Equal(PumpPhase.Validated, phases[1]);
        Assert.Equal(PumpPhase.RunFinished, phases[^1]);

        // Step a: started, output (it printed), finished.
        var aStart = recorder.Items.FindIndex(p => p.Phase == PumpPhase.StepStarted && p.ScriptName == "01_10_a.cs");
        var aOutput = recorder.Items.FindIndex(p => p.Phase == PumpPhase.StepOutput && p.ScriptName == "01_10_a.cs");
        var aFinish = recorder.Items.FindIndex(p => p.Phase == PumpPhase.StepFinished && p.ScriptName == "01_10_a.cs");
        Assert.True(aStart >= 0 && aOutput > aStart && aFinish > aOutput);

        // Step b: started then finished, after step a finished.
        var bStart = recorder.Items.FindIndex(p => p.Phase == PumpPhase.StepStarted && p.ScriptName == "01_20_b.cs");
        var bFinish = recorder.Items.FindIndex(p => p.Phase == PumpPhase.StepFinished && p.ScriptName == "01_20_b.cs");
        Assert.True(bStart > aFinish && bFinish > bStart);
    }

    [Fact]
    public async Task Result_is_set_only_at_step_finished()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "Print(\"x\"); return Warn(\"w\");");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);
        var recorder = new Recorder();

        await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            recorder, TestContext.Current.CancellationToken);

        foreach (var p in recorder.Items)
        {
            if (p.Phase == PumpPhase.StepFinished)
                Assert.NotNull(p.Result);
            else
                Assert.Null(p.Result);
        }

        var finished = recorder.Items.Single(p => p.Phase == PumpPhase.StepFinished);
        Assert.Equal(Severity.Warning, finished.Result!.Severity);
    }

    [Fact]
    public async Task StepOutput_carries_the_captured_text()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_a.cs", "Print(\"captured line\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var engine = NewEngine(scaffold, gateway);
        var recorder = new Recorder();

        await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            recorder, TestContext.Current.CancellationToken);

        var output = recorder.Items.Single(p => p.Phase == PumpPhase.StepOutput);
        Assert.Contains("captured line", output.Message);
    }
}
