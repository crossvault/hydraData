// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Host.Tests;

/// <summary>
/// End-to-end host wiring (T09.8): <see cref="HostBootstrap.RunAsync"/> loads <c>appsettings.json</c> +
/// <c>connections.xml</c> from a base directory, runs, and returns the process exit code (0/1/2).
/// Configuration-time failures (missing files) map to exit code 1.
/// </summary>
public class HostBootstrapTests : IDisposable
{
    private readonly string _baseDir;

    public HostBootstrapTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "hydradata-host-bootstrap", Path.GetRandomFileName());
        Directory.CreateDirectory(_baseDir);
    }

    private void WriteAppSettings(int retentionDays = 14) =>
        File.WriteAllText(Path.Combine(_baseDir, "appsettings.json"), $$"""
            {
              "Pump": {
                "WorkspaceBase": "./_runs",
                "AllowUnsafeDirectAccess": false,
                "ReadAllowlist": [ "./input" ],
                "WriteAllowlist": [ "./output" ],
                "StepTimeoutSeconds": 120,
                "RunDirRetentionDays": {{retentionDays}},
                "ScriptFolders": [ "./scripts" ],
                "ConnectionsFile": "./connections.xml"
              }
            }
            """);

    private void WriteConnections() =>
        File.WriteAllText(Path.Combine(_baseDir, "connections.xml"), HostScaffold.ValidConnectionsXml);

    private void AddScript(string fileName, string body)
    {
        var scriptDir = Path.Combine(_baseDir, "scripts");
        Directory.CreateDirectory(scriptDir);
        File.WriteAllText(Path.Combine(scriptDir, fileName), body);
    }

    [Fact]
    public async Task Happy_path_runs_and_returns_exit_0()
    {
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_a.cs", "return Ok();");

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        // A durable host log is written inside the run's RunDir (so RunDirRetention reaps it).
        // The RunId is a GUID; find the run directory and verify host.log lives inside it.
        var runsDir = Path.Combine(_baseDir, "_runs");
        var runDirs = Directory.GetDirectories(runsDir);
        var hostLog = runDirs.SelectMany(d => Directory.GetFiles(d, "host.log")).FirstOrDefault();
        Assert.NotNull(hostLog);
    }

    [Fact]
    public async Task Runtime_fail_returns_exit_2()
    {
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_fail.cs", "return Fail(\"boom\");");

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Validation_failure_returns_exit_1()
    {
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_typo.cs", "Qery(); return Ok();");

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Missing_connections_file_maps_to_exit_1()
    {
        WriteAppSettings();
        AddScript("01_10_a.cs", "return Ok();");
        // No connections.xml written.

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Malformed_connections_file_maps_to_exit_1()
    {
        WriteAppSettings();
        File.WriteAllText(Path.Combine(_baseDir, "connections.xml"), "<not-closed>");
        AddScript("01_10_a.cs", "return Ok();");

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Missing_script_folder_maps_to_exit_1()
    {
        WriteAppSettings();
        WriteConnections();
        // No ./scripts folder created -> DiscoveryService throws DirectoryNotFoundException -> exit 1.

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
    }

    // [B1] Cancellation contract: pre-cancelled token → exit 2, not a thrown exception.
    [Fact]
    public async Task Pre_cancelled_token_returns_exit_2()
    {
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_a.cs", "return Ok();");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancel before the call

        var exit = await HostBootstrap.RunAsync(_baseDir, cts.Token);

        Assert.Equal(2, exit);
    }

    // [B2] Total guard: missing appsettings.json (config load runs inside the try) → exit 1.
    [Fact]
    public async Task Missing_appsettings_json_maps_to_exit_1()
    {
        // Do NOT write appsettings.json — ConfigurationBuilder with optional:false throws FileNotFoundException.

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
    }

    // [B2] Total guard: malformed (non-JSON) appsettings.json → exit 1.
    [Fact]
    public async Task Malformed_appsettings_json_maps_to_exit_1()
    {
        File.WriteAllText(Path.Combine(_baseDir, "appsettings.json"), "{ this is not valid json }}}");

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
    }

    // Well-formed appsettings.json that is missing the "Pump" section entirely (distinct from the malformed
    // JSON case above). PumpSettings binds to its defaults — including ConnectionsFile="./connections.xml",
    // which does not exist here — so config loading fails (FileNotFoundException) and maps to exit 1. This
    // documents that an absent Pump section is treated as defaults, not as a crash escaping to Program.cs.
    [Fact]
    public async Task Appsettings_present_but_Pump_section_absent_maps_to_exit_1()
    {
        // Valid JSON, no Pump section. No connections.xml / scripts written either.
        File.WriteAllText(Path.Combine(_baseDir, "appsettings.json"), """
            {
              "Logging": { "LogLevel": { "Default": "Information" } }
            }
            """);

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDir))
                Directory.Delete(_baseDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
