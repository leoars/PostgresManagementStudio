using Npgsql;

namespace PGMS.PostgreSql;

internal static class ConstraintInspection
{
    internal static async Task RequireTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string schema, string table, CancellationToken token)
    {
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        const string sql = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname=@schema AND r.relname=@table AND r.relkind IN ('r','p'))";
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
            throw new InvalidOperationException("Ordinary or partitioned table was not found. Check the exact schema and table names and inspect again.");
    }
}
