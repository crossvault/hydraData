// Copyright (c) 2026 crossVault GmbH.

using System.ComponentModel;
using System.Security.Authentication;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// Unit-level lifecycle checks for the lazy slot. These need no database: they verify that a slot
/// opens nothing until <see cref="IDbSlot.Executor"/> is touched, and that Commit/Rollback/Dispose
/// are safe no-ops when no DB access occurred.
/// </summary>
public class DbSlotLifecycleTests
{
    private static ConnectionInfo Info() =>
        new("stage", DbType.Mssql, "Server=localhost;Database=none;Connect Timeout=1;");

    [Fact]
    public void Gateway_Open_does_not_open_connection_eagerly()
    {
        IConnectionGateway gateway = new ConnectionGateway();

        // Opening the slot must not touch the network; an unreachable server would otherwise throw.
        using var slot = gateway.Open(Info());
        Assert.NotNull(slot);
    }

    [Fact]
    public void Commit_is_noop_when_never_accessed()
    {
        IConnectionGateway gateway = new ConnectionGateway();
        using var slot = gateway.Open(Info());

        // No Executor access => no transaction => commit does nothing (no throw).
        slot.Commit();
    }

    [Fact]
    public void Rollback_is_noop_when_never_accessed()
    {
        IConnectionGateway gateway = new ConnectionGateway();
        using var slot = gateway.Open(Info());
        slot.Rollback();
    }

    [Fact]
    public void Dispose_is_idempotent_when_never_accessed()
    {
        IConnectionGateway gateway = new ConnectionGateway();
        var slot = gateway.Open(Info());
        slot.Dispose();
        slot.Dispose(); // second dispose must not throw.
    }

    [Fact]
    public void Executor_after_dispose_throws()
    {
        IConnectionGateway gateway = new ConnectionGateway();
        var slot = gateway.Open(Info());
        slot.Dispose();
        Assert.Throws<ObjectDisposedException>(() => slot.Executor);
    }

    [Fact]
    public void Commit_after_dispose_throws()
    {
        IConnectionGateway gateway = new ConnectionGateway();
        var slot = gateway.Open(Info());
        slot.Dispose();

        Assert.Throws<ObjectDisposedException>(slot.Commit);
    }

    [Fact]
    public void Rollback_after_dispose_throws()
    {
        IConnectionGateway gateway = new ConnectionGateway();
        var slot = gateway.Open(Info());
        slot.Dispose();

        Assert.Throws<ObjectDisposedException>(slot.Rollback);
    }

    [Fact]
    public void Pgsql_connect_timeout_defaults_to_15_when_absent()
    {
        var result = DbSlot.WithPgsqlConnectTimeout("Host=localhost;Database=stage");
        var builder = new NpgsqlConnectionStringBuilder(result);

        Assert.True(builder.ShouldSerialize("Timeout"));
        Assert.Equal(15, builder.Timeout);
    }

    [Fact]
    public void Pgsql_connect_timeout_preserves_operator_value()
    {
        var result = DbSlot.WithPgsqlConnectTimeout("Host=localhost;Database=stage;Timeout=42");

        Assert.Equal(42, new NpgsqlConnectionStringBuilder(result).Timeout);
    }

    [Fact]
    public void Pgsql_command_timeout_uses_explicit_step_timeout()
    {
        var result = DbSlot.WithPgsqlCommandTimeout(
            "Host=localhost;Database=stage", seconds: 2);
        var builder = new NpgsqlConnectionStringBuilder(result);

        Assert.True(builder.ShouldSerialize("Command Timeout"));
        Assert.Equal(2, builder.CommandTimeout);
    }

    [Fact]
    public void Pgsql_command_timeout_preserves_operator_value()
    {
        var result = DbSlot.WithPgsqlCommandTimeout(
            "Host=localhost;Database=stage;Command Timeout=5", seconds: 2);

        Assert.Equal(5, new NpgsqlConnectionStringBuilder(result).CommandTimeout);
    }

    [Fact]
    public void Mssql_connect_timeout_defaults_to_15_when_absent()
    {
        var result = DbSlot.WithMssqlConnectTimeout("Server=localhost;Database=stage");
        var builder = new SqlConnectionStringBuilder(result);

        Assert.True(builder.ShouldSerialize("Connect Timeout"));
        Assert.Equal(15, builder.ConnectTimeout);
    }

    [Theory]
    [InlineData("Connect Timeout=90")]
    [InlineData("Connection Timeout=90")]
    [InlineData("Timeout=90")]
    public void Mssql_connect_timeout_preserves_every_operator_synonym(string timeout)
    {
        var result = DbSlot.WithMssqlConnectTimeout($"Server=localhost;{timeout}");

        Assert.Equal(90, new SqlConnectionStringBuilder(result).ConnectTimeout);
    }

    [Fact]
    public void Mssql_connect_timeout_does_not_treat_quoted_value_as_a_timeout_keyword()
    {
        var result = DbSlot.WithMssqlConnectTimeout(
            "Server=localhost;Application Name=\"Connect Timeout monitor\"");
        var builder = new SqlConnectionStringBuilder(result);

        Assert.True(builder.ShouldSerialize("Connect Timeout"));
        Assert.Equal(15, builder.ConnectTimeout);
    }

    [Fact]
    public void Pgsql_statement_timeout_milliseconds_uses_long_multiplication()
    {
        Assert.Equal(2_147_483_647_000L,
            DbSlot.PgsqlStatementTimeoutMilliseconds(int.MaxValue));
    }

    [Fact]
    public void Open_failure_disposes_the_acquired_connection()
    {
        var connection = new RecordingSlotConnection
        {
            OpenException = new InvalidOperationException("open failed"),
        };
        using var slot = new DbSlot(Info(), commandTimeoutSeconds: null,
            new SingleConnectionFactory(connection));

        var ex = Assert.Throws<InvalidOperationException>(() => slot.Executor);

        Assert.Equal("open failed", ex.Message);
        Assert.Equal(["open", "dispose"], connection.Events);
    }

    [Fact]
    public void BeginTransaction_failure_disposes_the_open_connection()
    {
        var connection = new RecordingSlotConnection
        {
            BeginTransactionException = new InvalidOperationException("begin failed"),
        };
        using var slot = new DbSlot(Info(), commandTimeoutSeconds: null,
            new SingleConnectionFactory(connection));

        var ex = Assert.Throws<InvalidOperationException>(() => slot.Executor);

        Assert.Equal("begin failed", ex.Message);
        Assert.Equal(["open", "begin", "dispose"], connection.Events);
    }

    [Fact]
    public void Pgsql_statement_timeout_failure_disposes_the_open_connection()
    {
        var connection = new RecordingSlotConnection
        {
            SetStatementTimeoutException = new InvalidOperationException("SET failed"),
        };
        var info = new ConnectionInfo(
            "stage", DbType.Pgsql, "Host=localhost;Database=none;Timeout=1");
        using var slot = new DbSlot(info, commandTimeoutSeconds: 2,
            new SingleConnectionFactory(connection));

        var ex = Assert.Throws<InvalidOperationException>(() => slot.Executor);

        Assert.Equal("SET failed", ex.Message);
        Assert.Equal(["open", "statement-timeout:2000", "dispose"], connection.Events);
    }

    [Fact]
    public void Gateway_rejects_null_info()
    {
        IConnectionGateway gateway = new ConnectionGateway();
        Assert.Throws<ArgumentNullException>(() => gateway.Open(null!));
    }

    // FIX D: cert-failure detection must use a STRUCTURED signal (Win32 SChannel cert code /
    // AuthenticationException), not English message substrings, so it works on non-English hosts.

    [Theory]
    [InlineData(unchecked((int)0x800B0109))]
    [InlineData(unchecked((int)0x80090325))]
    [InlineData(unchecked((int)0x800B010A))]
    public void IsTlsCertFailure_detects_inner_win32_schannel_cert_error(int nativeErrorCode)
    {
        // The real signal path: an outer exception wrapping a Win32Exception with one of the
        // SChannel certificate native error codes — no English cert text anywhere.
        var inner = new Win32Exception(nativeErrorCode);
        var ex = new InvalidOperationException("Verbindung fehlgeschlagen.", inner);

        Assert.True(DbSlot.IsTlsCertFailure(ex));
    }

    [Fact]
    public void IsTlsCertFailure_detects_inner_authentication_exception()
    {
        var ex = new InvalidOperationException("opaque", new AuthenticationException("handshake"));
        Assert.True(DbSlot.IsTlsCertFailure(ex));
    }

    [Fact]
    public void IsTlsCertFailure_does_not_match_unrelated_exception_chain()
    {
        // A plain, unrelated failure (e.g. a login/timeout error with no cert signal and no cert text)
        // must NOT be mis-attributed to a TLS cert problem.
        var ex = new InvalidOperationException(
            "Login failed for user 'sa'.", new TimeoutException("connection timed out"));

        Assert.False(DbSlot.IsTlsCertFailure(ex));
    }

    [Fact]
    public void IsTlsCertFailure_fallback_requires_certificate_word_not_chain_or_trust_alone()
    {
        // The narrowed fallback must NOT fire on "chain" or "trust" without "certificate":
        // these are common English words that can appear in unrelated errors (e.g. retry chain,
        // trust boundary). Only "certificate" is specific enough to serve as a lone fallback signal.
        var chainOnly = new InvalidOperationException("The retry chain failed after 3 attempts.");
        var trustOnly = new InvalidOperationException("Trust boundary violation in plugin sandbox.");

        Assert.False(DbSlot.IsTlsCertFailure(chainOnly),
            "Fallback must not trigger on 'chain' without 'certificate'.");
        Assert.False(DbSlot.IsTlsCertFailure(trustOnly),
            "Fallback must not trigger on 'trust' without 'certificate'.");
    }

    [Fact]
    public void IsTlsCertFailure_fallback_matches_certificate_word()
    {
        // The fallback is still active for messages that DO contain "certificate" (the one retained word).
        var ex = new InvalidOperationException("The remote certificate is invalid according to the validation procedure.");

        Assert.True(DbSlot.IsTlsCertFailure(ex));
    }

    private sealed class SingleConnectionFactory(RecordingSlotConnection connection)
        : IDbSlotConnectionFactory
    {
        public IDbSlotConnection Create(DbType dbType, string connectionString) => connection;
    }

    private sealed class RecordingSlotConnection : IDbSlotConnection
    {
        public Exception? OpenException { get; init; }

        public Exception? BeginTransactionException { get; init; }

        public Exception? SetStatementTimeoutException { get; init; }

        public List<string> Events { get; } = [];

        public void Open()
        {
            Events.Add("open");
            if (OpenException is not null) throw OpenException;
        }

        public void SetPgsqlStatementTimeout(long milliseconds)
        {
            Events.Add($"statement-timeout:{milliseconds}");
            if (SetStatementTimeoutException is not null) throw SetStatementTimeoutException;
        }

        public System.Data.IDbTransaction BeginTransaction()
        {
            Events.Add("begin");
            throw BeginTransactionException
                ?? new InvalidOperationException("A successful fake transaction was not configured.");
        }

        public IDbExecutor CreateExecutor(
            System.Data.IDbTransaction transaction, int? commandTimeoutSeconds) =>
            throw new InvalidOperationException("An executor was not expected in this failure test.");

        public void Dispose() => Events.Add("dispose");
    }
}
