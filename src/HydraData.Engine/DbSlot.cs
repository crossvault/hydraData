// Copyright (c) 2026 crossVault GmbH.

using System.ComponentModel;
using System.Data;
using System.Security.Authentication;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace HydraData.Engine;

/// <summary>
/// A database slot that lazily opens its ADO.NET connection and transaction on first access to
/// <see cref="Executor"/>. Pooling is left to the ADO.NET driver; the engine does not self-pool
///.
/// </summary>
internal sealed class DbSlot : IDbSlot
{
    /// <summary>
    /// Default ADO.NET <c>Connect Timeout</c> (seconds) applied when the connection string does not
    /// specify one, making the engine's fail-fast policy explicit instead of relying on provider defaults.
    /// </summary>
    internal const int DefaultConnectTimeoutSeconds = 15;

    private readonly ConnectionInfo _info;
    private readonly int? _commandTimeoutSeconds;
    private readonly IDbSlotConnectionFactory _connectionFactory;
    private IDbSlotConnection? _connection;
    private IDbTransaction? _transaction;
    private IDbExecutor? _executor;
    private bool _disposed;

    /// <summary>Creates a slot for the given connection. No connection is opened until first use.</summary>
    /// <param name="info">The resolved connection.</param>
    /// <param name="commandTimeoutSeconds">
    /// Optional Dapper/bulk command timeout (seconds), derived from <c>PumpOptions.StepTimeout</c> by the
    /// step plumbing. <see langword="null"/> leaves the provider default command timeout in place.
    /// </param>
    public DbSlot(ConnectionInfo info, int? commandTimeoutSeconds = null)
        : this(info, commandTimeoutSeconds, ProviderDbSlotConnectionFactory.Instance)
    {
    }

    internal DbSlot(
        ConnectionInfo info,
        int? commandTimeoutSeconds,
        IDbSlotConnectionFactory connectionFactory)
    {
        _info = info;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public IDbExecutor Executor
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureOpen();
            return _executor!;
        }
    }

    /// <inheritdoc />
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // No transaction was started (no DB access happened): nothing to commit.
        _transaction?.Commit();
    }

    /// <inheritdoc />
    public void Rollback()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _transaction?.Rollback();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _transaction?.Dispose();
        _connection?.Dispose();
    }

    private void EnsureOpen()
    {
        if (_executor is not null) return;

        var connectionString = _info.DbType switch
        {
            DbType.Mssql => WithMssqlConnectTimeout(_info.ConnectionString),
            DbType.Pgsql => WithPgsqlCommandTimeout(
                WithPgsqlConnectTimeout(_info.ConnectionString), _commandTimeoutSeconds),
            _ => throw new ArgumentOutOfRangeException(
                nameof(_info), _info.DbType, "Unknown DbType; cannot open slot."),
        };

        var connection = _connectionFactory.Create(_info.DbType, connectionString);
        try
        {
            if (_info.DbType == DbType.Mssql)
                OpenMssql(connection);
            else
                connection.Open();

            // Publish ownership immediately after a successful provider Open, before any session
            // setup or transaction work that can throw.
            _connection = connection;

            if (_info.DbType == DbType.Pgsql
                && _commandTimeoutSeconds is { } seconds
                && seconds > 0)
            {
                connection.SetPgsqlStatementTimeout(PgsqlStatementTimeoutMilliseconds(seconds));
            }

            var transaction = connection.BeginTransaction();
            _transaction = transaction;
            _executor = connection.CreateExecutor(transaction, _commandTimeoutSeconds);
        }
        catch
        {
            // Open failures happen before the field assignment; later failures happen after it.
            // Dispose the acquired resource in both cases, and clear partial state so a retry cannot
            // retain a failed connection or transaction.
            _transaction?.Dispose();
            _transaction = null;
            connection.Dispose();
            _connection = null;
            throw;
        }
    }

    // Opens the MSSQL connection, turning the cryptic TLS cert-chain SqlException (Encrypt=true default
    // against an on-prem server with a self-signed cert) into a clear, actionable hint.
    private void OpenMssql(IDbSlotConnection connection)
    {
        try
        {
            connection.Open();
        }
        catch (SqlException ex) when (IsTlsCertFailure(ex))
        {
            throw new InvalidOperationException(
                $"Connection to '{_info.Id}' failed TLS validation; on-prem SQL Server with a " +
                "self-signed certificate needs Encrypt/TrustServerCertificate in the connection " +
                "parameters (see the connection configuration documentation).",
                ex);
        }
    }

    // Windows SChannel certificate-chain error codes (HRESULT/NativeErrorCode) surfaced by the TLS
    // handshake when the server certificate cannot be trusted (e.g. an on-prem SQL Server with a
    // self-signed cert under Encrypt=true).
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109); // CERT_E_UNTRUSTEDROOT
    private const int SecEUntrustedRoot = unchecked((int)0x80090325);  // SEC_E_UNTRUSTED_ROOT
    private const int CertEChaining = unchecked((int)0x800B010A);      // CERT_E_CHAINING

    // Detects a TLS/cert-chain trust failure by a STRUCTURED signal rather than English message text:
    // walk the inner-exception chain for an AuthenticationException (the TLS handshake failure type) or
    // a Win32Exception whose NativeErrorCode is a known SChannel cert error. A locale-independent
    // message-substring check is kept ONLY as a last-resort fallback (for providers/platforms that do
    // not surface the structured inner exception). Taking an Exception makes the predicate unit-testable.
    internal static bool IsTlsCertFailure(Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
                return true;
            if (inner is Win32Exception win32 && IsSChannelCertError(win32.NativeErrorCode))
                return true;
        }

        // Last-resort, locale-brittle fallback: only used when no structured signal was present.
        // Narrowed to "certificate" only — "chain" and "trust" are common English words that can
        // false-positive on unrelated errors. The structured Win32/SChannel path (above) handles the
        // real cases; this fallback is a safety net for platforms that do not surface those codes.
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsSChannelCertError(int nativeErrorCode) =>
        nativeErrorCode is CertEUntrustedRoot or SecEUntrustedRoot or CertEChaining;

    // Sets an explicit Connect Timeout on the MSSQL string if the operator did not specify one.
    internal static string WithMssqlConnectTimeout(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        // ShouldSerialize detects whether the operator explicitly supplied any provider-recognised
        // synonym (Connect Timeout, Connection Timeout or Timeout) without inspecting quoted values.
        if (!builder.ShouldSerialize("Connect Timeout"))
            builder.ConnectTimeout = DefaultConnectTimeoutSeconds;
        return builder.ConnectionString;
    }

    // Sets an explicit Timeout (Npgsql's connect timeout) if the operator did not specify one.
    // ContainsKey cannot distinguish an unset known keyword from a supplied value in Npgsql;
    // ShouldSerialize reports only values that were explicitly supplied or assigned.
    internal static string WithPgsqlConnectTimeout(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.ShouldSerialize("Timeout"))
            builder.Timeout = DefaultConnectTimeoutSeconds;
        return builder.ConnectionString;
    }

    // Threads the per-step command timeout onto the PGSQL connection string as the CommandTimeout
    // keyword. This bounds normal Dapper commands (Query/Scalar/Execute) at the client side.
    // NpgsqlBinaryImporter (binary COPY) is NOT bounded by this client-side timeout; COPY is bounded
    // server-side via SET statement_timeout set by SetPgsqlStatementTimeout (called after Open).
    // An operator-supplied CommandTimeout in the connection string is respected and not overridden.
    internal static string WithPgsqlCommandTimeout(string connectionString, int? seconds)
    {
        if (seconds is not { } commandTimeoutSeconds) return connectionString;

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.ShouldSerialize("Command Timeout"))
            builder.CommandTimeout = commandTimeoutSeconds;
        return builder.ConnectionString;
    }

    internal static long PgsqlStatementTimeoutMilliseconds(int seconds) => seconds * 1000L;
}

/// <summary>Internal factory seam for provider connections owned by <see cref="DbSlot"/>.</summary>
internal interface IDbSlotConnectionFactory
{
    IDbSlotConnection Create(DbType dbType, string connectionString);
}

/// <summary>
/// Provider-neutral connection lifecycle used by <see cref="DbSlot"/>. Concrete adapters retain
/// provider-specific executor construction without exposing a public test seam.
/// </summary>
internal interface IDbSlotConnection : IDisposable
{
    void Open();

    void SetPgsqlStatementTimeout(long milliseconds);

    IDbTransaction BeginTransaction();

    IDbExecutor CreateExecutor(IDbTransaction transaction, int? commandTimeoutSeconds);
}

internal sealed class ProviderDbSlotConnectionFactory : IDbSlotConnectionFactory
{
    internal static ProviderDbSlotConnectionFactory Instance { get; } = new();

    private ProviderDbSlotConnectionFactory()
    {
    }

    public IDbSlotConnection Create(DbType dbType, string connectionString) => dbType switch
    {
        DbType.Mssql => new MssqlSlotConnection(connectionString),
        DbType.Pgsql => new PgsqlSlotConnection(connectionString),
        _ => throw new ArgumentOutOfRangeException(
            nameof(dbType), dbType, "Unknown DbType; cannot create connection."),
    };

    private sealed class MssqlSlotConnection(string connectionString) : IDbSlotConnection
    {
        private readonly SqlConnection _connection = new(connectionString);

        public void Open() => _connection.Open();

        public void SetPgsqlStatementTimeout(long milliseconds) =>
            throw new InvalidOperationException("PostgreSQL statement timeout cannot be set on MSSQL.");

        public IDbTransaction BeginTransaction() => _connection.BeginTransaction();

        public IDbExecutor CreateExecutor(
            IDbTransaction transaction, int? commandTimeoutSeconds) =>
            new MssqlExecutor(
                _connection,
                (SqlTransaction)transaction,
                commandTimeoutSeconds);

        public void Dispose() => _connection.Dispose();
    }

    private sealed class PgsqlSlotConnection(string connectionString) : IDbSlotConnection
    {
        private readonly NpgsqlConnection _connection = new(connectionString);

        public void Open() => _connection.Open();

        public void SetPgsqlStatementTimeout(long milliseconds)
        {
            using var command = new NpgsqlCommand(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"SET statement_timeout = {milliseconds}"),
                _connection);
            command.ExecuteNonQuery();
        }

        public IDbTransaction BeginTransaction() => _connection.BeginTransaction();

        public IDbExecutor CreateExecutor(
            IDbTransaction transaction, int? commandTimeoutSeconds) =>
            new PgsqlExecutor(
                _connection,
                (NpgsqlTransaction)transaction,
                commandTimeoutSeconds);

        public void Dispose() => _connection.Dispose();
    }
}
