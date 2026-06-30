// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine.Tests.Fakes;

/// <summary>
/// Test scaffold for end-to-end <see cref="PumpEngine"/> runs: writes step scripts into a temp script
/// folder, allocates a temp workspace base, and discovers a <see cref="ScriptContext"/>. Implements
/// <see cref="IDisposable"/> to clean up both temp trees.
/// </summary>
internal sealed class EngineScaffold : IDisposable
{
    private readonly string _scriptDir;

    public EngineScaffold()
    {
        var root = Path.Combine(Path.GetTempPath(), "hydradata-engine-tests", Path.GetRandomFileName());
        _scriptDir = Path.Combine(root, "scripts");
        WorkspaceBase = Path.Combine(root, "_runs");
        Directory.CreateDirectory(_scriptDir);
        Directory.CreateDirectory(WorkspaceBase);
    }

    /// <summary>The workspace base passed into <see cref="PumpOptions"/>.</summary>
    public string WorkspaceBase { get; }

    /// <summary>Writes a step file (e.g. <c>01_10_step.cs</c>) with the given body into the script folder.</summary>
    public EngineScaffold AddStep(string fileName, string body)
    {
        File.WriteAllText(Path.Combine(_scriptDir, fileName), body);
        return this;
    }

    /// <summary>Discovers the script context from the single script folder.</summary>
    public ScriptContext Discover() => new DiscoveryService().Discover([_scriptDir]);

    /// <summary>An empty extern context.</summary>
    public static ExternContext Extern() => ExternContext.FromValues(new Dictionary<string, object?>());

    /// <summary>A connection directory with a single MSSQL <c>stage</c> connection (the Default).</summary>
    public static IConnectionDirectory Connections()
    {
        const string xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="stage">
                <Parameters>
                  <Parameter key="Server"   value="localhost" type="String"  />
                  <Parameter key="Database" value="stage"     type="String"  />
                </Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;
        return new ConnectionDirectory(ConnectionRegistry.Parse(xml));
    }

    public void Dispose()
    {
        try
        {
            var root = Directory.GetParent(_scriptDir)?.FullName;
            if (root is not null && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file (e.g. DuckDB) must not fail the test.
        }
    }
}
