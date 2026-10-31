using Npgsql;

namespace PGMS.PostgreSql;

internal static class PrivilegeAclReader
{
    internal const int MaximumEntries = EffectivePrivilegeService.MaximumAclEntries;

    internal static async Task<(IReadOnlyList<PrivilegeAclEntry> Entries,bool Truncated)> ReadAsync(
        NpgsqlConnection connection,NpgsqlTransaction transaction,PrivilegeTarget target,uint oid,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string source=target switch
        {
            PrivilegeTarget.Routine => "SELECT proacl AS acl,proowner AS owner FROM pg_catalog.pg_proc WHERE oid=@oid",
            PrivilegeTarget.Database => "SELECT datacl AS acl,datdba AS owner FROM pg_catalog.pg_database WHERE oid=@oid",
            PrivilegeTarget.Schema => "SELECT nspacl AS acl,nspowner AS owner FROM pg_catalog.pg_namespace WHERE oid=@oid",
            PrivilegeTarget.Table => "SELECT relacl AS acl,relowner AS owner FROM pg_catalog.pg_class WHERE oid=@oid AND relkind IN ('r','p')",
            PrivilegeTarget.Sequence => "SELECT relacl AS acl,relowner AS owner FROM pg_catalog.pg_class WHERE oid=@oid AND relkind='S'",
            _ => throw new ArgumentException("Unsupported ACL target.",nameof(target))
        };
        string kind=target switch { PrivilegeTarget.Routine => "f", PrivilegeTarget.Database => "d", PrivilegeTarget.Schema => "n", PrivilegeTarget.Table => "r", PrivilegeTarget.Sequence => "s", _ => throw new ArgumentException("Unsupported ACL target.") };
        string columnEntries=target==PrivilegeTarget.Table ? """
            UNION ALL
            SELECT a.attnum,a.attname,false AS builtin,e.*
            FROM (SELECT attnum,attname,attacl FROM pg_catalog.pg_attribute
                  WHERE attrelid=@oid AND attnum>0 AND NOT attisdropped ORDER BY attnum LIMIT @columnLimit) a
            CROSS JOIN LATERAL pg_catalog.aclexplode(a.attacl) e
            """ : "";
        string sql=$"""
            WITH entries AS (
                SELECT NULL::smallint AS attnum,NULL::text AS column_name,o.acl IS NULL AS builtin,e.*
                FROM ({source}) o
                CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(o.acl,pg_catalog.acldefault('{kind}'::"char",o.owner))) e
                {columnEntries}
            )
            SELECT attnum,column_name,grantor,pg_catalog.pg_get_userbyid(grantor),grantee,
                CASE WHEN grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(grantee) END,
                privilege_type,is_grantable,builtin,
                CASE WHEN grantee=0 THEN true ELSE pg_catalog.pg_has_role(grantee,'USAGE') END
            FROM entries ORDER BY attnum NULLS FIRST,grantee,grantor,privilege_type LIMIT @entryLimit
            """;
        await using var command=new NpgsqlCommand(sql,connection,transaction){CommandTimeout=15};
        command.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,oid);
        command.Parameters.AddWithValue("entryLimit",MaximumEntries+1);
        command.Parameters.AddWithValue("columnLimit",EffectivePrivilegeService.MaximumColumns);
        var entries=new List<PrivilegeAclEntry>();
        await using var rows=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        bool truncated=false;
        while(await rows.ReadAsync(token).ConfigureAwait(false))
        {
            if(entries.Count==MaximumEntries){truncated=true;break;}
            entries.Add(new(rows.IsDBNull(0)?target.ToString():"Column",rows.IsDBNull(1)?null:rows.GetString(1),rows.IsDBNull(0)?null:rows.GetInt16(0),
                rows.GetFieldValue<uint>(2),rows.GetString(3),rows.GetFieldValue<uint>(4),rows.GetString(5),rows.GetString(6),rows.GetBoolean(7),
                rows.GetBoolean(8)?PrivilegeAclSource.BuiltInDefault:PrivilegeAclSource.Stored,rows.GetBoolean(9)));
        }
        return (entries.AsReadOnly(),truncated);
    }
}
