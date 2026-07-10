// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Narrow, read-only view of a single resolved database connection. The identity
/// <see cref="Id"/> is <c>targetSystem|name</c> lowercased; it introduces no new identity,
/// only exposes the existing one as a property. Provider connection strings are deliberately
/// absent so safe-mode scripts cannot read database credentials through this API.
/// </summary>
public interface IConnection
{
    /// <summary>Stable identity, <c>targetSystem|name</c> lowercased (e.g. <c>"mssql|stage"</c>).</summary>
    string Id { get; }

    /// <summary>Logical connection name as written in <c>connections.xml</c> (e.g. <c>"stage"</c>).</summary>
    string Name { get; }

    /// <summary>Physical database type of this connection.</summary>
    DbType DbType { get; }
}
