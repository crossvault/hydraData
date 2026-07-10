// Copyright (c) 2026 crossVault GmbH.

using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace HydraData.Engine.IntegrationTests;

/// <summary>
/// Integration tests for the MSSQL DB seam (Query/Scalar/Execute, Commit/Rollback, SqlBulkCopy
/// including nulls). Requires Docker; the orchestrator runs these at the M2 boundary.
/// </summary>
public sealed class MssqlDbSeamTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private ConnectionInfo _info = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _info = new ConnectionInfo("stage", DbType.Mssql, _container.GetConnectionString());
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private IDbSlot OpenSlot() => new ConnectionGateway().Open(_info);

    [Fact]
    public void Execute_and_Query_and_Scalar_roundtrip()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;

        exec.Execute("CREATE TABLE dbo.people (id INT, name NVARCHAR(50));", null);
        var affected = exec.Execute(
            "INSERT INTO dbo.people (id, name) VALUES (@id, @name);",
            new { id = 1, name = "Müller" });
        Assert.Equal(1, affected);

        var count = exec.Scalar<int>("SELECT COUNT(*) FROM dbo.people;", null);
        Assert.Equal(1, count);

        var rows = exec.Query("SELECT id, name FROM dbo.people;", null);
        var row = Assert.Single(rows);
        Assert.Equal(1, (int)row.id);
        Assert.Equal("Müller", (string)row.name);

        slot.Commit();
    }

    [Fact]
    public void Commit_persists_across_slots()
    {
        using (var slot = OpenSlot())
        {
            slot.Executor.Execute("CREATE TABLE dbo.t_commit (id INT);", null);
            slot.Executor.Execute("INSERT INTO dbo.t_commit VALUES (1);", null);
            slot.Commit();
        }

        using var verify = OpenSlot();
        Assert.Equal(1, verify.Executor.Scalar<int>("SELECT COUNT(*) FROM dbo.t_commit;", null));
        verify.Commit();
    }

    [Fact]
    public void Rollback_discards_changes()
    {
        using (var setup = OpenSlot())
        {
            setup.Executor.Execute("CREATE TABLE dbo.t_rb (id INT);", null);
            setup.Commit();
        }

        using (var slot = OpenSlot())
        {
            slot.Executor.Execute("INSERT INTO dbo.t_rb VALUES (1);", null);
            slot.Rollback();
        }

        using var verify = OpenSlot();
        Assert.Equal(0, verify.Executor.Scalar<int>("SELECT COUNT(*) FROM dbo.t_rb;", null));
        verify.Commit();
    }

    [Fact]
    public void BulkInsert_inserts_rows_including_nulls()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;

        exec.Execute("CREATE TABLE dbo.bulk_rows (id INT, name NVARCHAR(50));", null);

        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "A" },
            new Dictionary<string, object?> { ["id"] = 2, ["name"] = null }, // SQL NULL
        };
        exec.BulkInsert("dbo.bulk_rows", rows);

        Assert.Equal(2, exec.Scalar<int>("SELECT COUNT(*) FROM dbo.bulk_rows;", null));
        Assert.Equal(1, exec.Scalar<int>("SELECT COUNT(*) FROM dbo.bulk_rows WHERE name IS NULL;", null));

        slot.Commit();
    }

    [Fact]
    public void BulkInsert_with_empty_rows_is_noop()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;
        exec.Execute("CREATE TABLE dbo.bulk_empty (id INT);", null);

        exec.BulkInsert("dbo.bulk_empty", []);

        Assert.Equal(0, exec.Scalar<int>("SELECT COUNT(*) FROM dbo.bulk_empty;", null));
        slot.Commit();
    }

    [Fact]
    public void No_DB_access_opens_no_slot_connection()
    {
        // A slot that is never used must not require a reachable server.
        using var slot = OpenSlot();
        slot.Commit(); // no-op: nothing was opened.
    }

    [Fact]
    public void BulkInsert_round_trips_full_type_matrix_including_nulls()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;

        // Column SQL types match the CLR types: the typed DataTable (FIX 2) types each column from the
        // first row's value type so SqlBulkCopy converts Guid/DateTimeOffset/byte[] correctly.
        exec.Execute(
            """
            CREATE TABLE dbo.bulk_types (
                c_int    INT              NULL,
                c_long   BIGINT           NULL,
                c_short  SMALLINT         NULL,
                c_dec    DECIMAL(18,4)    NULL,
                c_double FLOAT            NULL,
                c_float  REAL             NULL,
                c_bool   BIT              NULL,
                c_guid   UNIQUEIDENTIFIER NULL,
                c_str    NVARCHAR(50)     NULL,
                c_dt     DATETIME2        NULL,
                c_dto    DATETIMEOFFSET   NULL,
                c_bytes  VARBINARY(MAX)   NULL);
            """, null);

        var guid = Guid.NewGuid();
        var dec = 1234.5678m;
        var dt = new DateTime(2026, 6, 24, 13, 45, 12, DateTimeKind.Unspecified);
        var dto = new DateTimeOffset(2026, 6, 24, 13, 45, 12, TimeSpan.FromHours(2));
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["c_int"] = 42, ["c_long"] = 9_000_000_000L, ["c_short"] = (short)7,
                ["c_dec"] = dec, ["c_double"] = 3.14159d, ["c_float"] = 2.5f,
                ["c_bool"] = true, ["c_guid"] = guid, ["c_str"] = "hello",
                ["c_dt"] = dt, ["c_dto"] = dto, ["c_bytes"] = bytes,
            },
            new Dictionary<string, object?>
            {
                ["c_int"] = null, ["c_long"] = null, ["c_short"] = null,
                ["c_dec"] = null, ["c_double"] = null, ["c_float"] = null,
                ["c_bool"] = null, ["c_guid"] = null, ["c_str"] = null,
                ["c_dt"] = null, ["c_dto"] = null, ["c_bytes"] = null,
            },
        };
        exec.BulkInsert("dbo.bulk_types", rows);

        Assert.Equal(2, exec.Scalar<int>("SELECT COUNT(*) FROM dbo.bulk_types;", null));

        var present = exec.Query("SELECT * FROM dbo.bulk_types WHERE c_int = 42;", null);
        var row = Assert.Single(present);
        Assert.Equal(42, (int)row.c_int);
        Assert.Equal(9_000_000_000L, (long)row.c_long);
        Assert.Equal((short)7, (short)row.c_short);
        Assert.Equal(dec, (decimal)row.c_dec);
        Assert.Equal(3.14159d, (double)row.c_double);
        Assert.Equal(2.5f, (float)row.c_float);
        Assert.True((bool)row.c_bool);
        Assert.Equal(guid, (Guid)row.c_guid);
        Assert.Equal("hello", (string)row.c_str);
        Assert.Equal(dt, (DateTime)row.c_dt);
        Assert.Equal(dto, (DateTimeOffset)row.c_dto);
        Assert.Equal(bytes, (byte[])row.c_bytes);

        // The all-null row: every column is NULL.
        Assert.Equal(1, exec.Scalar<int>(
            "SELECT COUNT(*) FROM dbo.bulk_types WHERE c_int IS NULL AND c_long IS NULL AND c_short IS NULL " +
            "AND c_dec IS NULL AND c_double IS NULL AND c_float IS NULL AND c_bool IS NULL " +
            "AND c_guid IS NULL AND c_str IS NULL AND c_dt IS NULL AND c_dto IS NULL AND c_bytes IS NULL;",
            null));

        slot.Commit();
    }

    [Fact]
    public void BulkInsert_types_column_from_first_non_null_value_when_row0_is_null()
    {
        // FIX C: the 'g' column is NULL in row 0 and a real Guid in row 1. The DataTable column type
        // must be inferred from the first NON-NULL value (the Guid), not from row 0's null (which would
        // type the column as object and make SqlBulkCopy mis-coerce the Guid). The Guid must round-trip.
        using var slot = OpenSlot();
        var exec = slot.Executor;

        exec.Execute("CREATE TABLE dbo.bulk_lead_null (id INT, g UNIQUEIDENTIFIER NULL);", null);

        var guid = Guid.NewGuid();
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["g"] = null },
            new Dictionary<string, object?> { ["id"] = 2, ["g"] = guid },
        };
        exec.BulkInsert("dbo.bulk_lead_null", rows);

        Assert.Equal(2, exec.Scalar<int>("SELECT COUNT(*) FROM dbo.bulk_lead_null;", null));
        var stored = exec.Scalar<Guid>("SELECT g FROM dbo.bulk_lead_null WHERE id = 2;", null);
        Assert.Equal(guid, stored);
        Assert.Equal(1, exec.Scalar<int>("SELECT COUNT(*) FROM dbo.bulk_lead_null WHERE g IS NULL;", null));

        slot.Commit();
    }

    [Fact]
    public void BulkInsert_all_null_guid_and_text_columns_preserves_every_null()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;
        exec.Execute(
            "CREATE TABLE dbo.bulk_all_null (id INT NOT NULL, g UNIQUEIDENTIFIER NULL, note NVARCHAR(50) NULL);",
            null);

        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["g"] = null, ["note"] = null },
            new Dictionary<string, object?> { ["id"] = 2, ["g"] = null, ["note"] = null },
        };
        exec.BulkInsert("dbo.bulk_all_null", rows);

        Assert.Equal(2, exec.Scalar<int>("SELECT COUNT(*) FROM dbo.bulk_all_null;", null));
        Assert.Equal(2, exec.Scalar<int>("SELECT COUNT(*) FROM dbo.bulk_all_null WHERE g IS NULL;", null));
        Assert.Equal(2, exec.Scalar<int>("SELECT COUNT(*) FROM dbo.bulk_all_null WHERE note IS NULL;", null));
        slot.Commit();
    }

    [Fact]
    public void Command_timeout_aborts_waitfor_with_concrete_timeout_in_expected_window()
    {
        using (var setup = OpenSlot())
        {
            setup.Executor.Execute("CREATE TABLE dbo.command_timeout_setup (id INT);", null);
            setup.Commit();
        }

        using var slot = new ConnectionGateway().Open(_info, commandTimeoutSeconds: 2);
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.ThrowsAny<Exception>(() =>
            slot.Executor.Execute("WAITFOR DELAY '00:00:30';", null));
        stopwatch.Stop();

        Assert.True(IsMssqlTimeout(exception), $"Expected a concrete MSSQL timeout exception chain, got: {exception}");
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void BulkInsert_timeout_aborts_blocked_table_with_concrete_timeout_in_expected_window()
    {
        using (var setup = OpenSlot())
        {
            setup.Executor.Execute("CREATE TABLE dbo.bulk_timeout_target (id INT NOT NULL);", null);
            setup.Commit();
        }

        SqlConnection? blockerConnection = null;
        SqlTransaction? blockerTransaction = null;
        try
        {
            blockerConnection = new SqlConnection(_info.ConnectionString);
            blockerConnection.Open();
            blockerTransaction = blockerConnection.BeginTransaction();
            using (var blockerCommand = blockerConnection.CreateCommand())
            {
                blockerCommand.Transaction = blockerTransaction;
                blockerCommand.CommandText =
                    "SELECT TOP (0) * FROM dbo.bulk_timeout_target WITH (TABLOCKX, HOLDLOCK);";
                blockerCommand.ExecuteNonQuery();
            }

            using var slot = new ConnectionGateway().Open(_info, commandTimeoutSeconds: 2);
            var rows = new List<IDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["id"] = 1 },
            };
            var stopwatch = Stopwatch.StartNew();

            var exception = Assert.ThrowsAny<Exception>(() =>
                slot.Executor.BulkInsert("dbo.bulk_timeout_target", rows));
            stopwatch.Stop();

            Assert.True(
                IsMssqlTimeout(exception),
                $"Expected a concrete MSSQL timeout exception chain, got: {exception}");
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));
        }
        finally
        {
            blockerTransaction?.Dispose();
            blockerConnection?.Dispose();
        }
    }

    [Fact]
    public void BulkInsert_volume_smoke()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;

        exec.Execute("CREATE TABLE dbo.bulk_volume (id INT, name NVARCHAR(20));", null);

        const int count = 100_001; // > 100,000 per T03.5 DoD.
        var rows = Enumerable.Range(0, count).Select(i =>
            (IDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = i,
                ["name"] = "row",
            });
        exec.BulkInsert("dbo.bulk_volume", rows);

        Assert.Equal(count, exec.Scalar<int>("SELECT COUNT(*) FROM dbo.bulk_volume;", null));
        slot.Commit();
    }

    [Fact]
    public void BulkInsert_heterogeneous_key_rows_fail_fast()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;
        exec.Execute("CREATE TABLE dbo.bulk_het (id INT, name NVARCHAR(20));", null);

        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "A" },
            new Dictionary<string, object?> { ["id"] = 2 }, // missing 'name' → differs from first.
        };

        Assert.Throws<InvalidOperationException>(() => exec.BulkInsert("dbo.bulk_het", rows));
    }

    [Fact]
    public void BulkInsert_keepnulls_stores_null_not_column_default()
    {
        // KeepNulls fix: SqlBulkCopyOptions.KeepNulls must be set so that a source DBNull written to a
        // column with a DEFAULT constraint is stored as SQL NULL, not replaced by the column default.
        // Without KeepNulls, SqlBulkCopy would substitute the DEFAULT value, diverging from the
        // documented contract ("null values insert SQL NULL") and from PGSQL behaviour.
        using var slot = OpenSlot();
        var exec = slot.Executor;

        exec.Execute(
            """
            CREATE TABLE dbo.bulk_keep_nulls (
                id   INT          NOT NULL,
                name NVARCHAR(50) NULL DEFAULT N'fallback');
            """, null);

        // Row 2 sends explicit null for 'name'. With KeepNulls the stored value must be NULL,
        // not the column default 'fallback'.
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "explicit" },
            new Dictionary<string, object?> { ["id"] = 2, ["name"] = null },
        };
        exec.BulkInsert("dbo.bulk_keep_nulls", rows);

        // The null row must be stored as NULL — not as 'fallback'.
        var nullCount = exec.Scalar<int>(
            "SELECT COUNT(*) FROM dbo.bulk_keep_nulls WHERE id = 2 AND name IS NULL;", null);
        Assert.Equal(1, nullCount);

        // Sanity: the explicit value in row 1 round-trips correctly.
        var explicitVal = exec.Scalar<string>(
            "SELECT name FROM dbo.bulk_keep_nulls WHERE id = 1;", null);
        Assert.Equal("explicit", explicitVal);

        slot.Commit();
    }

    [Fact]
    public void ConnectionRegistry_built_mssql_with_port_opens_end_to_end()
    {
        // Proves the Port fix: build a ConnectionInfo from XML using Server + Port, then open it.
        // Derive host/port/credentials from the container's own (Data Source=host,port) string.
        var containerBuilder = new SqlConnectionStringBuilder(_container.GetConnectionString());
        var dataSource = containerBuilder.DataSource; // e.g. "127.0.0.1,49170" or "tcp:host,port"
        var commaIdx = dataSource.LastIndexOf(',');
        Assert.True(commaIdx > 0, $"Expected host,port data source but got '{dataSource}'.");
        var host = dataSource[..commaIdx].Replace("tcp:", string.Empty, StringComparison.Ordinal);
        var port = dataSource[(commaIdx + 1)..];
        var user = containerBuilder.UserID;
        var password = containerBuilder.Password;

        var xml = $"""
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="fromxml">
                <Parameters>
                  <Parameter key="Server"                 value="{host}"     type="String"  />
                  <Parameter key="Port"                   value="{port}"     type="Numeric" />
                  <Parameter key="User ID"                value="{user}"     type="String"  />
                  <Parameter key="Password"               value="{password}" type="String"  />
                  <Parameter key="TrustServerCertificate" value="true"       type="String"  />
                </Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        var registry = ConnectionRegistry.Parse(xml);
        var info = (ConnectionInfo)Assert.Single(registry.Connections);

        // The built string must address the port via 'host,port', not a bare 'Port=' keyword.
        Assert.Contains($"{host},{port}", info.ConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain("Port=", info.ConnectionString, StringComparison.OrdinalIgnoreCase);

        using var slot = new ConnectionGateway().Open(info);
        Assert.Equal(1, slot.Executor.Scalar<int>("SELECT 1;", null));
        slot.Commit();
    }

    private static bool IsMssqlTimeout(Exception exception)
    {
        if (exception is SqlException { Number: -2 } or TimeoutException)
            return true;

        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Any(IsMssqlTimeout))
            return true;

        return exception.InnerException is not null && IsMssqlTimeout(exception.InnerException);
    }
}
