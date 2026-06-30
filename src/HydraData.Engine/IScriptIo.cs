// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Internal seam for the file/CSV/Excel/DuckDB surface of <see cref="PumpContext"/>. These operations
/// depend on the workspace sandbox and the encapsulated tool wrappers (Sep, MiniExcel, DuckDB) that
/// are built in cluster 05. PumpContext delegates the corresponding
/// script methods here so the contract is fixed now and the implementation can land later without
/// changing the script-facing API.
/// </summary>
/// <remarks>
/// Signatures stay deliberately close to the script API table. Selectors
/// and writer callbacks keep ref-struct (Sep) details out of the script surface. The default
/// implementation (<see cref="NotImplementedScriptIo"/>) throws so DB-only steps run before cluster 05.
/// </remarks>
internal interface IScriptIo
{
    /// <summary>Reads an Excel sheet into materialised rows.</summary>
    List<dynamic> ReadExcel(string path, string? sheet = null);

    /// <summary>Streams an Excel sheet row by row without materialising the whole sheet.</summary>
    IEnumerable<dynamic> StreamExcel(string path, string? sheet = null);

    /// <summary>Reads a CSV file into materialised rows.</summary>
    List<dynamic> ReadCsv(string path);

    /// <summary>Reads a CSV file fast (Sep), projecting each row through <paramref name="selector"/>.</summary>
    /// <remarks>
    /// The dictionary passed to <paramref name="selector"/> is a single mutable instance reused and
    /// cleared per row. It is valid only during the selector's synchronous call; it must not be retained
    /// or captured across rows. Copy any value you need to keep into your
    /// projected result inside the selector.
    /// </remarks>
    List<T> ReadCsvFast<T>(string path, Func<IReadOnlyDictionary<string, string>, T> selector);

    /// <summary>Writes rows to an Excel file, optionally using a template.</summary>
    void WriteExcel(string path, IEnumerable<object> rows, string? template = null);

    /// <summary>Writes rows to a CSV file.</summary>
    void WriteCsv(string path, IEnumerable<object> rows);

    /// <summary>Writes rows to a CSV file fast (Sep) via a per-row writer callback.</summary>
    void WriteCsvFast<T>(string path, IEnumerable<T> rows, Action<ICsvRowWriter, T> write);

    /// <summary>Runs a DuckDB query over files/DB extracts and materialises the result.</summary>
    List<dynamic> Analyze(string sql, object? param = null);

    /// <summary>Opens an in-memory DuckDB handle.</summary>
    IDuckHandle Duck();

    /// <summary>Opens a named, run-local DuckDB handle persisted under the run's <c>duck/</c> directory.</summary>
    IDuckHandle Duck(string name);

    /// <summary>Opens raw, unsafe access (direct drivers / free DuckDB data source). Requires <c>@unsafe</c>.</summary>
    IRawAccess Raw();
}

/// <summary>
/// Opaque DuckDB handle returned by <c>Duck()</c>; shape is defined by cluster 05. Public because
/// scripts hold and use the handle directly (it crosses the script-facing <see cref="PumpContext"/>
/// surface); the <see cref="IScriptIo"/> factory that produces it stays internal.
/// </summary>
public interface IDuckHandle : IDisposable
{
    /// <summary>Executes a non-query DuckDB statement.</summary>
    /// <param name="sql">DuckDB SQL text.</param>
    /// <param name="param">Optional parameter object.</param>
    void Execute(string sql, object? param = null);

    /// <summary>Runs a DuckDB query and materialises the result.</summary>
    /// <param name="sql">DuckDB SQL text.</param>
    /// <param name="param">Optional parameter object.</param>
    /// <returns>The materialised rows.</returns>
    List<dynamic> Query(string sql, object? param = null);
}

/// <summary>
/// Per-row writer surface for <c>WriteCsvFast</c>; keeps Sep ref-structs out of scripts. Public
/// because the script-supplied writer callback receives it.
/// </summary>
public interface ICsvRowWriter
{
    /// <summary>Sets the string value of the named column.</summary>
    /// <param name="column">Column name.</param>
    /// <param name="value">String value (may be <see langword="null"/>).</param>
    void Set(string column, string? value);

    /// <summary>Formats a value into the named column using invariant culture.</summary>
    /// <param name="column">Column name.</param>
    /// <param name="value">Value to format (may be <see langword="null"/>).</param>
    void Format(string column, object? value);
}

/// <summary>
/// Opaque raw-access handle returned by <c>Raw()</c>; shape is defined by cluster 05. Public because
/// scripts hold it.
/// </summary>
/// <remarks>
/// This is the deferred raw-access seam that cluster 05 (M3) will give real members. It is intentionally
/// empty here so that <see cref="IScriptIo.Raw"/> has a return type now; the implementation and concrete
/// members land when the cluster-05 workspace wiring is complete.
/// </remarks>
public interface IRawAccess
{
}

/// <summary>
/// Default <see cref="IScriptIo"/> that throws for every member. Used until cluster 05 provides a
/// workspace-backed implementation, so DB-only steps run while IO is not yet wired.
/// </summary>
internal sealed class NotImplementedScriptIo : IScriptIo
{
    /// <summary>A shared, stateless instance.</summary>
    public static NotImplementedScriptIo Instance { get; } = new();

    private static InvalidOperationException NotWired() =>
        new("File/CSV/Excel/DuckDB script IO is not available in this run (cluster 05 not wired).");

    public List<dynamic> ReadExcel(string path, string? sheet = null) => throw NotWired();

    public IEnumerable<dynamic> StreamExcel(string path, string? sheet = null) => throw NotWired();

    public List<dynamic> ReadCsv(string path) => throw NotWired();

    public List<T> ReadCsvFast<T>(string path, Func<IReadOnlyDictionary<string, string>, T> selector) =>
        throw NotWired();

    public void WriteExcel(string path, IEnumerable<object> rows, string? template = null) => throw NotWired();

    public void WriteCsv(string path, IEnumerable<object> rows) => throw NotWired();

    public void WriteCsvFast<T>(string path, IEnumerable<T> rows, Action<ICsvRowWriter, T> write) =>
        throw NotWired();

    public List<dynamic> Analyze(string sql, object? param = null) => throw NotWired();

    public IDuckHandle Duck() => throw NotWired();

    public IDuckHandle Duck(string name) => throw NotWired();

    public IRawAccess Raw() => throw NotWired();
}
