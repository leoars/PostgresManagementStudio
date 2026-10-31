using System.Data;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record SequenceSnapshot(uint Oid, string Schema, string Name, string Owner, string DataType,
    long Start, long Increment, long Minimum, long Maximum, long Cache, bool Cycle, string? Comment, string? OwnedBy)
{
    public SequenceDependency? Dependency { get; init; }
}

public sealed record SequenceDependency(uint TableOid, int ColumnNumber, string Kind);

public sealed class SequenceService(string connectionString)
{
    public async Task<SequenceSnapshot> InspectAsync(string schema, string name, CancellationToken token = default)
    {
        ObjectSql.Relation(schema, name);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx))
            await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        const string sql = """
            SELECT c.oid, pg_catalog.pg_get_userbyid(c.relowner), pg_catalog.format_type(s.seqtypid, NULL),
                   s.seqstart, s.seqincrement, s.seqmin, s.seqmax, s.seqcache, s.seqcycle,
                   pg_catalog.obj_description(c.oid, 'pg_class'),
                   pg_catalog.quote_ident(tn.nspname)||'.'||pg_catalog.quote_ident(t.relname)||'.'||pg_catalog.quote_ident(a.attname),
                   d.refobjid, d.refobjsubid, d.deptype::text
            FROM pg_catalog.pg_sequence s
            JOIN pg_catalog.pg_class c ON c.oid=s.seqrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_catalog.pg_depend d ON d.classid='pg_catalog.pg_class'::regclass
                AND d.refclassid='pg_catalog.pg_class'::regclass AND d.objid=c.oid AND d.objsubid=0 AND d.deptype IN ('a','i')
            LEFT JOIN pg_catalog.pg_class t ON t.oid=d.refobjid
            LEFT JOIN pg_catalog.pg_namespace tn ON tn.oid=t.relnamespace
            LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid=t.oid AND a.attnum=d.refobjsubid
            WHERE n.nspname=@schema AND c.relname=@name AND c.relkind='S'
            """;
        await using var command = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("name", name);
        SequenceSnapshot snapshot;
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
                throw new InvalidOperationException("Sequence was not found. Check the schema and exact sequence name.");
            snapshot = new(reader.GetFieldValue<uint>(0), schema, name, reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10))
            { Dependency = reader.IsDBNull(11) ? null : new(reader.GetFieldValue<uint>(11), reader.GetInt32(12), reader.GetString(13)) };
        }
        await tx.CommitAsync(token).ConfigureAwait(false);
        return snapshot;
    }
}
