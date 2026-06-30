// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T07.1–T07.3 — engine logging: scopes carry RunId (run-level) and ScriptName (step-level); the level
/// convention (Information/Warning/Error) holds; and no ConnectionString/secret is ever logged.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class EngineLoggingTests
{
    private const string SecretMarker = "S3cr3tP@ss";

    private static PumpEngine NewEngine(EngineScaffold scaffold, FakeConnectionGateway gateway, TestLogger logger)
    {
        var options = new PumpOptions(scaffold.WorkspaceBase, PumpFolderPolicy.Empty);
        return new PumpEngine(options, new FakeGuidProvider(Guid.NewGuid()), timeProvider: null, gateway, logger);
    }

    // A connection directory whose connection string contains a secret — used to prove it is never logged.
    private static IConnectionDirectory ConnectionsWithSecret()
    {
        var xml = $"""
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="stage">
                <Parameters>
                  <Parameter key="Server"   value="localhost" type="String" />
                  <Parameter key="Database" value="stage"     type="String" />
                  <Parameter key="Password" value="{SecretMarker}" type="String" />
                </Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;
        return new ConnectionDirectory(ConnectionRegistry.Parse(xml));
    }

    [Fact]
    public async Task Run_scope_carries_run_id_and_step_scope_carries_script_name()
    {
        using var scaffold = new EngineScaffold().AddStep("01_10_a.cs", "return Ok();");
        var gateway = new FakeConnectionGateway();
        var logger = new TestLogger();
        var engine = NewEngine(scaffold, gateway, logger);

        var report = await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        // RunId scope present on at least one entry and equals the report RunId.
        var runIds = logger.ScopeValues("RunId").OfType<Guid>().Distinct().ToList();
        Assert.Contains(report.RunId, runIds);

        // ScriptName scope present (step-level scope).
        var scriptNames = logger.ScopeValues("ScriptName").OfType<string>().Distinct().ToList();
        Assert.Contains("01_10_a.cs", scriptNames);
    }

    [Fact]
    public async Task Levels_follow_the_convention()
    {
        using var scaffold = new EngineScaffold()
            .AddStep("01_10_ok.cs", "return Ok();")
            .AddStep("01_20_warn.cs", "// @haltOnError: false\nreturn Warn(\"w\");")
            .AddStep("01_30_fail.cs", "// @haltOnError: false\nreturn Fail(\"boom\");");
        var gateway = new FakeConnectionGateway();
        var logger = new TestLogger();
        var engine = NewEngine(scaffold, gateway, logger);

        await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections(),
            ct: TestContext.Current.CancellationToken);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information); // run/step start, Ok verdict
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);     // Warn verdict
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);       // Fail verdict
    }

    [Fact]
    public async Task ConnectionString_is_never_logged()
    {
        using var scaffold = new EngineScaffold().AddStep("01_10_a.cs", "Execute(\"x\"); return Ok();");
        var gateway = new FakeConnectionGateway();
        var logger = new TestLogger();
        var engine = NewEngine(scaffold, gateway, logger);

        await engine.ExecuteAsync(scaffold.Discover(), EngineScaffold.Extern(), ConnectionsWithSecret(),
            ct: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Entries, e =>
            e.Message.Contains(SecretMarker, StringComparison.Ordinal)
            || e.Message.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Default_null_logger_does_not_throw()
    {
        // Constructing and validating with no logger uses NullLogger.Instance under the hood.
        using var scaffold = new EngineScaffold().AddStep("01_10_a.cs", "return Ok();");
        var options = new PumpOptions(scaffold.WorkspaceBase, PumpFolderPolicy.Empty);
        var engine = new PumpEngine(options, new FakeGuidProvider(Guid.NewGuid()));

        var report = engine.Validate(scaffold.Discover(), EngineScaffold.Extern(), EngineScaffold.Connections());
        Assert.True(report.IsValid);
    }
}
