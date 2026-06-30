// Copyright (c) 2026 crossVault GmbH.

using System.Globalization;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace HydraData.Engine.IntegrationTests;

/// <summary>
/// Integration tests for the PGSQL DB seam (Query/Scalar/Execute, Commit/Rollback, binary COPY
/// bulk insert including nulls). Requires Docker; the orchestrator runs these at the M2 boundary.
/// </summary>
public sealed class PgsqlDbSeamTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17").Build();
    private ConnectionInfo _info = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _info = new ConnectionInfo("stage", DbType.Pgsql, _container.GetConnectionString());
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private IDbSlot OpenSlot() => new ConnectionGateway().Open(_info);

    [Fact]
    public void Execute_and_Query_and_Scalar_roundtrip()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;

        exec.Execute("CREATE TABLE people (id INT, name TEXT);", null);
        var affected = exec.Execute(
            "INSERT INTO people (id, name) VALUES (@id, @name);",
            new { id = 1, name = "Müller" });
        Assert.Equal(1, affected);

        var count = exec.Scalar<long>("SELECT COUNT(*) FROM people;", null);
        Assert.Equal(1L, count);

        var rows = exec.Query("SELECT id, name FROM people;", null);
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
            slot.Executor.Execute("CREATE TABLE t_commit (id INT);", null);
            slot.Executor.Execute("INSERT INTO t_commit VALUES (1);", null);
            slot.Commit();
        }

        using var verify = OpenSlot();
        Assert.Equal(1L, verify.Executor.Scalar<long>("SELECT COUNT(*) FROM t_commit;", null));
        verify.Commit();
    }

    [Fact]
    public void Rollback_discards_changes()
    {
        using (var setup = OpenSlot())
        {
            setup.Executor.Execute("CREATE TABLE t_rb (id INT);", null);
            setup.Commit();
        }

        using (var slot = OpenSlot())
        {
            slot.Executor.Execute("INSERT INTO t_rb VALUES (1);", null);
            slot.Rollback();
        }

        using var verify = OpenSlot();
        Assert.Equal(0L, verify.Executor.Scalar<long>("SELECT COUNT(*) FROM t_rb;", null));
        verify.Commit();
    }

    [Fact]
    public void BulkInsert_via_binary_copy_inserts_rows_including_nulls()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;

        exec.Execute("CREATE TABLE bulk_rows (id INT, name TEXT);", null);

        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "A" },
            new Dictionary<string, object?> { ["id"] = 2, ["name"] = null }, // WriteNull()
        };
        exec.BulkInsert("bulk_rows", rows);

        Assert.Equal(2L, exec.Scalar<long>("SELECT COUNT(*) FROM bulk_rows;", null));
        Assert.Equal(1L, exec.Scalar<long>("SELECT COUNT(*) FROM bulk_rows WHERE name IS NULL;", null));

        slot.Commit();
    }

    [Fact]
    public void BulkInsert_with_empty_rows_is_noop()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;
        exec.Execute("CREATE TABLE bulk_empty (id INT);", null);

        exec.BulkInsert("bulk_empty", []);

        Assert.Equal(0L, exec.Scalar<long>("SELECT COUNT(*) FROM bulk_empty;", null));
        slot.Commit();
    }

    [Fact]
    public void No_DB_access_opens_no_slot_connection()
    {
        using var slot = OpenSlot();
        slot.Commit(); // no-op: nothing was opened.
    }

    [Fact]
    public void BulkInsert_round_trips_full_type_matrix_including_nulls()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;

        // Column SQL types match the CLR types fed into the typed binary-COPY writer (FIX 2): the
        // PgsqlExecutor maps each CLR value to an explicit NpgsqlDbType rather than letting Npgsql infer.
        exec.Execute(
            """
            CREATE TABLE bulk_types (
                c_int     INT,
                c_long    BIGINT,
                c_short   SMALLINT,
                c_dec     NUMERIC(18,4),
                c_double  DOUBLE PRECISION,
                c_float   REAL,
                c_bool    BOOLEAN,
                c_guid    UUID,
                c_str     TEXT,
                c_dt      TIMESTAMP,
                c_dto     TIMESTAMPTZ,
                c_dt_utc  TIMESTAMPTZ,
                c_date    DATE,
                c_time    TIME,
                c_bytes   BYTEA);
            """, null);

        var guid = Guid.NewGuid();
        var dec = 1234.5678m;
        var dt = new DateTime(2026, 6, 24, 13, 45, 12, DateTimeKind.Unspecified);
        // FIX A: a DateTimeOffset with a NON-zero offset (+2h) — binary COPY only accepts offset 0 for
        // timestamptz, so the writer must store the UTC instant. Round-trip compared as UTC.
        var dto = new DateTimeOffset(2026, 6, 24, 13, 45, 12, TimeSpan.FromHours(2));
        // FIX A: a DateTime with Kind=Utc — must map to timestamptz (written as-is) without throwing.
        var dtUtc = new DateTime(2026, 6, 24, 11, 45, 12, DateTimeKind.Utc);
        var dateOnly = new DateOnly(2026, 6, 24); // FIX B
        var timeOnly = new TimeOnly(13, 45, 12);  // FIX B
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["c_int"] = 42, ["c_long"] = 9_000_000_000L, ["c_short"] = (short)7,
                ["c_dec"] = dec, ["c_double"] = 3.14159d, ["c_float"] = 2.5f,
                ["c_bool"] = true, ["c_guid"] = guid, ["c_str"] = "hello",
                ["c_dt"] = dt, ["c_dto"] = dto, ["c_dt_utc"] = dtUtc,
                ["c_date"] = dateOnly, ["c_time"] = timeOnly, ["c_bytes"] = bytes,
            },
            new Dictionary<string, object?>
            {
                ["c_int"] = null, ["c_long"] = null, ["c_short"] = null,
                ["c_dec"] = null, ["c_double"] = null, ["c_float"] = null,
                ["c_bool"] = null, ["c_guid"] = null, ["c_str"] = null,
                ["c_dt"] = null, ["c_dto"] = null, ["c_dt_utc"] = null,
                ["c_date"] = null, ["c_time"] = null, ["c_bytes"] = null,
            },
        };
        exec.BulkInsert("bulk_types", rows);

        Assert.Equal(2L, exec.Scalar<long>("SELECT COUNT(*) FROM bulk_types;", null));

        var present = exec.Query("SELECT * FROM bulk_types WHERE c_int = 42;", null);
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
        // Npgsql returns timestamptz as a UTC DateTime; compare on the UTC instant. The +2h offset
        // DateTimeOffset must have been stored as its UTC instant (11:45:12Z) — proving the offset
        // was normalised rather than rejected.
        Assert.Equal(dto.UtcDateTime, ((DateTime)row.c_dto).ToUniversalTime());
        Assert.Equal(dtUtc, ((DateTime)row.c_dt_utc).ToUniversalTime());
        Assert.Equal(dateOnly, (DateOnly)row.c_date);
        Assert.Equal(timeOnly, (TimeOnly)row.c_time);
        Assert.Equal(bytes, (byte[])row.c_bytes);

        // The all-null row: every column is NULL.
        Assert.Equal(1L, exec.Scalar<long>(
            "SELECT COUNT(*) FROM bulk_types WHERE c_int IS NULL AND c_long IS NULL AND c_short IS NULL " +
            "AND c_dec IS NULL AND c_double IS NULL AND c_float IS NULL AND c_bool IS NULL " +
            "AND c_guid IS NULL AND c_str IS NULL AND c_dt IS NULL AND c_dto IS NULL AND c_dt_utc IS NULL " +
            "AND c_date IS NULL AND c_time IS NULL AND c_bytes IS NULL;",
            null));

        slot.Commit();
    }

    [Fact]
    public void BulkInsert_types_column_from_first_non_null_value_when_row0_is_null()
    {
        // PGSQL twin of MssqlDbSeamTests.BulkInsert_types_column_from_first_non_null_value_when_row0_is_null:
        // the 'g' column is NULL in row 0 and a real Guid in row 1. The binary-COPY writer types each column
        // per-value (NpgsqlTypeOf), so a leading null must not poison the typing of the later Guid. The Guid
        // must round-trip.
        using var slot = OpenSlot();
        var exec = slot.Executor;

        exec.Execute("CREATE TABLE bulk_lead_null (id INT, g UUID NULL);", null);

        var guid = Guid.NewGuid();
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["g"] = null },
            new Dictionary<string, object?> { ["id"] = 2, ["g"] = guid },
        };
        exec.BulkInsert("bulk_lead_null", rows);

        Assert.Equal(2L, exec.Scalar<long>("SELECT COUNT(*) FROM bulk_lead_null;", null));
        var stored = exec.Scalar<Guid>("SELECT g FROM bulk_lead_null WHERE id = 2;", null);
        Assert.Equal(guid, stored);
        Assert.Equal(1L, exec.Scalar<long>("SELECT COUNT(*) FROM bulk_lead_null WHERE g IS NULL;", null));

        slot.Commit();
    }

    [Fact]
    public void BulkInsert_null_into_default_column_stores_null_not_default()
    {
        // PGSQL symmetry with MssqlDbSeamTests.BulkInsert_keepnulls_stores_null_not_column_default: a column
        // with a DEFAULT and a null source value must store SQL NULL, not the column default. PGSQL binary
        // COPY's WriteNull() always stores NULL (it never substitutes the DEFAULT), so this must hold without
        // any KeepNulls-style flag (the MSSQL path needs SqlBulkCopyOptions.KeepNulls for the same contract).
        using var slot = OpenSlot();
        var exec = slot.Executor;

        exec.Execute(
            """
            CREATE TABLE bulk_default_null (
                id   INT  NOT NULL,
                name TEXT NULL DEFAULT 'fallback');
            """, null);

        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "explicit" },
            new Dictionary<string, object?> { ["id"] = 2, ["name"] = null },
        };
        exec.BulkInsert("bulk_default_null", rows);

        // The null row must be stored as NULL — not as the column default 'fallback'.
        Assert.Equal(1L, exec.Scalar<long>(
            "SELECT COUNT(*) FROM bulk_default_null WHERE id = 2 AND name IS NULL;", null));

        // Sanity: the explicit value in row 1 round-trips correctly.
        Assert.Equal("explicit", exec.Scalar<string>(
            "SELECT name FROM bulk_default_null WHERE id = 1;", null));

        slot.Commit();
    }

    [Fact]
    public void BulkInsert_unsupported_clr_type_throws_clear_engine_error()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;
        exec.Execute("CREATE TABLE bulk_bad (c INTERVAL);", null);

        var rows = new List<IDictionary<string, object?>>
        {
            // TimeSpan is deliberately NOT in the supported ETL matrix → clear engine error naming the type.
            new Dictionary<string, object?> { ["c"] = TimeSpan.FromMinutes(5) },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => exec.BulkInsert("bulk_bad", rows));
        Assert.Contains("c", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TimeSpan", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BulkInsert_volume_smoke()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;

        exec.Execute("CREATE TABLE bulk_volume (id INT, name TEXT);", null);

        const int count = 100_001; // > 100,000 per T03.5 DoD.
        var rows = Enumerable.Range(0, count).Select(i =>
            (IDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = i,
                ["name"] = "row",
            });
        exec.BulkInsert("bulk_volume", rows);

        Assert.Equal((long)count, exec.Scalar<long>("SELECT COUNT(*) FROM bulk_volume;", null));
        slot.Commit();
    }

    [Fact]
    public void BulkInsert_quotes_mixed_case_table_identifier()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;

        // A mixed-case table must be created quoted, then bulk-inserted via the quoted COPY path.
        exec.Execute("""CREATE TABLE "MixedCase" (id INT);""", null);

        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1 },
        };
        exec.BulkInsert("MixedCase", rows);

        Assert.Equal(1L, exec.Scalar<long>("""SELECT COUNT(*) FROM "MixedCase";""", null));
        slot.Commit();
    }

    [Fact]
    public void BulkInsert_heterogeneous_key_rows_fail_fast()
    {
        using var slot = OpenSlot();
        var exec = slot.Executor;
        exec.Execute("CREATE TABLE bulk_het (id INT, name TEXT);", null);

        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "A" },
            new Dictionary<string, object?> { ["id"] = 2 }, // missing 'name' → differs from first.
        };

        Assert.Throws<InvalidOperationException>(() => exec.BulkInsert("bulk_het", rows));
    }

    [Fact]
    public void Command_timeout_aborts_a_long_query_quickly()
    {
        // FIX G: the command timeout must actually fire. Open the slot with a ~2s command timeout and
        // run pg_sleep(30): the call must abort with a timeout error in a couple of seconds, NOT after
        // 30s. Asserts both that an exception is thrown and that it happened well before the sleep ends.
        using var slot = new ConnectionGateway().Open(_info, commandTimeoutSeconds: 2);
        var exec = slot.Executor;

        var started = DateTime.UtcNow;
        Assert.ThrowsAny<Exception>(() => exec.Scalar<int>("SELECT pg_sleep(30);", null));
        var elapsed = DateTime.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(15),
            $"Expected the 2s command timeout to abort pg_sleep(30) quickly, but it took {elapsed}.");
    }

    [Fact]
    public void BulkInsert_copy_timeout_aborts_quickly_when_trigger_blocks()
    {
        // The PGSQL binary COPY command timeout is connection-level (CommandTimeout in the connection
        // string, set by DbSlot.WithPgsqlCommandTimeout). To verify it fires on COPY (not only on
        // plain queries), create a table with a BEFORE INSERT trigger that calls pg_sleep(30). The
        // binary COPY invokes the trigger per row, so it blocks. With a ~2s command timeout the COPY
        // must abort well before 30s.
        using var setup = OpenSlot();
        setup.Executor.Execute(
            """
            CREATE TABLE copy_timeout_target (id INT);
            CREATE OR REPLACE FUNCTION fn_copy_sleep()
              RETURNS trigger LANGUAGE plpgsql AS
              $$ BEGIN PERFORM pg_sleep(30); RETURN NEW; END; $$;
            CREATE TRIGGER trg_copy_sleep
              BEFORE INSERT ON copy_timeout_target
              FOR EACH ROW EXECUTE FUNCTION fn_copy_sleep();
            """, null);
        setup.Commit();

        // Open a separate slot with a 2-second command timeout.
        using var slot = new ConnectionGateway().Open(_info, commandTimeoutSeconds: 2);
        var exec = slot.Executor;

        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1 },
        };

        var started = DateTime.UtcNow;
        Assert.ThrowsAny<Exception>(() => exec.BulkInsert("copy_timeout_target", rows));
        var elapsed = DateTime.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(15),
            $"Expected the 2s COPY command timeout to abort quickly, but it took {elapsed}.");
    }

    [Fact]
    public void ConnectionRegistry_built_pgsql_from_xml_opens_end_to_end()
    {
        // FIX 4 symmetry (mirrors the MSSQL ConnectionRegistry_built_*_opens_end_to_end test): build a
        // PGSQL ConnectionInfo from a connections.xml document, then open it against the container.
        // Derive host/port/database/credentials from the container's own connection string.
        var b = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());

        var xml = $"""
            <ConnectionStrings>
              <ConnectionString targetSystem="PGSQL" name="fromxml">
                <Parameters>
                  <Parameter key="Host"     value="{b.Host}"                                       type="String"  />
                  <Parameter key="Port"     value="{b.Port.ToString(CultureInfo.InvariantCulture)}" type="Numeric" />
                  <Parameter key="Database" value="{b.Database}"                                   type="String"  />
                  <Parameter key="Username" value="{b.Username}"                                   type="String"  />
                  <Parameter key="Password" value="{b.Password}"                                   type="String"  />
                </Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        var registry = ConnectionRegistry.Parse(xml);
        var info = (ConnectionInfo)Assert.Single(registry.Connections);

        using var slot = new ConnectionGateway().Open(info);
        Assert.Equal(1, slot.Executor.Scalar<int>("SELECT 1;", null));
        slot.Commit();
    }
}
