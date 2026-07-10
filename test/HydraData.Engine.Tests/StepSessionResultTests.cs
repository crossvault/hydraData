// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

public class StepSessionResultTests
{
    private static ValidationReport PassingReport() => new([]);

    private static ValidationReport FailingReport() => new(
        [new ScriptDiagnostic("01_10_x.cs", 1, 1, "CS1002", Severity.Error, "; expected")]);

    private static StepRunResult SuccessfulRun() => new(
        "01_10_x.cs",
        StepResult.Ok(),
        Severity.Success,
        Committed: true,
        Output: string.Empty);

    [Fact]
    public void NotFound_requires_message() =>
        Assert.Throws<ArgumentNullException>(() => StepSessionResult.NotFound(null!));

    [Fact]
    public void Unreadable_requires_message() =>
        Assert.Throws<ArgumentNullException>(() => StepSessionResult.Unreadable(null!));

    [Fact]
    public void Invalid_requires_report() =>
        Assert.Throws<ArgumentNullException>(() => StepSessionResult.Invalid(null!));

    [Fact]
    public void Invalid_rejects_passing_report()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            StepSessionResult.Invalid(PassingReport()));

        Assert.Contains("failing report", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ran_requires_report() =>
        Assert.Throws<ArgumentNullException>(() =>
            StepSessionResult.Ran(null!, SuccessfulRun()));

    [Fact]
    public void Ran_requires_result() =>
        Assert.Throws<ArgumentNullException>(() =>
            StepSessionResult.Ran(PassingReport(), null!));

    [Fact]
    public void Ran_rejects_failing_report()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            StepSessionResult.Ran(FailingReport(), SuccessfulRun()));

        Assert.Contains("passing report", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
