// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine.Tests.Fakes;

/// <summary>Factory helpers for building a <see cref="PumpContext"/> in tests (internal ctor via IVT).</summary>
internal static class PumpContextFactory
{
    /// <summary>A default MSSQL connection used by DB-touching tests.</summary>
    public static ConnectionInfo DefaultConnection { get; } =
        new("stage", DbType.Mssql, "Server=localhost;Database=none;");

    /// <summary>A second, distinct connection (PGSQL) for connection-switching tests.</summary>
    public static ConnectionInfo SecondConnection { get; } =
        new("stage", DbType.Pgsql, "Host=localhost;Database=none;");

    /// <summary>Builds a context with empty state, an empty extern context and the given gateway.</summary>
    /// <param name="gateway">The (fake) gateway.</param>
    /// <param name="connection">Default connection; defaults to <see cref="DefaultConnection"/>.</param>
    /// <param name="unsafeAllowed">Whether <c>@unsafe</c> is set.</param>
    /// <param name="connections">Optional connection directory backing the <c>GetConnection</c> overloads.</param>
    public static PumpContext Create(
        IConnectionGateway gateway,
        ConnectionInfo? connection = null,
        bool unsafeAllowed = false,
        IConnectionDirectory? connections = null) =>
        new(
            new PumpState(),
            new PumpState(),
            ExternContext.FromValues(new Dictionary<string, object?>()),
            gateway,
            connection ?? DefaultConnection,
            unsafeAllowed,
            io: null,
            logger: null,
            connections: connections);
}
