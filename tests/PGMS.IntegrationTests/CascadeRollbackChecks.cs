using Npgsql;
using PGMS.PostgreSql;

internal static class CascadeRollbackChecks
{
    private sealed class RejectJournal(bool cancel) : IPrivilegeCommitJournal
    {
        internal PrivilegeCommitReceipt? Receipt;
        public Task RecordAsync(PrivilegeCommitReceipt receipt,CancellationToken token)
        {
            Receipt=receipt;
            return cancel ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(new IOException("Synthetic journal failure before COMMIT"));
        }
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        string child="cascade_rollback_"+Guid.NewGuid().ToString("N"),qc=SqlIdentifier.Quote(child),renamed=child+"_old";
        await f.Admin($"CREATE ROLE {qc};GRANT USAGE ON SCHEMA {f.QS} TO {f.QR}");
        await using var observer=new NpgsqlConnection(connection);await observer.OpenAsync();
        async Task Wait(Task pending,string app,int blocker)
        {
            var until=DateTime.UtcNow.AddSeconds(10);
            while(!pending.IsCompleted && DateTime.UtcNow<until)
            {
                await using var command=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND @blocker=ANY(pg_catalog.pg_blocking_pids(pid)))",observer);
                command.Parameters.AddWithValue("app",app);command.Parameters.AddWithValue("blocker",blocker);
                if((bool)(await command.ExecuteScalarAsync())!)return;
                await Task.Delay(20);
            }
            throw new Exception("CASCADE did not reach the exact fixture catalog lock.");
        }
        try
        {
            foreach(string? column in new string?[]{null,"value"})foreach(bool option in new[]{false,true})
            {
                string name="target_"+(column is null?"table":"column")+(option?"_option":"_all"),target=f.QS+"."+SqlIdentifier.Quote(name),privilege=column is null?"SELECT":"SELECT(value)";
                await f.Owner($"CREATE TABLE {target}(value integer,other integer);GRANT {privilege} ON {target} TO {f.QR} WITH GRANT OPTION;GRANT UPDATE ON {target} TO {f.QR}");
                await f.Reader($"GRANT {privilege} ON {target} TO {qc}");
                var request=new PrivilegeRequest(PrivilegeTarget.Table,name,f.Recipient,"SELECT",Revoke:true,Schema:f.Schema,GrantOption:option,Column:column);
                var catalog=await new PrivilegeGrantorCatalog(f.CreatorConnection).InspectAsync(request);var owner=catalog.Candidates.Single(x=>x.Name==f.Creator);
                request=request with{Grantor=new(catalog.Target.DatabaseOid,owner.Oid,owner.Name)};
                var preview=await new PrivilegeRevokeInspector(f.CreatorConnection).InspectAsync(request);
                var initial=(await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,name,f.Schema)).AclEntries.ToHashSet();
                foreach(bool cancel in new[]{false,true})
                {
                    var journal=new RejectJournal(cancel);var service=new PrivilegeService(f.CreatorConnection,journal);
                    bool refused=false;try{await service.ApplyReviewedRevokeAsync(preview);}catch(IOException)when(!cancel){refused=true;}catch(OperationCanceledException)when(cancel){refused=true;}
                    check(refused && journal.Receipt is not null,"CASCADE journal failure/cancellation occurs after guarded DDL and before COMMIT: "+name+"/"+cancel);
                    var actual=(await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,name,f.Schema)).AclEntries.ToHashSet();
                    check(initial.SetEquals(actual) && await service.ReconcileCommitAsync(journal.Receipt!)==PrivilegeCommitStatus.Aborted,"Failed journal rolls back root, dependent and unrelated ACL with aborted xid: "+name+"/"+cancel);
                }
                foreach(string scenario in new[]{"cancel","rename-dependent"})
                {
                    string app="PGMS.CascadeRollback."+Guid.NewGuid().ToString("N");
                    string settings=new NpgsqlConnectionStringBuilder(f.CreatorConnection){ApplicationName=app}.ConnectionString;
                    await using var locker=new NpgsqlConnection(connection);await locker.OpenAsync();await using var tx=await locker.BeginTransactionAsync();
                    string sql=column is null?"SELECT oid FROM pg_catalog.pg_class WHERE oid=@oid FOR UPDATE":"SELECT attnum FROM pg_catalog.pg_attribute WHERE attrelid=@oid AND attnum=1 FOR UPDATE";
                    await using(var command=new NpgsqlCommand(sql,locker,tx)){command.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,catalog.Target.ObjectOid);await command.ExecuteScalarAsync();}
                    using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var pending=new PrivilegeService(settings).ApplyReviewedRevokeAsync(preview,cancellation.Token);
                    bool changedName=false;
                    try
                    {
                        await Wait(pending,app,locker.ProcessID);
                        if(scenario=="cancel")cancellation.Cancel();
                        else {await f.Admin($"ALTER ROLE {qc} RENAME TO {SqlIdentifier.Quote(renamed)}");changedName=true;}
                        await tx.RollbackAsync();
                        bool refused=false;try{await pending;}catch(OperationCanceledException)when(scenario=="cancel"){refused=true;}catch(InvalidOperationException)when(scenario=="rename-dependent"){refused=true;}
                        check(refused,"CASCADE refuses after exact catalog wait: "+name+"/"+scenario);
                    }
                    finally
                    {
                        cancellation.Cancel();if(!pending.IsCompleted){try{await tx.RollbackAsync();}catch(InvalidOperationException){}try{await pending;}catch(Exception e)when(e is NpgsqlException or InvalidOperationException or OperationCanceledException){}}
                        if(changedName)await f.Admin($"ALTER ROLE {SqlIdentifier.Quote(renamed)} RENAME TO {qc}");
                    }
                    check(initial.SetEquals((await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,name,f.Schema)).AclEntries),"Refused locked CASCADE preserves complete ACL: "+name+"/"+scenario);
                }
            }
        }
        finally{await f.Admin($"DROP SCHEMA IF EXISTS {f.QS} CASCADE;DROP OWNED BY {qc};DROP ROLE {qc}");}
    }
}
