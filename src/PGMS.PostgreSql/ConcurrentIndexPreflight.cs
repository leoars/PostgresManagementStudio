using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

/// <summary>A point-in-time observation, never an authorization token for later DDL.</summary>
public sealed class ConcurrentIndexPreflight
{
    public ExpressionIndexEdit Proposal { get; }
    public string Sql { get; }
    public IReadOnlyList<string> Blockers { get; }
    public bool EligibleAtInspection => Blockers.Count == 0;
    public const string ExecutionContract = "Execute outside a transaction. Failure or cancellation can leave an invalid index. Reinspect before any retry; names can change after inspection.";

    internal ConcurrentIndexPreflight(ExpressionIndexEdit proposal, string sql, IEnumerable<string> blockers)
    {
        Proposal = proposal;
        Sql = sql;
        Blockers = Array.AsReadOnly(blockers.ToArray());
    }
}

public sealed class ConcurrentIndexInspector(string connectionString)
{
    public async Task<ConcurrentIndexPreflight> InspectAsync(ExpressionIndexEdit proposal, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        token.ThrowIfCancellationRequested();
        string ordinarySql = proposal.ToSql();
        string prefix = proposal.Unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ";
        string sql = prefix + "CONCURRENTLY " + ordinarySql[prefix.Length..];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var target = await ExpressionIndexCatalog.Read(connection, transaction, proposal.Target.Schema, proposal.Target.Name, token).ConfigureAwait(false);
        var blockers = new List<string>();
        if (!proposal.Target.Matches(target)) blockers.Add("Target identity or column definition changed. Inspect again.");
        await using var command = new NpgsqlCommand("""
            SELECT current_setting('server_version_num')::integer,
                   pg_catalog.pg_is_in_recovery(),
                   current_setting('default_transaction_read_only')::boolean,
                   pg_catalog.pg_has_role(current_user,@owner,'USAGE'),
                   EXISTS(SELECT 1 FROM pg_catalog.pg_class WHERE relnamespace=@namespace AND relname=@name),
                   EXISTS(SELECT 1 FROM pg_catalog.pg_stat_progress_create_index WHERE datid=@database AND relid=@table)
            """, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("owner", NpgsqlDbType.Oid, target.OwnerOid);
        command.Parameters.AddWithValue("namespace", NpgsqlDbType.Oid, target.NamespaceOid);
        command.Parameters.AddWithValue("database", NpgsqlDbType.Oid, target.DatabaseOid);
        command.Parameters.AddWithValue("table", NpgsqlDbType.Oid, target.Oid);
        command.Parameters.AddWithValue("name", proposal.Name);
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            await reader.ReadAsync(token).ConfigureAwait(false);
            int version = reader.GetInt32(0);
            if (version is < 170000 or >= 190000) blockers.Add("This concurrent-index flow is validated only on PostgreSQL 17 and 18.");
            if (reader.GetBoolean(1)) blockers.Add("Recovery server does not accept index creation.");
            if (reader.GetBoolean(2)) blockers.Add("The connection defaults to read-only transactions.");
            if (!reader.GetBoolean(3)) blockers.Add("Current role does not inherit the table owner's privileges.");
            if (reader.GetBoolean(4)) blockers.Add("A relation already occupies the requested index name. Inspect it; do not use IF NOT EXISTS as reconciliation.");
            if (reader.GetBoolean(5)) blockers.Add("An index operation is already visible on this table.");
        }
        foreach (var op in proposal.Keys.Select(key => key.OperatorClass).OfType<IndexOperatorClass>().Distinct())
        {
            await using var check = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_opclass o
                  JOIN pg_catalog.pg_namespace n ON n.oid=o.opcnamespace
                  JOIN pg_catalog.pg_am m ON m.oid=o.opcmethod
                  WHERE o.oid=@oid AND n.oid=@ns AND n.nspname=@schema AND o.opcname=@name
                    AND o.opcintype=@type AND o.opcmethod=@method AND m.amname='btree')
                """, connection, transaction) { CommandTimeout = 15 };
            check.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, op.Oid);
            check.Parameters.AddWithValue("ns", NpgsqlDbType.Oid, op.NamespaceOid);
            check.Parameters.AddWithValue("schema", op.Schema);
            check.Parameters.AddWithValue("name", op.Name);
            check.Parameters.AddWithValue("type", NpgsqlDbType.Oid, op.InputTypeOid);
            check.Parameters.AddWithValue("method", NpgsqlDbType.Oid, op.MethodOid);
            if (await check.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
                blockers.Add("Operator class identity changed. Inspect again.");
        }
        return new(proposal, sql, blockers);
    }
}
