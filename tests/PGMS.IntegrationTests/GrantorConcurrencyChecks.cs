using Npgsql;
using PGMS.PostgreSql;

internal static class GrantorConcurrencyChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        string child="grantor_race_"+Guid.NewGuid().ToString("N"),qc=SqlIdentifier.Quote(child),old=f.Creator+"_old",qold=SqlIdentifier.Quote(old),side=f.Creator+"_side",qside=SqlIdentifier.Quote(side);
        await f.Admin($"CREATE ROLE {qc};GRANT {f.QO} TO {f.QR} WITH INHERIT FALSE,SET TRUE");
        await f.Owner($"CREATE TABLE {f.QS}.target(value integer)");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",child,"SELECT",Schema:f.Schema);
        var catalog=await new PrivilegeGrantorCatalog(f.RecipientConnection).InspectAsync(request);
        var candidate=catalog.Candidates.Single(x=>x.Name==f.Creator);
        request=request with{Grantor=new(catalog.Target.DatabaseOid,candidate.Oid,candidate.Name)};
        var recipient=(await new PrivilegeService(connection).InspectRecipientAsync(child))!;
        async Task<bool> Has(){await using var c=new NpgsqlCommand("SELECT has_table_privilege(@role,@table,'SELECT')",admin);c.Parameters.AddWithValue("role",child);c.Parameters.AddWithValue("table",f.QS+".target");return (bool)(await c.ExecuteScalarAsync())!;}
        async Task Wait(Task pending,string app,int blocker)
        {
            var until=DateTime.UtcNow.AddSeconds(10);
            while(!pending.IsCompleted && DateTime.UtcNow<until)
            {
                await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND @blocker=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                c.Parameters.AddWithValue("app",app);c.Parameters.AddWithValue("blocker",blocker);
                if((bool)(await c.ExecuteScalarAsync())!)return;await Task.Delay(20);
            }
            throw new Exception("Selected grantor operation did not reach fixture table lock.");
        }
        try
        {
            await f.Admin($"ALTER ROLE {f.QO} RENAME TO {qold};CREATE ROLE {f.QO};GRANT {f.QO} TO {f.QR} WITH SET TRUE");
            try
            {
                bool refused=false;try{await new PrivilegeService(f.RecipientConnection).ApplyInspectedAsync(request,catalog.Target,recipient);}catch(InvalidOperationException){refused=true;}
                check(refused && !await Has(),"Same-name grantor replacement cannot receive a stale selection");
            }
            finally{await f.Admin($"DROP ROLE {f.QO};ALTER ROLE {qold} RENAME TO {f.QO}");}
            foreach(string scenario in new[]{"membership","rename","aba","cancel"})foreach(bool revoke in new[]{false,true})
            {
                await f.Admin($"GRANT {f.QO} TO {f.QR} WITH INHERIT FALSE,SET TRUE");
                await f.Owner($"{(revoke?"GRANT SELECT ON":"REVOKE SELECT ON")} {f.QS}.target {(revoke?"TO":"FROM")} {qc}");
                string app="PGMS.GrantorRace."+Guid.NewGuid().ToString("N");
                string settings=new NpgsqlConnectionStringBuilder(f.RecipientConnection){ApplicationName=app}.ConnectionString;
                await using var locker=new NpgsqlConnection(connection);await locker.OpenAsync();await using var tx=await locker.BeginTransactionAsync();
                await using(var c=new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_class WHERE oid=@oid FOR UPDATE",locker,tx))
                {c.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,catalog.Target.ObjectOid);await c.ExecuteScalarAsync();}
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var pending=new PrivilegeService(settings).ApplyInspectedAsync(request with{Revoke=revoke},catalog.Target,recipient,deadline.Token);
                bool renamed=false,sideCreated=false;
                try
                {
                    await Wait(pending,app,locker.ProcessID);check(true,"Grantor bound before exact target lock: "+scenario+"/"+revoke);
                    if(scenario=="membership")await f.Admin($"REVOKE {f.QO} FROM {f.QR}");
                    if(scenario is "rename" or "aba"){await f.Admin($"ALTER ROLE {f.QO} RENAME TO {qold}");renamed=true;}
                    if(scenario=="aba")
                    {
                        await f.Admin($"CREATE ROLE {f.QO};ALTER ROLE {f.QO} RENAME TO {qside};ALTER ROLE {qold} RENAME TO {f.QO}");
                        renamed=false;sideCreated=true;
                    }
                    if(scenario=="cancel")deadline.Cancel();
                    await tx.RollbackAsync();
                    bool changed=false,refused=false;
                    try{changed=await pending;}
                    catch(OperationCanceledException)when(scenario=="cancel"){refused=true;}
                    catch(InvalidOperationException)when(scenario!="aba"){refused=true;}
                    catch(PostgresException e)when(scenario=="rename" && e.SqlState=="42704"){refused=true;}
                    if(scenario=="aba")
                    {
                        check(changed && await Has()==!revoke,"Bound grantor OID survives a restored-name ABA without switching role: "+revoke);
                        var actual=await new EffectivePrivilegeService(connection).InspectAsync(request.Target,request.Name,f.Schema);
                        check(actual.AclEntries.Where(e=>e.Grantee==child && e.Privilege=="SELECT").All(e=>e.GrantorOid==candidate.Oid),"ABA never records temporary same-name grantor: "+revoke);
                    }
                    else
                    {
                        check(refused && !changed,"Concurrent grantor change/cancel refuses commit: "+scenario+"/"+revoke);
                        check(await Has()==revoke,"Refused operation preserves recipient ACL: "+scenario+"/"+revoke);
                    }
                }
                finally
                {
                    deadline.Cancel();if(!pending.IsCompleted){try{await tx.RollbackAsync();}catch(InvalidOperationException){}try{await pending;}catch(Exception e)when(e is NpgsqlException or InvalidOperationException or OperationCanceledException){}}
                    if(renamed)await f.Admin($"ALTER ROLE {qold} RENAME TO {f.QO}");
                    if(sideCreated)await f.Admin($"DROP ROLE {qside}");
                }
            }
        }
        finally{await f.Admin($"DROP SCHEMA IF EXISTS {f.QS} CASCADE;DROP OWNED BY {qc};DROP ROLE {qc}");}
    }
}
