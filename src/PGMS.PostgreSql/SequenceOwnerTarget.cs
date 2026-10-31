using Npgsql;

namespace PGMS.PostgreSql;

public sealed record SequenceOwnerTarget(uint TableOid, string Schema, string Table, string Column,
    int ColumnNumber, uint OwnerOid, string Owner, string Kind, string Persistence, uint TypeOid, int TypeModifier, string Identity, string Generated);

public sealed class SequenceOwnerTargetService(string connectionString)
{
    public async Task<SequenceOwnerTarget> InspectAsync(string schema, string table, string column, CancellationToken token = default)
    {
        ObjectSql.Relation(schema, table); SqlIdentifier.Quote(column);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx))
            await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var result = await ReadAsync(connection, tx, schema, table, column, token).ConfigureAwait(false);
        await tx.CommitAsync(token).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<SequenceOwnerTarget>> ListColumnsAsync(string schema, string table, CancellationToken token = default)
    {
        ObjectSql.Relation(schema, table);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx))
            await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var rows = await ReadRowsAsync(connection, tx, schema, table, null, token).ConfigureAwait(false);
        await tx.CommitAsync(token).ConfigureAwait(false);
        return rows;
    }
    internal static async Task<SequenceOwnerTarget> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string schema, string table, string column, CancellationToken token)
    {
        var rows = await ReadRowsAsync(connection, tx, schema, table, column, token).ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : throw new InvalidOperationException("Destination column was not found. Inspect the exact table and column names.");
    }
    private static async Task<IReadOnlyList<SequenceOwnerTarget>> ReadRowsAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string schema, string table, string? column, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.oid, a.attnum::integer, c.relowner, pg_catalog.pg_get_userbyid(c.relowner),
                   c.relkind::text, c.relpersistence::text, a.atttypid, a.atttypmod, a.attidentity::text, a.attgenerated::text, a.attname
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid
            WHERE n.nspname=@schema AND c.relname=@table AND (@column IS NULL OR a.attname=@column)
              AND a.attnum>0 AND NOT a.attisdropped
              AND (@column IS NOT NULL OR (c.relkind='r' AND c.relpersistence='p' AND a.attidentity='' AND a.attgenerated=''))
            ORDER BY a.attnum
            """, connection, tx) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("table", table); command.Parameters.AddWithValue("column", NpgsqlTypes.NpgsqlDbType.Text, (object?)column ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<SequenceOwnerTarget>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            rows.Add(new(reader.GetFieldValue<uint>(0), schema, table, reader.GetString(10), reader.GetInt32(1), reader.GetFieldValue<uint>(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetFieldValue<uint>(6), reader.GetInt32(7), reader.GetString(8), reader.GetString(9)));
        return rows;
    }
}
