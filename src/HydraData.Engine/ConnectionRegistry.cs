// Copyright (c) 2026 crossVault GmbH.

using System.Data.Common;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HydraData.Engine;

/// <summary>
/// Parses <c>connections.xml</c> into <see cref="ConnectionInfo"/> entries and resolves them by id.
/// Builds the provider-specific connection string from the <c>Parameters</c> element, ignores the
/// deprecated <c>type</c> attribute on <c>&lt;ConnectionString&gt;</c> (optionally warning), and treats
/// duplicate ids (<c>targetSystem|name</c>, case-insensitive) as a warning at parse time plus a hard
/// error on resolution — never silent last-wins.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly IReadOnlyDictionary<string, ConnectionInfo> _byId;
    private readonly IReadOnlySet<string> _duplicateIds;
    private readonly IReadOnlyList<ConnectionInfo> _declarationOrder;

    private ConnectionRegistry(
        IReadOnlyDictionary<string, ConnectionInfo> byId,
        IReadOnlySet<string> duplicateIds,
        IReadOnlyList<ConnectionInfo> declarationOrder,
        IReadOnlyList<ConnectionRegistryWarning> warnings)
    {
        _byId = byId;
        _duplicateIds = duplicateIds;
        _declarationOrder = declarationOrder;
        Warnings = warnings;
    }

    /// <summary>Non-fatal diagnostics gathered while parsing (deprecated attribute, duplicate ids).</summary>
    public IReadOnlyList<ConnectionRegistryWarning> Warnings { get; }

    /// <summary>
    /// All uniquely-resolvable connections, in declaration order (first occurrence of each id).
    /// The first entry is the <c>Default</c> connection; the rest are <c>Extern</c>.
    /// </summary>
    public IReadOnlyList<ConnectionInfo> Connections => _declarationOrder;

    /// <summary>Parses a <c>connections.xml</c> document from a string.</summary>
    /// <param name="xml">The XML content.</param>
    /// <param name="logger">
    /// Optional diagnostic logger. Parse warnings are logged at Warning; secrets/connection strings are
    /// never logged. Defaults to <see cref="NullLogger.Instance"/>.
    /// </param>
    /// <returns>A populated registry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="xml"/> is null.</exception>
    /// <exception cref="System.Xml.XmlException">The XML is malformed.</exception>
    /// <exception cref="FormatException">A required attribute is missing or a target system is unknown.</exception>
    public static ConnectionRegistry Parse(string xml, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return ParseDocument(XDocument.Parse(xml), logger ?? NullLogger.Instance);
    }

    /// <summary>Loads and parses a <c>connections.xml</c> file from disk.</summary>
    /// <param name="path">Path to the XML file.</param>
    /// <param name="logger">
    /// Optional diagnostic logger. Parse warnings are logged at Warning; secrets/connection strings are
    /// never logged. Defaults to <see cref="NullLogger.Instance"/>.
    /// </param>
    /// <returns>A populated registry.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or whitespace.</exception>
    /// <exception cref="System.Xml.XmlException">The XML is malformed.</exception>
    /// <exception cref="FormatException">A required attribute is missing or a target system is unknown.</exception>
    public static ConnectionRegistry Load(string path, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseDocument(XDocument.Load(path), logger ?? NullLogger.Instance);
    }

    private static ConnectionRegistry ParseDocument(XDocument doc, ILogger logger)
    {
        var warnings = new List<ConnectionRegistryWarning>();
        var byId = new Dictionary<string, ConnectionInfo>(StringComparer.OrdinalIgnoreCase);
        var duplicateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declarationOrder = new List<ConnectionInfo>();

        var elements = doc.Descendants("ConnectionString");
        foreach (var element in elements)
        {
            var name = RequireAttribute(element, "name");
            var targetSystem = RequireAttribute(element, "targetSystem");
            var dbType = ParseTargetSystem(targetSystem);

            // Phase D: a leftover `type` attribute on <ConnectionString> is tolerated and ignored.
            if (element.Attribute("type") is not null)
                warnings.Add(new ConnectionRegistryWarning(
                    $"Veraltetes Attribut type ignoriert (Connection name='{name}', targetSystem='{targetSystem}')."));

            var connectionString = BuildConnectionString(element, dbType);
            var info = new ConnectionInfo(name, dbType, connectionString);

            if (byId.TryAdd(info.Id, info))
            {
                declarationOrder.Add(info);
            }
            else
            {
                duplicateIds.Add(info.Id);
                warnings.Add(new ConnectionRegistryWarning(
                    $"Doppelte Connection-Id '{info.Id}' (targetSystem|name, case-insensitiv); " +
                    "Auflösung wirft einen Fehler."));
            }
        }

        // Surface parse warnings (deprecated attribute, duplicate ids) to the operator. The warning text is
        // engine-generated and names only the connection id (targetSystem|name) — never a connection string.
        foreach (var warning in warnings)
            logger.LogWarning("connections.xml: {Warning}", warning.Message);

        return new ConnectionRegistry(byId, duplicateIds, declarationOrder, warnings);
    }

    /// <summary>Returns all parsed connections; throws if any duplicate id was detected.</summary>
    /// <remarks>Used by callers that must materialise the full set (no silent last-wins).</remarks>
    /// <exception cref="InvalidOperationException">One or more duplicate ids were detected.</exception>
    public IReadOnlyList<ConnectionInfo> ResolveAll()
    {
        if (_duplicateIds.Count > 0)
            throw new InvalidOperationException(
                $"Connections enthalten doppelte Ids: [{string.Join(", ", _duplicateIds)}]. " +
                "Mehrdeutige Connections können nicht aufgelöst werden.");

        return Connections;
    }

    /// <summary>
    /// Resolves a connection by its canonical id (<c>targetSystem|name</c>, case-insensitive),
    /// returning <see langword="null"/> when no entry matches.
    /// </summary>
    /// <param name="id">The canonical id, case-insensitive.</param>
    /// <returns>The matching connection, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The id was declared more than once.</exception>
    public ConnectionInfo? TryResolve(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        // The id dictionary and duplicate set are case-insensitive; lookup needs no lowercasing.
        // Ids are stored canonically lowercased (see ConnectionInfo.MakeId); report that form.
        if (_duplicateIds.Contains(id))
            throw new InvalidOperationException(
                $"Connection-Id '{id.ToLowerInvariant()}' ist mehrfach deklariert; " +
                "keine eindeutige Auflösung möglich.");

        return _byId.GetValueOrDefault(id);
    }

    // ── parsing helpers ──────────────────────────────────────────────────────

    private static string RequireAttribute(XElement element, string attributeName)
    {
        var value = element.Attribute(attributeName)?.Value;
        if (string.IsNullOrWhiteSpace(value))
            // Never interpolate the element subtree or attribute values here: it can contain
            // secrets (e.g. a Password parameter). Name only the element and the missing attribute.
            throw new FormatException(
                $"<{element.Name.LocalName}>: Pflichtattribut '{attributeName}' fehlt oder ist leer.");
        return value;
    }

    private static DbType ParseTargetSystem(string targetSystem) =>
        targetSystem.Trim().ToUpperInvariant() switch
        {
            "MSSQL" => DbType.Mssql,
            "PGSQL" => DbType.Pgsql,
            _ => throw new FormatException(
                $"Unbekanntes targetSystem '{targetSystem}'. Erwartet: MSSQL oder PGSQL."),
        };

    private static string BuildConnectionString(XElement connectionElement, DbType dbType)
    {
        // Collect (key, value) pairs in declaration order. Parameter.type (Numeric|String) only
        // governs how the value text is serialised, not which provider.
        var parameters = new List<(string Key, object Value)>();
        foreach (var parameter in connectionElement.Elements("Parameters").Elements("Parameter"))
        {
            var key = parameter.Attribute("key")?.Value;
            if (string.IsNullOrWhiteSpace(key))
                // Do not interpolate the <Parameter> subtree (its value may be a secret).
                throw new FormatException(
                    "<Parameter>: Pflichtattribut 'key' fehlt oder ist leer.");

            var value = parameter.Attribute("value")?.Value ?? string.Empty;
            var type = parameter.Attribute("type")?.Value;
            parameters.Add((key, SerializeValue(key, value, type)));
        }

        // Route through the PROVIDER-SPECIFIC builder so keywords are validated at parse time and
        // provider quirks (MSSQL Server=host,port) are handled correctly.
        return dbType switch
        {
            DbType.Mssql => BuildMssqlConnectionString(parameters),
            DbType.Pgsql => BuildPgsqlConnectionString(parameters),
            _ => throw new ArgumentOutOfRangeException(nameof(dbType), dbType, "Unknown DbType."),
        };
    }

    private static string BuildMssqlConnectionString(IReadOnlyList<(string Key, object Value)> parameters)
    {
        // MSSQL has no 'Port' keyword: it is folded into Data Source as 'Server=<host>,<port>'.
        var builder = new SqlConnectionStringBuilder();
        string? server = null;
        object? port = null;

        foreach (var (key, value) in parameters)
        {
            if (string.Equals(key, "Port", StringComparison.OrdinalIgnoreCase))
            {
                port = value;
                continue;
            }

            if (string.Equals(key, "Server", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Data Source", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Address", StringComparison.OrdinalIgnoreCase))
            {
                server = value.ToString();
                continue;
            }

            ApplyKey(builder, "MSSQL", key, value);
        }

        if (port is not null)
        {
            if (string.IsNullOrEmpty(server))
                throw new FormatException(
                    "MSSQL: Parameter 'Port' wurde angegeben, aber kein 'Server'/'Data Source'.");
            // SqlClient addresses a non-default port as 'host,port' in Data Source.
            builder.DataSource = $"{server},{Convert.ToString(port, CultureInfo.InvariantCulture)}";
        }
        else if (server is not null)
        {
            builder.DataSource = server;
        }

        return builder.ConnectionString;
    }

    private static string BuildPgsqlConnectionString(IReadOnlyList<(string Key, object Value)> parameters)
    {
        // Npgsql accepts Host/Port/Database/Username/Password directly; just validate keywords.
        var builder = new NpgsqlConnectionStringBuilder();
        foreach (var (key, value) in parameters)
            ApplyKey(builder, "PGSQL", key, value);

        return builder.ConnectionString;
    }

    private static void ApplyKey(DbConnectionStringBuilder builder, string provider, string key, object value)
    {
        try
        {
            builder[key] = value;
        }
        catch (ArgumentException ex)
        {
            // An unknown/invalid keyword surfaces as a clear FormatException naming key + provider.
            throw new FormatException(
                $"{provider}: Ungültiger Connection-String-Schlüssel '{key}'.", ex);
        }
    }

    private static object SerializeValue(string key, string value, string? type)
    {
        // Default (no/unknown type) is treated as String. Only "Numeric" forces numeric parsing.
        if (string.Equals(type, "Numeric", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return l;
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                return d;

            throw new FormatException(
                $"Parameter '{key}' ist als Numeric deklariert, der Wert '{value}' ist aber keine Zahl.");
        }

        return value;
    }
}
