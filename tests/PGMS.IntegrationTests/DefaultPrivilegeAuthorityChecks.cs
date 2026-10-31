using Npgsql;
using PGMS.PostgreSql;

internal static class DefaultPrivilegeAuthorityChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),creator="da_creator_"+suffix,actor="da_actor_"+suffix,role="da_role_"+suffix,schema="da_"+suffix;
        string qc=SqlIdentifier.Quote(creator),qa=SqlIdentifier.Quote(actor),qr=SqlIdentifier.Quote(role),qs=SqlIdentifier.Quote(schema);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        string acting=new NpgsqlConnectionStringBuilder(connection){Username=actor,Pooling=false}.ConnectionString;
        var catalog=new DefaultPrivilegeCatalog(acting);var service=new DefaultPrivilegeService(acting);
        var request=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Tables,creator,role,"SELECT",schema);
        try
        {
            await Sql($"CREATE ROLE {qc};CREATE ROLE {qa} LOGIN;CREATE ROLE {qr};CREATE SCHEMA {qs} AUTHORIZATION {qc}");
            var recipient=(await new PrivilegeService(acting).InspectRecipientAsync(role))!;
            foreach(var mode in new[]{("none",false),("set-only",false),("inherit-only",true),("both",true)})
            {
                await Sql($"REVOKE {qc} FROM {qa}");
                if(mode.Item1!="none")await Sql($"GRANT {qc} TO {qa} WITH INHERIT {(mode.Item2?"TRUE":"FALSE")}, SET {(mode.Item1=="inherit-only"?"FALSE":"TRUE")}");
                var snapshot=await catalog.InspectAsync(request.Target,creator,schema);
                check(snapshot.CreatorPrivilegesAvailable==mode.Item2,"Default creator authority reflects actual INHERIT availability: "+mode.Item1);
                bool applied=false,denied=false;try{applied=await service.ApplyInspectedAsync(request,snapshot,recipient);}catch(PostgresException ex)when(ex.SqlState=="42501"){denied=true;}
                check(applied==mode.Item2 && denied==!mode.Item2,"Native default authorization agrees with inspection: "+mode.Item1);
                var after=await catalog.InspectAsync(request.Target,creator,schema);
                check(after.Entries.Any(e=>e.Recipient==role && e.GrantorOid==snapshot.Identity.CreatorOid)==mode.Item2,"Default ACL grantor is creator rather than connected member: "+mode.Item1);
                await Sql((request with{Revoke=true}).ToSql());
            }
            var authorized=await catalog.InspectAsync(request.Target,creator,schema);
            await Sql($"REVOKE {qc} FROM {qa}");
            bool refused=false;try{await service.ApplyInspectedAsync(request,authorized,recipient);}catch(PostgresException ex)when(ex.SqlState=="42501"){refused=true;}
            check(refused && (await catalog.InspectAsync(request.Target,creator,schema)).SchemaPolicyOid==0,"Revoked membership after inspection cannot authorize stale Apply");
            await Sql($"GRANT {qc} TO {qa} WITH INHERIT TRUE, SET FALSE");
            var current=await catalog.InspectAsync(request.Target,creator,schema);await service.ApplyInspectedAsync(request,current,recipient);
            await using(var client=new NpgsqlConnection(acting))
            {await client.OpenAsync();await using var c=new NpgsqlCommand($"CREATE TABLE {qs}.actor_owned(id int)",client);await c.ExecuteNonQueryAsync();}
            await Sql($"SET ROLE {qc};CREATE TABLE {qs}.creator_owned(id int);RESET ROLE");
            await using(var c=new NpgsqlCommand("SELECT NOT pg_catalog.has_table_privilege(@role,@actor,'SELECT') AND pg_catalog.has_table_privilege(@role,@creator,'SELECT')",admin))
            {c.Parameters.AddWithValue("role",role);c.Parameters.AddWithValue("actor",qs+".actor_owned");c.Parameters.AddWithValue("creator",qs+".creator_owned");check((bool)(await c.ExecuteScalarAsync())!,"Inherited creator authority does not inherit creation-time default policies for member-owned objects");}
            await Sql($"REVOKE {qc} FROM {qa};GRANT {qc} TO {qa} WITH INHERIT FALSE, SET TRUE");
            await using(var client=new NpgsqlConnection(acting))
            {
                await client.OpenAsync();await using(var c=new NpgsqlCommand($"SET ROLE {qc};CREATE TABLE {qs}.set_role_owned(id int)",client))await c.ExecuteNonQueryAsync();
            }
            await using(var c=new NpgsqlCommand("SELECT pg_catalog.has_table_privilege(@role,@table,'SELECT')",admin))
            {c.Parameters.AddWithValue("role",role);c.Parameters.AddWithValue("table",qs+".set_role_owned");check((bool)(await c.ExecuteScalarAsync())!,"Explicit SET ROLE changes actual creator for object creation in that session");}
            check(!(await catalog.InspectAsync(request.Target,creator,schema)).CreatorPrivilegesAvailable,"Default inspection uses separate connection and does not inherit editor SET ROLE");
        }
        finally{await Sql($"RESET ROLE;DROP SCHEMA IF EXISTS {qs} CASCADE;DROP OWNED BY {qc},{qa};DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {qa};DROP ROLE IF EXISTS {qc}");}
    }
}
