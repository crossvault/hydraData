// Copyright (c) 2026 crossVault GmbH.

using Testcontainers.MsSql;
using Xunit;

namespace HydraData.Engine.IntegrationTests;

/// <summary>
/// MSSQL counterpart of <see cref="PumpEngineIntegrationTests"/> (FIX 4 symmetry): the full
/// Discover → Validate → Execute pipeline through the production <see cref="PumpEngine"/> (real
/// <c>ConnectionGateway</c>) against a real SQL Server, asserting commit on <c>Ok</c> and rollback on
/// <c>Fail</c> by re-querying. Requires Docker; the orchestrator runs these at the boundary.
/// </summary>
public sealed class PumpEngineMssqlIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hydradata-it", Path.GetRandomFileName());
    private string _scriptDir = null!;
    private string _workspaceBase = null!;
    private ConnectionInfo _info = null!;
    private IConnectionDirectory _connections = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _scriptDir = Path.Combine(_root, "scripts");
        _workspaceBase = Path.Combine(_root, "_runs");
        Directory.CreateDirectory(_scriptDir);
        Directory.CreateDirectory(_workspaceBase);

        _info = new ConnectionInfo("stage", DbType.Mssql, _container.GetConnectionString());
        _connections = new SingleConnectionDirectory(_info);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Step(string fileName, string body) =>
        File.WriteAllText(Path.Combine(_scriptDir, fileName), body);

    private static ExternContext Extern() => ExternContext.FromValues(new Dictionary<string, object?>());

    private ScriptContext Discover() => new DiscoveryService().Discover([_scriptDir]);

    private PumpEngine Engine() => new(new PumpOptions(_workspaceBase, PumpFolderPolicy.Empty));

    private IDbSlot OpenSlot() => new ConnectionGateway().Open(_info);

    [Fact]
    public async Task Full_run_commits_data_and_returns_exit_code_0()
    {
        Step("01_10_create.cs", "Execute(\"CREATE TABLE dbo.people (id INT, name NVARCHAR(50));\"); return Ok();");
        Step("01_20_insert.cs", "Execute(\"INSERT INTO dbo.people VALUES (1, N'Müller');\"); return Ok();");
        Step("01_30_verify.cs",
            "var n = Scalar<int>(\"SELECT COUNT(*) FROM dbo.people;\"); " +
            "Expect(n == 1, \"row not visible\"); return Ok();");

        var report = await Engine().ExecuteAsync(Discover(), Extern(), _connections,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(3, report.Steps.Count);
        Assert.All(report.Steps, s => Assert.True(s.Committed));

        // Re-query in a fresh slot to prove the data committed.
        using var verify = OpenSlot();
        Assert.Equal(1, verify.Executor.Scalar<int>("SELECT COUNT(*) FROM dbo.people;", null));
        verify.Commit();
    }

    [Fact]
    public async Task Failing_step_rolls_back_and_returns_exit_code_2()
    {
        // Seed an empty table out-of-band so the failing step has somewhere to (not) write.
        using (var setup = OpenSlot())
        {
            setup.Executor.Execute("CREATE TABLE dbo.t_rb (id INT);", null);
            setup.Commit();
        }

        Step("01_10_fail.cs", "Execute(\"INSERT INTO dbo.t_rb VALUES (1);\"); return Fail(\"abort\");");

        var report = await Engine().ExecuteAsync(Discover(), Extern(), _connections,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);
        Assert.False(report.Steps[^1].Committed);

        // Re-query: the insert rolled back, nothing landed.
        using var verify = OpenSlot();
        Assert.Equal(0, verify.Executor.Scalar<int>("SELECT COUNT(*) FROM dbo.t_rb;", null));
        verify.Commit();
    }

    /// <summary>A minimal single-connection directory for the integration run.</summary>
    private sealed class SingleConnectionDirectory(ConnectionInfo info) : IConnectionDirectory
    {
        public IConnection Default => info;

        public IReadOnlyList<IConnection> Extern => [];

        public IConnection GetConnection(string name, DbType dbType) => info;

        public IConnection GetConnection(string name, DbType sourceDbType, DbType targetDbType) => info;

        public IConnection GetConnection(string name, string dbType) => info;

        public IConnection? GetById(string id) =>
            string.Equals(id, info.Id, StringComparison.OrdinalIgnoreCase) ? info : null;

        public IReadOnlyList<IConnection> Where(DbType? dbType = null, string? id = null) => [info];
    }
}
