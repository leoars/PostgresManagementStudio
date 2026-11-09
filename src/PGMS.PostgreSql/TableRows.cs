using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

/// <summary>A column of Select Top / Edit Top. TypeName has no modifier (character varying, numeric), so assigning a cast value still checks the
/// column's length and precision. ReadOnly: identity GENERATED ALWAYS or generated column.</summary>
public sealed record TableRowColumn(string Name, string TypeName, bool NotNull, bool InKey, bool ReadOnly);

/// <summary>Rows read by Edit Top: every value as PostgreSQL text, null for NULL.</summary>
public sealed record TableRowsSnapshot(string Schema, string Name, string RelationKind, IReadOnlyList<TableRowColumn> Columns, IReadOnlyList<string?[]> Rows, int Top)
{
    /// <summary>With a primary key, rows are found by it; otherwise by every column, as SSMS does for tables without a key.</summary>
    public bool HasKey => Columns.Any(c => c.InKey);
    public string QualifiedName => SqlIdentifier.Quote(Schema) + "." + SqlIdentifier.Quote(Name);
}

/// <summary>The row did not change exactly one row; nothing was committed.</summary>
public sealed class TableRowsException(string message) : Exception(message);

/// <summary>Select Top n Rows and Edit Top n Rows of Object Explorer (M1044). Every change runs in its own transaction and is rolled back unless it
/// touches exactly one row, so a row without a key that has an identical twin is never changed twice (SSMS refuses it the same way).</summary>
public sealed class TableRowsService(string connectionString)
{
    /// <summary>SSMS 22.11 writes <c>SELECT TOP (1000) [c1]\n      ,[c2]\n  FROM [db].[schema].[table]</c>; PostgreSQL has LIMIT and no cross-database
    /// names, so the database is left out and LIMIT closes the statement.</summary>
    public static string SelectTopSql(string schema, string table, IReadOnlyList<string> columns, int top)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        var sql = new StringBuilder("SELECT ");
        if (columns.Count == 0) sql.Append('*');
        for (int i = 0; i < columns.Count; i++) sql.Append(i == 0 ? "" : "\r\n      ,").Append(SqlIdentifier.Quote(columns[i]));
        sql.Append("\r\n  FROM ").Append(SqlIdentifier.Quote(schema)).Append('.').Append(SqlIdentifier.Quote(table));
        sql.Append("\r\n LIMIT ").Append(top).Append("\r\n");
        return sql.ToString();
    }

    public async Task<(string Kind, IReadOnlyList<TableRowColumn> Columns)> ReadColumnsAsync(string schema, string table, CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        return await ReadColumnsAsync(connection, schema, table, token);
    }

    private static async Task<(string Kind, IReadOnlyList<TableRowColumn> Columns)> ReadColumnsAsync(NpgsqlConnection connection, string schema, string table, CancellationToken token)
    {
        const string sql = """
            select c.relkind::text, a.attname, format_type(a.atttypid, null), a.attnotnull,
                   coalesce(a.attnum = any(pk.conkey), false), a.attidentity = 'a' or a.attgenerated <> ''
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            join pg_attribute a on a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped
            left join pg_constraint pk on pk.conrelid = c.oid and pk.contype = 'p'
            where n.nspname = $1 and c.relname = $2
            order by a.attnum
            """;
        await using var command = new NpgsqlCommand(sql, connection) { Parameters = { new() { Value = schema }, new() { Value = table } } };
        await using var reader = await command.ExecuteReaderAsync(token);
        string? kind = null; var columns = new List<TableRowColumn>();
        while (await reader.ReadAsync(token))
        {
            kind = reader.GetString(0);
            columns.Add(new(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        }
        if (kind is null) throw new TableRowsException($"{schema}.{table} was not found. Refresh Object Explorer.");
        return (kind, columns);
    }

    public async Task<TableRowsSnapshot> LoadAsync(string schema, string table, int top, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        var (kind, columns) = await ReadColumnsAsync(connection, schema, table, token);
        var rows = new List<string?[]>();
        if (columns.Count > 0)
        {
            string list = string.Join(", ", columns.Select(c => SqlIdentifier.Quote(c.Name) + "::text"));
            await using var command = new NpgsqlCommand($"SELECT {list} FROM {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(table)} LIMIT {top}", connection);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var row = new string?[columns.Count];
                for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetString(i);
                rows.Add(row);
            }
        }
        return new(schema, table, kind, columns, rows, top);
    }

    /// <summary>The SQL shown for the SSMS SQL pane: SELECT TOP (200) ... becomes ... LIMIT 200.</summary>
    public static string EditTopSql(TableRowsSnapshot snapshot) =>
        "SELECT " + string.Join(", ", snapshot.Columns.Select(c => SqlIdentifier.Quote(c.Name))) + " FROM " + snapshot.QualifiedName + " LIMIT " + snapshot.Top;

    private static NpgsqlParameter Text(string? value) => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };
    private static string Returning(TableRowsSnapshot s) => " RETURNING " + string.Join(", ", s.Columns.Select(c => SqlIdentifier.Quote(c.Name) + "::text"));

    /// <summary>WHERE that finds the row by its original values: the key columns, or every column compared as text (works for types without equality).</summary>
    private static string Where(TableRowsSnapshot s, string?[] original, NpgsqlCommand command)
    {
        var parts = new List<string>();
        for (int i = 0; i < s.Columns.Count; i++)
        {
            var column = s.Columns[i];
            if (s.HasKey && !column.InKey) continue;
            command.Parameters.Add(Text(original[i]));
            string p = "$" + command.Parameters.Count;
            parts.Add(s.HasKey ? $"{SqlIdentifier.Quote(column.Name)} = {p}::{column.TypeName}" : $"{SqlIdentifier.Quote(column.Name)}::text IS NOT DISTINCT FROM {p}");
        }
        return " WHERE " + string.Join(" AND ", parts);
    }

    private async Task<T> InTransactionAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var result = await work(connection, transaction);
        await transaction.CommitAsync(token);
        return result;
    }

    private static async Task<string?[]> SingleRowAsync(NpgsqlCommand command, int width, CancellationToken token)
    {
        var rows = new List<string?[]>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var row = new string?[width];
                for (int i = 0; i < width; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetString(i);
                rows.Add(row);
            }
        if (rows.Count != 1) throw new TableRowsException($"The row values updated or deleted either do not make the row unique or they alter multiple rows ({rows.Count} rows).");
        return rows[0];
    }

    /// <summary>Changes the given columns of one row and returns the row as stored (defaults, triggers and casts applied).</summary>
    public Task<string?[]> UpdateAsync(TableRowsSnapshot snapshot, string?[] original, IReadOnlyDictionary<int, string?> changes, CancellationToken token = default)
    {
        if (changes.Count == 0) return Task.FromResult(original);
        return InTransactionAsync(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
            var sets = new List<string>();
            foreach (var (index, value) in changes.OrderBy(c => c.Key))
            {
                var column = snapshot.Columns[index];
                if (column.ReadOnly) throw new TableRowsException($"Column {column.Name} cannot be changed: it is an identity GENERATED ALWAYS or a generated column.");
                command.Parameters.Add(Text(value));
                sets.Add($"{SqlIdentifier.Quote(column.Name)} = ${command.Parameters.Count}::{column.TypeName}");
            }
            command.CommandText = $"UPDATE {snapshot.QualifiedName} SET {string.Join(", ", sets)}" + Where(snapshot, original, command) + Returning(snapshot);
            return await SingleRowAsync(command, snapshot.Columns.Count, token);
        }, token);
    }

    /// <summary>Inserts a row with the given columns (the others take their defaults) and returns it as stored.</summary>
    public Task<string?[]> InsertAsync(TableRowsSnapshot snapshot, IReadOnlyDictionary<int, string?> values, CancellationToken token = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
            var names = new List<string>(); var parameters = new List<string>();
            foreach (var (index, value) in values.OrderBy(v => v.Key))
            {
                var column = snapshot.Columns[index];
                if (column.ReadOnly) continue;
                command.Parameters.Add(Text(value));
                names.Add(SqlIdentifier.Quote(column.Name)); parameters.Add($"${command.Parameters.Count}::{column.TypeName}");
            }
            command.CommandText = $"INSERT INTO {snapshot.QualifiedName}" + (names.Count == 0 ? " DEFAULT VALUES" : $" ({string.Join(", ", names)}) VALUES ({string.Join(", ", parameters)})") + Returning(snapshot);
            return await SingleRowAsync(command, snapshot.Columns.Count, token);
        }, token);

    /// <summary>Deletes the rows, all or none: each must match exactly one row.</summary>
    public Task DeleteAsync(TableRowsSnapshot snapshot, IReadOnlyList<string?[]> rows, CancellationToken token = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            foreach (var row in rows)
            {
                await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
                command.CommandText = $"DELETE FROM {snapshot.QualifiedName}" + Where(snapshot, row, command);
                int count = await command.ExecuteNonQueryAsync(token);
                if (count != 1) throw new TableRowsException($"The row values updated or deleted either do not make the row unique or they alter multiple rows ({count} rows).");
            }
            return true;
        }, token);
}
