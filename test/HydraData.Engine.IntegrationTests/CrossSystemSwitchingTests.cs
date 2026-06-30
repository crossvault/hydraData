// Copyright (c) 2026 crossVault GmbH.

using System.Globalization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace HydraData.Engine.IntegrationTests;

/// <summary>
/// In-script connection switching: a single step reads from MSSQL and writes
/// to PGSQL within one run, proving the per-connection slot fan-out commits/rolls back both connections.
/// Requires Docker (MSSQL + PGSQL containers); the orchestrator runs these at the boundary.
/// </summary>
public sealed class CrossSystemSwitchingTests : IAsyncLifetime, IDisposable
{
    private readonly MsSqlContainer _mssql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly PostgreSqlContainer _pgsql = new PostgreSqlBuilder("postgres:17").Build();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HydraData-it", Path.GetRandomFileName());
    private string _scriptDir = null!;
    private string _workspaceBase = null!;
    private ConnectionInfo _mssqlInfo = null!;
    private ConnectionInfo _pgsqlInfo = null!;
    private IConnectionDirectory _connections = null!;

    public async ValueTask InitializeAsync()
    {
        await _mssql.StartAsync();
        await _pgsql.StartAsync();

        _scriptDir = Path.Combine(_root, "scripts");
        _workspaceBase = Path.Combine(_root, "_runs");
        Directory.CreateDirectory(_scriptDir);
        Directory.CreateDirectory(_workspaceBase);

        var connectionsFile = Path.Combine(_root, "connections.xml");
        WriteConnectionsXml(connectionsFile, _mssql.GetConnectionString(), _pgsql.GetConnectionString());
        _connections = new ConnectionDirectory(ConnectionRegistry.Load(connectionsFile));
        _mssqlInfo = (ConnectionInfo)_connections.GetConnection("stage", DbType.Mssql);
        _pgsqlInfo = (ConnectionInfo)_connections.GetConnection("stage", DbType.Pgsql);
    }

    public async ValueTask DisposeAsync()
    {
        await _mssql.DisposeAsync();
        await _pgsql.DisposeAsync();
    }

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

    private IDbSlot OpenMssql() => new ConnectionGateway().Open(_mssqlInfo);

    private IDbSlot OpenPgsql() => new ConnectionGateway().Open(_pgsqlInfo);

    private static string DescribeFailure(RunReport report)
    {
        var preflight = string.Join(Environment.NewLine,
            report.PreflightErrors.Select(d => $"{d.ScriptName}:{d.Line}:{d.Column} {d.Code}: {d.Message}"));
        var steps = string.Join(Environment.NewLine,
            report.Steps.Select(s => $"{s.ScriptName}: {s.Result?.Message}"));
        return $"ExitCode={report.ExitCode}{Environment.NewLine}{preflight}{Environment.NewLine}{steps}";
    }

    private static void WriteConnectionsXml(string path, string mssqlConnectionString, string pgsqlConnectionString)
    {
        var mssql = new SqlConnectionStringBuilder(mssqlConnectionString);
        var pgsql = new NpgsqlConnectionStringBuilder(pgsqlConnectionString);
        var (mssqlHost, mssqlPort) = SplitMssqlDataSource(mssql.DataSource);

        var doc = new XDocument(
            new XElement("ConnectionStrings",
                new XElement("ConnectionString",
                    new XAttribute("targetSystem", "MSSQL"),
                    new XAttribute("name", "stage"),
                    new XElement("Parameters",
                        Parameter("Server", mssqlHost),
                        Parameter("Port", mssqlPort, "Numeric"),
                        Parameter("User ID", mssql.UserID),
                        Parameter("Password", mssql.Password),
                        Parameter("TrustServerCertificate", "true"))),
                new XElement("ConnectionString",
                    new XAttribute("targetSystem", "PGSQL"),
                    new XAttribute("name", "stage"),
                    new XElement("Parameters",
                        Parameter("Host", pgsql.Host),
                        Parameter("Port", pgsql.Port.ToString(CultureInfo.InvariantCulture), "Numeric"),
                        Parameter("Database", pgsql.Database),
                        Parameter("Username", pgsql.Username),
                        Parameter("Password", pgsql.Password)))));

        doc.Save(path);
    }

    private static XElement Parameter(string key, string? value, string type = "String") =>
        new("Parameter",
            new XAttribute("key", key),
            new XAttribute("value", value ?? string.Empty),
            new XAttribute("type", type));

    private static (string Host, string Port) SplitMssqlDataSource(string dataSource)
    {
        var commaIdx = dataSource.LastIndexOf(',');
        Assert.True(commaIdx > 0, $"Expected host,port MSSQL data source but got '{dataSource}'.");

        var host = dataSource[..commaIdx].Replace("tcp:", string.Empty, StringComparison.Ordinal);
        var port = dataSource[(commaIdx + 1)..];
        return (host, port);
    }

    [Fact]
    public async Task Step_reads_mssql_writes_pgsql_commits_both()
    {
        // Seed the MSSQL source out-of-band.
        using (var seed = OpenMssql())
        {
            seed.Executor.Execute("CREATE TABLE dbo.src (id INT, name NVARCHAR(50));", null);
            seed.Executor.Execute("INSERT INTO dbo.src (id, name) VALUES (1, 'Müller');", null);
            seed.Commit();
        }

        // Prepare the PGSQL destination out-of-band.
        using (var prep = OpenPgsql())
        {
            prep.Executor.Execute("CREATE TABLE dst (id INT, name TEXT);", null);
            prep.Commit();
        }

        // The step reads from the default (MSSQL) connection and writes the row to PGSQL (cross-system).
        Step("01_10_copy.cs",
            "var rows = Query(\"SELECT id, name FROM dbo.src;\"); " +
            "var pg = GetConnection(CurrentConnection.Name, CurrentConnection.DbType, \"pgsql\"); " +
            "foreach (var r in rows) Execute(pg, \"INSERT INTO dst (id, name) VALUES (@id, @name);\", new { id = (int)r.id, name = (string)r.name }); " +
            "return Ok();");

        var report = await Engine().ExecuteAsync(Discover(), Extern(), _connections,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ExitCode);
        Assert.All(report.Steps, s => Assert.True(s.Committed));

        // Re-query each system in a fresh slot to prove BOTH committed independently.
        using var verifyPg = OpenPgsql();
        Assert.Equal(1, verifyPg.Executor.Scalar<int>("SELECT COUNT(*) FROM dst WHERE name = 'Müller';", null));
        verifyPg.Commit();

        using var verifyMs = OpenMssql();
        Assert.Equal(1, verifyMs.Executor.Scalar<int>("SELECT COUNT(*) FROM dbo.src;", null));
        verifyMs.Commit();
    }

    [Fact]
    public async Task Failing_step_rolls_back_both_systems()
    {
        // Destination tables on both systems, seeded empty.
        using (var prepMs = OpenMssql())
        {
            prepMs.Executor.Execute("CREATE TABLE dbo.rb_ms (id INT);", null);
            prepMs.Commit();
        }

        using (var prepPg = OpenPgsql())
        {
            prepPg.Executor.Execute("CREATE TABLE rb_pg (id INT);", null);
            prepPg.Commit();
        }

        // The step writes to BOTH connections, then fails: both slots must roll back.
        Step("01_10_writeboth.cs",
            "Execute(\"INSERT INTO dbo.rb_ms VALUES (1);\"); " +
            "var pg = GetConnection(CurrentConnection.Name, CurrentConnection.DbType, \"pgsql\"); " +
            "Execute(pg, \"INSERT INTO rb_pg VALUES (1);\"); " +
            "return Fail(\"abort\");");

        var report = await Engine().ExecuteAsync(Discover(), Extern(), _connections,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ExitCode);
        Assert.False(report.Steps[^1].Committed);

        // Re-query each system: nothing landed on either side.
        using var verifyMs = OpenMssql();
        Assert.Equal(0, verifyMs.Executor.Scalar<int>("SELECT COUNT(*) FROM dbo.rb_ms;", null));
        verifyMs.Commit();

        using var verifyPg = OpenPgsql();
        Assert.Equal(0, verifyPg.Executor.Scalar<int>("SELECT COUNT(*) FROM rb_pg;", null));
        verifyPg.Commit();
    }

    [Fact]
    public async Task Dynamic_script_uses_connections_xml_pgsql_update_and_duckdb_analysis()
    {
        Assert.IsType<ConnectionDirectory>(_connections);

        using (var seed = OpenMssql())
        {
            seed.Executor.Execute(
                "CREATE TABLE dbo.items (id INT, name NVARCHAR(50), qty INT, note NVARCHAR(50) NULL);",
                null);
            seed.Executor.Execute(
                "INSERT INTO dbo.items (id, name, qty, note) VALUES " +
                "(1, 'alpha', 10, NULL), (2, 'beta', 15, 'old'), (3, 'gamma', 5, NULL);",
                null);
            seed.Commit();
        }

        using (var prep = OpenPgsql())
        {
            prep.Executor.Execute("CREATE TABLE items (id INT, name TEXT, qty INT, note TEXT);", null);
            prep.Commit();
        }

        Step("01_10_duck_pipeline.cs",
            """
            var pg = GetConnection(CurrentConnection.Name, CurrentConnection.DbType, "pgsql");
            var rows = Query("SELECT id, name, qty, note FROM dbo.items ORDER BY id;");
            var bulk = new List<IDictionary<string, object>>();

            foreach (var r in rows)
            {
                bulk.Add(new Dictionary<string, object>
                {
                    ["id"] = (int)r.id,
                    ["name"] = (string)r.name,
                    ["qty"] = (int)r.qty,
                    ["note"] = r.note == null ? DBNull.Value : (object)(string)r.note
                });
            }

            BulkInsert(pg, "items", bulk);
            Execute(pg, "UPDATE items SET note = @note WHERE qty >= @qty;", new { note = "duck-candidate", qty = 10 });

            var duck = Duck();
            duck.Execute("CREATE TABLE stage (id INTEGER, name VARCHAR, qty INTEGER, note VARCHAR);");

            foreach (var r in Query(pg, "SELECT id, name, qty, note FROM items ORDER BY id;"))
            {
                var name = ((string)r.name).Replace("'", "''");
                var note = r.note == null ? "NULL" : "'" + ((string)r.note).Replace("'", "''") + "'";
                duck.Execute($"INSERT INTO stage VALUES ({(int)r.id}, '{name}', {(int)r.qty}, {note});");
            }

            var totals = duck.Query("SELECT CAST(SUM(qty) AS INTEGER) AS total FROM stage WHERE note = 'duck-candidate';");
            var total = Convert.ToInt32(totals[0].total);
            Expect(total == 25, "DuckDB aggregate mismatch", new { total });

            Execute(pg, "CREATE TABLE audit (metric TEXT, value INT);");
            Execute(pg, "INSERT INTO audit (metric, value) VALUES (@metric, @value);", new { metric = "duck_qty", value = total });
            return Ok();
            """);

        var report = await Engine().ExecuteAsync(Discover(), Extern(), _connections,
            ct: TestContext.Current.CancellationToken);

        Assert.True(report.ExitCode == 0, DescribeFailure(report));

        Assert.All(report.Steps, s => Assert.True(s.Committed));

        using var verifyPg = OpenPgsql();
        Assert.Equal(3L, verifyPg.Executor.Scalar<long>("SELECT COUNT(*) FROM items;", null));
        Assert.Equal(2L, verifyPg.Executor.Scalar<long>("SELECT COUNT(*) FROM items WHERE note = 'duck-candidate';", null));
        Assert.Equal(25, verifyPg.Executor.Scalar<int>("SELECT value FROM audit WHERE metric = 'duck_qty';", null));
        verifyPg.Commit();

        using var verifyMs = OpenMssql();
        Assert.Equal(3, verifyMs.Executor.Scalar<int>("SELECT COUNT(*) FROM dbo.items;", null));
        verifyMs.Commit();
    }
}
