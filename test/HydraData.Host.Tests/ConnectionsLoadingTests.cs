// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Xunit;

namespace HydraData.Host.Tests;

/// <summary>
/// Loading <c>connections.xml</c> into an <see cref="IConnectionDirectory"/>,
/// the way the host does it: read the file, parse via <see cref="ConnectionRegistry"/>, wrap in a
/// <see cref="ConnectionDirectory"/>. Covers the happy path plus malformed/missing-file negatives.
/// </summary>
public class ConnectionsLoadingTests
{
    [Fact]
    public void Valid_xml_builds_a_directory_with_default_connection()
    {
        using var scaffold = new HostScaffold();

        var directory = scaffold.Connections();

        Assert.Equal("stage", directory.Default.Name);
        Assert.Equal(DbType.Mssql, directory.Default.DbType);
        // Assert the Database parameter specifically landed in the built MSSQL string as the
        // 'Initial Catalog' keyword (SqlConnectionStringBuilder canonicalises Database → Initial Catalog),
        // rather than just checking that the literal "stage" appears anywhere (which the connection NAME
        // also satisfies). This pins that the Database=stage parameter was actually applied.
        var concrete = Assert.IsType<ConnectionInfo>(directory.Default);
        Assert.Contains("Initial Catalog=stage", concrete.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_connections_file_throws_when_loaded()
    {
        using var scaffold = new HostScaffold();
        File.Delete(scaffold.ConnectionsFile);

        Assert.Throws<FileNotFoundException>(() => ConnectionRegistry.Load(scaffold.ConnectionsFile));
    }

    [Fact]
    public void Malformed_xml_throws_xml_exception()
    {
        using var scaffold = new HostScaffold();
        scaffold.WriteConnections("<ConnectionStrings><ConnectionString name=\"x\"</ConnectionStrings>");

        Assert.Throws<System.Xml.XmlException>(() =>
            ConnectionRegistry.Load(scaffold.ConnectionsFile));
    }

    [Fact]
    public void Missing_required_attribute_throws_format_exception()
    {
        using var scaffold = new HostScaffold();
        // Well-formed XML but missing the required targetSystem attribute.
        scaffold.WriteConnections("""
            <ConnectionStrings>
              <ConnectionString name="stage">
                <Parameters><Parameter key="Server" value="localhost" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """);

        Assert.Throws<FormatException>(() => ConnectionRegistry.Load(scaffold.ConnectionsFile));
    }
}
