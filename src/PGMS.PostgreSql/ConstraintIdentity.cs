using Npgsql;

namespace PGMS.PostgreSql;

public sealed record ConstraintIdentity(uint TableOid, uint ConstraintOid)
{
    internal async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string schema, string table, string name, CancellationToken token)
    {
        // Caller holds a relation lock that prevents object replacement until Apply commits.
        const string sql = "SELECT c.conrelid, c.oid FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class r ON r.oid=c.conrelid JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname=@schema AND r.relname=@table AND c.conname=@name";
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetFieldValue<uint>(0) != TableOid || reader.GetFieldValue<uint>(1) != ConstraintOid)
            throw new InvalidOperationException("Constraint identity changed since inspection. Inspect again and review the current constraint before applying.");
    }
}
