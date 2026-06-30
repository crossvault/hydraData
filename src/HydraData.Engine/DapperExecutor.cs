// Copyright (c) 2026 crossVault GmbH.

using System.Data;
using Dapper;

namespace HydraData.Engine;

/// <summary>
/// Shared Dapper-based <see cref="IDbExecutor"/>. <c>Query</c>/<c>Scalar</c>/<c>Execute</c> are
/// provider-agnostic over ADO.NET; <see cref="BulkInsert"/> is provider-specific (subclasses).
/// </summary>
/// <remarks>
/// Every Dapper call passes <see cref="CommandTimeoutSeconds"/> as Dapper's <c>commandTimeout</c>, so a
/// long-running server-side query is bounded by the per-step timeout. The
/// value is derived from <c>PumpOptions.StepTimeout</c> by the step plumbing; <see langword="null"/>
/// means "no override" (the ADO.NET provider's default command timeout applies).
/// </remarks>
internal abstract class DapperExecutor : IDbExecutor
{
    /// <summary>Initializes the executor over an open connection and its transaction.</summary>
    /// <param name="connection">The open ADO.NET connection.</param>
    /// <param name="transaction">The ambient transaction.</param>
    /// <param name="commandTimeoutSeconds">
    /// Optional Dapper <c>commandTimeout</c> (seconds) applied to every query. <see langword="null"/>
    /// leaves the provider default in place.
    /// </param>
    protected DapperExecutor(
        IDbConnection connection, IDbTransaction transaction, int? commandTimeoutSeconds = null)
    {
        Connection = connection;
        Transaction = transaction;
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>The open ADO.NET connection.</summary>
    protected IDbConnection Connection { get; }

    /// <summary>The ambient transaction.</summary>
    protected IDbTransaction Transaction { get; }

    /// <summary>
    /// The Dapper <c>commandTimeout</c> (seconds) passed to every query, or <see langword="null"/> to
    /// leave the provider default. Bounds a long server-side query within the per-step timeout.
    /// </summary>
    protected int? CommandTimeoutSeconds { get; }

    /// <inheritdoc />
    public List<dynamic> Query(string sql, object? param)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return Connection.Query(sql, param, Transaction, commandTimeout: CommandTimeoutSeconds).ToList();
    }

    /// <inheritdoc />
    public T Scalar<T>(string sql, object? param)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return Connection.ExecuteScalar<T>(sql, param, Transaction, commandTimeout: CommandTimeoutSeconds)!;
    }

    /// <inheritdoc />
    public int Execute(string sql, object? param)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return Connection.Execute(sql, param, Transaction, commandTimeout: CommandTimeoutSeconds);
    }

    /// <inheritdoc />
    public abstract void BulkInsert(string table, IEnumerable<IDictionary<string, object?>> rows);
}
