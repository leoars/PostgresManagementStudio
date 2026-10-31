using Npgsql;

namespace PGMS.PostgreSql;

public sealed record PrivilegeRecipientIdentity(string Name, uint Oid, bool IsPublic)
{
    internal static InvalidOperationException Conflict() => new("Permission recipient identity changed or could not be confirmed. Nothing was committed. Inspect again before applying.");

    internal static async Task<PrivilegeRecipientIdentity?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name, bool isPublic, CancellationToken token)
    {
        if (isPublic) return new("PUBLIC", 0, true);
        _ = SqlIdentifier.Quote(name);
        await using var command = new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_roles WHERE rolname=@name", connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("name", name);
        object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is uint oid ? new(name, oid, false) : null;
    }

    // Compare the complete ACL outside the intended grantee, including columns.
    // Post-command name checks alone cannot exclude a recipient name ABA swap.
    // No display quotas or pg_authid access are used by this mutation guard.
    internal sealed record AclEntry(short ColumnNumber, uint Grantor, uint Grantee, string Privilege, bool Grantable);

    internal static async Task<HashSet<AclEntry>> ReadAclAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PrivilegeTargetIdentity target, CancellationToken token,int? maximumEntries=null)
    {
        string kind = target.Target switch { PrivilegeTarget.Routine => "f", PrivilegeTarget.Database => "d", PrivilegeTarget.Schema => "n", PrivilegeTarget.Sequence => "s", _ => "r" };
        string source = target.Target switch
        {
            PrivilegeTarget.Routine => "SELECT proacl AS acl,proowner AS owner FROM pg_catalog.pg_proc WHERE oid=@oid",
            PrivilegeTarget.Database => "SELECT datacl AS acl,datdba AS owner FROM pg_catalog.pg_database WHERE oid=@oid",
            PrivilegeTarget.Schema => "SELECT nspacl AS acl,nspowner AS owner FROM pg_catalog.pg_namespace WHERE oid=@oid",
            _ => "SELECT relacl AS acl,relowner AS owner FROM pg_catalog.pg_class WHERE oid=@oid"
        };
        string columns = target.Target == PrivilegeTarget.Table ? " UNION ALL SELECT a.attnum,e.* FROM pg_catalog.pg_attribute a CROSS JOIN LATERAL pg_catalog.aclexplode(CASE WHEN cardinality(a.attacl)>0 THEN a.attacl END) e WHERE a.attrelid=@oid AND a.attnum>0 AND NOT a.attisdropped" : "";
        string sql = $"SELECT attnum,grantor,grantee,privilege_type,is_grantable FROM (SELECT 0::smallint AS attnum,e.* FROM (SELECT owner,COALESCE(acl,pg_catalog.acldefault('{kind}'::\"char\",owner)) acl FROM ({source}) original) o CROSS JOIN LATERAL pg_catalog.aclexplode(CASE WHEN cardinality(o.acl)>0 THEN o.acl END) e{columns}) entries";
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, target.ObjectOid);
        var entries = new HashSet<AclEntry>();
        await using var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await rows.ReadAsync(token).ConfigureAwait(false))
        {
            if(maximumEntries is { } maximum && entries.Count>=maximum)throw new InvalidOperationException("The complete ACL exceeds the review limit. No partial revoke preview is authorized.");
            entries.Add(new(rows.GetInt16(0),rows.GetFieldValue<uint>(1),rows.GetFieldValue<uint>(2),rows.GetString(3),rows.GetBoolean(4)));
        }
        return entries;
    }
}
