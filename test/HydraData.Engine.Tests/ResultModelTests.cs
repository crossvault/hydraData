// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Xunit;

namespace HydraData.Engine.Tests;

public class ResultModelTests
{
    [Fact]
    public void Ok_defaults_to_success_severity_and_OK_message()
    {
        var r = StepResult.Ok();
        Assert.Equal(Severity.Success, r.Severity);
        Assert.Equal("OK", r.Message);
        Assert.Null(r.Details);
    }

    [Fact]
    public void Warn_and_Fail_carry_message_and_details()
    {
        var details = new { rows = 3 };
        var warn = StepResult.Warn("careful", details);
        var fail = StepResult.Fail("boom", details);

        Assert.Equal(Severity.Warning, warn.Severity);
        Assert.Equal("careful", warn.Message);
        Assert.Same(details, warn.Details);

        Assert.Equal(Severity.Error, fail.Severity);
        Assert.Equal("boom", fail.Message);
        Assert.Same(details, fail.Details);
    }

    [Fact]
    public void Severity_is_ordered_success_lt_warning_lt_error()
    {
        Assert.True((int)Severity.Success < (int)Severity.Warning);
        Assert.True((int)Severity.Warning < (int)Severity.Error);
        Assert.Equal(0, (int)Severity.Success);
        Assert.Equal(2, (int)Severity.Error);
    }

    [Theory]
    [InlineData(Severity.Success, Severity.Warning, Severity.Warning)]
    [InlineData(Severity.Warning, Severity.Error, Severity.Error)]
    [InlineData(Severity.Error, Severity.Success, Severity.Error)]
    public void Max_severity_picks_the_more_severe(Severity a, Severity b, Severity expected)
    {
        // Documents the comparison the StepRunner relies on for T02.6a (effective severity).
        Assert.Equal(expected, (Severity)System.Math.Max((int)a, (int)b));
    }

    [Fact]
    public void StepVerdict_carries_result_and_uses_its_message()
    {
        var result = StepResult.Fail("nope");
        var verdict = new StepVerdict(result);

        Assert.Same(result, verdict.Result);
        Assert.Equal("nope", verdict.Message);
    }

    [Fact]
    public void StepVerdict_rejects_null_result()
    {
        Assert.Throws<System.ArgumentNullException>(() => new StepVerdict(null!));
    }
}
