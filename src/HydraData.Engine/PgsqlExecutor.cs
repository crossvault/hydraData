// Copyright (c) 2026 crossVault GmbH.

using Npgsql;
using NpgsqlTypes;

namespace HydraData.Engine;

/// <summary>
/// PGSQL <see cref="IDbExecutor"/>. <see cref="BulkInsert"/> uses Npgsql binary <c>COPY</c>:
/// <c>null</c>/<see cref="DBNull"/> map to <c>WriteNull()</c>, everything else to the typed
/// <c>Write(value, NpgsqlDbType)</c> overload with the <see cref="NpgsqlDbType"/> chosen explicitly
/// from the runtime CLR type. The explicit type avoids
/// Npgsql inferring an OID from a boxed <see cref="object"/> (a known footgun for some types).
/// </summary>
internal sealed class PgsqlExecutor : DapperExecutor
{
    private readonly NpgsqlConnection _connection;

    /// <summary>Initializes the executor over an open <see cref="NpgsqlConnection"/> and transaction.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The ambient transaction.</param>
    /// <param name="commandTimeoutSeconds">
    /// Optional command timeout (seconds) applied to every Dapper query; <see langword="null"/> leaves
    /// the provider default.
    /// </param>
    public PgsqlExecutor(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int? commandTimeoutSeconds = null)
        : base(connection, transaction, commandTimeoutSeconds) => _connection = connection;

    /// <inheritdoc />
    public override void BulkInsert(string table, IEnumerable<IDictionary<string, object?>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(rows);

        using var enumerator = rows.GetEnumerator();
        if (!enumerator.MoveNext()) return; // empty row set: nothing to do.

        var first = enumerator.Current;
        var columns = first.Keys.ToList();

        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var copyCommand = $"COPY {QuoteQualifiedIdentifier(table)} ({columnList}) FROM STDIN (FORMAT BINARY)";

        // The binary COPY is bounded by the server-side statement_timeout set by DbSlot once after Open.
        // NpgsqlBinaryImporter has no per-importer client timeout, and the client-side CommandTimeout
        // (connection string) does NOT bound COPY — only plain queries. The session-level SET
        // statement_timeout (ms) is the reliable, PostgreSQL-native mechanism that aborts a blocked COPY
        // with error 57014 ("canceling statement due to statement timeout").
        using var writer = _connection.BeginBinaryImport(copyCommand);

        WriteRow(writer, columns, first);
        while (enumerator.MoveNext())
            WriteRow(writer, columns, enumerator.Current);

        writer.Complete();
    }

    /// <summary>Double-quotes a single PostgreSQL identifier, escaping embedded quotes.</summary>
    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Quotes a possibly schema-qualified identifier (<c>schema.table</c>) by quoting each dotted
    /// part, so mixed-case/space identifiers survive (PostgreSQL lowercases unquoted identifiers).
    /// </summary>
    private static string QuoteQualifiedIdentifier(string identifier) =>
        string.Join('.', identifier.Split('.').Select(QuoteIdentifier));

    private static void WriteRow(
        NpgsqlBinaryImporter writer, List<string> columns, IDictionary<string, object?> row)
    {
        EnsureSameKeys(columns, row);
        writer.StartRow();
        foreach (var column in columns)
        {
            var value = row.TryGetValue(column, out var v) ? v : null;
            if (value is null or DBNull)
            {
                writer.WriteNull();
                continue;
            }

            var (npgsqlType, toWrite) = NpgsqlTypeOf(column, value);
            writer.Write(toWrite, npgsqlType);
        }
    }

    /// <summary>
    /// Maps a non-null CLR value to its <see cref="NpgsqlDbType"/> and the value to write for the
    /// typed binary-COPY write, covering the supported ETL type matrix. A type outside the matrix
    /// throws a clear <see cref="InvalidOperationException"/> naming the column and CLR type, rather
    /// than silently deferring to Npgsql's wire-type inference.
    /// </summary>
    /// <remarks>
    /// Date/time handling matches Npgsql's binary-COPY rules for the chosen <see cref="NpgsqlDbType"/>:
    /// <list type="bullet">
    /// <item>
    /// <see cref="DateTimeOffset"/> → <see cref="NpgsqlDbType.TimestampTz"/>, written as
    /// <see cref="DateTimeOffset.ToUniversalTime"/> (binary COPY only accepts offset 0 for
    /// <c>timestamptz</c>; storing the UTC instant is lossless).
    /// </item>
    /// <item>
    /// <see cref="DateTime"/> with <see cref="DateTimeKind.Utc"/> → <see cref="NpgsqlDbType.TimestampTz"/>
    /// (already UTC, written as-is).
    /// </item>
    /// <item>
    /// <see cref="DateTime"/> with <see cref="DateTimeKind.Local"/> → <see cref="NpgsqlDbType.TimestampTz"/>,
    /// written as its UTC equivalent.
    /// </item>
    /// <item>
    /// <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/> → <see cref="NpgsqlDbType.Timestamp"/>
    /// (wall-clock, no time zone).
    /// </item>
    /// </list>
    /// </remarks>
    private static (NpgsqlDbType Type, object Value) NpgsqlTypeOf(string column, object value) => value switch
    {
        int => (NpgsqlDbType.Integer, value),
        long => (NpgsqlDbType.Bigint, value),
        short => (NpgsqlDbType.Smallint, value),
        decimal => (NpgsqlDbType.Numeric, value),
        double => (NpgsqlDbType.Double, value),
        float => (NpgsqlDbType.Real, value),
        bool => (NpgsqlDbType.Boolean, value),
        Guid => (NpgsqlDbType.Uuid, value),
        string => (NpgsqlDbType.Text, value),
        DateOnly => (NpgsqlDbType.Date, value),
        TimeOnly => (NpgsqlDbType.Time, value),
        // timestamptz binary COPY only accepts offset 0; store the UTC instant (lossless).
        DateTimeOffset dto => (NpgsqlDbType.TimestampTz, dto.ToUniversalTime()),
        // Kind drives the chosen type so the written value matches what Npgsql expects.
        DateTime { Kind: DateTimeKind.Utc } => (NpgsqlDbType.TimestampTz, value),
        DateTime { Kind: DateTimeKind.Local } local => (NpgsqlDbType.TimestampTz, local.ToUniversalTime()),
        DateTime => (NpgsqlDbType.Timestamp, value), // Unspecified: wall-clock, no tz.
        byte[] => (NpgsqlDbType.Bytea, value),
        _ => throw new InvalidOperationException(
            $"BulkInsert: column '{column}' has unsupported CLR type '{value.GetType().Name}' " +
            "for the PostgreSQL binary COPY. Supported: int, long, short, decimal, double, float, " +
            "bool, Guid, string, DateOnly, TimeOnly, DateTimeOffset, DateTime, byte[]."),
    };

    private static void EnsureSameKeys(List<string> columns, IDictionary<string, object?> row)
    {
        if (row.Count != columns.Count || columns.Any(c => !row.ContainsKey(c)))
            throw new InvalidOperationException(
                "BulkInsert: every row must have the same columns as the first row " +
                $"(expected: [{string.Join(", ", columns)}], received: [{string.Join(", ", row.Keys)}]).");
    }
}
