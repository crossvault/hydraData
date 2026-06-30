// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine.Tests.Fakes;

/// <summary>
/// An <see cref="IConnectionDirectory"/> with no connections: every resolve throws and
/// <see cref="Default"/> throws, modelling a misconfigured/missing connection for the preflight
/// (exit code 1) test.
/// </summary>
internal sealed class EmptyConnectionDirectory : IConnectionDirectory
{
    public IConnection Default =>
        throw new InvalidOperationException("Keine Connection konfiguriert; Default nicht verfügbar.");

    public IReadOnlyList<IConnection> Extern => [];

    public IConnection GetConnection(string name, DbType dbType) =>
        throw new InvalidOperationException("No connections configured.");

    public IConnection GetConnection(string name, DbType sourceDbType, DbType targetDbType) =>
        throw new InvalidOperationException("No connections configured.");

    public IConnection GetConnection(string name, string dbType) =>
        throw new InvalidOperationException("No connections configured.");

    public IConnection? GetById(string id) => null;

    public IReadOnlyList<IConnection> Where(DbType? dbType = null, string? id = null) => [];
}
