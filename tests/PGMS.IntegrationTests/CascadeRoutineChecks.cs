using Npgsql;
using PGMS.PostgreSql;

internal static class CascadeRoutineChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        string suffix=Guid.NewGuid().ToString("N"),child="routine_delegate_"+suffix,grand="routine_leaf_"+suffix,qc=SqlIdentifier.Quote(child),qg=SqlIdentifier.Quote(grand);
        await f.Admin($"CREATE ROLE {qc} LOGIN;CREATE ROLE {qg} LOGIN;GRANT USAGE ON SCHEMA {f.QS} TO {f.QR},{qc},{qg}");
        await using var childSession=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){Username=child,Pooling=false}.ConnectionString);await childSession.OpenAsync();
        await using var grandSession=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){Username=grand,Pooling=false}.ConnectionString);await grandSession.OpenAsync();
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Child(string sql){await using var command=new NpgsqlCommand(sql,childSession);await command.ExecuteNonQueryAsync();}
        async Task<bool> Execute(string sql){try{await using var command=new NpgsqlCommand(sql,grandSession);await command.ExecuteNonQueryAsync();return true;}catch(PostgresException e)when(e.SqlState=="42501"){return false;}}
        try
        {
            await f.Owner($"CREATE FUNCTION {f.QS}.fn(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1';CREATE FUNCTION {f.QS}.fn(text) RETURNS text LANGUAGE sql AS 'SELECT $1';CREATE PROCEDURE {f.QS}.proc(INOUT value integer) LANGUAGE plpgsql AS 'BEGIN value:=value+1;END';CREATE AGGREGATE {f.QS}.total(integer)(SFUNC=int4pl,STYPE=integer,INITCOND='0');REVOKE EXECUTE ON ALL ROUTINES IN SCHEMA {f.QS} FROM PUBLIC;GRANT EXECUTE ON FUNCTION {f.QS}.fn(text) TO {qg}");
            foreach(var item in new[]{(Name:"fn",Signature:"fn(integer)",Execute:$"SELECT {f.QS}.fn(3)"),(Name:"proc",Signature:"proc(integer)",Execute:$"CALL {f.QS}.proc(3)"),(Name:"total",Signature:"total(integer)",Execute:$"SELECT {f.QS}.total(x) FROM (VALUES(1),(2)) v(x)")})
            {
                string signature=f.QS+"."+item.Signature;
                await f.Owner($"GRANT EXECUTE ON ROUTINE {signature} TO {f.QR} WITH GRANT OPTION");
                await f.Reader($"GRANT EXECUTE ON ROUTINE {signature} TO {qc} WITH GRANT OPTION");
                await Child($"GRANT EXECUTE ON ROUTINE {signature} TO {qg}");
                await using var oidCommand=new NpgsqlCommand("SELECT @signature::regprocedure::oid",admin);oidCommand.Parameters.AddWithValue("signature",signature);
                var selection=await new RoutinePrivilegeCatalog(f.RecipientConnection).InspectAsync((uint)(await oidCommand.ExecuteScalarAsync())!);
                var request=new PrivilegeRequest(PrivilegeTarget.Routine,item.Name,child,"EXECUTE",Revoke:true,Schema:f.Schema,GrantOption:true){Routine=selection};
                var candidates=await new PrivilegeGrantorCatalog(f.RecipientConnection).InspectAsync(request);var grantor=candidates.Candidates.Single(x=>x.Name==f.Recipient);
                request=request with{Grantor=new(candidates.Target.DatabaseOid,grantor.Oid,grantor.Name)};
                var inspector=new PrivilegeRevokeInspector(f.RecipientConnection);var preview=await inspector.InspectAsync(request);
                check(preview.PotentialChanges.Count==2 && preview.PotentialChanges.All(x=>x.Privilege=="EXECUTE") && preview.Target.Routine==selection,"Routine CASCADE binds exact signature and two-step delegated origin: "+item.Name);
                check(await Execute(item.Execute),"Terminal role executes delegated routine before revoke: "+item.Name);
                check(await new PrivilegeService(f.RecipientConnection).ApplyReviewedRevokeAsync(preview),"Non-owner direct grantor revokes routine delegation with reviewed CASCADE: "+item.Name);
                check(!await Execute(item.Execute),"Terminal execution is denied after dependent EXECUTE removal: "+item.Name);
                var acl=await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Routine,item.Name,f.Schema,routine:selection);
                check(acl.AclEntries.Any(x=>x.Grantee==child && !x.GrantOption) && acl.AclEntries.Any(x=>x.Grantee==f.Recipient && x.GrantOption),"Routine option revoke preserves child base privilege and upstream grant option: "+item.Name);
                await f.Reader($"GRANT EXECUTE ON ROUTINE {signature} TO {qc} WITH GRANT OPTION");await Child($"GRANT EXECUTE ON ROUTINE {signature} TO {qg}");
                await f.Owner($"GRANT EXECUTE ON ROUTINE {signature} TO {qc} WITH GRANT OPTION");
                check(await new PrivilegeService(f.RecipientConnection).ApplyReviewedRevokeAsync(await inspector.InspectAsync(request with{GrantOption=false})) && await Execute(item.Execute),"Independent routine origin preserves terminal execution after conservative CASCADE: "+item.Name);
                check(await Execute($"SELECT {f.QS}.fn('overload'::text)"),"CASCADE leaves independent text overload executable: "+item.Name);
            }
        }
        finally{await childSession.CloseAsync();await grandSession.CloseAsync();await f.Admin($"DROP SCHEMA IF EXISTS {f.QS} CASCADE;DROP OWNED BY {qc},{qg};DROP ROLE {qc},{qg}");}
    }
}
