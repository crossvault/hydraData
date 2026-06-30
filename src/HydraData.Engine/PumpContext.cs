// Copyright (c) 2026 crossVault GmbH.

using Microsoft.Extensions.Logging;

namespace HydraData.Engine;

/// <summary>
/// The script-facing surface (the <see cref="ScriptHost"/> globals type). Every script executes with
/// a fresh <see cref="PumpContext"/> as <c>this</c>, so its public methods read like a built-in API
///. Methods use PascalCase, in contrast to the lowerCamelCase
/// <see cref="Fn"/> helpers.
/// </summary>
/// <remarks>
/// The context owns the per-step database slots: the first access per <see cref="ConnectionInfo.Id"/>
/// lazily opens one slot and subsequent accesses reuse it. The step
/// runner calls <see cref="CommitAll"/> or <see cref="RollbackAll"/> at step end to finalise and
/// dispose all open slots.
/// </remarks>
public sealed class PumpContext
{
    private readonly IConnectionGateway _gateway;
    private readonly ConnectionInfo? _defaultConnection;
    private readonly IConnectionDirectory? _connections;
    private readonly IScriptIo _io;
    private readonly ILogger _logger;
    private readonly bool _unsafeAllowed;
    private readonly int? _commandTimeoutSeconds;

    // One open slot per ConnectionInfo.Id (case-insensitive), reused within the step (T03.4a).
    private readonly Dictionary<string, IDbSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Note> _notes = [];

    /// <summary>Initializes a new <see cref="PumpContext"/> for a single step.</summary>
    /// <param name="state">Group-local state bag.</param>
    /// <param name="shared">Run-global state bag.</param>
    /// <param name="ctx">Read-only host context.</param>
    /// <param name="gateway">Database gateway used to open slots.</param>
    /// <param name="defaultConnection">
    /// The connection the DB methods target for this step. May be <see langword="null"/> when the step
    /// performs no database access; a DB call without a connection then fails fast.
    /// </param>
    /// <param name="unsafeAllowed">
    /// Whether the step's <c>@unsafe</c> meta is set. When <see langword="false"/>, <see cref="Raw"/>
    /// throws immediately (runtime guard; the validate-side PUMP010 check is separate).
    /// </param>
    /// <param name="io">
    /// File/CSV/Excel/DuckDB seam (cluster 05). Defaults to a not-wired stub that throws, so DB-only
    /// steps run before cluster 05 lands.
    /// </param>
    /// <param name="logger">Logger for <see cref="Log"/>. Defaults to a null logger.</param>
    /// <param name="connections">
    /// The connection directory backing <see cref="CurrentConnection"/> and the <c>GetConnection</c>
    /// overloads. May be <see langword="null"/> for
    /// tests/legacy callers that only target the implicit default connection; the directory-backed
    /// lookups then throw a clear message while the no-arg DB methods keep working.
    /// </param>
    /// <param name="commandTimeoutSeconds">
    /// Optional DB command timeout (seconds) threaded into every slot this context opens, derived from
    /// <c>PumpOptions.StepTimeout</c>. <see langword="null"/> leaves the ADO.NET provider default in
    /// place. A long server-side query is then bounded by this timeout.
    /// </param>
    internal PumpContext(
        PumpState state,
        PumpState shared,
        ExternContext ctx,
        IConnectionGateway gateway,
        ConnectionInfo? defaultConnection,
        bool unsafeAllowed,
        IScriptIo? io = null,
        ILogger? logger = null,
        IConnectionDirectory? connections = null,
        int? commandTimeoutSeconds = null)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Shared = shared ?? throw new ArgumentNullException(nameof(shared));
        Ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _defaultConnection = defaultConnection;
        _connections = connections;
        _unsafeAllowed = unsafeAllowed;
        _io = io ?? NotImplementedScriptIo.Instance;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    /// <summary>Group-local state bag (shared across steps of the same group).</summary>
    public PumpState State { get; }

    /// <summary>Run-global state bag (shared across the whole run).</summary>
    public PumpState Shared { get; }

    /// <summary>Read-only host context for this run.</summary>
    public ExternContext Ctx { get; }

    /// <summary>
    /// The step's cancellation token (caller cancellation linked with the per-step timeout). Long
    /// loops should observe it (e.g. <see cref="CancellationToken.ThrowIfCancellationRequested"/>),
    /// since Roslyn scripting does not auto-propagate it into <c>await</c> calls. Set by the
    /// <see cref="StepRunner"/> before execution; defaults to <see cref="CancellationToken.None"/>.
    /// </summary>
    public CancellationToken Cancellation { get; internal set; }

    // ── Verdicts ────────────────────────────────────────────────────────────────

    /// <summary>Returns a success verdict.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <param name="details">Optional structured details.</param>
    /// <returns>A success <see cref="StepResult"/>.</returns>
    public StepResult Ok(string message = "OK", object? details = null) => StepResult.Ok(message, details);

    /// <summary>Returns a warning verdict (the transaction still commits).</summary>
    /// <param name="message">Human-readable message.</param>
    /// <param name="details">Optional structured details.</param>
    /// <returns>A warning <see cref="StepResult"/>.</returns>
    public StepResult Warn(string message, object? details = null) => StepResult.Warn(message, details);

    /// <summary>Returns a failure verdict (the transaction is rolled back).</summary>
    /// <param name="message">Human-readable message.</param>
    /// <param name="details">Optional structured details.</param>
    /// <returns>A failure <see cref="StepResult"/>.</returns>
    public StepResult Fail(string message, object? details = null) => StepResult.Fail(message, details);

    /// <summary>
    /// Assertion: when <paramref name="condition"/> is <see langword="false"/>, throws a
    /// <see cref="StepVerdict"/> carrying <see cref="StepResult.Fail(string, object?)"/>; when
    /// <see langword="true"/>, does nothing.
    /// </summary>
    /// <param name="condition">The condition that must hold.</param>
    /// <param name="message">Failure message used when the condition is false.</param>
    /// <param name="details">Optional structured details for the failure.</param>
    /// <exception cref="StepVerdict">Thrown when <paramref name="condition"/> is <see langword="false"/>.</exception>
    public void Expect(bool condition, string message, object? details = null)
    {
        if (!condition)
            throw new StepVerdict(StepResult.Fail(message, details));
    }

    // ── Notes ─────────────────────────────────────────────────────────────────

    /// <summary>Records a note with <see cref="Severity.Success"/> (no effect on the transaction).</summary>
    /// <param name="message">The note message.</param>
    public void Note(string message) => Note(message, Severity.Success);

    /// <summary>
    /// Records a note with the given severity. An <see cref="Severity.Error"/> note raises the step's
    /// effective severity and forces a rollback even when the step returns <c>Ok</c>/<c>Warn</c>
    ///.
    /// </summary>
    /// <param name="message">The note message.</param>
    /// <param name="severity">The note severity.</param>
    public void Note(string message, Severity severity) => _notes.Add(new Note(message, severity));

    /// <summary>The notes recorded during this step, in order.</summary>
    public IReadOnlyList<Note> Notes => _notes;

    // ── Database ──────────────────────────────────────────────────────────────

    /// <summary>Runs a query and materialises all rows.</summary>
    /// <param name="sql">SQL text.</param>
    /// <param name="param">Optional parameter object (Dapper conventions).</param>
    /// <returns>The materialised rows.</returns>
    public List<dynamic> Query(string sql, object? param = null) => Slot().Executor.Query(sql, param);

    /// <summary>Runs a query returning a single scalar value.</summary>
    /// <typeparam name="T">Scalar result type.</typeparam>
    /// <param name="sql">SQL text.</param>
    /// <param name="param">Optional parameter object.</param>
    /// <returns>
    /// The scalar value. On DB NULL: a reference-type <typeparamref name="T"/> returns
    /// <see langword="null"/>; a value-type <typeparamref name="T"/> returns
    /// <c>default(<typeparamref name="T"/>)</c> (e.g. <c>0</c> for <see cref="int"/>).
    /// See <see cref="IDbExecutor.Scalar{T}"/> for the full null contract.
    /// </returns>
    public T Scalar<T>(string sql, object? param = null) => Slot().Executor.Scalar<T>(sql, param);

    /// <summary>Executes a non-query command.</summary>
    /// <param name="sql">SQL text.</param>
    /// <param name="param">Optional parameter object.</param>
    /// <returns>The number of affected rows.</returns>
    public int Execute(string sql, object? param = null) => Slot().Executor.Execute(sql, param);

    /// <summary>Bulk-inserts rows into a table using the provider's native bulk API.</summary>
    /// <param name="table">Target table name (trusted identifier).</param>
    /// <param name="rows">Rows as column/value maps; the first row defines the column set.</param>
    public void BulkInsert(string table, IEnumerable<IDictionary<string, object?>> rows) =>
        Slot().Executor.BulkInsert(table, rows);

    // ── Connection switching ─────────────────────────

    /// <summary>
    /// The run's default connection — the implicit target of the no-argument DB methods. Scripts use it
    /// to drive the cross-system idiom, e.g.
    /// <c>GetConnection(CurrentConnection.Name, CurrentConnection.DbType, "pgsql")</c>
    ///.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The step has no default connection (a no-connection run). DB-free steps still run; only touching
    /// <see cref="CurrentConnection"/> on such a step throws.
    /// </exception>
    public IConnection CurrentConnection =>
        _defaultConnection
        ?? throw new InvalidOperationException(
            "No current connection is available for this step (the run has no default connection).");

    /// <summary>Resolves the connection with the given <paramref name="name"/> and <paramref name="dbType"/>.</summary>
    /// <param name="name">Logical connection name.</param>
    /// <param name="dbType">Physical database type.</param>
    /// <returns>The matching connection.</returns>
    /// <exception cref="InvalidOperationException">No connection directory is wired, or no matching connection exists.</exception>
    public IConnection GetConnection(string name, DbType dbType) => Directory.GetConnection(name, dbType);

    /// <summary>
    /// Cross-system resolve: identifies the logical connection by <paramref name="name"/> and
    /// <paramref name="source"/>, then returns the entry with the same name whose physical type is
    /// <paramref name="target"/>.
    /// </summary>
    /// <param name="name">Logical connection name.</param>
    /// <param name="source">Source physical database type (must exist).</param>
    /// <param name="target">Target physical database type to resolve to.</param>
    /// <returns>The target-system connection.</returns>
    /// <exception cref="InvalidOperationException">No connection directory is wired, or the source or target counterpart is missing.</exception>
    public IConnection GetConnection(string name, DbType source, DbType target) =>
        Directory.GetConnection(name, source, target);

    /// <summary>Resolves a connection by name and a provider string (<c>"mssql"</c>/<c>"pgsql"</c>, case-insensitive).</summary>
    /// <param name="name">Logical connection name.</param>
    /// <param name="dbType">Provider string, <c>"mssql"</c> or <c>"pgsql"</c> (case-insensitive).</param>
    /// <returns>The matching connection.</returns>
    /// <exception cref="InvalidOperationException">No connection directory is wired, or no matching connection exists.</exception>
    /// <exception cref="ArgumentException"><paramref name="dbType"/> is not a known provider string.</exception>
    public IConnection GetConnection(string name, string dbType) => Directory.GetConnection(name, dbType);

    /// <summary>
    /// Cross-system resolve with a provider string for the target: identifies the logical connection by
    /// <paramref name="name"/> and <paramref name="source"/>, then returns the same-name entry whose
    /// provider is <paramref name="target"/> (<c>"mssql"</c>/<c>"pgsql"</c>). This is the script idiom
    /// <c>GetConnection(CurrentConnection.Name, CurrentConnection.DbType, "pgsql")</c>.
    /// </summary>
    /// <param name="name">Logical connection name.</param>
    /// <param name="source">Source physical database type (must exist).</param>
    /// <param name="target">Target provider string, <c>"mssql"</c> or <c>"pgsql"</c> (case-insensitive).</param>
    /// <returns>The target-system connection.</returns>
    /// <exception cref="InvalidOperationException">No connection directory is wired, or the source or target counterpart is missing.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> is not a known provider string.</exception>
    public IConnection GetConnection(string name, DbType source, string target) =>
        Directory.GetConnection(name, source, ConnectionInfo.ParseProvider(target));

    /// <summary>Returns the connection with the given canonical id, or <see langword="null"/> on a miss.</summary>
    /// <param name="id">Canonical id (<c>targetSystem|name</c>, case-insensitive).</param>
    /// <returns>The matching connection, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">No connection directory is wired.</exception>
    public IConnection? GetById(string id) => Directory.GetById(id);

    /// <summary>Filters connections by optional <paramref name="dbType"/> and/or optional <paramref name="id"/>.</summary>
    /// <param name="dbType">When set, restricts to this physical type.</param>
    /// <param name="id">When set, restricts to this canonical id (case-insensitive).</param>
    /// <returns>The matching connections (possibly empty).</returns>
    /// <exception cref="InvalidOperationException">No connection directory is wired.</exception>
    public IReadOnlyList<IConnection> Where(DbType? dbType = null, string? id = null) =>
        Directory.Where(dbType, id);

    /// <summary>Runs a query against <paramref name="conn"/> and materialises all rows.</summary>
    /// <param name="conn">The connection to target (opens/reuses a slot per <see cref="ConnectionInfo.Id"/>).</param>
    /// <param name="sql">SQL text.</param>
    /// <param name="param">Optional parameter object (Dapper conventions).</param>
    /// <returns>The materialised rows.</returns>
    public List<dynamic> Query(IConnection conn, string sql, object? param = null) =>
        Slot(conn).Executor.Query(sql, param);

    /// <summary>Runs a query against <paramref name="conn"/> returning a single scalar value.</summary>
    /// <typeparam name="T">Scalar result type.</typeparam>
    /// <param name="conn">The connection to target (opens/reuses a slot per <see cref="ConnectionInfo.Id"/>).</param>
    /// <param name="sql">SQL text.</param>
    /// <param name="param">Optional parameter object.</param>
    /// <returns>
    /// The scalar value. On DB NULL: a reference-type <typeparamref name="T"/> returns
    /// <see langword="null"/>; a value-type <typeparamref name="T"/> returns
    /// <c>default(<typeparamref name="T"/>)</c>. See <see cref="IDbExecutor.Scalar{T}"/> for the full null contract.
    /// </returns>
    public T Scalar<T>(IConnection conn, string sql, object? param = null) =>
        Slot(conn).Executor.Scalar<T>(sql, param);

    /// <summary>Executes a non-query command against <paramref name="conn"/>.</summary>
    /// <param name="conn">The connection to target (opens/reuses a slot per <see cref="ConnectionInfo.Id"/>).</param>
    /// <param name="sql">SQL text.</param>
    /// <param name="param">Optional parameter object.</param>
    /// <returns>The number of affected rows.</returns>
    public int Execute(IConnection conn, string sql, object? param = null) =>
        Slot(conn).Executor.Execute(sql, param);

    /// <summary>Bulk-inserts rows into a table on <paramref name="conn"/> using the provider's native bulk API.</summary>
    /// <param name="conn">The connection to target (opens/reuses a slot per <see cref="ConnectionInfo.Id"/>).</param>
    /// <param name="table">Target table name (trusted identifier).</param>
    /// <param name="rows">Rows as column/value maps; the first row defines the column set.</param>
    public void BulkInsert(IConnection conn, string table, IEnumerable<IDictionary<string, object?>> rows) =>
        Slot(conn).Executor.BulkInsert(table, rows);

    // ── Output ──────────────────────────────────────────────────────────────────

    /// <summary>Writes a line to the captured standard output.</summary>
    /// <param name="message">The text to print.</param>
    public void Print(string message) => Console.WriteLine(message);

    /// <summary>Logs an informational message via the injected logger.</summary>
    /// <param name="message">The message to log.</param>
#pragma warning disable CA2254 // Script-supplied message is the log template by design.
    public void Log(string message) => _logger.LogInformation(message);
#pragma warning restore CA2254

    /// <summary>
    /// Renders rows as a pure ASCII table to the captured output.
    /// Column widths are the widest cell including the header; a <see langword="null"/> cell renders as an
    /// empty cell; borders use <c>+</c>, <c>-</c> and <c>|</c>. No Spectre renderer is used — the embedded
    /// engine does not reference Spectre, so interactive Spectre output is impossible by construction.
    /// </summary>
    /// <param name="rows">The rows to render.</param>
    public void Table(IEnumerable<object> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var rendered = AsciiTable.Render(rows);
        if (rendered.Length > 0)
            // The renderer already terminates with a newline; Write (not WriteLine) avoids a trailing blank line.
            Console.Out.Write(rendered);
    }

    // ── File / CSV / Excel / DuckDB (delegated to the cluster-05 seam) ──────────

    /// <summary>Reads an Excel sheet into materialised rows.</summary>
    /// <param name="path">Workspace-relative file path.</param>
    /// <param name="sheet">Optional sheet name.</param>
    /// <returns>The materialised rows.</returns>
    public List<dynamic> ReadExcel(string path, string? sheet = null) => _io.ReadExcel(path, sheet);

    /// <summary>Streams an Excel sheet row by row.</summary>
    /// <param name="path">Workspace-relative file path.</param>
    /// <param name="sheet">Optional sheet name.</param>
    /// <returns>A lazily enumerated row sequence.</returns>
    public IEnumerable<dynamic> StreamExcel(string path, string? sheet = null) => _io.StreamExcel(path, sheet);

    /// <summary>Reads a CSV file into materialised rows.</summary>
    /// <param name="path">Workspace-relative file path.</param>
    /// <returns>The materialised rows.</returns>
    public List<dynamic> ReadCsv(string path) => _io.ReadCsv(path);

    /// <summary>Reads a CSV file fast (Sep), projecting each row through <paramref name="selector"/>.</summary>
    /// <typeparam name="T">Projected row type.</typeparam>
    /// <param name="path">Workspace-relative file path.</param>
    /// <param name="selector">Per-row projection (no ref-struct leaks into the script).</param>
    /// <returns>The projected rows.</returns>
    /// <remarks>
    /// The dictionary passed to <paramref name="selector"/> is a single mutable instance reused and
    /// cleared per row. It is valid only during the selector's synchronous call; it must not be retained
    /// or captured across rows. Copy any value you need to keep into your
    /// projected result inside the selector.
    /// </remarks>
    public List<T> ReadCsvFast<T>(string path, Func<IReadOnlyDictionary<string, string>, T> selector) =>
        _io.ReadCsvFast(path, selector);

    /// <summary>Writes rows to an Excel file, optionally using a template.</summary>
    /// <param name="path">Workspace-relative file path.</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="template">Optional template path.</param>
    public void WriteExcel(string path, IEnumerable<object> rows, string? template = null) =>
        _io.WriteExcel(path, rows, template);

    /// <summary>Writes rows to a CSV file.</summary>
    /// <param name="path">Workspace-relative file path.</param>
    /// <param name="rows">The rows to write.</param>
    public void WriteCsv(string path, IEnumerable<object> rows) => _io.WriteCsv(path, rows);

    /// <summary>Writes rows to a CSV file fast (Sep) via a per-row writer callback.</summary>
    /// <typeparam name="T">Source row type.</typeparam>
    /// <param name="path">Workspace-relative file path.</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="write">Per-row writer callback.</param>
    public void WriteCsvFast<T>(string path, IEnumerable<T> rows, Action<ICsvRowWriter, T> write) =>
        _io.WriteCsvFast(path, rows, write);

    /// <summary>Runs a DuckDB query over files/DB extracts and materialises the result.</summary>
    /// <param name="sql">DuckDB SQL text.</param>
    /// <param name="param">Optional parameter object.</param>
    /// <returns>The materialised rows.</returns>
    public List<dynamic> Analyze(string sql, object? param = null) => _io.Analyze(sql, param);

    /// <summary>Opens an in-memory DuckDB handle.</summary>
    /// <returns>A DuckDB handle.</returns>
    public IDuckHandle Duck() => _io.Duck();

    /// <summary>Opens a named, run-local DuckDB handle persisted under the run's <c>duck/</c> directory.</summary>
    /// <param name="name">A safe DuckDB database name (no path or <c>..</c>).</param>
    /// <returns>A DuckDB handle.</returns>
    public IDuckHandle Duck(string name) => _io.Duck(name);

    /// <summary>
    /// Opens raw, unsafe access (direct drivers / free DuckDB data source). Requires the step's
    /// <c>@unsafe</c> meta to be set; otherwise throws immediately (runtime guard, runtime contract
    /// section 20). The validate-side engine-freedom check (PUMP010) is separate.
    /// </summary>
    /// <returns>A raw-access handle.</returns>
    /// <exception cref="InvalidOperationException">The step did not declare <c>@unsafe</c>.</exception>
    public IRawAccess Raw()
    {
        if (!_unsafeAllowed)
            throw new InvalidOperationException(
                "Raw access requires '@unsafe: true' in the step meta. The step did not declare it.");
        return _io.Raw();
    }

    private IConnectionDirectory Directory =>
        _connections
        ?? throw new InvalidOperationException(
            "No connection directory is available for this step; connection switching is not wired.");

    // ── Slot management (T03.4a) ────────────────────────────────────────────────

    private IDbSlot Slot()
    {
        var info = _defaultConnection
            ?? throw new InvalidOperationException(
                "No connection is available for this step; a database method was called without a connection.");
        return OpenOrReuse(info);
    }

    private IDbSlot Slot(IConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);

        // IConnection is the script-facing OPAQUE handle (Name + DbType, deliberately no ConnectionString so
        // scripts can't read secrets). The gateway needs the concrete ConnectionInfo (which carries the string).
        // Resolve the handle back to its ConnectionInfo honestly, rather than down-casting:
        //   • A ConnectionInfo already IS the source of truth (it carries the string) — use it directly. This is
        //     the common case: GetConnection / CurrentConnection hand out directory entries, which are
        //     ConnectionInfo.
        //   • A foreign IConnection (some other implementation) is resolved by IDENTITY (Name + DbType) through
        //     the directory — the single source of truth backing GetConnection. A token whose (Name, DbType) is
        //     in the directory now resolves correctly; one that is not (or no directory wired) surfaces the
        //     directory's normal "unknown connection" / "not wired" error.
        var info = conn as ConnectionInfo ?? ResolveThroughDirectory(conn);
        return OpenOrReuse(info);
    }

    // Resolve a foreign IConnection handle to its concrete ConnectionInfo via the directory, by identity.
    private ConnectionInfo ResolveThroughDirectory(IConnection conn)
    {
        var resolved = Directory.GetConnection(conn.Name, conn.DbType);
        return resolved as ConnectionInfo
            ?? throw new InvalidOperationException(
                "The connection directory returned a connection that is not a ConnectionInfo; " +
                "the engine's gateway requires the concrete ConnectionInfo type.");
    }

    // First access per ConnectionInfo.Id lazily opens a slot; subsequent accesses reuse it (T03.4a).
    private IDbSlot OpenOrReuse(ConnectionInfo info)
    {
        if (_slots.TryGetValue(info.Id, out var existing))
            return existing;

        var slot = _gateway.Open(info, _commandTimeoutSeconds);
        _slots[info.Id] = slot;
        return slot;
    }

    /// <summary>
    /// Commits every open slot, then disposes and clears them. Called by the step runner when the
    /// step's effective outcome commits.
    /// </summary>
    internal void CommitAll() => FinishAll(static slot => slot.Commit());

    /// <summary>
    /// Rolls back every open slot, then disposes and clears them. Called by the step runner when the
    /// step's effective outcome rolls back.
    /// </summary>
    internal void RollbackAll() => FinishAll(static slot => slot.Rollback());

    private void FinishAll(Action<IDbSlot> finalize)
    {
        // Finalise then dispose every slot; ensure ALL are disposed even if one throws.
        // Both the finalize call and the Dispose call are individually guarded so a throwing Dispose
        // cannot strand the remaining slots in a multi-slot fan-out (e.g. cross-system scripts that
        // opened one slot per connection). _slots.Clear runs unconditionally in a final block.
        List<Exception>? errors = null;
        foreach (var slot in _slots.Values)
        {
            try
            {
                finalize(slot);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }

            // Dispose is guarded separately so a throwing Dispose cannot skip remaining slots.
            try
            {
                slot.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        _slots.Clear();

        if (errors is { Count: > 0 })
            throw new AggregateException("One or more connection slots failed to finalise.", errors);
    }
}

/// <summary>A note recorded by a step via <c>Note</c>, carrying a message and a severity.</summary>
/// <param name="Message">The note message.</param>
/// <param name="Severity">The note severity (default <see cref="Severity.Success"/>).</param>
public sealed record Note(string Message, Severity Severity = Severity.Success);
