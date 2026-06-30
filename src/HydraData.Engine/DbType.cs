// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Physical database type of a connection. Mirrors the <c>targetSystem</c> XML attribute
/// (<c>MSSQL</c>/<c>PGSQL</c>); in scripts it is the alias <c>currentConnection.DbType</c>.
/// There is no business category (no DQR/PUMP); see runtime contract
/// </summary>
public enum DbType
{
    /// <summary>Microsoft SQL Server (<c>targetSystem="MSSQL"</c>).</summary>
    Mssql,

    /// <summary>PostgreSQL (<c>targetSystem="PGSQL"</c>).</summary>
    Pgsql,
}
