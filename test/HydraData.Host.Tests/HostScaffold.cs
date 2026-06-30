// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace HydraData.Host.Tests;

/// <summary>
/// Test scaffold for host runs: a temp working directory with a script folder, a workspace base and a
/// <c>connections.xml</c>. Builds a <see cref="PumpRunner"/> over the real <see cref="PumpEngine"/> with a
/// single MSSQL connection (scripts that only return verdicts never open the DB, so no server is needed).
/// </summary>
internal sealed class HostScaffold : IDisposable
{
    public HostScaffold()
    {
        Root = Path.Combine(Path.GetTempPath(), "hydradata-host-tests", Path.GetRandomFileName());
        ScriptDir = Path.Combine(Root, "scripts");
        WorkspaceBase = Path.Combine(Root, "_runs");
        ConnectionsFile = Path.Combine(Root, "connections.xml");
        Directory.CreateDirectory(ScriptDir);
        Directory.CreateDirectory(WorkspaceBase);
        WriteConnections(ValidConnectionsXml);
    }

    public string Root { get; }

    public string ScriptDir { get; }

    public string WorkspaceBase { get; }

    public string ConnectionsFile { get; }

    public const string ValidConnectionsXml = """
        <ConnectionStrings>
          <ConnectionString targetSystem="MSSQL" name="stage">
            <Parameters>
              <Parameter key="Server"   value="localhost" type="String" />
              <Parameter key="Database" value="stage"     type="String" />
            </Parameters>
          </ConnectionString>
        </ConnectionStrings>
        """;

    public HostScaffold AddStep(string fileName, string body)
    {
        File.WriteAllText(Path.Combine(ScriptDir, fileName), body);
        return this;
    }

    public void WriteConnections(string xml) => File.WriteAllText(ConnectionsFile, xml);

    public IConnectionDirectory Connections() =>
        new ConnectionDirectory(ConnectionRegistry.Parse(File.ReadAllText(ConnectionsFile)));

    /// <summary>Builds a runner over the real engine, a string-capturing non-TTY presenter and the script folder.</summary>
    public PumpRunner BuildRunner(out StringWriter output)
    {
        var options = new PumpOptions(WorkspaceBase, PumpFolderPolicy.Empty);
        var engine = new PumpEngine(options, logger: NullLogger.Instance);
        var discovery = new DiscoveryService();
        var sw = new StringWriter();
        output = sw;
        var presenter = new ConsolePresenter(sw, isInteractive: false);
        return new PumpRunner(engine, discovery, Connections(), presenter, NullLogger.Instance);
    }

    public Task<int> RunAsync(out StringWriter output, CancellationToken ct = default) =>
        BuildRunner(out output).RunAsync([ScriptDir], ExternContext.FromValues(new Dictionary<string, object?>()), ct);

    /// <summary>Resumes a run from <paramref name="resumeFrom"/> over the real engine (steps before it are skipped).</summary>
    public Task<int> RunResumeAsync(StepOrder resumeFrom, out StringWriter output, CancellationToken ct = default) =>
        BuildRunner(out output).RunResumeAsync(
            [ScriptDir], ExternContext.FromValues(new Dictionary<string, object?>()), resumeFrom, ct);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
