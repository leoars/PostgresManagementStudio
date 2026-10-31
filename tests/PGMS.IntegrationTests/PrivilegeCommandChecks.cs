using Npgsql;
using PGMS.PostgreSql;

internal static class PrivilegeCommandChecks
{
    public static async Task RunAsync(string connection, Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"), schema="cmd_s_é'\"_"+suffix, role="cmd_r_é'\"_"+suffix;
        string future="cmd_future_"+suffix, database="cmd_db_"+suffix;
        string qs=SqlIdentifier.Quote(schema), qr=SqlIdentifier.Quote(role), qf=SqlIdentifier.Quote(future), table=ObjectSql.Relation(schema,"item'\"");
        await using var admin=new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string text) { await using var command=new NpgsqlCommand(text,admin); await command.ExecuteNonQueryAsync(); }
        var service=new PrivilegeService(connection);
        var limitedConnection=new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString;
        var inspect=new EffectivePrivilegeService(limitedConnection);
        var request=new PrivilegeRequest(PrivilegeTarget.Table,"item'\"",role,"SELECT",Schema:schema);
        try
        {
            await Sql($"CREATE ROLE {qr} LOGIN; CREATE SCHEMA {qs}; CREATE TABLE {table}(id integer); INSERT INTO {table} VALUES(17); GRANT USAGE ON SCHEMA {qs} TO {qr};");
            await service.ApplyAsync(request);
            var granted=await inspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema);
            check(granted.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT") is {Allowed:true,GrantOption:false},"Ordinary grant preserves nondelegable behavior");
            await service.ApplyAsync(request with {Revoke=true});
            var revoked=await inspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema);
            check(!revoked.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed,"Ordinary restrictive revoke removes direct table authority");
            var publicRequest=request with {RecipientIsPublic=true,Role=""};
            await service.ApplyAsync(publicRequest);
            var publicGrant=await inspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema);
            check(publicGrant.AclEntries.Any(e=>e.GranteeOid==0 && e.Privilege=="SELECT" && !e.GrantOption) && publicGrant.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed,"PUBLIC table grant is visible as OID0 and gives limited role authority");
            await Sql($"CREATE ROLE {qf} LOGIN; GRANT USAGE ON SCHEMA {qs} TO {qf};");
            await using(var futureConnection=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){Username=future}.ConnectionString))
            {
                await futureConnection.OpenAsync();
                await using var read=new NpgsqlCommand($"SELECT id FROM {table}",futureConnection);
                check((int)(await read.ExecuteScalarAsync())! == 17,"PUBLIC also applies to a role created after the grant");
            }
            await service.ApplyAsync(request);
            await service.ApplyAsync(publicRequest with {Revoke=true});
            var directAfterPublic=await inspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema);
            check(directAfterPublic.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed && !directAfterPublic.AclEntries.Any(e=>e.GranteeOid==0 && e.Privilege=="SELECT"),"Revoking PUBLIC leaves independent direct grant intact");
            await service.ApplyAsync(request with {Revoke=true});
            check(!(await inspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema)).Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed,"Removing both PUBLIC and direct grant removes effective SELECT");
            await Sql($"REVOKE USAGE ON SCHEMA {qs} FROM {qr};");
            var schemaRequest=new PrivilegeRequest(PrivilegeTarget.Schema,schema,"","USAGE",RecipientIsPublic:true);
            check(!(await inspect.InspectAsync(PrivilegeTarget.Schema,schema)).Privileges.Single(p=>p.Privilege=="USAGE").Allowed,"Schema fixture starts without direct or PUBLIC USAGE");
            await service.ApplyAsync(schemaRequest);
            check((await inspect.InspectAsync(PrivilegeTarget.Schema,schema)).Privileges.Single(p=>p.Privilege=="USAGE").Allowed,"PUBLIC schema grant enables limited role USAGE");
            await service.ApplyAsync(schemaRequest with {Revoke=true});
            check(!(await inspect.InspectAsync(PrivilegeTarget.Schema,schema)).Privileges.Single(p=>p.Privilege=="USAGE").Allowed,"PUBLIC schema revoke removes its USAGE contribution");
            await Sql($"CREATE DATABASE {SqlIdentifier.Quote(database)};");
            var databaseRequest=new PrivilegeRequest(PrivilegeTarget.Database,database,"","CONNECT",RecipientIsPublic:true);
            await service.ApplyAsync(databaseRequest with {Revoke=true});
            check(!(await inspect.InspectAsync(PrivilegeTarget.Database,database)).Privileges.Single(p=>p.Privilege=="CONNECT").Allowed,"PUBLIC database revoke removes default CONNECT without connecting to target");
            await service.ApplyAsync(databaseRequest);
            check((await inspect.InspectAsync(PrivilegeTarget.Database,database)).Privileges.Single(p=>p.Privilege=="CONNECT").Allowed,"PUBLIC database grant restores CONNECT");
            await service.ApplyAsync(databaseRequest with {Revoke=true});
            check(!(await inspect.InspectAsync(PrivilegeTarget.Database,database)).Privileges.Single(p=>p.Privilege=="CONNECT").Allowed,"Repeated PUBLIC database revoke removes CONNECT again");
            foreach(var delegable in new[] {databaseRequest with {RecipientIsPublic=false,Role=role,GrantOption=true},schemaRequest with {RecipientIsPublic=false,Role=role,GrantOption=true}})
            {
                await service.ApplyAsync(delegable);
                check((await inspect.InspectAsync(delegable.Target,delegable.Name)).Privileges.Single(p=>p.Privilege==delegable.Privilege) is {Allowed:true,GrantOption:true},"Grant option effective on "+delegable.Target);
                await service.ApplyAsync(delegable with {Revoke=true});
                check((await inspect.InspectAsync(delegable.Target,delegable.Name)).Privileges.Single(p=>p.Privilege==delegable.Privilege) is {Allowed:true,GrantOption:false},"Grant-option-only revoke preserves base privilege on "+delegable.Target);
            }
            await service.ApplyAsync(request with {GrantOption=true});
            var delegableTable=await inspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema);
            check(delegableTable.Owner!=role && delegableTable.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT") is {Allowed:true,GrantOption:true},"A non-owner role receives effective table grant option");
            var delegateService=new PrivilegeService(limitedConnection);
            var downstream=request with {Role=future};
            await delegateService.ApplyAsync(downstream);
            var downstreamSnapshot=await inspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema);
            check(downstreamSnapshot.AclEntries.Any(e=>e.Grantor==role && e.Grantee==future && e.Privilege=="SELECT"),"Limited role delegates SELECT with its own grantor identity");
            await using(var delegatedReader=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){Username=future}.ConnectionString))
            {
                await delegatedReader.OpenAsync();
                await using var read=new NpgsqlCommand($"SELECT id FROM {table}",delegatedReader);
                check((int)(await read.ExecuteScalarAsync())! == 17,"Downstream recipient reads data through delegated privilege");
            }
            bool dependencyRefused=false;
            try { await service.ApplyAsync(request with {Revoke=true,GrantOption=true}); }
            catch(PostgresException ex) when(ex.SqlState=="2BP01") {dependencyRefused=true;}
            check(dependencyRefused,"RESTRICT refuses removing grant option with a dependent downstream grant");
            var retained=await inspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema);
            check(retained.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").GrantOption && retained.AclEntries.Any(e=>e.Grantee==future && e.Grantor==role),"Refused revoke rolls back without removing delegation or dependent ACL");
            await delegateService.ApplyAsync(downstream with {Revoke=true});
            await service.ApplyAsync(request with {Revoke=true,GrantOption=true});
            check((await inspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema)).Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT") is {Allowed:true,GrantOption:false},"Removing downstream grant permits revoking only delegation while preserving SELECT");
            var futureInspect=new EffectivePrivilegeService(new NpgsqlConnectionStringBuilder(connection){Username=future}.ConnectionString);
            check(!(await futureInspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema)).Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed,"Explicit downstream revoke removes recipient SELECT without CASCADE");
            bool warningRefused=false;
            try { await delegateService.ApplyAsync(downstream); }
            catch(InvalidOperationException ex) when(ex.Message.StartsWith("PostgreSQL did not confirm a clean operation:")) {warningRefused=true;}
            check(warningRefused,"A server warning for missing grant authority is not reported as success");
            check(!(await futureInspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema)).AclEntries.Any(e=>e.Grantee==future),"Warning path leaves no invented downstream grant");
            using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel(); bool refused=false;
                try {await service.ApplyAsync(downstream,canceled.Token);} catch(OperationCanceledException) {refused=true;}
                check(refused && !(await futureInspect.InspectAsync(PrivilegeTarget.Table,request.Name,schema)).AclEntries.Any(e=>e.Grantee==future),"Pre-canceled grant changes no ACL");
            }
            bool invalidPublic=false;
            try {await new PrivilegeService("not a connection string").ApplyAsync(request with {RecipientIsPublic=true,GrantOption=true});}
            catch(ArgumentException ex) when(ex.Message=="PUBLIC cannot receive grant options.") {invalidPublic=true;}
            check(invalidPublic,"Invalid PUBLIC grant-option request is refused before parsing connection");
        }
        finally { await Sql($"DROP DATABASE IF EXISTS {SqlIdentifier.Quote(database)};"); await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE; DROP ROLE IF EXISTS {qr}; DROP ROLE IF EXISTS {qf};"); }
    }
}
