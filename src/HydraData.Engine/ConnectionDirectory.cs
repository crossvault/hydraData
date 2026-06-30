// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Default <see cref="IConnectionDirectory"/> implementation backed by a <see cref="ConnectionRegistry"/>.
/// All lookups go through the registry, which raises a hard error on ambiguous (duplicate) ids.
/// </summary>
public sealed class ConnectionDirectory : IConnectionDirectory
{
    private readonly ConnectionRegistry _registry;
    private readonly IReadOnlyList<ConnectionInfo> _all;

    /// <summary>Creates a directory over the given registry.</summary>
    /// <param name="registry">The parsed connection registry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The registry contains duplicate ids.</exception>
    public ConnectionDirectory(ConnectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _all = registry.ResolveAll();
    }

    /// <inheritdoc />
    public IConnection Default =>
        _all.Count > 0
            ? _all[0]
            : throw new InvalidOperationException("Keine Connection konfiguriert; Default nicht verfügbar.");

    /// <inheritdoc />
    public IReadOnlyList<IConnection> Extern => _all.Skip(1).ToList();

    /// <inheritdoc />
    public IConnection GetConnection(string name, DbType dbType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = ConnectionInfo.MakeId(ConnectionInfo.TargetSystem(dbType), name);
        return _registry.TryResolve(id)
               ?? throw NotFound(name, dbType, id);
    }

    /// <inheritdoc />
    public IConnection GetConnection(string name, DbType sourceDbType, DbType targetDbType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The source must exist to identify the logical connection.
        var sourceId = ConnectionInfo.MakeId(ConnectionInfo.TargetSystem(sourceDbType), name);
        if (_registry.TryResolve(sourceId) is null)
            throw new InvalidOperationException(
                $"Cross-System-Auflösung fehlgeschlagen: Quell-Connection '{name}' " +
                $"(DbType {sourceDbType}, Id '{sourceId}') existiert nicht.");

        var targetId = ConnectionInfo.MakeId(ConnectionInfo.TargetSystem(targetDbType), name);
        return _registry.TryResolve(targetId)
               ?? throw new InvalidOperationException(
                   $"Cross-System-Auflösung fehlgeschlagen: Ziel-Connection '{name}' " +
                   $"(DbType {targetDbType}, Id '{targetId}') existiert nicht.");
    }

    /// <inheritdoc />
    public IConnection GetConnection(string name, string dbType) =>
        GetConnection(name, ConnectionInfo.ParseProvider(dbType));

    /// <inheritdoc />
    public IConnection? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _registry.TryResolve(id);
    }

    /// <inheritdoc />
    public IReadOnlyList<IConnection> Where(DbType? dbType = null, string? id = null)
    {
        IEnumerable<ConnectionInfo> query = _all;

        if (dbType is { } t)
            query = query.Where(c => c.DbType == t);

        if (!string.IsNullOrWhiteSpace(id))
        {
            var key = id.ToLowerInvariant();
            query = query.Where(c => string.Equals(c.Id, key, StringComparison.Ordinal));
        }

        return query.ToList();
    }

    private static InvalidOperationException NotFound(string name, DbType dbType, string id) =>
        new($"Keine Connection mit Name '{name}' und DbType {dbType} (Id '{id}') gefunden.");
}
