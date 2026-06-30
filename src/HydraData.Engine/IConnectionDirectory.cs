// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Thin facade over <see cref="ConnectionRegistry"/> for resolving connections from scripts and the
/// host. Every lookup except <see cref="GetById"/> throws a diagnostic
/// exception when no entry matches; <see cref="GetById"/> returns <see langword="null"/>.
/// </summary>
public interface IConnectionDirectory
{
    /// <summary>The default connection (first declared in <c>connections.xml</c>).</summary>
    /// <exception cref="InvalidOperationException">No connections are configured.</exception>
    IConnection Default { get; }

    /// <summary>All connections other than <see cref="Default"/>.</summary>
    IReadOnlyList<IConnection> Extern { get; }

    /// <summary>Resolves the connection with the given <paramref name="name"/> and <paramref name="dbType"/>.</summary>
    /// <param name="name">Logical connection name.</param>
    /// <param name="dbType">Physical database type.</param>
    /// <returns>The matching connection.</returns>
    /// <exception cref="InvalidOperationException">No matching connection exists.</exception>
    IConnection GetConnection(string name, DbType dbType);

    /// <summary>
    /// Cross-system resolve: identifies the logical connection by <paramref name="name"/> and
    /// <paramref name="sourceDbType"/>, then returns the entry with the same name whose physical
    /// type is <paramref name="targetDbType"/>.
    /// </summary>
    /// <param name="name">Logical connection name.</param>
    /// <param name="sourceDbType">Source physical database type (must exist).</param>
    /// <param name="targetDbType">Target physical database type to resolve to.</param>
    /// <returns>The target-system connection.</returns>
    /// <exception cref="InvalidOperationException">The source or the target counterpart is missing.</exception>
    IConnection GetConnection(string name, DbType sourceDbType, DbType targetDbType);

    /// <summary>Resolves a connection by name and a provider string (<c>"mssql"</c>/<c>"pgsql"</c>, case-insensitive).</summary>
    /// <param name="name">Logical connection name.</param>
    /// <param name="dbType">Provider string, <c>"mssql"</c> or <c>"pgsql"</c> (case-insensitive).</param>
    /// <returns>The matching connection.</returns>
    /// <exception cref="ArgumentException"><paramref name="dbType"/> is not a known provider string.</exception>
    /// <exception cref="InvalidOperationException">No matching connection exists.</exception>
    IConnection GetConnection(string name, string dbType);

    /// <summary>Returns the connection with the given canonical id, or <see langword="null"/> on a miss.</summary>
    /// <param name="id">Canonical id (<c>targetSystem|name</c>, case-insensitive).</param>
    /// <returns>The matching connection, or <see langword="null"/>.</returns>
    IConnection? GetById(string id);

    /// <summary>Filters connections by optional <paramref name="dbType"/> and/or optional <paramref name="id"/>.</summary>
    /// <param name="dbType">When set, restricts to this physical type.</param>
    /// <param name="id">When set, restricts to this canonical id (case-insensitive).</param>
    /// <returns>The matching connections (possibly empty).</returns>
    IReadOnlyList<IConnection> Where(DbType? dbType = null, string? id = null);
}
