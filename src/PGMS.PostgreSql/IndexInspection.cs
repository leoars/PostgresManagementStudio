using Npgsql;

namespace PGMS.PostgreSql;

public sealed record InspectedIndex(uint TableOid, TableIndexInfo Index)
{
    public string Name => Index.Name;
    public string Summary => $"{Name} — {Index.Method}; unique={Index.Unique}; valid={Index.Valid}; ready={Index.Ready}";
}

public sealed class IndexInspectionService(string connectionString)
{
    public async Task<IReadOnlyList<InspectedIndex>> InspectAsync(string schema, string table, CancellationToken token = default)
    {
        ObjectSql.Relation(schema, table);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await ConstraintInspection.RequireTableAsync(connection, transaction, schema, table, token).ConfigureAwait(false);
        const string sql = """
            SELECT i.indrelid, i.indexrelid, c.relname, am.amname, i.indisunique, i.indisprimary,
                i.indisvalid, i.indisready, pg_catalog.pg_get_indexdef(i.indexrelid)
            FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid
            JOIN pg_catalog.pg_class t ON t.oid=i.indrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace
            JOIN pg_catalog.pg_am am ON am.oid=c.relam
            WHERE n.nspname=@schema AND t.relname=@table ORDER BY c.relname, i.indexrelid
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<InspectedIndex>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            result.Add(new(reader.GetFieldValue<uint>(0), new(reader.GetFieldValue<uint>(1), reader.GetString(2), reader.GetString(3),
                reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetString(8))));
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return result;
    }
}
