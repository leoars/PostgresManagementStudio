using Npgsql;
using NpgsqlTypes;
using System.Data;

namespace PGMS.PostgreSql;

public sealed record DefaultPrivilegeIdentity(uint DatabaseOid, uint CreatorOid,
    uint NamespaceOid, DefaultPrivilegeTarget Target, string Creator, string? Schema);
public enum DefaultPrivilegeOrigin { BuiltIn, Global, Schema }
public sealed record DefaultPrivilegeAclEntry(DefaultPrivilegeOrigin Origin,
    uint GrantorOid, string Grantor, uint RecipientOid, string Recipient,
    string Privilege, bool Grantable);
public sealed record DefaultPrivilegeSnapshot(DefaultPrivilegeIdentity Identity,
    uint GlobalPolicyOid, uint SchemaPolicyOid, IReadOnlyList<DefaultPrivilegeAclEntry> Entries,
    bool CreatorPrivilegesAvailable);

public sealed class DefaultPrivilegeCatalog(string connectionString)
{
    public async Task<DefaultPrivilegeSnapshot> InspectAsync(DefaultPrivilegeTarget target,
        string creator, string? schema = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // Validate the scope without introducing an arbitrary recipient or SQL fragment.
        _ = new DefaultPrivilegeRequest(target, creator, "", DefaultPrivilegeRequest.Allowed(target)[0],
            schema, RecipientIsPublic: true).ToSql();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return await ReadAsync(connection, transaction, target, creator, schema, token).ConfigureAwait(false);
    }

    internal static async Task<DefaultPrivilegeSnapshot> ReadAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, DefaultPrivilegeTarget target, string creator, string? schema, CancellationToken token)
    {
        const string sql = """
            WITH identity AS (
                SELECT db.oid database_oid,r.oid creator_oid,coalesce(n.oid,0::oid) namespace_oid
                FROM pg_catalog.pg_database db CROSS JOIN pg_catalog.pg_roles r
                LEFT JOIN pg_catalog.pg_namespace n ON n.nspname=@schema
                WHERE db.datname=pg_catalog.current_database() AND r.rolname=@creator
                  AND (@schema IS NULL OR n.oid IS NOT NULL)
            ), policies AS (
                SELECT i.*,g.oid global_oid,s.oid schema_oid,
                       coalesce(g.defaclacl,pg_catalog.acldefault(CAST(CASE WHEN @kind='S' THEN 's' ELSE @kind END AS "char"),i.creator_oid)) global_acl,
                       s.defaclacl schema_acl
                FROM identity i
                LEFT JOIN pg_catalog.pg_default_acl g ON g.defaclrole=i.creator_oid
                    AND g.defaclnamespace=0 AND g.defaclobjtype=CAST(@kind AS "char")
                LEFT JOIN pg_catalog.pg_default_acl s ON s.defaclrole=i.creator_oid
                    AND s.defaclnamespace=i.namespace_oid AND i.namespace_oid<>0
                    AND s.defaclobjtype=CAST(@kind AS "char")
            )
            SELECT p.database_oid,p.creator_oid,p.namespace_oid,coalesce(p.global_oid,0::oid),coalesce(p.schema_oid,0::oid),
                   source.origin,a.grantor,coalesce(grantor.rolname,a.grantor::text),
                   a.grantee,CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE coalesce(grantee.rolname,a.grantee::text) END,
                   a.privilege_type,a.is_grantable,pg_catalog.pg_has_role(current_user,p.creator_oid,'USAGE')
            FROM policies p
            CROSS JOIN LATERAL (VALUES (CASE WHEN p.global_oid IS NULL THEN 0 ELSE 1 END,p.global_acl),(2,p.schema_acl)) source(origin,acl)
            LEFT JOIN LATERAL pg_catalog.aclexplode(CASE WHEN cardinality(source.acl)>0 THEN source.acl END) a ON true
            LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=a.grantor
            LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=a.grantee
            ORDER BY source.origin,a.grantor,a.grantee,a.privilege_type,a.is_grantable
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("creator", creator);
        command.Parameters.AddWithValue("schema", NpgsqlDbType.Text, (object?)schema ?? DBNull.Value);
        command.Parameters.AddWithValue("kind", DefaultPrivilegeRequest.CatalogKind(target));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        DefaultPrivilegeIdentity? identity = null;
        uint globalOid = 0, schemaOid = 0;
        bool creatorPrivilegesAvailable = false;
        var entries = new List<DefaultPrivilegeAclEntry>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            identity ??= new(reader.GetFieldValue<uint>(0), reader.GetFieldValue<uint>(1), reader.GetFieldValue<uint>(2), target, creator, schema);
            globalOid = reader.GetFieldValue<uint>(3); schemaOid = reader.GetFieldValue<uint>(4);
            creatorPrivilegesAvailable = reader.GetBoolean(12);
            if (!reader.IsDBNull(6)) entries.Add(new((DefaultPrivilegeOrigin)reader.GetInt32(5),
                reader.GetFieldValue<uint>(6), reader.GetString(7), reader.GetFieldValue<uint>(8), reader.GetString(9), reader.GetString(10), reader.GetBoolean(11)));
        }
        if (identity is null) throw new InvalidOperationException("Default privilege creator or schema no longer exists. Inspect again.");
        return new(identity, globalOid, schemaOid, entries.AsReadOnly(), creatorPrivilegesAvailable);
    }
}
