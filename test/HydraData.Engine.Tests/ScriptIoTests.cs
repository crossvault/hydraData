// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T05.3–T05.6: the workspace-backed <see cref="IScriptIo"/> — safemode dual consent, encapsulated
/// CSV/Excel roundtrips with sandbox enforcement, <c>WriteCsvFast</c> escaping, and DuckDB name
/// normalisation/sandboxing. DuckDB runs in-process (no Docker), so these stay in the unit suite.
/// </summary>
public sealed class ScriptIoTests : IDisposable
{
    private static readonly Guid FixedRunId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _baseDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public ScriptIoTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "HydraData-io-tests", Guid.NewGuid().ToString("N"));
        _inputDir = Path.Combine(_baseDir, "input");
        _outputDir = Path.Combine(_baseDir, "output");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
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

    private Workspace NewWorkspace() =>
        new(_baseDir, FixedRunId, new PumpFolderPolicy([_inputDir], [_outputDir]));

    private ScriptIo NewIo(bool engineUnsafe = false, bool scriptUnsafe = false) =>
        new(NewWorkspace(), engineUnsafe, scriptUnsafe);

    // ── Safemode ─────────────────────────────────────

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Raw_throws_without_dual_consent(bool engineUnsafe, bool scriptUnsafe)
    {
        var io = NewIo(engineUnsafe, scriptUnsafe);
        Assert.Throws<InvalidOperationException>(() => io.Raw());
    }

    [Fact]
    public void Raw_allowed_only_with_both_engine_flag_and_unsafe_meta()
    {
        var io = NewIo(engineUnsafe: true, scriptUnsafe: true);
        var raw = io.Raw();
        Assert.NotNull(raw);
    }

    // ── CSV roundtrip + sandbox (T05.4) ──────────────────────────────────────────

    [Fact]
    public void ReadCsv_materialises_rows_from_input()
    {
        File.WriteAllText(Path.Combine(_inputDir, "k.csv"), "Id;Name\n1;Alice\n2;Bob\n");
        var io = NewIo();

        var rows = io.ReadCsv(Path.Combine(_inputDir, "k.csv"));

        Assert.Equal(2, rows.Count);
        var first = (IDictionary<string, object?>)rows[0];
        Assert.Equal("1", first["Id"]);
        Assert.Equal("Alice", first["Name"]);
    }

    [Fact]
    public void ReadCsvFast_projects_via_selector_without_exposing_refstruct()
    {
        File.WriteAllText(Path.Combine(_inputDir, "k.csv"), "Id;Name\n1;Alice\n2;Bob\n");
        var io = NewIo();

        // The selector only ever sees IReadOnlyDictionary<string,string> — no Sep Row/Col type.
        var names = io.ReadCsvFast(
            Path.Combine(_inputDir, "k.csv"),
            row => row["Name"]);

        Assert.Equal(["Alice", "Bob"], names);
    }

    [Fact]
    public void WriteCsv_then_ReadCsv_roundtrips_under_rundir()
    {
        var io = NewIo();
        var rows = new object[]
        {
            new { Id = 1, Name = "Alice" },
            new { Id = 2, Name = "Bob" },
        };

        // RunDir-relative path: implicitly both readable and writable.
        io.WriteCsv("out.csv", rows);
        var back = io.ReadCsv("out.csv");

        Assert.Equal(2, back.Count);
        Assert.Equal("Alice", ((IDictionary<string, object?>)back[0])["Name"]);
    }

    [Fact]
    public void ReadCsv_outside_allowlist_throws()
    {
        var outside = Path.Combine(_baseDir, "secret.csv");
        File.WriteAllText(outside, "Id\n1\n");
        var io = NewIo();

        Assert.Throws<UnauthorizedAccessException>(() => io.ReadCsv(outside));
    }

    [Fact]
    public void WriteCsv_outside_allowlist_throws()
    {
        var io = NewIo();
        var rows = new object[] { new { Id = 1 } };

        Assert.Throws<UnauthorizedAccessException>(
            () => io.WriteCsv(Path.Combine(_baseDir, "evil.csv"), rows));
    }

    // ── WriteCsvFast (T05.5) ─────────────────────────────────────────────────────

    [Fact]
    public void WriteCsvFast_roundtrips_via_callback()
    {
        var io = NewIo();
        var items = new[]
        {
            new { Id = 1, Name = "Alice" },
            new { Id = 2, Name = "Bob" },
        };

        io.WriteCsvFast(
            "fast.csv",
            items,
            (w, k) =>
            {
                w.Format("Id", k.Id);
                w.Set("Name", k.Name);
            });

        var back = io.ReadCsv("fast.csv");
        Assert.Equal(2, back.Count);
        Assert.Equal("1", ((IDictionary<string, object?>)back[0])["Id"]);
        Assert.Equal("Bob", ((IDictionary<string, object?>)back[1])["Name"]);
    }

    [Fact]
    public void WriteCsvFast_uses_semicolon_separator_by_default()
    {
        var io = NewIo();
        io.WriteCsvFast(
            Path.Combine(_outputDir, "sep.csv"),
            [new { A = "x", B = "y" }],
            (w, r) =>
            {
                w.Set("A", r.A);
                w.Set("B", r.B);
            });

        var text = File.ReadAllText(Path.Combine(_outputDir, "sep.csv"));
        Assert.Contains("A;B", text);
        Assert.Contains("x;y", text);
    }

    [Fact]
    public void WriteCsvFast_escapes_values_with_separator_quotes_and_newlines()
    {
        var io = NewIo();
        io.WriteCsvFast(
            "esc.csv",
            [new { V = "a;b\"c\nd" }],
            (w, r) => w.Set("V", r.V));

        // Sep's escaping must round-trip the value verbatim through ReadCsv.
        var back = io.ReadCsv("esc.csv");
        Assert.Single(back);
        Assert.Equal("a;b\"c\nd", ((IDictionary<string, object?>)back[0])["V"]);
    }

    [Fact]
    public void WriteCsvFast_outside_allowlist_throws()
    {
        var io = NewIo();
        Assert.Throws<UnauthorizedAccessException>(
            () => io.WriteCsvFast(
                Path.Combine(_baseDir, "evil.csv"),
                [new { A = 1 }],
                (w, r) => w.Format("A", r.A)));
    }

    // ── Excel roundtrip + sandbox (T05.4) ────────────────────────────────────────

    [Fact]
    public void WriteExcel_then_ReadExcel_roundtrips()
    {
        var io = NewIo();
        var rows = new object[]
        {
            new { Id = 1, Name = "Alice" },
            new { Id = 2, Name = "Bob" },
        };

        io.WriteExcel("out.xlsx", rows);
        var back = io.ReadExcel("out.xlsx");

        Assert.Equal(2, back.Count);
        var first = (IDictionary<string, object?>)back[0];
        Assert.Equal("Alice", first["Name"]?.ToString());
    }

    [Fact]
    public void ReadExcel_outside_allowlist_throws()
    {
        var io = NewIo();
        Assert.Throws<UnauthorizedAccessException>(
            () => io.ReadExcel(Path.Combine(_baseDir, "secret.xlsx")));
    }

    // ── DuckDB (T05.6) ───────────────────────────────────────────────────────────

    [Fact]
    public void Duck_in_memory_create_table_and_query()
    {
        var io = NewIo();
        using var db = io.Duck();

        db.Execute("CREATE TABLE t (id INTEGER, name TEXT);");
        db.Execute("INSERT INTO t VALUES (1, 'a'), (2, 'b');");
        var rows = db.Query("SELECT name FROM t ORDER BY id;");

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Analyze_runs_sql_in_memory()
    {
        var io = NewIo();
        var rows = io.Analyze("SELECT 21 * 2 AS answer;");

        Assert.Single(rows);
    }

    [Fact]
    public void Duck_named_lands_under_duck_directory()
    {
        var ws = NewWorkspace();
        var io = new ScriptIo(ws, engineAllowsUnsafe: false, scriptDeclaresUnsafe: false);

        using (var db = io.Duck("abgleich"))
        {
            db.Execute("CREATE TABLE t (id INTEGER);");
        }

        var expected = Path.Combine(ws.Duck, "abgleich.duckdb");
        Assert.True(File.Exists(expected), $"expected duck db at {expected}");
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("C:evil")]
    public void Duck_named_rejects_path_or_traversal(string name)
    {
        var io = NewIo();
        Assert.Throws<ArgumentException>(() => io.Duck(name));
    }

    [Fact]
    public void Duck_named_cannot_escape_to_a_free_path_in_safemode()
    {
        // The encapsulated Duck(name) API can never produce a free Data Source: a name that tries to
        // escape duck/ is rejected by name normalisation before any connection is opened. The free-path
        // unsafe gate in OpenDuck is the defensive backstop for the future Raw() seam (M3).
        var io = NewIo(engineUnsafe: false, scriptUnsafe: false);
        Assert.Throws<ArgumentException>(() => io.Duck("../../outside"));
    }

    [Fact]
    public void Duck_named_rejects_connection_string_option_injection()
    {
        // "a;threads=4" is a legal Windows filename but would inject connection-string options once
        // interpolated into a Data Source. NormalizeDuckName must reject ';' and '='.
        var io = NewIo();
        Assert.Throws<ArgumentException>(() => io.Duck("a;threads=4"));
        Assert.Throws<ArgumentException>(() => io.Duck("a=b"));
    }

    // ── DuckDB external-access sandbox ───────

    [Fact]
    public void Analyze_with_read_csv_outside_allowlist_throws_in_safemode()
    {
        // A safemode script (no @unsafe, AllowUnsafeDirectAccess=false) must not be able to reach an
        // arbitrary OS path through DuckDB's SQL file functions. Write a real CSV outside any allowlist
        // and confirm DuckDB rejects the read because external access is disabled.
        var secret = Path.Combine(_baseDir, "secret.csv");
        File.WriteAllText(secret, "k\n1\n");
        var io = NewIo(engineUnsafe: false, scriptUnsafe: false);

        var sql = $"SELECT * FROM read_csv('{secret.Replace("\\", "/")}');";

        // DuckDB surfaces the disabled-external-access guard as a "Permission Error: ... file system
        // operations are disabled by configuration". Assert that category specifically so a broken/typo'd
        // query (which DuckDB reports as a Parser/Binder/Catalog Error) cannot make this pass green.
        var ex = Assert.ThrowsAny<Exception>(() => io.Analyze(sql));
        Assert.Contains("Permission Error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_with_read_csv_is_allowed_under_dual_consent_unsafe()
    {
        // With both the engine flag and @unsafe granted, external access stays enabled, so the same SQL
        // file function works. This proves the safemode rejection above is caused by the sandbox switch,
        // not by a malformed query.
        var data = Path.Combine(_baseDir, "data.csv");
        File.WriteAllText(data, "k\n1\n2\n");
        var io = NewIo(engineUnsafe: true, scriptUnsafe: true);

        var sql = $"SELECT count(*) AS n FROM read_csv('{data.Replace("\\", "/")}');";
        var rows = io.Analyze(sql);

        Assert.Single(rows);
    }

    [Fact]
    public void Duck_in_memory_read_csv_outside_allowlist_throws_in_safemode()
    {
        // The same guard applies to Duck() handles, not just Analyze().
        var secret = Path.Combine(_baseDir, "secret2.csv");
        File.WriteAllText(secret, "k\n1\n");
        var io = NewIo(engineUnsafe: false, scriptUnsafe: false);

        using var db = io.Duck();
        var sql = $"SELECT * FROM read_csv('{secret.Replace("\\", "/")}');";

        var ex = Assert.ThrowsAny<Exception>(() => db.Query(sql));
        Assert.Contains("Permission Error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duck_named_persisted_handle_read_csv_outside_allowlist_throws_in_safemode()
    {
        // Item 5: the same external-access guard must apply to a NAMED, PERSISTED handle Duck("db"), not
        // only to in-memory Duck()/Analyze. OpenDuck runs `SET enable_external_access=false` for every
        // non-unsafe handle — including the persisted db file under duck/ — so a read_csv of an OS path
        // outside the read allowlist must be denied with an external-access error.
        var secret = Path.Combine(_baseDir, "secret3.csv");
        File.WriteAllText(secret, "k\n1\n");
        var io = NewIo(engineUnsafe: false, scriptUnsafe: false);

        using var db = io.Duck("persisted");
        var sql = $"SELECT * FROM read_csv('{secret.Replace("\\", "/")}');";

        var ex = Assert.ThrowsAny<Exception>(() => db.Query(sql));
        Assert.Contains("Permission Error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Excel streaming (T05.4) ──────────────────────────────────────────────────

    [Fact]
    public void WriteExcel_then_StreamExcel_roundtrips()
    {
        var io = NewIo();
        var rows = new object[]
        {
            new { Id = 1, Name = "Alice" },
            new { Id = 2, Name = "Bob" },
        };

        io.WriteExcel("stream.xlsx", rows);
        var back = io.StreamExcel("stream.xlsx").ToList();

        Assert.Equal(2, back.Count);
        var first = (IDictionary<string, object?>)back[0];
        Assert.Equal("Alice", first["Name"]?.ToString());
    }
}
