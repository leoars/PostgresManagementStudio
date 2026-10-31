using Npgsql;
using PGMS.PostgreSql;

internal static class ExplicitGrantorChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        string child="explicit_child_"+Guid.NewGuid().ToString("N"),qc=SqlIdentifier.Quote(child);
        await f.Admin($"CREATE ROLE {qc};GRANT USAGE ON SCHEMA {f.QS} TO {f.QR}");
        try
        {
            await f.Owner($"CREATE TABLE {f.QS}.target(value integer);GRANT SELECT ON {f.QS}.target TO {f.QR} WITH GRANT OPTION");
            var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",child,"SELECT",Schema:f.Schema);
            var snapshot=await new PrivilegeGrantorCatalog(f.RecipientConnection).InspectAsync(request);
            PrivilegeGrantorIdentity Identity(string name){var c=snapshot.Candidates.Single(x=>x.Name==name);return new(snapshot.Target.DatabaseOid,c.Oid,c.Name);}
            var owner=Identity(f.Creator);var reader=Identity(f.Recipient);
            var service=new PrivilegeService(f.RecipientConnection);
            async Task<bool> Apply(PrivilegeRequest r){var target=await service.InspectTargetAsync(r.Target,r.Name,r.Schema,r.Column,routine:r.Routine);var recipient=(await service.InspectRecipientAsync(r.Role,r.RecipientIsPublic))!;return await service.ApplyInspectedAsync(r,target,recipient);}
            async Task<bool> Refused(PrivilegeRequest r){try{await Apply(r);return false;}catch(InvalidOperationException){return true;}}
            var byOwner=request with{Grantor=owner};
            check(byOwner.ToSql().StartsWith("BEGIN;\nSET LOCAL ROLE ",StringComparison.Ordinal) && byOwner.ToSql().Contains("GRANTED BY "+f.QO,StringComparison.Ordinal) && byOwner.ToSql().EndsWith("COMMIT;",StringComparison.Ordinal),"Explicit grantor script scopes role selection to a transaction");
            check(await Refused(byOwner),"Selecting owner without SET membership is refused before any grant");
            await f.Admin($"GRANT {f.QO} TO {f.QR} WITH INHERIT FALSE,SET TRUE");
            check(await Apply(byOwner),"SET-only member explicitly grants as owner through guarded service");
            var acl=await new EffectivePrivilegeService(connection).InspectAsync(request.Target,request.Name,request.Schema);
            check(acl.AclEntries.Any(e=>e.GrantorOid==owner.Oid && e.Grantee==child && e.Privilege=="SELECT"),"Explicit owner selection is the actual stored ACL grantor");
            check(!await Apply(byOwner),"Idempotent selected-origin grant rolls back with no committed delta");
            check(await Apply(request with{Grantor=reader}),"Direct grant-option holder creates a second independent grant origin");
            acl=await new EffectivePrivilegeService(connection).InspectAsync(request.Target,request.Name,request.Schema);
            check(acl.AclEntries.Count(e=>e.Grantee==child && e.Privilege=="SELECT")==2,"Two selected grantors remain distinct for the same recipient and privilege");
            check(await Apply(byOwner with{Revoke=true}),"Selected-owner revoke removes only its own grant");
            acl=await new EffectivePrivilegeService(connection).InspectAsync(request.Target,request.Name,request.Schema);
            check(acl.AclEntries.Count(e=>e.Grantee==child && e.Privilege=="SELECT")==1 && acl.AclEntries.Any(e=>e.Grantee==child && e.GrantorOid==reader.Oid),"Revoke preserves another origin for the same recipient");
            check(await Apply(request with{Grantor=reader,Revoke=true}),"Selecting remaining grantor removes the remaining origin");
            await f.Admin($"GRANT {f.QO} TO {f.QR} WITH INHERIT TRUE,SET TRUE");
            check(await Refused(request with{Privilege="UPDATE",Grantor=reader}),"Inherited authority that records another grantor is rolled back");
            acl=await new EffectivePrivilegeService(connection).InspectAsync(request.Target,request.Name,request.Schema);
            check(!acl.AclEntries.Any(e=>e.Grantee==child && e.Privilege=="UPDATE"),"Rejected inherited-origin operation leaves no grant behind");
            check(await Apply(byOwner with{Privilege="UPDATE",Column="value"}),"Explicit owner supports column grant with captured attnum");
            check(await Apply(byOwner with{Privilege="UPDATE",Column="value",Revoke=true}),"Explicit owner revokes selected column origin");
            await f.Owner($"CREATE SEQUENCE {f.QS}.seq;CREATE FUNCTION {f.QS}.fn(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1';REVOKE EXECUTE ON FUNCTION {f.QS}.fn(integer) FROM PUBLIC");
            foreach(var r in new[]{new PrivilegeRequest(PrivilegeTarget.Sequence,"seq",child,"USAGE",Schema:f.Schema){Grantor=owner},new PrivilegeRequest(PrivilegeTarget.Schema,f.Schema,child,"USAGE"){Grantor=owner}})
                check(await Apply(r) && await Apply(r with{Revoke=true}),"Explicit grantor applies and revokes through real "+r.Target+" ACL");
            await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
            await using var command=new NpgsqlCommand("SELECT @name::regprocedure::oid",admin);command.Parameters.AddWithValue("name",f.QS+".fn(integer)");
            var routine=await new RoutinePrivilegeCatalog(connection).InspectAsync((uint)(await command.ExecuteScalarAsync())!);
            var routineRequest=new PrivilegeRequest(PrivilegeTarget.Routine,"fn",child,"EXECUTE",Schema:f.Schema){Routine=routine,Grantor=owner};
            check(await Apply(routineRequest) && await Apply(routineRequest with{Revoke=true}),"Explicit grantor preserves exact routine overload through grant/revoke");
            check(await Refused(byOwner with{Grantor=owner with{DatabaseOid=owner.DatabaseOid+1}}) && await Refused(byOwner with{Grantor=owner with{Oid=owner.Oid+1}}),"Forged database or grantor OID is rejected");
            await f.Admin($"REVOKE {f.QO} FROM {f.QR}");
            check(await Refused(byOwner),"Revoked SET membership is revalidated for an earlier selected grantor");
            await using var clean=new NpgsqlConnection(f.RecipientConnection);await clean.OpenAsync();await using var who=new NpgsqlCommand("SELECT current_user",clean);
            check((string)(await who.ExecuteScalarAsync())! == f.Recipient,"Explicit role transaction does not alter later administrative sessions");
        }
        finally{await f.Admin($"DROP SCHEMA IF EXISTS {f.QS} CASCADE;DROP OWNED BY {qc};DROP ROLE {qc}");}
    }
}
