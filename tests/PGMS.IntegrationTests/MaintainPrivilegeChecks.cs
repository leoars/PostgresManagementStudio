using Npgsql;
using PGMS.PostgreSql;

internal static class MaintainPrivilegeChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="maintain_"+suffix,owner="maintain_owner_"+suffix,worker="maintain_worker_"+suffix,downstream="maintain_down_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner),qw=SqlIdentifier.Quote(worker),qd=SqlIdentifier.Quote(downstream),table=ObjectSql.Relation(schema,"target");
        var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",worker,"MAINTAIN",Schema:schema);
        check(request.ToSql()==$"GRANT MAINTAIN ON TABLE {table} TO {qw};","MAINTAIN scripts exact table and recipient identifiers");
        bool column=false;try{(request with{Column="id"}).ToSql();}catch(ArgumentException){column=true;}
        check(column,"MAINTAIN cannot be emitted as a column privilege");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        string As(string role)=>new NpgsqlConnectionStringBuilder(connection){Username=role,Pooling=false}.ConnectionString;
        async Task<bool> Lock(string role)
        {
            await using var client=new NpgsqlConnection(As(role));await client.OpenAsync();
            await using var tx=await client.BeginTransactionAsync();
            try
            {
                await using(var cmd=new NpgsqlCommand($"LOCK TABLE {table} IN EXCLUSIVE MODE",client,tx))await cmd.ExecuteNonQueryAsync();
                await using var held=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_locks WHERE pid=pg_catalog.pg_backend_pid() AND relation=@table::regclass AND mode='ExclusiveLock' AND granted)",client,tx);
                held.Parameters.AddWithValue("table",table);return (bool)(await held.ExecuteScalarAsync())!;
            }
            catch(PostgresException ex)when(ex.SqlState=="42501"){return false;}
            finally{await tx.RollbackAsync();}
        }
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qw} LOGIN;CREATE ROLE {qd} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo};CREATE TABLE {table}(id int);INSERT INTO {table} VALUES(77);ALTER TABLE {table} OWNER TO {qo};GRANT USAGE ON SCHEMA {qs} TO {qw},{qd}");
            check(!await Lock(worker),"Non-owner without MAINTAIN cannot acquire maintenance lock");
            var service=new PrivilegeService(As(owner));
            var target=await service.InspectTargetAsync(request.Target,request.Name,schema);
            var recipient=(await service.InspectRecipientAsync(worker))!;
            check(await service.ApplyInspectedAsync(request with{GrantOption=true},target,recipient),"Limited owner grants MAINTAIN with inspected target and recipient");
            var state=await new EffectivePrivilegeService(As(worker)).InspectAsync(request.Target,request.Name,schema);
            check(state.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="MAINTAIN") is{Allowed:true,GrantOption:true} && !state.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed,"MAINTAIN delegation does not grant table data SELECT");
            check(await Lock(worker),"MAINTAIN authorizes actual EXCLUSIVE relation lock for a non-owner");
            var delegateService=new PrivilegeService(As(worker));var child=request with{Role=downstream};
            await delegateService.ApplyAsync(child);
            check(await Lock(downstream),"Delegated MAINTAIN authorizes downstream maintenance operation");
            bool dependency=false;try{await service.ApplyAsync(request with{GrantOption=true,Revoke=true});}catch(PostgresException ex)when(ex.SqlState=="2BP01"){dependency=true;}
            check(dependency && await Lock(worker) && await Lock(downstream),"RESTRICT preserves both maintenance grants when delegation has dependencies");
            await delegateService.ApplyAsync(child with{Revoke=true});
            check(!await Lock(downstream),"Explicit downstream revoke removes maintenance authority");
            await service.ApplyAsync(request with{GrantOption=true,Revoke=true});
            state=await new EffectivePrivilegeService(As(worker)).InspectAsync(request.Target,request.Name,schema);
            check(state.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="MAINTAIN") is{Allowed:true,GrantOption:false},"Grant-option-only revoke preserves base MAINTAIN");
            await service.ApplyAsync(request with{Revoke=true});check(!await Lock(worker),"Base MAINTAIN revoke removes maintenance authority");
            await service.ApplyAsync(request with{RecipientIsPublic=true,Role=""});check(await Lock(worker),"Explicit PUBLIC MAINTAIN authorizes existing non-owner");
            await service.ApplyAsync(request with{RecipientIsPublic=true,Role="",Revoke=true});check(!await Lock(worker),"PUBLIC MAINTAIN revoke removes that source of authority");
            await using var preserved=new NpgsqlCommand($"SELECT id FROM {table}",admin);check((int)(await preserved.ExecuteScalarAsync())! ==77,"Maintenance permission operations preserve fixture data");
        }
        finally{await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qo};DROP ROLE IF EXISTS {qw};DROP ROLE IF EXISTS {qd}");}
    }
}
