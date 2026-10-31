using Npgsql;
using PGMS.PostgreSql;

internal static class RoutineConcurrencyChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="routine_aba_"+suffix,owner="raba_owner_"+suffix,
            role="raba_role_"+suffix,oldRole=role+"_old",sideRole=role+"_side";
        string qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner),qr=SqlIdentifier.Quote(role);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        string AsOwner(string app)=>new NpgsqlConnectionStringBuilder(connection){Username=owner,ApplicationName=app,Pooling=false}.ConnectionString;
        async Task<uint> Oid(string signature)
        {await using var c=new NpgsqlCommand("SELECT @signature::regprocedure::oid",admin);c.Parameters.AddWithValue("signature",signature);return (uint)(await c.ExecuteScalarAsync())!;}
        async Task<bool> Has(uint routine,uint recipient)
        {await using var c=new NpgsqlCommand("SELECT pg_catalog.has_function_privilege(@role,@oid,'EXECUTE')",admin);c.Parameters.AddWithValue("role",NpgsqlTypes.NpgsqlDbType.Oid,recipient);c.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,routine);return (bool)(await c.ExecuteScalarAsync())!;}
        async Task Wait(Task pending,string app,int blocker)
        {
            var until=DateTime.UtcNow.AddSeconds(10);
            while(!pending.IsCompleted && DateTime.UtcNow<until)
            {
                await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND @blocker=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                c.Parameters.AddWithValue("app",app);c.Parameters.AddWithValue("blocker",blocker);
                if((bool)(await c.ExecuteScalarAsync())!)return;await Task.Delay(25);
            }
            throw new Exception("Routine race did not reach its exact catalog lock.");
        }
        async Task Lock(NpgsqlConnection c,NpgsqlTransaction tx,uint oid)
        {await using var cmd=new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_proc WHERE oid=@oid FOR UPDATE",c,tx);cmd.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,oid);await cmd.ExecuteScalarAsync();}
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr};CREATE SCHEMA {qs} AUTHORIZATION {qo}");
            var service=new PrivilegeService(AsOwner("PGMS.RoutineABA.Setup"));
            var recipient=(await service.InspectRecipientAsync(role))!;
            foreach(bool procedure in new[]{false,true})foreach(bool revoke in new[]{false,true})
            {
                string kind=procedure?"PROCEDURE":"FUNCTION",body=procedure?"LANGUAGE plpgsql AS 'BEGIN NULL; END'":"RETURNS integer LANGUAGE sql AS 'SELECT $1'";
                string tag=(procedure?"procedure":"function")+"/"+(revoke?"revoke":"grant");
                await Sql($"SET ROLE {qo};CREATE {kind} {qs}.target(integer) {body};REVOKE EXECUTE ON ROUTINE {qs}.target(integer) FROM PUBLIC;RESET ROLE");
                uint original=await Oid($"{qs}.target(integer)");
                var selected=await new RoutinePrivilegeCatalog(AsOwner("PGMS.RoutineABA.Setup")).InspectAsync(original);
                var request=new PrivilegeRequest(PrivilegeTarget.Routine,"target",role,"EXECUTE",revoke,schema){Routine=selected};
                var identity=await service.InspectTargetAsync(request.Target,request.Name,schema,routine:selected);
                if(revoke)await service.ApplyAsync(request with{Revoke=false});
                await Sql($"ALTER {kind} {qs}.target(integer) RENAME TO original;SET ROLE {qo};CREATE {kind} {qs}.target(integer) {body};REVOKE EXECUTE ON ROUTINE {qs}.target(integer) FROM PUBLIC;RESET ROLE");
                uint replacement=await Oid($"{qs}.target(integer)");
                if(revoke)await Sql($"GRANT EXECUTE ON ROUTINE {qs}.target(integer) TO {qr}");
                bool swapped=false;
                await using(var locker=new NpgsqlConnection(connection))
                {
                    await locker.OpenAsync();await using var tx=await locker.BeginTransactionAsync();await Lock(locker,tx,replacement);
                    string app="PGMS.RoutineABA.Target."+Guid.NewGuid().ToString("N");using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    var pending=new PrivilegeService(AsOwner(app)).ApplyInspectedAsync(request,identity,recipient,deadline.Token);
                    try
                    {
                        await Wait(pending,app,locker.ProcessID);check(true,"Routine target race reaches replacement pg_proc tuple: "+tag);
                        try
                        {
                            await using(var c=new NpgsqlCommand($"ALTER {kind} {qs}.target(integer) RENAME TO replacement;ALTER {kind} {qs}.original(integer) RENAME TO target",locker,tx){CommandTimeout=10})await c.ExecuteNonQueryAsync();
                            await tx.CommitAsync();swapped=true;
                        }
                        catch(PostgresException ex)when(ex.SqlState=="40P01"){await tx.RollbackAsync();}
                        bool refused=false;string outcome="";
                        try{refused=!await pending;outcome="rolled-back empty ACL delta";}
                        catch(InvalidOperationException){refused=true;outcome="target identity guard";}
                        catch(PostgresException ex)when(ex.SqlState=="40P01"){refused=true;outcome="native deadlock refusal";}
                        catch(PostgresException ex)when(ex.SqlState=="XX000" && ex.MessageText=="tuple concurrently updated"){refused=true;outcome="native concurrent tuple refusal";}
                        check(refused,"Routine target ABA refuses commit ("+outcome+", swap="+swapped+"): "+tag);
                        check(await Has(original,recipient.Oid)==revoke && await Has(replacement,recipient.Oid)==revoke,"Target race preserves both exact routine ACLs: "+tag);
                    }
                    finally
                    {
                        deadline.Cancel();if(!pending.IsCompleted){try{await tx.RollbackAsync();}catch(InvalidOperationException){}try{await pending;}catch(Exception ex)when(ex is NpgsqlException or OperationCanceledException or InvalidOperationException){}}
                    }
                }
                await Sql($"DROP {kind} {qs}.target(integer);DROP {kind} {qs}.{(swapped?"replacement":"original")}(integer)");

                await Sql($"SET ROLE {qo};CREATE {kind} {qs}.target(integer) {body};REVOKE EXECUTE ON ROUTINE {qs}.target(integer) FROM PUBLIC;RESET ROLE");
                original=await Oid($"{qs}.target(integer)");selected=await new RoutinePrivilegeCatalog(AsOwner("PGMS.RoutineABA.Setup")).InspectAsync(original);
                request=request with{Routine=selected};identity=await service.InspectTargetAsync(request.Target,request.Name,schema,routine:selected);
                if(revoke)await service.ApplyAsync(request with{Revoke=false});
                await Sql($"ALTER ROLE {qr} RENAME TO {SqlIdentifier.Quote(oldRole)};CREATE ROLE {qr}");
                var replacementRecipient=(await service.InspectRecipientAsync(role))!;
                if(revoke)await Sql($"GRANT EXECUTE ON ROUTINE {qs}.target(integer) TO {qr}");
                await using(var locker=new NpgsqlConnection(connection))
                {
                    await locker.OpenAsync();await using var tx=await locker.BeginTransactionAsync();await Lock(locker,tx,original);
                    string app="PGMS.RoutineABA.Recipient."+Guid.NewGuid().ToString("N");using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    var pending=new PrivilegeService(AsOwner(app)).ApplyInspectedAsync(request,identity,recipient,deadline.Token);
                    try
                    {
                        await Wait(pending,app,locker.ProcessID);check(true,"Routine recipient race reaches pg_proc ACL lock: "+tag);
                        await Sql($"ALTER ROLE {qr} RENAME TO {SqlIdentifier.Quote(sideRole)};ALTER ROLE {SqlIdentifier.Quote(oldRole)} RENAME TO {qr}");
                        await tx.RollbackAsync();
                        bool refused=false;try{await pending;}catch(InvalidOperationException ex)when(ex.Message.Contains("recipient identity",StringComparison.Ordinal)){refused=true;}
                        check(refused,"Complete routine ACL guard rejects recipient ABA after names return: "+tag);
                        check(await Has(original,recipient.Oid)==revoke && await Has(original,replacementRecipient.Oid)==revoke,"Recipient race preserves both exact role ACLs: "+tag);
                    }
                    finally
                    {
                        deadline.Cancel();if(!pending.IsCompleted){try{await tx.RollbackAsync();}catch(InvalidOperationException){}try{await pending;}catch(Exception ex)when(ex is NpgsqlException or OperationCanceledException or InvalidOperationException){}}
                    }
                }
                await Sql($"DROP {kind} {qs}.target(integer);DROP ROLE {SqlIdentifier.Quote(sideRole)}");
            }
        }
        finally{await Sql($"RESET ROLE;DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qo};DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {SqlIdentifier.Quote(oldRole)};DROP ROLE IF EXISTS {SqlIdentifier.Quote(sideRole)}");}
    }
}
