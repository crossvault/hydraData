// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Host.Tests;

/// <summary>
/// Exit-code mapping: the runner returns <see cref="RunReport.ExitCode"/> verbatim
/// as the process exit code. Driven against the real engine with tiny scripts (no DB) so 0/1/2 are produced
/// genuinely, not faked.
/// </summary>
public class PumpRunnerTests
{
    [Fact]
    public async Task All_steps_ok_maps_to_exit_0()
    {
        using var scaffold = new HostScaffold()
            .AddStep("01_10_a.cs", "return Ok();")
            .AddStep("01_20_b.cs", "return Warn(\"heads up\");");

        var exit = await scaffold.RunAsync(out _, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Validation_failure_maps_to_exit_1()
    {
        using var scaffold = new HostScaffold()
            .AddStep("01_10_ok.cs", "return Ok();")
            .AddStep("01_20_typo.cs", "Qery(\"select 1\"); return Ok();"); // CS0103: aborts before execution

        var exit = await scaffold.RunAsync(out _, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Runtime_failure_maps_to_exit_2()
    {
        using var scaffold = new HostScaffold()
            .AddStep("01_10_fail.cs", "return Fail(\"boom\");");

        var exit = await scaffold.RunAsync(out _, TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Empty_script_folder_is_a_clean_run_exit_0()
    {
        // No steps discovered: nothing to validate or run; a no-op run is a success (no error).
        using var scaffold = new HostScaffold();

        var exit = await scaffold.RunAsync(out _, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Non_tty_run_writes_plain_capturable_summary()
    {
        using var scaffold = new HostScaffold()
            .AddStep("01_10_a.cs", "return Ok();");

        var exit = await scaffold.RunAsync(out var output, TestContext.Current.CancellationToken);
        var text = output.ToString();

        Assert.Equal(0, exit);
        // Plain-text streaming + summary are present and capturable (no Spectre control sequences needed).
        Assert.Contains("Discovery complete.", text);
        Assert.Contains("01_10_a.cs", text);
        Assert.Contains("exit code 0", text);
    }
}
