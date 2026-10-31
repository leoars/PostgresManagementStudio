using Npgsql;
using PGMS.PostgreSql;

internal static class PrivilegeRecipientChecks
{
    internal static async Task RunAsync(string connection, Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="recipient_"+suffix,role="recipient_role_"+suffix,old=role+"_old",side=role+"_side";
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),qo=SqlIdentifier.Quote(old),qside=SqlIdentifier.Quote(side),table=ObjectSql.Relation(schema,"target");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        async Task<bool> Has(uint oid){await using var cmd=new NpgsqlCommand("SELECT pg_catalog.has_table_privilege(@role,@table,'SELECT')",admin);cmd.Parameters.AddWithValue("role",NpgsqlTypes.NpgsqlDbType.Oid,oid);cmd.Parameters.AddWithValue("table",table);return (bool)(await cmd.ExecuteScalarAsync())!;}
        var service=new PrivilegeService(connection);
        var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",role,"SELECT",Schema:schema);
        try
        {
            await Sql($"CREATE ROLE {qr}; CREATE SCHEMA {qs}; CREATE TABLE {table}(id int); CREATE SEQUENCE {ObjectSql.Relation(schema,"counter")}");
            var recipient=(await service.InspectRecipientAsync(role))!;
            check(recipient.Name==role && recipient.Oid>0 && !recipient.IsPublic,"Recipient inspection captures named role OID");
            var cases=new[]{request,request with{Target=PrivilegeTarget.Database,Name="postgres",Privilege="CONNECT"},request with{Target=PrivilegeTarget.Schema,Name=schema,Privilege="USAGE"},request with{Target=PrivilegeTarget.Sequence,Name="counter",Privilege="USAGE"},request with{Column="id",Privilege="UPDATE"}};
            foreach(var item in cases)
            {
                var target=await service.InspectTargetAsync(item.Target,item.Name,item.Schema,item.Column);
                check(await service.ApplyInspectedAsync(item,target,recipient),"Recipient-bound GRANT commits: "+item.Target+"/"+item.Column);
                check(await service.ApplyInspectedAsync(item with{Revoke=true},target,recipient),"Recipient-bound REVOKE commits: "+item.Target+"/"+item.Column);
            }
            var identity=await service.InspectTargetAsync(request.Target,request.Name,schema);
            await Sql($"ALTER ROLE {qr} RENAME TO {qo}; CREATE ROLE {qr}");
            var replacement=(await service.InspectRecipientAsync(role))!;
            bool refused=false;try{await service.ApplyInspectedAsync(request,identity,recipient);}catch(InvalidOperationException ex)when(ex.Message.Contains("recipient identity",StringComparison.Ordinal)){refused=true;}
            check(refused && replacement.Oid!=recipient.Oid,"Same-name role replacement refuses inspected recipient");
            check(!await Has(recipient.Oid) && !await Has(replacement.Oid),"Both recipient OIDs retain original access after refusal");
            await Sql($"DROP ROLE {qr}; ALTER ROLE {qo} RENAME TO {qr}");
            var publicIdentity=(await service.InspectRecipientAsync("ignored",true))!;
            check(publicIdentity.IsPublic && publicIdentity.Oid==0,"PUBLIC identity is explicit and distinct from a quoted role");
            check(await service.ApplyInspectedAsync(request with{RecipientIsPublic=true},identity,publicIdentity),"Recipient-bound PUBLIC GRANT succeeds");
            check(await service.ApplyInspectedAsync(request with{RecipientIsPublic=true,Revoke=true},identity,publicIdentity),"Recipient-bound PUBLIC REVOKE succeeds");
            bool mismatch=false;try{await service.ApplyInspectedAsync(request,identity,publicIdentity);}catch(InvalidOperationException){mismatch=true;}
            check(mismatch,"PUBLIC identity cannot authorize a named-role request");

            // Hold only the ACL target catalog tuple. Native GRANT resolves B, then
            // waits here while names return to A. Post-name equality is insufficient.
            await Sql($"ALTER ROLE {qr} RENAME TO {qo}; CREATE ROLE {qr}");
            replacement=(await service.InspectRecipientAsync(role))!;
            await using(var locker=new NpgsqlConnection(connection))
            {
                await locker.OpenAsync();await using var held=await locker.BeginTransactionAsync();
                await using(var cmd=new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_class WHERE oid=@oid FOR UPDATE",locker,held)){cmd.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,identity.ObjectOid);await cmd.ExecuteScalarAsync();}
                var named=new NpgsqlConnectionStringBuilder(connection){ApplicationName="RecipientABA_"+suffix};
                Task<bool> applying=new PrivilegeService(named.ConnectionString).ApplyInspectedAsync(request,identity,recipient);
                bool waiting=false;
                try
                {
                    for(int i=0;i<100 && !applying.IsCompleted;i++)
                    {
                        await using var wait=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND @blocker=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                        wait.Parameters.AddWithValue("app",named.ApplicationName!);wait.Parameters.AddWithValue("blocker",locker.ProcessID);
                        waiting=(bool)(await wait.ExecuteScalarAsync())!;if(waiting)break;await Task.Delay(25);
                    }
                    check(waiting,"Recipient ABA observes native GRANT waiting on the held target tuple");
                    await Sql($"ALTER ROLE {qr} RENAME TO {qside}; ALTER ROLE {qo} RENAME TO {qr}");
                }
                finally {await held.RollbackAsync();}
                bool abaRefused=false;try{await applying;}catch(InvalidOperationException ex)when(ex.Message.Contains("recipient identity",StringComparison.Ordinal)){abaRefused=true;}
                check(abaRefused,"Complete ACL guard refuses recipient ABA after names return to inspected OID");
            }
            check(!await Has(recipient.Oid) && !await Has(replacement.Oid),"Recipient ABA rollback preserves ACL for both exact OIDs");
            await Sql($"DROP ROLE {qside}");
            var serializable=new NpgsqlConnectionStringBuilder(connection){Options="-c default_transaction_isolation=serializable"};
            check(await new PrivilegeService(serializable.ConnectionString).ApplyInspectedAsync(request,identity,recipient),"Guard explicitly uses ReadCommitted even when profile defaults to serializable");
            await service.ApplyAsync(request with{Revoke=true});
            var snapshot=await new EffectivePrivilegeService(connection).InspectAsync(request.Target,request.Name,schema,recipient:role);
            check(snapshot.RecipientIdentity==recipient,"Read-only ACL snapshot retains selected recipient identity");
            check(!await service.ApplyInspectedAsync(request with{Revoke=true},identity,recipient),"Idempotent REVOKE rolls back and reports no committed ACL change");
            await service.ApplyInspectedAsync(request,identity,recipient);
            check(!await service.ApplyInspectedAsync(request,identity,recipient) && await Has(recipient.Oid),"Idempotent GRANT rolls back without falsely confirming a recipient write");
            await service.ApplyAsync(request with{Revoke=true});
            bool missing=false;try{await service.ApplyCurrentAsync(request with{Role="missing_"+suffix});}catch(PostgresException ex)when(ex.SqlState=="42704"){missing=true;}
            check(missing,"Missing recipient preserves native SQLSTATE42704");
        }
        finally
        {
            await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE; DROP ROLE IF EXISTS {qr}; DROP ROLE IF EXISTS {qo}; DROP ROLE IF EXISTS {qside}");
        }
    }
}
