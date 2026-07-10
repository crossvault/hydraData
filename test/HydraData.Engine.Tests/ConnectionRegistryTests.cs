// Copyright (c) 2026 crossVault GmbH.

using System.Data.Common;
using Xunit;

namespace HydraData.Engine.Tests;

public class ConnectionRegistryTests
{
    private const string MssqlStageXml = """
        <ConnectionStrings>
          <ConnectionString targetSystem="MSSQL" name="stage">
            <Parameters>
              <Parameter key="Server"   value="db01"  type="String"  />
              <Parameter key="Database" value="stage" type="String"  />
              <Parameter key="Port"     value="1433"  type="Numeric" />
            </Parameters>
          </ConnectionString>
        </ConnectionStrings>
        """;

    private const string PgsqlStageXml = """
        <ConnectionStrings>
          <ConnectionString targetSystem="PGSQL" name="warehouse">
            <Parameters>
              <Parameter key="Host"     value="db02"      type="String"  />
              <Parameter key="Database" value="warehouse" type="String"  />
              <Parameter key="Port"     value="5432"      type="Numeric" />
            </Parameters>
          </ConnectionString>
        </ConnectionStrings>
        """;

    private static IDictionary<string, string> AsKeyValues(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return builder.Keys.Cast<string>()
            .ToDictionary(k => k, k => builder[k]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parses_mssql_connection_with_all_parameters()
    {
        var registry = ConnectionRegistry.Parse(MssqlStageXml);
        var conn = Assert.Single(registry.Connections);

        Assert.Equal("mssql|stage", conn.Id);
        Assert.Equal(DbType.Mssql, conn.DbType);

        // MSSQL has no 'Port' keyword: the port is folded into the data source as 'host,port'.
        var kv = AsKeyValues(conn.ConnectionString);
        Assert.Equal("db01,1433", kv["Data Source"]);
        Assert.Equal("stage", kv["Initial Catalog"]);
        Assert.DoesNotContain("Port=", conn.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parses_pgsql_connection_with_all_parameters()
    {
        var registry = ConnectionRegistry.Parse(PgsqlStageXml);
        var conn = Assert.Single(registry.Connections);

        Assert.Equal("pgsql|warehouse", conn.Id);
        Assert.Equal(DbType.Pgsql, conn.DbType);

        var kv = AsKeyValues(conn.ConnectionString);
        Assert.Equal("db02", kv["Host"]);
        Assert.Equal("5432", kv["Port"]);
    }

    [Fact]
    public void Numeric_parameter_is_serialized_without_quotes()
    {
        // A numeric value never needs quoting; a string value with a special char does.
        var xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="x">
                <Parameters>
                  <Parameter key="Server"           value="db01"   type="String"  />
                  <Parameter key="Port"             value="1433"   type="Numeric" />
                  <Parameter key="Application Name" value="my;app" type="String"  />
                </Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        var registry = ConnectionRegistry.Parse(xml);
        var conn = Assert.Single(registry.Connections);

        // Port is folded into the data source (no bare 'Port=' keyword for MSSQL).
        var kv = AsKeyValues(conn.ConnectionString);
        Assert.Equal("db01,1433", kv["Data Source"]);
        // String containing ';' must be quoted by the builder so the value round-trips.
        Assert.Equal("my;app", kv["Application Name"]);
    }

    [Fact]
    public void Numeric_parameter_with_non_numeric_value_throws()
    {
        var xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="x">
                <Parameters>
                  <Parameter key="Port" value="notanumber" type="Numeric" />
                </Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        Assert.Throws<FormatException>(() => ConnectionRegistry.Parse(xml));
    }

    [Fact]
    public void Deprecated_type_attribute_on_ConnectionString_is_ignored_with_warning()
    {
        var xml = """
            <ConnectionStrings>
              <ConnectionString type="PUMP" targetSystem="MSSQL" name="stage">
                <Parameters>
                  <Parameter key="Server" value="db01" type="String" />
                </Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        var registry = ConnectionRegistry.Parse(xml);

        // Not an error: connection parses normally.
        var conn = Assert.Single(registry.Connections);
        Assert.Equal("mssql|stage", conn.Id);

        // Optional warning is emitted.
        Assert.Contains(registry.Warnings,
            w => w.Message.Contains("Veraltetes Attribut type ignoriert", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_ids_warn_and_throw_on_resolve()
    {
        var xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="stage">
                <Parameters><Parameter key="Server" value="db01" type="String" /></Parameters>
              </ConnectionString>
              <ConnectionString targetSystem="MSSQL" name="STAGE">
                <Parameters><Parameter key="Server" value="db99" type="String" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        var registry = ConnectionRegistry.Parse(xml);

        // Warning is emitted at parse time.
        Assert.Contains(registry.Warnings,
            w => w.Message.Contains("Doppelte", StringComparison.OrdinalIgnoreCase));

        // Hard error on resolution (no silent last-wins).
        var allEx = Assert.Throws<InvalidOperationException>(() => registry.ResolveAll());
        Assert.Contains("mssql|stage", allEx.Message, StringComparison.Ordinal);

        var oneEx = Assert.Throws<InvalidOperationException>(() => registry.TryResolve("MSSQL|stage"));
        Assert.Contains("mssql|stage", oneEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_returns_null_for_unknown_id()
    {
        var registry = ConnectionRegistry.Parse(MssqlStageXml);
        Assert.Null(registry.TryResolve("pgsql|nope"));
    }

    [Fact]
    public void TryResolve_is_case_insensitive()
    {
        var registry = ConnectionRegistry.Parse(MssqlStageXml);
        Assert.NotNull(registry.TryResolve("MSSQL|STAGE"));
    }

    [Fact]
    public void Malformed_xml_throws()
    {
        Assert.Throws<System.Xml.XmlException>(() => ConnectionRegistry.Parse("<ConnectionStrings><oops"));
    }

    [Fact]
    public void Missing_required_name_attribute_throws()
    {
        var xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL">
                <Parameters><Parameter key="Server" value="db01" type="String" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;
        Assert.Throws<FormatException>(() => ConnectionRegistry.Parse(xml));
    }

    [Fact]
    public void Unknown_target_system_throws()
    {
        var xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="ORACLE" name="x">
                <Parameters><Parameter key="Server" value="db01" type="String" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;
        Assert.Throws<FormatException>(() => ConnectionRegistry.Parse(xml));
    }

    [Fact]
    public void Missing_Parameters_yields_empty_connection_string()
    {
        var xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="bare" />
            </ConnectionStrings>
            """;

        var registry = ConnectionRegistry.Parse(xml);
        var conn = Assert.Single(registry.Connections);
        Assert.Equal(string.Empty, conn.ConnectionString);
    }

    [Fact]
    public void Namespaced_connection_elements_throw_clear_format_exception()
    {
        var xml = """
            <ConnectionStrings xmlns="urn:hydradata:connections">
              <ConnectionString targetSystem="MSSQL" name="stage">
                <Parameters>
                  <Parameter key="Server" value="db01" type="String" />
                </Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        var ex = Assert.Throws<FormatException>(() => ConnectionRegistry.Parse(xml));

        Assert.Contains("ConnectionString", ex.Message, StringComparison.Ordinal);
        Assert.Contains("urn:hydradata:connections", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameter_missing_key_throws()
    {
        var xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="x">
                <Parameters><Parameter value="db01" type="String" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;
        Assert.Throws<FormatException>(() => ConnectionRegistry.Parse(xml));
    }

    [Fact]
    public void Mssql_connection_string_folds_port_into_data_source_and_has_no_bare_port_keyword()
    {
        var registry = ConnectionRegistry.Parse(MssqlStageXml);
        var conn = Assert.Single(registry.Connections);

        // Provider-specific MSSQL builder: Server + Port collapse to 'Data Source=host,port'.
        Assert.Contains("db01,1433", conn.ConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain("Port=", conn.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pgsql_connection_string_keeps_host_and_port()
    {
        var registry = ConnectionRegistry.Parse(PgsqlStageXml);
        var conn = Assert.Single(registry.Connections);

        var kv = AsKeyValues(conn.ConnectionString);
        Assert.Equal("db02", kv["Host"]);
        Assert.Equal("5432", kv["Port"]);
    }

    [Fact]
    public void Unknown_keyword_surfaces_clear_format_exception_naming_key_and_provider()
    {
        var xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="x">
                <Parameters><Parameter key="NotARealKeyword" value="oops" type="String" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        var ex = Assert.Throws<FormatException>(() => ConnectionRegistry.Parse(xml));
        Assert.Contains("NotARealKeyword", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MSSQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_reads_from_a_real_temp_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"connections_{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, MssqlStageXml);
            var registry = ConnectionRegistry.Load(path);
            var conn = Assert.Single(registry.Connections);
            Assert.Equal("mssql|stage", conn.Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_error_message_does_not_leak_parameter_values()
    {
        // A missing 'name' attribute with a secret-bearing Parameter must not echo the value.
        var xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL">
                <Parameters><Parameter key="Password" value="s3cr3t" type="String" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        var ex = Assert.Throws<FormatException>(() => ConnectionRegistry.Parse(xml));
        Assert.DoesNotContain("s3cr3t", ex.Message, StringComparison.Ordinal);
        Assert.Contains("name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deprecation_warning_names_connection_and_target_system()
    {
        var xml = """
            <ConnectionStrings>
              <ConnectionString type="PUMP" targetSystem="MSSQL" name="stage">
                <Parameters><Parameter key="Server" value="db01" type="String" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        var registry = ConnectionRegistry.Parse(xml);
        var warning = Assert.Single(
            registry.Warnings,
            w => w.Message.Contains("Veraltetes Attribut type", StringComparison.Ordinal));
        Assert.Contains("stage", warning.Message, StringComparison.Ordinal);
        Assert.Contains("MSSQL", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_type_attribute_defaults_to_string_serialization()
    {
        var xml = """
            <ConnectionStrings>
              <ConnectionString targetSystem="MSSQL" name="x">
                <Parameters><Parameter key="Server" value="db01" /></Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        var registry = ConnectionRegistry.Parse(xml);
        var conn = Assert.Single(registry.Connections);
        Assert.Equal("db01", AsKeyValues(conn.ConnectionString)["Data Source"]);
    }
}
