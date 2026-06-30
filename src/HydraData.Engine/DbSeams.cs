// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Opens a database slot (one transaction) for a resolved connection. Slots are reused per
/// <see cref="ConnectionInfo.Id"/> within a step.
/// </summary>
internal interface IConnectionGateway
{
    /// <summary>Opens a slot for the given connection. The slot lazily opens the ADO.NET connection.</summary>
    /// <param name="info">The resolved connection to open against.</param>
    /// <param name="commandTimeoutSeconds">
    /// Optional command timeout (seconds) threaded down to every Dapper query and bulk insert on the
    /// slot, derived from <c>PumpOptions.StepTimeout</c>. <see langword="null"/> leaves the ADO.NET
    /// provider default command timeout in place.
    /// </param>
    /// <returns>A live slot wrapping a connection and transaction.</returns>
    IDbSlot Open(ConnectionInfo info, int? commandTimeoutSeconds = null);
}

/// <summary>
/// One open database connection plus its ambient transaction. <see cref="Commit"/>/<see cref="Rollback"/>
/// finalise the transaction; <see cref="IDisposable.Dispose"/> closes the connection.
/// </summary>
internal interface IDbSlot : IDisposable
{
    /// <summary>The executor scoped to this slot's connection and transaction.</summary>
    IDbExecutor Executor { get; }

    /// <summary>Commits the slot's transaction.</summary>
    void Commit();

    /// <summary>Rolls back the slot's transaction.</summary>
    void Rollback();
}

/// <summary>
/// Encapsulated database access for a single slot. Signatures are fixed by runtime contract
/// </summary>
internal interface IDbExecutor
{
    /// <summary>Runs a query and materialises all rows as dynamic records.</summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="param">Optional parameter object (Dapper conventions).</param>
    /// <returns>The materialised rows.</returns>
    List<dynamic> Query(string sql, object? param);

    /// <summary>Runs a query returning a single scalar value coerced to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The scalar result type.</typeparam>
    /// <param name="sql">The SQL text.</param>
    /// <param name="param">Optional parameter object.</param>
    /// <returns>
    /// The scalar value, or a type-specific null on DB NULL:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///     A reference-type <typeparamref name="T"/> (e.g. <see cref="string"/>) returns
    ///     <see langword="null"/> when the result set is empty or the scalar value is SQL NULL.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     A value-type <typeparamref name="T"/> (e.g. <see cref="int"/>) returns
    ///     <c>default(<typeparamref name="T"/>)</c> (e.g. <c>0</c>) — not a boxed null —
    ///     because the Dapper <c>ExecuteScalar&lt;T&gt;</c> call uses the <c>!</c>
    ///     null-forgiveness operator internally. Use <c>Scalar&lt;int?&gt;</c> if you need
    ///     to distinguish SQL NULL from the value zero.
    ///     </description>
    ///   </item>
    /// </list>
    /// This null contract is intentional and stable; the signature is not nullable
    /// (<c>T?</c>) to avoid a breaking change on the public call sites.
    /// </returns>
    T Scalar<T>(string sql, object? param);

    /// <summary>Executes a non-query command and returns the affected row count.</summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="param">Optional parameter object.</param>
    /// <returns>The number of affected rows.</returns>
    int Execute(string sql, object? param);

    /// <summary>Bulk-inserts rows into a table using the provider's native bulk API.</summary>
    /// <param name="table">
    /// Target table name. It is interpolated into the bulk command as a <em>trusted</em> identifier
    ///; it is not parameterised. The PGSQL executor
    /// quotes it (supporting <c>schema.table</c>); the MSSQL <see cref="System.Data.IDataReader"/>
    /// path lets the driver handle the destination table name.
    /// </param>
    /// <param name="rows">
    /// Rows as column-name/value maps; <see langword="null"/> values insert SQL NULL. The column set
    /// is taken from the <em>first</em> row — every subsequent row must expose exactly the same keys.
    /// A row whose key set differs from the first row's fails fast with
    /// <see cref="InvalidOperationException"/> (rather than silently dropping columns).
    /// </param>
    /// <exception cref="InvalidOperationException">A later row's key set differs from the first row's.</exception>
    void BulkInsert(string table, IEnumerable<IDictionary<string, object?>> rows);
}
