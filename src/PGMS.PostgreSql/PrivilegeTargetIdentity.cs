using Npgsql;

namespace PGMS.PostgreSql;

// OIDs are identities inside the configured cluster, not global server identities.
public sealed record PrivilegeTargetIdentity(PrivilegeTarget Target, string Name, string? Schema,
    uint DatabaseOid, uint ObjectOid, uint? NamespaceOid, string? Column, short? ColumnNumber)
{
    public RoutinePrivilegeSelection? Routine { get; init; }
    internal void ValidateRequest(PrivilegeRequest request)
    {
        if (DatabaseOid == 0 || ObjectOid == 0 || Target != request.Target || Name != request.Name ||
            Schema != (Target is PrivilegeTarget.Table or PrivilegeTarget.Sequence or PrivilegeTarget.Routine ? request.Schema : null) ||
            Routine != request.Routine || Column != request.Column || (Column is null ? ColumnNumber is not null : ColumnNumber is <= 0))
            throw Conflict();
        // Unknown attnum is allowed only to preserve native validation errors.
        // It can never equal a real post-command column identity, so cannot commit.
    }

    internal static InvalidOperationException Conflict() => new("Permission target identity changed or could not be confirmed. Nothing was committed. Inspect again before applying.");

    internal static async Task<(PrivilegeTargetIdentity Identity, bool Written, bool EmptyColumnAcl)> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PrivilegeTarget target,
        string name, string schema, string? column, CancellationToken token, RoutinePrivilegeSelection? routine = null)
    {
        if (target == PrivilegeTarget.Routine)
        {
            if (routine is null || column is not null || routine.Name != name || routine.Schema != schema)
                throw new ArgumentException("Select an exact routine overload from the catalog.");
            var observed = await RoutinePrivilegeCatalog.ReadAsync(connection, transaction, routine.ObjectOid, token).ConfigureAwait(false);
            if (observed.Selection != routine) throw Conflict();
            return (new PrivilegeTargetIdentity(target, name, schema, routine.DatabaseOid, routine.ObjectOid, routine.NamespaceOid, null, null) { Routine = routine }, observed.Written, false);
        }
        if (routine is not null) throw new ArgumentException("Routine selection requires routine target.");
        _ = SqlIdentifier.Quote(name);
        if (target is PrivilegeTarget.Table or PrivilegeTarget.Sequence) _ = SqlIdentifier.Quote(schema);
        if (column is not null) { if (target != PrivilegeTarget.Table) throw new ArgumentException("Column target requires table."); _ = SqlIdentifier.Quote(column); }
        string body = target switch
        {
            PrivilegeTarget.Database => "SELECT d.oid,NULL::oid AS namespace,NULL::int2 AS attnum,d.xmin::text AS writer,false AS empty_acl FROM pg_catalog.pg_database d WHERE d.datname=@name",
            PrivilegeTarget.Schema => "SELECT n.oid,NULL::oid AS namespace,NULL::int2 AS attnum,n.xmin::text AS writer,false AS empty_acl FROM pg_catalog.pg_namespace n WHERE n.nspname=@name",
            PrivilegeTarget.Table or PrivilegeTarget.Sequence => "SELECT c.oid,n.oid AS namespace,a.attnum,CASE WHEN @column::text IS NULL THEN c.xmin::text ELSE a.xmin::text END AS writer,(@column::text IS NOT NULL AND a.attacl IS NULL) AS empty_acl FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attname=@column AND a.attnum>0 AND NOT a.attisdropped WHERE n.nspname=@schema AND c.relname=@name AND c.relkind IN (" + (target == PrivilegeTarget.Sequence ? "'S'" : "'r','p','v','m','f'") + ") AND (@column::text IS NULL OR a.attnum IS NOT NULL)",
            _ => throw new ArgumentException("Unsupported permission target.")
        };
        // ACL updates retain the updated catalog tuple until transaction end. Merely
        // resolving the same name after GRANT is insufficient for an ABA name swap.
        // The fresh transaction has no preceding writes. Do not add DDL here.
        string sql = "SELECT t.oid,t.namespace,t.attnum,(SELECT oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database())," +
            "COALESCE(t.writer::numeric = mod(pg_catalog.pg_current_xact_id_if_assigned()::text::numeric,4294967296),false),t.empty_acl FROM (" + body + ") t";
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("name", name); command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("column", NpgsqlTypes.NpgsqlDbType.Text, (object?)column ?? DBNull.Value);
        await using var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await rows.ReadAsync(token).ConfigureAwait(false)) throw new PrivilegeTargetMissingException();
        var identity = new PrivilegeTargetIdentity(target, name, target is PrivilegeTarget.Table or PrivilegeTarget.Sequence ? schema : null,
            rows.GetFieldValue<uint>(3), rows.GetFieldValue<uint>(0), rows.IsDBNull(1) ? null : rows.GetFieldValue<uint>(1), column, rows.IsDBNull(2) ? null : rows.GetInt16(2));
        return (identity, rows.GetBoolean(4), rows.GetBoolean(5));
    }
}

internal sealed class PrivilegeTargetMissingException() : InvalidOperationException("Permission target or selected column was not found.") { }
