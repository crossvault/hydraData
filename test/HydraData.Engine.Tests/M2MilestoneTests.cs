// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// M2 Definition of Done: a trivial in-memory step sequence runs through the <see cref="StepRunner"/>
/// with the correct commit/rollback decision and output capture, using the fake DB gateway. No real
/// database is required.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public class M2MilestoneTests
{
    private sealed record Step(string Code, bool ExpectCommit);

    [Fact]
    public async Task Trivial_step_sequence_runs_with_correct_policy_and_capture()
    {
        var steps = new[]
        {
            new Step("Print(\"a\"); Execute(\"x\"); return Ok();", ExpectCommit: true),
            new Step("Print(\"b\"); Execute(\"x\"); return Warn(\"w\");", ExpectCommit: true),
            new Step("Print(\"c\"); Execute(\"x\"); return Fail(\"f\");", ExpectCommit: false),
            new Step("Print(\"d\"); Execute(\"x\"); Expect(false, \"x\"); return Ok();", ExpectCommit: false),
            new Step("Print(\"e\"); Execute(\"x\"); throw new System.Exception();", ExpectCommit: false),
        };

        var state = new PumpState();
        var shared = new PumpState();
        var ctx = ExternContext.FromValues(new Dictionary<string, object?>());

        var index = 0;
        foreach (var step in steps)
        {
            // Fresh gateway per step: one transaction per connection per step.
            var gateway = new FakeConnectionGateway();
            var runner = new StepRunner(new ScriptCompiler(), gateway);

            var outcome = await runner.RunAsync(
                step.Code, state, shared, ctx, PumpContextFactory.DefaultConnection, unsafeAllowed: false,
                ct: TestContext.Current.CancellationToken);

            var expectedChar = (char)('a' + index);
            Assert.Contains(expectedChar.ToString(), outcome.Output, StringComparison.Ordinal);

            var slot = Assert.Single(gateway.Slots);
            Assert.Equal(step.ExpectCommit, outcome.Committed);
            Assert.Equal(step.ExpectCommit ? 1 : 0, slot.Commits);
            Assert.Equal(step.ExpectCommit ? 0 : 1, slot.Rollbacks);
            Assert.True(slot.Disposed);

            index++;
        }
    }
}
