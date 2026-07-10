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

    private string MakeOldRunDir()
    {
        var runDir = Path.Combine(_baseDir, "_runs", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "marker.txt"), "old");
        Directory.SetLastWriteTimeUtc(runDir, DateTime.UtcNow.AddDays(-30));
        return runDir;
    }

    private string[] GetGuidRunDirs()
    {
        var runsDir = Path.Combine(_baseDir, "_runs");
        return Directory.Exists(runsDir)
            ? Directory.GetDirectories(runsDir)
                .Where(dir => Guid.TryParseExact(Path.GetFileName(dir), "D", out _))
                .ToArray()
            : [];
    }

    [Fact]
    public async Task Retention_enabled_deletes_old_guid_run_and_creates_new_run()
    {
        WriteAppSettings();
        WriteConnections();
        AddScript("01_10_a.cs", "return Ok();");
        var oldRunDir = MakeOldRunDir();

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(oldRunDir));
        Assert.Single(GetGuidRunDirs());
    }

    [Fact]
    public async Task Zero_retention_days_preserves_old_guid_run_and_creates_new_run()
    {
        WriteAppSettings(retentionDays: 0);
        WriteConnections();
        AddScript("01_10_a.cs", "return Ok();");
        var oldRunDir = MakeOldRunDir();

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(oldRunDir));
        Assert.Equal(2, GetGuidRunDirs().Length);
    }

    [Fact]
    public async Task Run_id_alignment_places_host_log_and_engine_artifact_in_one_guid_run_dir()
    {
        WriteAppSettings();
        WriteConnections();
        AddScript(
            "01_10_artifact.cs",
            "WriteCsv(\"engine-artifact.csv\", new object[] { new { Value = 1 } }); return Ok();");

        var exit = await HostBootstrap.RunAsync(_baseDir, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        var runDir = Assert.Single(GetGuidRunDirs());
        Assert.True(File.Exists(Path.Combine(runDir, "host.log")));
        Assert.True(File.Exists(Path.Combine(runDir, "engine-artifact.csv")));
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
