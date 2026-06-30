// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

public class ConnectionDirectoryTests
{
    // Two entries with the same name "stage", one MSSQL, one PGSQL — the cross-system pair.
    private const string TwoStageXml = """
        <ConnectionStrings>
          <ConnectionString targetSystem="MSSQL" name="stage">
            <Parameters><Parameter key="Server" value="db01" type="String" /></Parameters>
          </ConnectionString>
          <ConnectionString targetSystem="PGSQL" name="stage">
            <Parameters><Parameter key="Host" value="db02" type="String" /></Parameters>
          </ConnectionString>
          <ConnectionString targetSystem="MSSQL" name="reporting">
            <Parameters><Parameter key="Server" value="db03" type="String" /></Parameters>
          </ConnectionString>
        </ConnectionStrings>
        """;

    private static IConnectionDirectory Directory(string xml) =>
        new ConnectionDirectory(ConnectionRegistry.Parse(xml));

    [Fact]
    public void Default_is_first_connection()
    {
        var dir = Directory(TwoStageXml);
        Assert.Equal("mssql|stage", dir.Default.Id);
    }

    [Fact]
    public void Extern_is_all_but_default()
    {
        var dir = Directory(TwoStageXml);
        var ids = dir.Extern.Select(c => c.Id).ToList();
        Assert.Equal(["pgsql|stage", "mssql|reporting"], ids);
    }

    [Fact]
    public void Default_throws_when_no_connections()
    {
        var dir = Directory("<ConnectionStrings />");
        Assert.Throws<InvalidOperationException>(() => dir.Default);
    }

    [Fact]
    public void GetConnection_by_name_and_dbType()
    {
        var dir = Directory(TwoStageXml);
        Assert.Equal("mssql|stage", dir.GetConnection("stage", DbType.Mssql).Id);
        Assert.Equal("pgsql|stage", dir.GetConnection("stage", DbType.Pgsql).Id);
    }

    [Fact]
    public void GetConnection_by_name_and_dbType_miss_throws_with_diagnostics()
    {
        var dir = Directory(TwoStageXml);
        var ex = Assert.Throws<InvalidOperationException>(() => dir.GetConnection("nope", DbType.Mssql));
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Mssql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetConnection_by_provider_string_case_insensitive()
    {
        var dir = Directory(TwoStageXml);
        Assert.Equal("mssql|stage", dir.GetConnection("stage", "MsSqL").Id);
        Assert.Equal("pgsql|stage", dir.GetConnection("stage", "pgsql").Id);
    }

    [Fact]
    public void GetConnection_by_unknown_provider_string_throws()
    {
        var dir = Directory(TwoStageXml);
        Assert.Throws<ArgumentException>(() => dir.GetConnection("stage", "oracle"));
    }

    [Fact]
    public void GetConnection_by_provider_string_valid_provider_unknown_name_throws()
    {
        var dir = Directory(TwoStageXml);
        // Valid provider ("mssql") but a name that does not exist → miss, not silent null.
        var ex = Assert.Throws<InvalidOperationException>(() => dir.GetConnection("nope", "mssql"));
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetById_returns_connection_or_null()
    {
        var dir = Directory(TwoStageXml);
        Assert.Equal("pgsql|stage", dir.GetById("PGSQL|stage")?.Id);
        Assert.Null(dir.GetById("mssql|unknown"));
        Assert.Null(dir.GetById(null!));
        Assert.Null(dir.GetById("  "));
    }

    [Fact]
    public void Where_filters_by_dbType()
    {
        var dir = Directory(TwoStageXml);
        var mssql = dir.Where(DbType.Mssql);
        Assert.Equal(["mssql|stage", "mssql|reporting"], mssql.Select(c => c.Id));
    }

    [Fact]
    public void Where_filters_by_id_case_insensitive()
    {
        var dir = Directory(TwoStageXml);
        var byId = dir.Where(id: "PGSQL|STAGE");
        Assert.Equal("pgsql|stage", Assert.Single(byId).Id);
    }

    [Fact]
    public void Where_with_no_filter_returns_all()
    {
        var dir = Directory(TwoStageXml);
        Assert.Equal(3, dir.Where().Count);
    }

    // ── Cross-system (T03.3a) ────────────────────────────────────────────────

    [Fact]
    public void CrossSystem_resolves_source_to_target()
    {
        var dir = Directory(TwoStageXml);
        var target = dir.GetConnection("stage", DbType.Mssql, DbType.Pgsql);
        Assert.Equal("pgsql|stage", target.Id);
    }

    [Fact]
    public void CrossSystem_resolves_target_to_source_other_direction()
    {
        var dir = Directory(TwoStageXml);
        var target = dir.GetConnection("stage", DbType.Pgsql, DbType.Mssql);
        Assert.Equal("mssql|stage", target.Id);
    }

    [Fact]
    public void CrossSystem_missing_source_throws()
    {
        var dir = Directory(TwoStageXml);
        // "reporting" exists only as MSSQL; using PGSQL as source must fail.
        var ex = Assert.Throws<InvalidOperationException>(
            () => dir.GetConnection("reporting", DbType.Pgsql, DbType.Mssql));
        Assert.Contains("reporting", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Quell", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossSystem_missing_target_counterpart_throws()
    {
        var dir = Directory(TwoStageXml);
        // "reporting" exists only as MSSQL; the PGSQL counterpart is missing.
        var ex = Assert.Throws<InvalidOperationException>(
            () => dir.GetConnection("reporting", DbType.Mssql, DbType.Pgsql));
        Assert.Contains("reporting", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Ziel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_construction_throws_on_duplicate_ids()
    {
        var dupXml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="stage">
                <Parameters><Parameter key="Server" value="a" type="String" /></Parameters>
              </ConnectionString>
              <ConnectionString targetSystem="MSSQL" name="stage">
                <Parameters><Parameter key="Server" value="b" type="String" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;
        Assert.Throws<InvalidOperationException>(() => Directory(dupXml));
    }
}
