// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Xunit;

namespace HydraData.Host.Tests;

public sealed class ResumeDispatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Resume_from_middle_skips_earlier_step_and_writes_later_artifacts_and_host_log()
    {
        using var scaffold = CreateHostScaffold()
            .AddStep("01_10_first.cs", WriteArtifact("first.csv", 10))
            .AddStep("01_20_second.cs", WriteArtifact("second.csv", 20))
            .AddStep("01_30_third.cs", WriteArtifact("third.csv", 30));

        var exit = await HostBootstrap.RunResumeAsync(scaffold.Root, new StepOrder(1, 20, null, null), Ct);

        Assert.Equal(0, exit);
        var runDir = GetRunDir(scaffold);
        Assert.False(File.Exists(Path.Combine(runDir, "first.csv")));
        Assert.True(File.Exists(Path.Combine(runDir, "second.csv")));
        Assert.True(File.Exists(Path.Combine(runDir, "third.csv")));
        Assert.True(File.Exists(Path.Combine(runDir, "host.log")));
    }

    [Fact]
    public async Task Failure_after_resume_point_returns_exit_2()
    {
        using var scaffold = CreateHostScaffold()
            .AddStep("01_10_first.cs", "return Ok();")
            .AddStep("01_20_fail.cs", "return Fail(\"boom\");")
            .AddStep("01_30_third.cs", "return Ok();");

        var exit = await HostBootstrap.RunResumeAsync(scaffold.Root, new StepOrder(1, 20, null, null), Ct);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Validation_error_before_resume_point_still_returns_exit_1()
    {
        using var scaffold = CreateHostScaffold()
            .AddStep("01_10_invalid.cs", "Qery(); return Ok();")
            .AddStep("01_20_second.cs", "return Ok();");

        var exit = await HostBootstrap.RunResumeAsync(scaffold.Root, new StepOrder(1, 20, null, null), Ct);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Resume_beyond_all_steps_returns_exit_0_with_all_artifacts_skipped()
    {
        using var scaffold = CreateHostScaffold()
            .AddStep("01_10_first.cs", WriteArtifact("first.csv", 10))
            .AddStep("01_20_second.cs", WriteArtifact("second.csv", 20))
            .AddStep("01_30_third.cs", WriteArtifact("third.csv", 30));

        var exit = await HostBootstrap.RunResumeAsync(scaffold.Root, new StepOrder(2, 10, null, null), Ct);

        Assert.Equal(0, exit);
        var runDir = GetRunDir(scaffold);
        Assert.False(File.Exists(Path.Combine(runDir, "first.csv")));
        Assert.False(File.Exists(Path.Combine(runDir, "second.csv")));
        Assert.False(File.Exists(Path.Combine(runDir, "third.csv")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("xx")]
    public void Resume_dispatch_rejects_unparseable_order(string? text)
    {
        var parsed = OrderKeyParser.TryParse(text, out var order);

        Assert.False(parsed);
        Assert.Null(order);
    }

    private static HostScaffold CreateHostScaffold()
    {
        var scaffold = new HostScaffold();
        File.WriteAllText(Path.Combine(scaffold.Root, "appsettings.json"), """
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
        return scaffold;
    }

    private static string WriteArtifact(string fileName, int value) =>
        $"WriteCsv(\"{fileName}\", new object[] {{ new {{ Value = {value} }} }}); return Ok();";

    private static string GetRunDir(HostScaffold scaffold) =>
        Assert.Single(Directory.GetDirectories(scaffold.WorkspaceBase));
}
