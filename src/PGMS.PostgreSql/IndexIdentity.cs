using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public sealed record IndexIdentity(uint TableOid, uint IndexOid)
{
    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table, string name, CancellationToken token)
    {
        // Do not acquire expected-object locks here: post-DROP locks must witness the actual deletion.
        const string sql = """
            SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_index i
                JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                JOIN pg_catalog.pg_class t ON t.oid=i.indrelid
                WHERE n.nspname=@schema AND c.relname=@name AND t.relname=@table
                    AND t.relnamespace=n.oid AND i.indrelid=@tableOid AND i.indexrelid=@indexOid)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("table", table); command.Parameters.AddWithValue("name", name);
        AddIdentity(command);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw Conflict();
    }

    public async Task VerifyDroppedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        // Standard DROP INDEX retains its relation locks until commit. Check after executing but before
        // committing, so a concurrent rename/name swap cannot commit a deletion of the wrong index.
        // This contract assumes the executor's fresh transaction and no event-trigger DDL interference.
        const string sql = """
            SELECT NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE oid=@indexOid)
                AND EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE oid=@tableOid)
                AND (SELECT count(DISTINCT relation) FROM pg_catalog.pg_locks
                    WHERE locktype='relation' AND pid=pg_catalog.pg_backend_pid()
                      AND database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database())
                      AND granted AND mode='AccessExclusiveLock' AND relation IN (@tableOid,@indexOid))=2
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        AddIdentity(command);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw Conflict();
    }

    private void AddIdentity(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("tableOid", NpgsqlDbType.Oid, TableOid);
        command.Parameters.AddWithValue("indexOid", NpgsqlDbType.Oid, IndexOid);
    }
    private static InvalidOperationException Conflict() => new("Index identity changed. The operation was not committed. Inspect again and review the current index.");
}
