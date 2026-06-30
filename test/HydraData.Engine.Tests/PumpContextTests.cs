// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T02.2 + T03.4a: PumpContext verdicts, notes, the Raw runtime guard, and per-id slot reuse with
/// commit/rollback fan-out.
/// </summary>
public class PumpContextTests
{
    [Fact]
    public void Expect_true_is_noop()
    {
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway());
        ctx.Expect(true, "should not throw"); // no exception
    }

    [Fact]
    public void Expect_false_throws_fail_verdict()
    {
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway());

        var verdict = Assert.Throws<StepVerdict>(() => ctx.Expect(false, "bad", new { n = 1 }));

        Assert.Equal(Severity.Error, verdict.Result.Severity);
        Assert.Equal("bad", verdict.Result.Message);
        Assert.NotNull(verdict.Result.Details);
    }

    [Fact]
    public void Note_defaults_to_success_and_records_in_order()
    {
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway());

        ctx.Note("first");
        ctx.Note("second", Severity.Error);

        Assert.Collection(
            ctx.Notes,
            n => { Assert.Equal("first", n.Message); Assert.Equal(Severity.Success, n.Severity); },
            n => { Assert.Equal("second", n.Message); Assert.Equal(Severity.Error, n.Severity); });
    }

    [Fact]
    public void Raw_throws_when_unsafe_not_declared()
    {
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway(), unsafeAllowed: false);

        Assert.Throws<InvalidOperationException>(() => ctx.Raw());
    }

    [Fact]
    public void DB_call_without_connection_fails_fast()
    {
        var ctx = new PumpContext(
            new PumpState(), new PumpState(),
            ExternContext.FromValues(new Dictionary<string, object?>()),
            new FakeConnectionGateway(),
            defaultConnection: null,
            unsafeAllowed: false);

        Assert.Throws<InvalidOperationException>(() => ctx.Execute("update t set x = 1"));
    }

    [Fact]
    public void Slot_is_opened_once_per_id_and_reused()
    {
        var gateway = new FakeConnectionGateway();
        var ctx = PumpContextFactory.Create(gateway);

        // Three DB calls against the same (single) connection => exactly one slot opened.
        ctx.Query("select 1");
        ctx.Execute("update t set x = 1");
        ctx.Scalar<int>("select count(*) from t");

        Assert.Single(gateway.Slots);
        Assert.Equal(3, gateway.Slots[0].FakeExecutor.Statements.Count);
    }

    [Fact]
    public void CommitAll_commits_and_disposes_every_open_slot()
    {
        var gateway = new FakeConnectionGateway();
        var ctx = PumpContextFactory.Create(gateway);

        ctx.Query("select 1");
        ctx.CommitAll();

        var slot = Assert.Single(gateway.Slots);
        Assert.Equal(1, slot.Commits);
        Assert.Equal(0, slot.Rollbacks);
        Assert.True(slot.Disposed);
    }

    [Fact]
    public void RollbackAll_rolls_back_and_disposes_every_open_slot()
    {
        var gateway = new FakeConnectionGateway();
        var ctx = PumpContextFactory.Create(gateway);

        ctx.Execute("delete from t");
        ctx.RollbackAll();

        var slot = Assert.Single(gateway.Slots);
        Assert.Equal(0, slot.Commits);
        Assert.Equal(1, slot.Rollbacks);
        Assert.True(slot.Disposed);
    }

    [Fact]
    public void No_DB_access_opens_no_slot_and_commit_is_noop()
    {
        var gateway = new FakeConnectionGateway();
        var ctx = PumpContextFactory.Create(gateway);

        ctx.CommitAll(); // nothing opened => no-op

        Assert.Empty(gateway.Slots);
    }

    // ── Connection switching ─────────────────────────

    [Fact]
    public void CurrentConnection_is_the_default_connection()
    {
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway());

        Assert.Same(PumpContextFactory.DefaultConnection, ctx.CurrentConnection);
    }

    [Fact]
    public void CurrentConnection_throws_when_no_default_connection()
    {
        var ctx = new PumpContext(
            new PumpState(), new PumpState(),
            ExternContext.FromValues(new Dictionary<string, object?>()),
            new FakeConnectionGateway(),
            defaultConnection: null,
            unsafeAllowed: false);

        Assert.Throws<InvalidOperationException>(() => ctx.CurrentConnection);
    }

    [Fact]
    public void No_arg_DB_call_uses_the_default_connection_only()
    {
        var gateway = new FakeConnectionGateway();
        var ctx = PumpContextFactory.Create(gateway);

        ctx.Execute("update t set x = 1");

        var id = Assert.Single(gateway.OpenedIds);
        Assert.Equal(PumpContextFactory.DefaultConnection.Id, id);
        Assert.Single(gateway.Slots);
    }

    [Fact]
    public void Targeting_two_distinct_connections_opens_two_slots()
    {
        var gateway = new FakeConnectionGateway();
        var ctx = PumpContextFactory.Create(gateway);

        ctx.Execute(PumpContextFactory.DefaultConnection, "insert into a values (1)");
        ctx.Execute(PumpContextFactory.SecondConnection, "insert into b values (1)");

        Assert.Equal(2, gateway.Slots.Count);
        Assert.Equal(
            [PumpContextFactory.DefaultConnection.Id, PumpContextFactory.SecondConnection.Id],
            gateway.OpenedIds);
    }

    [Fact]
    public void Targeting_the_same_connection_twice_reuses_one_slot()
    {
        var gateway = new FakeConnectionGateway();
        var ctx = PumpContextFactory.Create(gateway);

        ctx.Query(PumpContextFactory.SecondConnection, "select 1");
        ctx.Execute(PumpContextFactory.SecondConnection, "update t set x = 1");

        Assert.Single(gateway.Slots);
        Assert.Equal(2, gateway.SlotFor(PumpContextFactory.SecondConnection.Id).FakeExecutor.Statements.Count);
    }

    [Fact]
    public void CommitAll_fans_out_across_every_targeted_connection()
    {
        var gateway = new FakeConnectionGateway();
        var ctx = PumpContextFactory.Create(gateway);

        ctx.Execute(PumpContextFactory.DefaultConnection, "insert into a values (1)");
        ctx.Execute(PumpContextFactory.SecondConnection, "insert into b values (1)");
        ctx.CommitAll();

        var slotA = gateway.SlotFor(PumpContextFactory.DefaultConnection.Id);
        var slotB = gateway.SlotFor(PumpContextFactory.SecondConnection.Id);
        Assert.Equal(1, slotA.Commits);
        Assert.Equal(1, slotB.Commits);
        Assert.True(slotA.Disposed);
        Assert.True(slotB.Disposed);
    }

    [Fact]
    public void RollbackAll_fans_out_across_every_targeted_connection()
    {
        var gateway = new FakeConnectionGateway();
        var ctx = PumpContextFactory.Create(gateway);

        ctx.Execute(PumpContextFactory.DefaultConnection, "insert into a values (1)");
        ctx.Execute(PumpContextFactory.SecondConnection, "insert into b values (1)");
        ctx.RollbackAll();

        var slotA = gateway.SlotFor(PumpContextFactory.DefaultConnection.Id);
        var slotB = gateway.SlotFor(PumpContextFactory.SecondConnection.Id);
        Assert.Equal(1, slotA.Rollbacks);
        Assert.Equal(1, slotB.Rollbacks);
        Assert.Equal(0, slotA.Commits);
        Assert.Equal(0, slotB.Commits);
    }

    [Fact]
    public void Connection_targeted_DB_call_with_null_connection_throws()
    {
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway());

        Assert.Throws<ArgumentNullException>(() => ctx.Execute(null!, "update t set x = 1"));
    }

    // A4: a foreign IConnection token is resolved by identity through the directory.
    // A token not resolvable in the directory fails clearly with the directory error.
    [Fact]
    public void Foreign_IConnection_not_in_directory_fails_clearly()
    {
        var directory = new ConnectionDirectory(ConnectionRegistry.Parse(TwoSystemXml));
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway(), connections: directory);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ctx.Query(new ForeignConnection(), "select 1"));

        Assert.Contains("stub", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Foreign_IConnection_resolvable_in_directory_resolves_via_identity()
    {
        var directory = new ConnectionDirectory(ConnectionRegistry.Parse(TwoSystemXml));
        var gateway = new FakeConnectionGateway();
        var ctx = PumpContextFactory.Create(gateway, connections: directory);

        ctx.Query(new ForeignConnection("stage", DbType.Mssql), "select 1");

        var id = Assert.Single(gateway.OpenedIds);
        Assert.Equal("mssql|stage", id);
    }

    [Fact]
    public void Foreign_IConnection_without_directory_fails_clearly()
    {
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway());

        Assert.Throws<InvalidOperationException>(() =>
            ctx.Query(new ForeignConnection(), "select 1"));
    }

    /// <summary>A custom <see cref="IConnection"/> implementation that is NOT a <see cref="ConnectionInfo"/>.</summary>
    private sealed class ForeignConnection(string name = "stub", DbType dbType = DbType.Mssql) : IConnection
    {
        public string Id => ConnectionInfo.MakeId(ConnectionInfo.TargetSystem(DbType), Name);
        public string Name { get; } = name;
        public DbType DbType { get; } = dbType;
        public string ConnectionString => "Server=nowhere;";
    }


    [Fact]
    public void GetConnection_overloads_delegate_to_the_directory()
    {
        var directory = new ConnectionDirectory(ConnectionRegistry.Parse(TwoSystemXml));
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway(), connections: directory);

        // by name + DbType
        Assert.Equal("mssql|stage", ctx.GetConnection("stage", DbType.Mssql).Id);
        // by name + provider string
        Assert.Equal("pgsql|stage", ctx.GetConnection("stage", "pgsql").Id);
        // cross-system: identify by source DbType, resolve to target DbType
        Assert.Equal("pgsql|stage", ctx.GetConnection("stage", DbType.Mssql, DbType.Pgsql).Id);
        // cross-system with a provider string target (the script idiom)
        Assert.Equal("pgsql|stage", ctx.GetConnection("stage", DbType.Mssql, "pgsql").Id);
        // by id
        Assert.Equal("pgsql|stage", ctx.GetById("pgsql|stage")!.Id);
        Assert.Null(ctx.GetById("mssql|missing"));
        // filter
        Assert.Equal(2, ctx.Where().Count);
        Assert.Single(ctx.Where(DbType.Mssql));
    }

    [Fact]
    public void GetConnection_throws_when_no_directory_is_wired()
    {
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway());

        Assert.Throws<InvalidOperationException>(() => ctx.GetConnection("stage", DbType.Pgsql));
    }

    // ── Scalar<T> null contract (Item 5) ─────────────────────────────────────

    [Fact]
    public void Scalar_string_returns_null_on_DB_NULL()
    {
        // FakeDbExecutor.Scalar<T> returns default!, which for string is null.
        // This pins the documented null contract: ref-type T → null on DB NULL.
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway());

        var result = ctx.Scalar<string>("SELECT NULL");

        Assert.Null(result);
    }

    [Fact]
    public void Scalar_int_returns_zero_on_DB_NULL()
    {
        // FakeDbExecutor.Scalar<T> returns default!, which for int is 0.
        // This pins the documented null contract: value-type T → default(T) on DB NULL.
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway());

        var result = ctx.Scalar<int>("SELECT NULL");

        Assert.Equal(0, result);
    }

    private const string TwoSystemXml =
        """
        <ConnectionStrings>
          <ConnectionString targetSystem="MSSQL" name="stage">
            <Parameters>
              <Parameter key="Server"   value="db01"  type="String" />
              <Parameter key="Database" value="stage" type="String" />
            </Parameters>
          </ConnectionString>
          <ConnectionString targetSystem="PGSQL" name="stage">
            <Parameters>
              <Parameter key="Host"     value="db02"  type="String" />
              <Parameter key="Database" value="stage" type="String" />
            </Parameters>
          </ConnectionString>
        </ConnectionStrings>
        """;
}
