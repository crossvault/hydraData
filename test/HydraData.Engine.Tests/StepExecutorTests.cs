// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// S1.0 — locks the extracted per-step seam <see cref="StepExecutor.RunOneAsync"/> directly against the
/// fake gateway: an Ok step commits its slot; a Fail step rolls back and signals halt. This is the single
/// source of truth the batch engine and the future step-session share, so a tiny direct test guards it.
/// Runs in the console-capture collection because StepOutputCapture is process-global.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class StepExecutorTests
{
    private static (StepExecutor executor, FakeConnectionGateway gateway, EngineScaffold scaffold) New(
        bool allowUnsafeDirectAccess = false)
    {
        var scaffold = new EngineScaffold();
        var gateway = new FakeConnectionGateway();
        var options = new PumpOptions(
            scaffold.WorkspaceBase,
            PumpFolderPolicy.Empty,
            AllowUnsafeDirectAccess: allowUnsafeDirectAccess);
        var workspace = new Workspace(scaffold.WorkspaceBase, Guid.NewGuid(), options.Folders);
        var executor = new StepExecutor(options, new ScriptCompiler(), gateway, TimeProvider.System, workspace);
        return (executor, gateway, scaffold);
    }

    private static StepDescriptor Discover(EngineScaffold scaffold, string fileName)
    {
        var ctx = scaffold.Discover();
        return ctx.Steps.Single(s => s.FileName == fileName);
    }

    private static Task<(StepRunResult Result, bool Halt)> Run(
        StepExecutor executor, StepDescriptor step, CancellationToken ct) =>
        // The executor never reads files (one loaded-step model): the caller supplies the script text.
        executor.RunOneAsync(
            step,
            File.ReadAllText(step.FilePath),
            new PumpState(),
            new PumpState(),
            EngineScaffold.Extern(),
            PumpContextFactory.DefaultConnection,
            EngineScaffold.Connections(),
            progress: null,
            ct: ct);

    [Fact]
    public async Task Ok_step_commits_its_slot()
    {
        var (executor, gateway, scaffold) = New();
        using (scaffold)
        {
            scaffold.AddStep("01_10_ok.cs", "Execute(\"insert\"); return Ok();");
            var step = Discover(scaffold, "01_10_ok.cs");

            var (result, halt) = await Run(executor, step, TestContext.Current.CancellationToken);

            Assert.True(result.Ran);
            Assert.True(result.Committed);
            Assert.Equal(Severity.Success, result.EffectiveSeverity);
            Assert.False(halt);
            Assert.Equal(1, gateway.Slots[0].Commits);
            Assert.Equal(0, gateway.Slots[0].Rollbacks);
        }
    }

    [Fact]
    public async Task Fail_step_rolls_back_and_signals_halt()
    {
        var (executor, gateway, scaffold) = New();
        using (scaffold)
        {
            scaffold.AddStep("01_10_fail.cs", "Execute(\"x\"); return Fail(\"boom\");");
            var step = Discover(scaffold, "01_10_fail.cs");

            var (result, halt) = await Run(executor, step, TestContext.Current.CancellationToken);

            Assert.True(result.Ran);
            Assert.False(result.Committed);
            Assert.Equal(Severity.Error, result.EffectiveSeverity);
            Assert.True(halt); // @haltOnError defaults to true
            Assert.Equal(1, gateway.Slots[0].Rollbacks);
            Assert.Equal(0, gateway.Slots[0].Commits);
        }
    }

    [Fact]
    public async Task Unsafe_step_can_call_Raw_when_engine_also_grants_access()
    {
        // Coverage-only: locks the existing StepExecutor dual-consent wiring end-to-end.
        var (executor, _, scaffold) = New(allowUnsafeDirectAccess: true);
        using (scaffold)
        {
            scaffold.AddStep("01_10_unsafe.cs", "// @unsafe: true\nRaw(); return Ok();");
            var step = Discover(scaffold, "01_10_unsafe.cs");

            var (result, halt) = await Run(executor, step, TestContext.Current.CancellationToken);

            Assert.True(result.Ran);
            Assert.True(result.Committed);
            Assert.Equal(Severity.Success, result.EffectiveSeverity);
            Assert.False(halt);
        }
    }

    [Fact]
    public async Task Unsafe_step_calling_Raw_without_engine_grant_is_runtime_error()
    {
        // Coverage-only defensive runtime gate; normal batch execution rejects this earlier as PUMP010.
        var (executor, _, scaffold) = New(allowUnsafeDirectAccess: false);
        using (scaffold)
        {
            scaffold.AddStep("01_10_unsafe.cs", "// @unsafe: true\nRaw(); return Ok();");
            var step = Discover(scaffold, "01_10_unsafe.cs");

            var (result, halt) = await Run(executor, step, TestContext.Current.CancellationToken);

            Assert.True(result.Ran);
            Assert.False(result.Committed);
            Assert.Equal(Severity.Error, result.EffectiveSeverity);
            Assert.NotNull(result.Result);
            Assert.Contains("Raw access requires", result.Result!.Message, StringComparison.Ordinal);
            Assert.True(halt);
        }
    }
}
