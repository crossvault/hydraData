// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

public class ConnectionInfoTests
{
    [Fact]
    public void Id_is_targetSystem_pipe_name_lowercased_for_mssql()
    {
        var info = new ConnectionInfo("Stage", DbType.Mssql, "Server=db01;");
        Assert.Equal("mssql|stage", info.Id);
        Assert.Equal("Stage", info.Name);
        Assert.Equal(DbType.Mssql, info.DbType);
    }

    [Fact]
    public void Id_is_targetSystem_pipe_name_lowercased_for_pgsql()
    {
        var info = new ConnectionInfo("Warehouse", DbType.Pgsql, "Host=db02;");
        Assert.Equal("pgsql|warehouse", info.Id);
    }

    [Fact]
    public void Id_is_case_insensitive_in_name()
    {
        var lower = new ConnectionInfo("stage", DbType.Mssql, "x");
        var upper = new ConnectionInfo("STAGE", DbType.Mssql, "x");
        var mixed = new ConnectionInfo("StAgE", DbType.Mssql, "x");

        Assert.Equal(lower.Id, upper.Id);
        Assert.Equal(lower.Id, mixed.Id);
    }

    [Fact]
    public void MakeId_matches_constructor_id()
    {
        var info = new ConnectionInfo("stage", DbType.Pgsql, "x");
        Assert.Equal(info.Id, ConnectionInfo.MakeId("PGSQL", "stage"));
    }

    [Fact]
    public void TargetSystem_maps_enum_to_token()
    {
        Assert.Equal("MSSQL", ConnectionInfo.TargetSystem(DbType.Mssql));
        Assert.Equal("PGSQL", ConnectionInfo.TargetSystem(DbType.Pgsql));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_blank_name(string name) =>
        Assert.Throws<ArgumentException>(() => new ConnectionInfo(name, DbType.Mssql, "x"));

    [Theory]
    [InlineData("a|b")]
    [InlineData("|leading")]
    [InlineData("trailing|")]
    public void Constructor_rejects_name_containing_pipe(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => new ConnectionInfo(name, DbType.Mssql, "x"));
        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void Constructor_rejects_null_connection_string() =>
        Assert.Throws<ArgumentNullException>(() => new ConnectionInfo("stage", DbType.Mssql, null!));

    [Fact]
    public void Implements_IConnection()
    {
        IConnection conn = new ConnectionInfo("stage", DbType.Mssql, "x");
        Assert.Equal("mssql|stage", conn.Id);
    }

    [Fact]
    public void Script_facing_connection_interface_does_not_expose_connection_string()
    {
        Assert.Null(typeof(IConnection).GetProperty(nameof(ConnectionInfo.ConnectionString)));
        Assert.NotNull(typeof(ConnectionInfo).GetProperty(nameof(ConnectionInfo.ConnectionString)));
    }
}
