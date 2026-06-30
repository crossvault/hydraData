// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Narrow, read-only view of a single resolved database connection. The identity
/// <see cref="Id"/> is <c>targetSystem|name</c> lowercased; it introduces no new identity,
/// only exposes the existing one as a property.
/// </summary>
public interface IConnection
{
    /// <summary>Stable identity, <c>targetSystem|name</c> lowercased (e.g. <c>"mssql|stage"</c>).</summary>
    string Id { get; }

    /// <summary>Logical connection name as written in <c>connections.xml</c> (e.g. <c>"stage"</c>).</summary>
    string Name { get; }

    /// <summary>Physical database type of this connection.</summary>
    DbType DbType { get; }

    /// <summary>Provider-specific ADO.NET connection string built from the XML parameters.</summary>
    string ConnectionString { get; }
}
