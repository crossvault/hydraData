// Copyright (c) 2026 crossVault GmbH.

using System.Data;
using System.Globalization;
using Dapper;
using DuckDB.NET.Data;
using MiniExcelLibs;
using nietras.SeparatedValues;

namespace HydraData.Engine;

/// <summary>
/// Workspace-backed <see cref="IScriptIo"/> (cluster 05). All file reads go through
/// <see cref="Workspace.ResolveRead"/> and all writes through <see cref="Workspace.ResolveWrite"/>, so
/// the folder allowlist and run-directory sandbox apply uniformly.
/// CSV uses Sep, Excel uses MiniExcel, and DuckDB uses DuckDB.NET; the Sep ref-structs (<c>Row</c>/
/// <c>Col</c>) never cross the script-facing surface — readers materialise each row into a dictionary
/// and the writer buffers a row's values before handing them to Sep.
/// </summary>
/// <remarks>
/// Safemode is enforced here: unsafe direct access (<see cref="Raw"/>, a free
/// DuckDB <c>Data Source</c> outside the run directory) requires both the engine flag
/// <c>AllowUnsafeDirectAccess</c> and the step's <c>@unsafe</c> meta. The encapsulated tools
/// (CSV/Excel/<c>Analyze</c>/<c>Duck</c>) stay available in safemode within the sandbox.
/// </remarks>
internal sealed class ScriptIo : IScriptIo
{
    private const string InMemoryDataSource = ":memory:";

    private readonly Workspace _workspace;
    private readonly bool _engineAllowsUnsafe;
    private readonly bool _scriptDeclaresUnsafe;

    /// <summary>Initializes the IO surface for a single step.</summary>
    /// <param name="workspace">The run sandbox.</param>
    /// <param name="engineAllowsUnsafe">The engine flag <c>AllowUnsafeDirectAccess</c>.</param>
    /// <param name="scriptDeclaresUnsafe">Whether the step declared <c>@unsafe: true</c>.</param>
    public ScriptIo(Workspace workspace, bool engineAllowsUnsafe, bool scriptDeclaresUnsafe)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _engineAllowsUnsafe = engineAllowsUnsafe;
        _scriptDeclaresUnsafe = scriptDeclaresUnsafe;
    }

    /// <summary>Whether unsafe direct access is permitted (dual consent: engine flag AND <c>@unsafe</c>).</summary>
    private bool UnsafeAllowed => _engineAllowsUnsafe && _scriptDeclaresUnsafe;

    // ── Excel (MiniExcel) ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public List<dynamic> ReadExcel(string path, string? sheet = null)
    {
        var full = _workspace.ResolveRead(path);
        return MiniExcel.Query(full, useHeaderRow: true, sheetName: sheet).Cast<dynamic>().ToList();
    }

    /// <inheritdoc />
    public IEnumerable<dynamic> StreamExcel(string path, string? sheet = null)
    {
        var full = _workspace.ResolveRead(path);
        // MiniExcel.Query is lazy; sandbox resolution happens eagerly above.
        foreach (var row in MiniExcel.Query(full, useHeaderRow: true, sheetName: sheet))
            yield return row;
    }

    /// <summary>
    /// Resolves a write path through the sandbox and ensures its parent directory exists, so writing into
    /// a not-yet-materialised run directory (which is created lazily) succeeds.
    /// </summary>
    private string ResolveWriteTarget(string path)
    {
        var full = _workspace.ResolveWrite(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return full;
    }

    /// <inheritdoc />
    public void WriteExcel(string path, IEnumerable<object> rows, string? template = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var full = ResolveWriteTarget(path);

        if (template is null)
        {
            MiniExcel.SaveAs(full, rows, overwriteFile: true);
        }
        else
        {
            var templateFull = _workspace.ResolveRead(template);
            MiniExcel.SaveAsByTemplate(full, templateFull, new { rows });
        }
    }

    // ── CSV (Sep) ────────────────────────────────────────────────────────────────

    // Reads unescape quoted fields; writes escape (quote) fields that contain the separator, quotes or
    // newlines, so encapsulated CSV always round-trips.
    private static SepReader OpenSepReader(string full) =>
        Sep.Auto.Reader(o => o with { Unescape = true }).FromFile(full);

    private static SepWriter OpenSepWriter(string full) =>
        Sep.New(';').Writer(o => o with { Escape = true }).ToFile(full);

    /// <inheritdoc />
    public List<dynamic> ReadCsv(string path)
    {
        var full = _workspace.ResolveRead(path);
        var result = new List<dynamic>();

        using var reader = OpenSepReader(full);
        var names = reader.Header.ColNames;
        foreach (var row in reader)
        {
            // Materialise each row immediately; no Sep ref-struct escapes the enumeration.
            var bag = (IDictionary<string, object?>)new System.Dynamic.ExpandoObject();
            foreach (var name in names)
                bag[name] = row[name].ToString();
            result.Add(bag);
        }

        return result;
    }

    /// <inheritdoc />
    public List<T> ReadCsvFast<T>(string path, Func<IReadOnlyDictionary<string, string>, T> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var full = _workspace.ResolveRead(path);
        var result = new List<T>();

        using var reader = OpenSepReader(full);
        var names = reader.Header.ColNames;
        // One reusable dictionary per file; refilled per row. The selector must not retain it.
        var view = new Dictionary<string, string>(names.Count, StringComparer.Ordinal);
        foreach (var row in reader)
        {
            view.Clear();
            foreach (var name in names)
                view[name] = row[name].ToString();
            result.Add(selector(view));
        }

        return result;
    }

    /// <inheritdoc />
    public void WriteCsv(string path, IEnumerable<object> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var materialised = rows.ToList();
        var full = ResolveWriteTarget(path);

        var columns = DiscoverColumns(materialised);
        using var writer = OpenSepWriter(full);
        foreach (var item in materialised)
        {
            var map = ToMap(item);
            using var row = writer.NewRow();
            foreach (var column in columns)
                row[column].Set(Stringify(map.TryGetValue(column, out var v) ? v : null));
        }
    }

    /// <inheritdoc />
    public void WriteCsvFast<T>(string path, IEnumerable<T> rows, Action<ICsvRowWriter, T> write)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(write);
        var full = ResolveWriteTarget(path);

        using var writer = OpenSepWriter(full);
        var buffer = new RowBuffer();
        foreach (var item in rows)
        {
            buffer.Reset();
            write(buffer, item);

            using var row = writer.NewRow();
            // Sep performs separator/quote/newline escaping when writing each column value.
            foreach (var (column, value) in buffer.Values)
                row[column].Set(value ?? string.Empty);
        }
    }

    // ── DuckDB ───────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public List<dynamic> Analyze(string sql, object? param = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        // Analyze runs over an in-memory DuckDB. SQL-level file functions (read_csv/read_parquet/ATTACH/
        // COPY) can otherwise reach ANY OS path and bypass the read allowlist, so OpenDuck disables
        // DuckDB external access by default and only leaves it on under dual-consent unsafe
        //.
        using var handle = OpenDuck(InMemoryDataSource);
        return handle.Query(sql, param);
    }

    /// <inheritdoc />
    public IDuckHandle Duck() => OpenDuck(InMemoryDataSource);

    /// <inheritdoc />
    public IDuckHandle Duck(string name)
    {
        var fileName = NormalizeDuckName(name);
        // Always resolved under the run's duck/ directory (sandbox); a free path needs unsafe access.
        var path = Path.Combine(_workspace.Duck, fileName);
        return OpenDuck(path);
    }

    /// <inheritdoc />
    public IRawAccess Raw()
    {
        if (!UnsafeAllowed)
            throw new InvalidOperationException(
                "Raw access requires both the engine flag 'AllowUnsafeDirectAccess=true' and the step's " +
                "'@unsafe: true' meta. At least one is missing.");

        return new RawAccess();
    }

    /// <summary>
    /// Opens a DuckDB connection at <paramref name="dataSource"/>. In-memory and run-local
    /// (<c>duck/</c>) sources are always allowed; any other on-disk source is a free <c>Data Source</c>
    /// that requires unsafe access.
    /// </summary>
    private DuckHandle OpenDuck(string dataSource)
    {
        if (!IsSandboxedDataSource(dataSource) && !UnsafeAllowed)
            throw new InvalidOperationException(
                "A free DuckDB data source outside the run directory requires both the engine flag " +
                "'AllowUnsafeDirectAccess=true' and the step's '@unsafe: true' meta.");

        // Build the connection string via the typed builder so a crafted data source (e.g. a name that
        // contains ';' or '=') cannot inject extra connection-string options.
        var connectionString = new DuckDBConnectionStringBuilder { DataSource = dataSource }.ConnectionString;

        var connection = new DuckDBConnection(connectionString);
        try
        {
            connection.Open();

            // SANDBOX: DuckDB's SQL file functions (read_csv/read_parquet/ATTACH/COPY) can otherwise read
            // or attach ANY OS path, bypassing the read allowlist. Disable
            // external access BY DEFAULT for the encapsulated tools; only dual-consent unsafe leaves it on.
            // The persisted db file under duck/ stays accessible because it is the connection's own data
            // source, not an SQL-level file function.
            if (!UnsafeAllowed)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SET enable_external_access=false;";
                cmd.ExecuteNonQuery();
            }

            return new DuckHandle(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private bool IsSandboxedDataSource(string dataSource) =>
        // The ':memory:' short-circuit MUST precede IsInside: ':memory:' is not a real path and would
        // otherwise be normalised/compared against RunDir and wrongly judged outside the sandbox.
        string.Equals(dataSource, InMemoryDataSource, StringComparison.Ordinal)
        || Workspace.IsInside(_workspace.RunDir, dataSource);

    /// <summary>
    /// Normalises a DuckDB database name to a safe, run-local file name. Rejects path separators and
    /// <c>..</c> traversal so the database always lands directly under <c>duck/</c> (runtime contract
    /// section 20b).
    /// </summary>
    private static string NormalizeDuckName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Reject any path structure: separators, drive/volume prefixes and traversal segments. Also reject
        // ';', '=' and whitespace which are not invalid filename chars on Windows but would let a crafted
        // name inject DuckDB connection-string options once interpolated into a data source (runtime contract
        // section 20c). The typed DuckDBConnectionStringBuilder in OpenDuck is the second line of defence.
        if (name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal)
            || name.Contains(':', StringComparison.Ordinal)
            || name.Contains(';', StringComparison.Ordinal)
            || name.Contains('=', StringComparison.Ordinal)
            || name.Any(char.IsWhiteSpace)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                $"DuckDB name '{name}' must be a plain file name without path separators, ':', ';', '=', " +
                "whitespace or '..'.",
                nameof(name));
        }

        // A name that resolves to anything other than itself (e.g. "." or trailing dots) is rejected.
        if (Path.GetFileName(name) != name)
            throw new ArgumentException($"DuckDB name '{name}' is not a plain file name.", nameof(name));

        // Reject trailing dots: on Windows these are silently stripped by the filesystem, so "a." and "a"
        // would collide and the lexical name would not match the on-disk file.
        if (name.EndsWith('.'))
            throw new ArgumentException($"DuckDB name '{name}' must not end with a dot.", nameof(name));

        return name.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase) ? name : name + ".duckdb";
    }

    private static List<string> DiscoverColumns(IReadOnlyList<object> rows)
    {
        // Column set is taken from the first non-empty row's keys (runtime contract: the first row defines the
        // schema). The loop breaks as soon as a row contributes columns, so it does not union across rows.
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var key in ToMap(row).Keys)
            {
                if (seen.Add(key))
                    columns.Add(key);
            }

            // Mirror BulkInsert's "first row defines the columns" convention but tolerate later additions.
            if (columns.Count > 0)
                break;
        }

        return columns;
    }

    private static IReadOnlyDictionary<string, object?> ToMap(object? row)
    {
        switch (row)
        {
            case null:
                return new Dictionary<string, object?>();
            case IDictionary<string, object?> dict:
                return new Dictionary<string, object?>(dict);
            case IReadOnlyDictionary<string, object?> rdict:
                return rdict;
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        // OrderBy(MetadataToken) mirrors AsciiTable.ToMap: discover columns in declaration order so the
        // header / insert column order is stable across runtimes (CLR reflection order is not guaranteed).
        foreach (var prop in row.GetType().GetProperties().OrderBy(p => p.MetadataToken))
        {
            if (prop.GetIndexParameters().Length == 0 && prop.CanRead)
                map[prop.Name] = prop.GetValue(row);
        }

        return map;
    }

    private static string Stringify(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Buffers a single row's column/value pairs for <see cref="WriteCsvFast{T}"/> so the script-supplied
    /// callback never touches a Sep ref-struct. Preserves insertion order for a stable column layout.
    /// </summary>
    private sealed class RowBuffer : ICsvRowWriter
    {
        private readonly List<KeyValuePair<string, string?>> _values = [];
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

        public IReadOnlyList<KeyValuePair<string, string?>> Values => _values;

        public void Reset()
        {
            _values.Clear();
            _index.Clear();
        }

        public void Set(string column, string? value)
        {
            ArgumentException.ThrowIfNullOrEmpty(column);
            if (_index.TryGetValue(column, out var i))
                _values[i] = new(column, value);
            else
            {
                _index[column] = _values.Count;
                _values.Add(new(column, value));
            }
        }

        public void Format(string column, object? value) => Set(column, Stringify(value));
    }

    /// <summary>Concrete raw-access handle. Intentionally empty until raw members are designed.</summary>
    private sealed class RawAccess : IRawAccess
    {
    }

    /// <summary>
    /// DuckDB handle over a single <see cref="DuckDBConnection"/>. Uses Dapper for query/execute, since
    /// the connection is ADO.NET-compatible.
    /// </summary>
    private sealed class DuckHandle : IDuckHandle
    {
        private readonly DuckDBConnection _connection;

        public DuckHandle(DuckDBConnection connection) => _connection = connection;

        public void Execute(string sql, object? param = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            _connection.Execute(sql, param);
        }

        public List<dynamic> Query(string sql, object? param = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            return _connection.Query(sql, param).ToList();
        }

        public void Dispose() => _connection.Dispose();
    }
}
