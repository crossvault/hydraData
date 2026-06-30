// Copyright (c) 2026 crossVault GmbH.

using System.Globalization;

namespace HydraData.Engine;

/// <summary>
/// A single resolved connection. Implemented as a sealed class (not a record) by design
/// section 6. Carries no business category and no role (removed in phase D, section 21);
/// a connection is characterised only by its physical <see cref="DbType"/> and <see cref="Name"/>.
/// </summary>
public sealed class ConnectionInfo : IConnection
{
    /// <summary>Initializes a new <see cref="ConnectionInfo"/>.</summary>
    /// <param name="name">Logical connection name from <c>connections.xml</c>.</param>
    /// <param name="dbType">Physical database type.</param>
    /// <param name="connectionString">Provider-specific ADO.NET connection string.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is null, empty, whitespace, or contains the reserved <c>'|'</c>
    /// separator (which would corrupt the <c>targetSystem|name</c> <see cref="Id"/>).
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="connectionString"/> is null.</exception>
    public ConnectionInfo(string name, DbType dbType, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('|', StringComparison.Ordinal))
            throw new ArgumentException(
                "Connection-Name darf das reservierte Trennzeichen '|' nicht enthalten " +
                "(es würde die targetSystem|name-Id korrumpieren).",
                nameof(name));
        ArgumentNullException.ThrowIfNull(connectionString);

        Name = name;
        DbType = dbType;
        ConnectionString = connectionString;
        Id = MakeId(TargetSystem(dbType), name);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public DbType DbType { get; }

    /// <inheritdoc />
    public string ConnectionString { get; }

    /// <summary>
    /// Builds the canonical identity <c>targetSystem|name</c> lowercased
    /// (<see cref="CultureInfo.InvariantCulture"/>).
    /// </summary>
    /// <param name="targetSystem">Physical target system token (e.g. <c>"MSSQL"</c>).</param>
    /// <param name="name">Logical connection name.</param>
    /// <returns>The lowercased identity string.</returns>
    public static string MakeId(string targetSystem, string name) =>
        $"{targetSystem}|{name}".ToLowerInvariant();

    /// <summary>Parses a provider string (<c>"mssql"</c>/<c>"pgsql"</c>, case-insensitive) to a <see cref="DbType"/>.</summary>
    /// <param name="dbType">Provider string, <c>"mssql"</c> or <c>"pgsql"</c>.</param>
    /// <returns>The corresponding <see cref="DbType"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="dbType"/> is not a known provider string.</exception>
    internal static DbType ParseProvider(string dbType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbType);
        return dbType.Trim().ToLowerInvariant() switch
        {
            "mssql" => DbType.Mssql,
            "pgsql" => DbType.Pgsql,
            _ => throw new ArgumentException(
                $"Unbekannter Provider-String '{dbType}'. Erwartet: 'mssql' oder 'pgsql'.", nameof(dbType)),
        };
    }

    /// <summary>Maps a <see cref="DbType"/> to its <c>targetSystem</c> token.</summary>
    /// <param name="dbType">The database type.</param>
    /// <returns><c>"MSSQL"</c> or <c>"PGSQL"</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a known <see cref="DbType"/>.</exception>
    public static string TargetSystem(DbType dbType) => dbType switch
    {
        DbType.Mssql => "MSSQL",
        DbType.Pgsql => "PGSQL",
        _ => throw new ArgumentOutOfRangeException(nameof(dbType), dbType, "Unknown DbType."),
    };

    /// <summary>
    /// Casts <paramref name="connection"/> to <see cref="ConnectionInfo"/>.
    /// Throws <see cref="InvalidOperationException"/> when the cast fails (the directory returned a
    /// non-<see cref="ConnectionInfo"/> implementation that the engine's gateway cannot use).
    /// </summary>
    /// <param name="connection">The connection to cast.</param>
    /// <returns>The connection as <see cref="ConnectionInfo"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="connection"/> is not a <see cref="ConnectionInfo"/>.
    /// </exception>
    internal static ConnectionInfo AsConnectionInfo(IConnection connection) =>
        connection as ConnectionInfo
        ?? throw new InvalidOperationException(
            "The connection directory returned a connection that is not a ConnectionInfo; " +
            "the engine's gateway requires the concrete ConnectionInfo type.");
}
