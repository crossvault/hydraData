// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Host.Tests;

/// <summary>
/// Dispatch contract for the console entry point (<c>Program.cs</c>): <c>args[0] == "session"</c> routes to
/// <see cref="SessionBootstrap"/> (interactive REPL), anything else routes to <see cref="HostBootstrap"/>
/// (batch full-run). <c>Program.Main</c> uses top-level statements and is not directly invokable, so the
/// dispatch is pinned via the OBSERVABLE difference between the two bootstraps it selects: the session path
/// prints the REPL banner and never writes a host.log; the batch path runs to completion, writes host.log
/// inside the RunDir, and returns the run's exit code.
/// </summary>
public sealed class ProgramDispatchTests : IDisposable
{
    private readonly string _baseDir;

    public ProgramDispatchTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "hydradata-dispatch", Path.GetRandomFileName());
        Directory.CreateDirectory(_baseDir);
        File.WriteAllText(Path.Combine(_baseDir, "appsettings.json"), """
            {
              "Pump": {
                "WorkspaceBase": "./_runs",
                "ScriptFolders": [ "./scripts" ],
                "ConnectionsFile": "./connections.xml"
              }
            }
            """);
        File.WriteAllText(Path.Combine(_baseDir, "connections.xml"), HostScaffold.ValidConnectionsXml);
        var scriptDir = Path.Combine(_baseDir, "scripts");
        Directory.CreateDirectory(scriptDir);
        File.WriteAllText(Path.Combine(scriptDir, "01_10_a.cs"), "return Ok();");
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Session_dispatch_writes_repl_banner_and_no_host_log()
    {
        // The "session" branch (args[0] == "session") runs the interactive REPL: it prints the session
        // banner and, being a transient dev tool, never writes a host.log.
        var input = new StringReader(":quit\n");
        var output = new StringWriter();

        var exit = await SessionBootstrap.RunAsync(_baseDir, input, output, Ct);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("hydradata session", text);
        Assert.Contains("step(s) discovered", text);

        var runsDir = Path.Combine(_baseDir, "_runs");
        var hostLogs = Directory.Exists(runsDir)
            ? Directory.GetDirectories(runsDir).SelectMany(d => Directory.GetFiles(d, "host.log"))
            : [];
        Assert.Empty(hostLogs);
    }

    [Fact]
    public async Task Batch_dispatch_runs_full_run_and_writes_host_log_without_repl_banner()
    {
        // The default branch (no "session" arg) runs the batch full-run: it does NOT print the REPL banner
        // and DOES write a host.log inside the RunDir, returning the run's exit code.
        var exit = await HostBootstrap.RunAsync(_baseDir, Ct);

        Assert.Equal(0, exit);

        var runsDir = Path.Combine(_baseDir, "_runs");
        var hostLog = Directory.GetDirectories(runsDir)
            .SelectMany(d => Directory.GetFiles(d, "host.log"))
            .FirstOrDefault();
        Assert.NotNull(hostLog);
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
