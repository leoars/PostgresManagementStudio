using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public sealed record PrivilegeRevokeImpact(short ColumnNumber,string? Column,uint GrantorOid,string Grantor,
    uint RecipientOid,string Recipient,string Privilege,bool GrantOption,bool Direct);

public sealed class PrivilegeRevokePreview
{
    public PrivilegeRequest Request {get;}
    public PrivilegeTargetIdentity Target {get;}
    public PrivilegeRecipientIdentity Recipient {get;}
    public IReadOnlyList<PrivilegeRevokeImpact> PotentialChanges {get;}
    internal IReadOnlyList<PrivilegeRecipientIdentity.AclEntry> Acl {get;}
    internal IReadOnlyDictionary<uint,string> Roles {get;}
    internal async Task VerifyNamesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        foreach (var role in Roles)
        {
            await using var command = new NpgsqlCommand("SELECT rolname FROM pg_catalog.pg_roles WHERE oid=@oid", connection, transaction) { CommandTimeout = 15 };
            command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, role.Key);
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not string name || name != role.Value)
                throw PrivilegeGrantorIdentity.Conflict();
        }
        foreach (var column in PotentialChanges.Where(x => x.ColumnNumber != 0).DistinctBy(x => x.ColumnNumber))
        {
            await using var command = new NpgsqlCommand("SELECT attname FROM pg_catalog.pg_attribute WHERE attrelid=@oid AND attnum=@number AND NOT attisdropped", connection, transaction) { CommandTimeout = 15 };
            command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, Target.ObjectOid);
            command.Parameters.AddWithValue("number", column.ColumnNumber);
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not string name || name != column.Column)
                throw PrivilegeTargetIdentity.Conflict();
        }
    }

    internal void VerifyDelta(HashSet<PrivilegeRecipientIdentity.AclEntry> before, HashSet<PrivilegeRecipientIdentity.AclEntry> after)
    {
        bool Reviewed(PrivilegeRecipientIdentity.AclEntry e) => PotentialChanges.Any(x =>
            x.ColumnNumber == e.ColumnNumber && x.GrantorOid == e.Grantor && x.RecipientOid == e.Grantee && x.Privilege == e.Privilege && x.GrantOption == e.Grantable);
        // Only reviewed removals and grant-option downgrades are permitted. No new
        // grant, upgrade, unrelated mutation or removal of the root privilege in
        // GRANT OPTION FOR mode is accepted.
        foreach (var removed in before.Except(after))
        {
            if (!Reviewed(removed)) throw PrivilegeGrantorIdentity.Conflict();
            if (Request.GrantOption && removed.Grantor == Request.Grantor!.Oid && removed.Grantee == Recipient.Oid &&
                !after.Contains(removed with { Grantable = false })) throw PrivilegeGrantorIdentity.Conflict();
        }
        foreach (var added in after.Except(before))
            if (added.Grantable || !before.Contains(added with { Grantable = true }) || !Reviewed(added with { Grantable = true }))
                throw PrivilegeGrantorIdentity.Conflict();
    }

    internal PrivilegeRevokePreview(PrivilegeRequest request,PrivilegeTargetIdentity target,PrivilegeRecipientIdentity recipient,
        List<PrivilegeRevokeImpact> impacts,HashSet<PrivilegeRecipientIdentity.AclEntry> acl,Dictionary<uint,string> roles)
    {Request=request;Target=target;Recipient=recipient;PotentialChanges=impacts.AsReadOnly();Acl=acl.ToList().AsReadOnly();Roles=new System.Collections.ObjectModel.ReadOnlyDictionary<uint,string>(roles);}
}

public sealed class PrivilegeRevokeInspector(string connectionString)
{
    public const int MaximumAclEntries=4096;
    // Conservative reachability, not an exact emulation of PostgreSQL's ACL
    // dependency algorithm. Independent grant paths can preserve effective access.
    // No GRANT/REVOKE, temporary DDL or rollback simulation is executed here.
    public async Task<PrivilegeRevokePreview> InspectAsync(PrivilegeRequest request,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();_=request.ToSql();
        if(!request.Revoke || request.Grantor is null)throw new ArgumentException("Select an explicit grantor and a revoke operation before reviewing dependencies.");
        await using var connection=new NpgsqlConnection(connectionString);await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token).ConfigureAwait(false);
        await using(var readOnly=new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,transaction))await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await request.Grantor.VerifyAsync(connection,transaction,false,token).ConfigureAwait(false);
        var target=(await PrivilegeTargetIdentity.ReadAsync(connection,transaction,request.Target,request.Name,request.Schema,request.Column,token,request.Routine).ConfigureAwait(false)).Identity;
        var recipient=await PrivilegeRecipientIdentity.ReadAsync(connection,transaction,request.Role,request.RecipientIsPublic,token).ConfigureAwait(false)??throw PrivilegeRecipientIdentity.Conflict();
        var acl=await PrivilegeRecipientIdentity.ReadAclAsync(connection,transaction,target,token,MaximumAclEntries).ConfigureAwait(false);
        bool Scope(PrivilegeRecipientIdentity.AclEntry e)=>e.Privilege==request.Privilege && (target.ColumnNumber is null || e.ColumnNumber==target.ColumnNumber);
        var scoped=acl.Where(Scope).ToArray();
        var direct=scoped.Where(e=>e.Grantor==request.Grantor.Oid && e.Grantee==recipient.Oid && (!request.GrantOption || e.Grantable)).ToHashSet();
        var affected=direct.ToHashSet();var visited=new HashSet<uint>();var queue=new Queue<uint>();
        if(direct.Count>0 && recipient.Oid!=0)queue.Enqueue(recipient.Oid);
        while(queue.TryDequeue(out uint grantor))
        {
            token.ThrowIfCancellationRequested();if(!visited.Add(grantor))continue;
            foreach(var edge in scoped.Where(e=>e.Grantor==grantor))
            {affected.Add(edge);if(edge.Grantable && edge.Grantee!=0)queue.Enqueue(edge.Grantee);}
        }
        uint[] roleIds=affected.SelectMany(e=>new[]{e.Grantor,e.Grantee}).Append(request.Grantor.Oid).Append(recipient.Oid).Where(x=>x!=0).Distinct().ToArray();
        var roles=new Dictionary<uint,string>();
        await using(var command=new NpgsqlCommand("SELECT oid,rolname FROM pg_catalog.pg_roles WHERE oid=ANY(@roles)",connection,transaction){CommandTimeout=15})
        {
            command.Parameters.AddWithValue("roles",NpgsqlDbType.Array|NpgsqlDbType.Oid,roleIds);
            await using var rows=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while(await rows.ReadAsync(token).ConfigureAwait(false))roles.Add(rows.GetFieldValue<uint>(0),rows.GetString(1));
        }
        if(roleIds.Any(x=>!roles.ContainsKey(x)))throw PrivilegeGrantorIdentity.Conflict();
        var columns=new Dictionary<short,string>();
        if(target.Target==PrivilegeTarget.Table)
        {
            await using var command=new NpgsqlCommand("SELECT attnum,attname FROM pg_catalog.pg_attribute WHERE attrelid=@oid AND attnum>0 AND NOT attisdropped",connection,transaction){CommandTimeout=15};
            command.Parameters.AddWithValue("oid",NpgsqlDbType.Oid,target.ObjectOid);
            await using var rows=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while(await rows.ReadAsync(token).ConfigureAwait(false))columns.Add(rows.GetInt16(0),rows.GetString(1));
        }
        var impacts=affected.OrderBy(e=>e.ColumnNumber).ThenBy(e=>e.Grantor).ThenBy(e=>e.Grantee).Select(e=>new PrivilegeRevokeImpact(e.ColumnNumber,e.ColumnNumber==0?null:columns[e.ColumnNumber],e.Grantor,roles[e.Grantor],e.Grantee,e.Grantee==0?"PUBLIC":roles[e.Grantee],e.Privilege,e.Grantable,direct.Contains(e))).ToList();
        return new(request,target,recipient,impacts,acl,roles);
    }
}
