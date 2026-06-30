// Copyright (c) 2026 crossVault GmbH.

using System.ComponentModel;
using System.Security.Authentication;
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
    public void Gateway_rejects_null_info()
    {
        IConnectionGateway gateway = new ConnectionGateway();
        Assert.Throws<ArgumentNullException>(() => gateway.Open(null!));
    }

    // FIX D: cert-failure detection must use a STRUCTURED signal (Win32 SChannel cert code /
    // AuthenticationException), not English message substrings, so it works on non-English hosts.

    [Fact]
    public void IsTlsCertFailure_detects_inner_win32_schannel_cert_error()
    {
        // The real signal path: an outer exception wrapping a Win32Exception with the SChannel
        // SEC_E_UNTRUSTED_ROOT (0x80090325) native error code — no English cert text anywhere.
        var inner = new Win32Exception(unchecked((int)0x80090325));
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
}
