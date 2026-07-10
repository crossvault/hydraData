// Copyright (c) 2026 crossVault GmbH.

using System.Data;
using Microsoft.Data.SqlClient;

namespace HydraData.Engine;

/// <summary>
/// MSSQL <see cref="IDbExecutor"/>. <see cref="BulkInsert"/> uses <see cref="SqlBulkCopy"/>
///.
/// </summary>
internal sealed class MssqlExecutor : DapperExecutor
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;

    /// <summary>Initializes the executor over an open <see cref="SqlConnection"/> and transaction.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The ambient transaction.</param>
    /// <param name="commandTimeoutSeconds">
    /// Optional command timeout (seconds) applied to every Dapper query and to the
    /// <see cref="SqlBulkCopy"/>; <see langword="null"/> leaves the provider default.
    /// </param>
    public MssqlExecutor(
        SqlConnection connection, SqlTransaction transaction, int? commandTimeoutSeconds = null)
        : base(connection, transaction, commandTimeoutSeconds)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <inheritdoc />
    public override void BulkInsert(string table, IEnumerable<IDictionary<string, object?>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(rows);

        using var enumerator = rows.GetEnumerator();
        if (!enumerator.MoveNext()) return; // empty row set: nothing to do.

        var first = enumerator.Current;
        var columns = first.Keys.ToList();

        // Buffer the rows: column CLR types must be inferred from the first NON-NULL value of each
        // column (not just row 0), so a column that is null in row 0 but a real Guid/DateTimeOffset/
        // byte[] later is still typed correctly for SqlBulkCopy. Materialising is required because the
        // DataTable columns must be typed before any row is added.
        var bufferedRows = new List<IDictionary<string, object?>> { first };
        while (enumerator.MoveNext())
            bufferedRows.Add(enumerator.Current);

        using var dataTable = new DataTable();
        var columnTypes = InferColumnTypes(columns, bufferedRows);
        foreach (var column in columns)
            dataTable.Columns.Add(column, columnTypes[column]);

        foreach (var bufferedRow in bufferedRows)
            AddRow(dataTable, columns, bufferedRow);

        using var bulk = new SqlBulkCopy(_connection, SqlBulkCopyOptions.KeepNulls, _transaction)
        {
            DestinationTableName = table,
        };
        if (CommandTimeoutSeconds is { } timeout)
            bulk.BulkCopyTimeout = timeout;
        foreach (var column in columns)
            bulk.ColumnMappings.Add(column, column);

        bulk.WriteToServer(dataTable);
    }

    // Single pass over all buffered rows: for each column, capture the CLR type of the first non-null
    // value encountered. A column that is null in every row falls back to typeof(object) (harmless —
    // the column will contain only DBNull). One pass instead of one pass-per-column keeps this O(rows)
    // rather than O(rows × cols) while preserving identical results.
    private static Dictionary<string, Type> InferColumnTypes(
        List<string> columns, List<IDictionary<string, object?>> rows)
    {
        var types = new Dictionary<string, Type>(columns.Count, StringComparer.Ordinal);
        // Track which columns still need a type (those for which we have not yet seen a non-null value).
        var pending = new HashSet<string>(columns, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (pending.Count == 0) break; // All columns typed; no need to inspect further rows.
            foreach (var col in pending.ToList()) // snapshot to allow mutation inside the loop
            {
                if (row.TryGetValue(col, out var v) && v is not null and not DBNull)
                {
                    types[col] = v.GetType();
                    pending.Remove(col);
                }
            }
        }

        // Remaining unresolved columns (all-null) fall back to typeof(object).
        foreach (var col in pending)
            types[col] = typeof(object);

        return types;
    }

    private static void AddRow(
        DataTable dataTable, List<string> columns, IDictionary<string, object?> row)
    {
        EnsureSameKeys(columns, row);
        var values = new object[columns.Count];
        for (var i = 0; i < columns.Count; i++)
            values[i] = row.TryGetValue(columns[i], out var v) && v is not null ? v : DBNull.Value;

        dataTable.Rows.Add(values);
    }

    private static void EnsureSameKeys(List<string> columns, IDictionary<string, object?> row)
    {
        if (row.Count != columns.Count || columns.Any(c => !row.ContainsKey(c)))
            throw new InvalidOperationException(
                "BulkInsert: every row must have the same columns as the first row " +
                $"(expected: [{string.Join(", ", columns)}], received: [{string.Join(", ", row.Keys)}]).");
    }
}
