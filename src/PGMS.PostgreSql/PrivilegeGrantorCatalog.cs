using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public sealed record PrivilegeGrantorCandidate(uint Oid,string Name,bool IsOwner,
    bool CanSetRole,bool AvailableWithoutSet,bool DirectGrantOption,bool EffectiveGrantOption,bool AppearsAsGrantor);
public sealed record PrivilegeGrantorSnapshot(PrivilegeTargetIdentity Target,string Privilege,
    uint OwnerOid,uint CurrentRoleOid,uint SessionRoleOid,IReadOnlyList<PrivilegeGrantorCandidate> Candidates,bool Truncated);

// Candidates explain authority for one privilege/scope. This observation does not
// authorize a write or predict which inherited role PostgreSQL will record.
public sealed class PrivilegeGrantorCatalog(string connectionString)
{
    public const int MaximumCandidates=512;
    public async Task<PrivilegeGrantorSnapshot> InspectAsync(PrivilegeRequest request,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();_=request.ToSql();
        await using var connection=new NpgsqlConnection(connectionString);await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token).ConfigureAwait(false);
        await using(var readOnly=new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,transaction))await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var target=(await PrivilegeTargetIdentity.ReadAsync(connection,transaction,request.Target,request.Name,request.Schema,request.Column,token,request.Routine).ConfigureAwait(false)).Identity;
        string source=request.Target switch
        {
            PrivilegeTarget.Database=>"SELECT datdba owner,datacl acl FROM pg_catalog.pg_database WHERE oid=@oid",
            PrivilegeTarget.Schema=>"SELECT nspowner owner,nspacl acl FROM pg_catalog.pg_namespace WHERE oid=@oid",
            PrivilegeTarget.Routine=>"SELECT proowner owner,proacl acl FROM pg_catalog.pg_proc WHERE oid=@oid",
            _=>"SELECT relowner owner,relacl acl FROM pg_catalog.pg_class WHERE oid=@oid"
        };
        string kind=request.Target switch{PrivilegeTarget.Database=>"d",PrivilegeTarget.Schema=>"n",PrivilegeTarget.Routine=>"f",PrivilegeTarget.Sequence=>"s",_=>"r"};
        string function=request.Target switch{PrivilegeTarget.Database=>"has_database_privilege",PrivilegeTarget.Schema=>"has_schema_privilege",PrivilegeTarget.Routine=>"has_function_privilege",PrivilegeTarget.Sequence=>"has_sequence_privilege",_=>request.Column is null?"has_table_privilege":"has_column_privilege"};
        string column=request.Column is null?"":"UNION ALL SELECT e.* FROM pg_catalog.pg_attribute a CROSS JOIN LATERAL pg_catalog.aclexplode(CASE WHEN cardinality(a.attacl)>0 THEN a.attacl END) e WHERE a.attrelid=@oid AND a.attnum=@attnum";
        string sql=$"""
            WITH target AS ({source}), effective_acl AS (
                SELECT owner,coalesce(acl,pg_catalog.acldefault('{kind}'::"char",owner)) acl FROM target
            ), entries AS (
                SELECT e.* FROM effective_acl t CROSS JOIN LATERAL pg_catalog.aclexplode(CASE WHEN cardinality(t.acl)>0 THEN t.acl END) e
                {column}
            ), relevant AS (SELECT * FROM entries WHERE privilege_type=@privilege), candidates AS (
                SELECT owner oid FROM target UNION SELECT grantor FROM relevant
                UNION SELECT grantee FROM relevant WHERE is_grantable
                UNION SELECT oid FROM pg_catalog.pg_roles WHERE rolname=current_user
            )
            SELECT r.oid,r.rolname,r.oid=t.owner,
                pg_catalog.pg_has_role(session_user,r.oid,'SET'),pg_catalog.pg_has_role(current_user,r.oid,'USAGE'),
                r.oid=t.owner OR EXISTS(SELECT 1 FROM relevant e WHERE e.grantee=r.oid AND e.is_grantable),
                pg_catalog.{function}(r.oid,@oid,{(request.Column is null?"":"@attnum,")}@permission),
                EXISTS(SELECT 1 FROM relevant e WHERE e.grantor=r.oid),t.owner,
                (SELECT oid FROM pg_catalog.pg_roles WHERE rolname=current_user),
                (SELECT oid FROM pg_catalog.pg_roles WHERE rolname=session_user)
            FROM candidates c JOIN pg_catalog.pg_roles r ON r.oid=c.oid CROSS JOIN target t
            ORDER BY r.rolname,r.oid LIMIT @limit
            """;
        await using var command=new NpgsqlCommand(sql,connection,transaction){CommandTimeout=15};
        command.Parameters.AddWithValue("oid",NpgsqlDbType.Oid,target.ObjectOid);
        command.Parameters.AddWithValue("attnum",NpgsqlDbType.Smallint,target.ColumnNumber??0);
        command.Parameters.AddWithValue("privilege",request.Privilege);
        command.Parameters.AddWithValue("permission",request.Privilege+" WITH GRANT OPTION");
        command.Parameters.AddWithValue("limit",MaximumCandidates+1);
        var candidates=new List<PrivilegeGrantorCandidate>();bool truncated=false;uint owner=0,current=0,session=0;
        await using(var rows=await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while(await rows.ReadAsync(token).ConfigureAwait(false))
            {
                if(candidates.Count==MaximumCandidates){truncated=true;break;}
                candidates.Add(new(rows.GetFieldValue<uint>(0),rows.GetString(1),rows.GetBoolean(2),rows.GetBoolean(3),rows.GetBoolean(4),rows.GetBoolean(5),rows.GetBoolean(6),rows.GetBoolean(7)));
                owner=rows.GetFieldValue<uint>(8);current=rows.GetFieldValue<uint>(9);session=rows.GetFieldValue<uint>(10);
            }
        }
        if(owner==0 || current==0 || session==0)throw PrivilegeTargetIdentity.Conflict();
        return new(target,request.Privilege,owner,current,session,candidates.AsReadOnly(),truncated);
    }
}
