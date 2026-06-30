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
    /// specify one, so a dead host fails fast rather than waiting out an unbounded provider default.
    /// </summary>
    internal const int DefaultConnectTimeoutSeconds = 15;

    private readonly ConnectionInfo _info;
    private readonly int? _commandTimeoutSeconds;
    private IDbConnection? _connection;
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
    {
        _info = info;
        _commandTimeoutSeconds = commandTimeoutSeconds;
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
        // No transaction was started (no DB access happened): nothing to commit.
        _transaction?.Commit();
    }

    /// <inheritdoc />
    public void Rollback()
    {
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

        switch (_info.DbType)
        {
            case DbType.Mssql:
                {
                    var connection = new SqlConnection(WithMssqlConnectTimeout(_info.ConnectionString));
                    OpenMssql(connection);
                    var transaction = connection.BeginTransaction();
                    _connection = connection;
                    _transaction = transaction;
                    _executor = new MssqlExecutor(connection, transaction, _commandTimeoutSeconds);
                    break;
                }

            case DbType.Pgsql:
                {
                    var connection = new NpgsqlConnection(
                        WithPgsqlCommandTimeout(WithPgsqlConnectTimeout(_info.ConnectionString)));
                    connection.Open();
                    SetPgsqlStatementTimeout(connection);
                    var transaction = connection.BeginTransaction();
                    _connection = connection;
                    _transaction = transaction;
                    _executor = new PgsqlExecutor(connection, transaction, _commandTimeoutSeconds);
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(_info), _info.DbType, "Unknown DbType; cannot open slot.");
        }
    }

    // Opens the MSSQL connection, turning the cryptic TLS cert-chain SqlException (Encrypt=true default
    // against an on-prem server with a self-signed cert) into a clear, actionable hint.
    private void OpenMssql(SqlConnection connection)
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
    private string WithMssqlConnectTimeout(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        // SqlConnectionStringBuilder defaults ConnectTimeout to 15 and never reports "unset"; only
        // override when the operator left it at the provider default, so an explicit value is respected.
        if (!connectionString.Contains("Connect Timeout", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("Connection Timeout", StringComparison.OrdinalIgnoreCase))
            builder.ConnectTimeout = DefaultConnectTimeoutSeconds;
        return builder.ConnectionString;
    }

    // Sets an explicit Timeout (Npgsql's connect timeout) if the operator did not specify one. The
    // builder parses the string into canonical keys, so checking for the CONNECT-timeout key ("Timeout"
    // / "Connect Timeout") via ContainsKey distinguishes it from "Command Timeout" — a plain
    // Contains("Timeout") would also match CommandTimeout and wrongly suppress the connect default.
    private string WithPgsqlConnectTimeout(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.ContainsKey("Timeout") && !builder.ContainsKey("Connect Timeout"))
            builder.Timeout = DefaultConnectTimeoutSeconds;
        return builder.ConnectionString;
    }

    // Threads the per-step command timeout onto the PGSQL connection string as the CommandTimeout
    // keyword. This bounds normal Dapper commands (Query/Scalar/Execute) at the client side.
    // NpgsqlBinaryImporter (binary COPY) is NOT bounded by this client-side timeout; COPY is bounded
    // server-side via SET statement_timeout set by SetPgsqlStatementTimeout (called after Open).
    // An operator-supplied CommandTimeout in the connection string is respected and not overridden.
    private string WithPgsqlCommandTimeout(string connectionString)
    {
        if (_commandTimeoutSeconds is not { } seconds) return connectionString;

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.ContainsKey("Command Timeout"))
            builder.CommandTimeout = seconds;
        return builder.ConnectionString;
    }

    // Sets the PostgreSQL server-side statement_timeout for this session. This is issued once right
    // after Open() and bounds EVERY statement (including binary COPY) for the lifetime of the slot.
    // statement_timeout is expressed in milliseconds; commandTimeoutSeconds * 1000 converts it.
    // Only SET when a positive timeout is present — statement_timeout=0 disables it entirely, so
    // omitting the SET (leaving the server default) is the correct behaviour when no timeout is
    // configured. The ms value is a validated int formatted with InvariantCulture; SET statement_timeout
    // does not accept a bound parameter, so the int is formatted directly — no SQL-injection risk.
    private void SetPgsqlStatementTimeout(NpgsqlConnection connection)
    {
        if (_commandTimeoutSeconds is not { } seconds || seconds <= 0) return;

        var ms = seconds * 1000;
        using var cmd = new NpgsqlCommand(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"SET statement_timeout = {ms}"),
            connection);
        cmd.ExecuteNonQuery();
    }
}
