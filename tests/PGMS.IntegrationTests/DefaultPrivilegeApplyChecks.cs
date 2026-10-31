using Npgsql;
using PGMS.PostgreSql;

internal static class DefaultPrivilegeApplyChecks
{
    private sealed class Journal: IPrivilegeCommitJournal
    {
        internal bool Fail;internal PrivilegeCommitReceipt? Receipt;
        public Task RecordAsync(PrivilegeCommitReceipt receipt,CancellationToken token)
        {Receipt=receipt;if(Fail)throw new IOException("Synthetic default journal failure");return Task.CompletedTask;}
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),owner="dapply_owner_"+suffix,role="dapply_role_"+suffix,schema="dapply_"+suffix;
        string qo=SqlIdentifier.Quote(owner),qr=SqlIdentifier.Quote(role),qs=SqlIdentifier.Quote(schema);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        string owned=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        var catalog=new DefaultPrivilegeCatalog(owned);var journal=new Journal();var service=new DefaultPrivilegeService(owned,journal);
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr};CREATE SCHEMA {qs} AUTHORIZATION {qo}");
            var recipient=(await new PrivilegeService(owned).InspectRecipientAsync(role))!;
            foreach(var target in Enum.GetValues<DefaultPrivilegeTarget>())
            {
                string privilege=target==DefaultPrivilegeTarget.Tables?"SELECT":target==DefaultPrivilegeTarget.Routines?"EXECUTE":"USAGE";
                var request=new DefaultPrivilegeRequest(target,owner,role,privilege,target==DefaultPrivilegeTarget.Schemas?null:schema);
                var initial=await catalog.InspectAsync(target,owner,request.Schema);
                check(await service.ApplyInspectedAsync(request,initial,recipient),"Guarded default Apply commits by ordinary creator: "+target);
                check(await service.ReconcileCommitAsync(journal.Receipt!)==PrivilegeCommitStatus.Committed,"Default receipt confirms committed transaction: "+target);
                var granted=await catalog.InspectAsync(target,owner,request.Schema);
                check(!await service.ApplyInspectedAsync(request,granted,recipient),"Identical default policy rolls back without claiming a new commit: "+target);
                check(await service.ApplyInspectedAsync(request with{GrantOption=true},granted,recipient),"Guarded default Apply grants delegation: "+target);
                var delegated=await catalog.InspectAsync(target,owner,request.Schema);
                check(await service.ApplyInspectedAsync(request with{GrantOption=true,Revoke=true},delegated,recipient),"Guarded default Apply revokes only delegation: "+target);
                granted=await catalog.InspectAsync(target,owner,request.Schema);
                check(await service.ApplyInspectedAsync(request with{Revoke=true},granted,recipient),"Guarded default revoke supports deletion of policy row: "+target);
                var reverted=await catalog.InspectAsync(target,owner,request.Schema);
                check(reverted.GlobalPolicyOid==0 && reverted.SchemaPolicyOid==0 && reverted.Entries.SequenceEqual(initial.Entries),"Reverting default restores absent policy and exact baseline: "+target);
            }
            var table=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Tables,owner,role,"SELECT",schema);
            var observed=await catalog.InspectAsync(table.Target,owner,schema);
            journal.Fail=true;bool failed=false;try{await service.ApplyInspectedAsync(table,observed,recipient);}catch(IOException){failed=true;}
            check(failed && (await catalog.InspectAsync(table.Target,owner,schema)).SchemaPolicyOid==0,"Default journal failure rolls back newly created policy");
            check(await service.ReconcileCommitAsync(journal.Receipt!)==PrivilegeCommitStatus.Aborted,"Failed default journal receipt confirms aborted without replay");journal.Fail=false;
            await Sql((table with{Privilege="INSERT"}).ToSql());
            bool stale=false;try{await service.ApplyInspectedAsync(table,observed,recipient);}catch(InvalidOperationException){stale=true;}
            check(stale && !(await catalog.InspectAsync(table.Target,owner,schema)).Entries.Any(e=>e.Recipient==role && e.Privilege=="SELECT"),"Changed default policy rejects stale inspection before adding privilege");
            await Sql((table with{Privilege="INSERT",Revoke=true}).ToSql());
            observed=await catalog.InspectAsync(table.Target,owner,schema);
            await Sql($"DROP ROLE {qr};CREATE ROLE {qr}");
            stale=false;try{await service.ApplyInspectedAsync(table,observed,recipient);}catch(InvalidOperationException){stale=true;}
            check(stale && (await catalog.InspectAsync(table.Target,owner,schema)).SchemaPolicyOid==0,"Recreated default recipient cannot inherit inspected identity");
            recipient=(await new PrivilegeService(owned).InspectRecipientAsync(role))!;
            foreach(bool cancel in new[]{true,false})
            {
                await Sql((table with{Privilege="INSERT"}).ToSql());
                var lockedSnapshot=await catalog.InspectAsync(table.Target,owner,schema);
                await using var locker=new NpgsqlConnection(connection);await locker.OpenAsync();await using var tx=await locker.BeginTransactionAsync();
                await using(var c=new NpgsqlCommand((table with{Privilege="UPDATE"}).ToSql(),locker,tx))await c.ExecuteNonQueryAsync();
                string app="PGMS.DefaultWait."+Guid.NewGuid().ToString("N");
                string waiting=new NpgsqlConnectionStringBuilder(owned){ApplicationName=app}.ConnectionString;
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var pending=new DefaultPrivilegeService(waiting).ApplyInspectedAsync(table,lockedSnapshot,recipient,deadline.Token);
                try
                {
                    bool blocked=false;var until=DateTime.UtcNow.AddSeconds(8);
                    while(!pending.IsCompleted && DateTime.UtcNow<until)
                    {
                        await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND @pid=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                        c.Parameters.AddWithValue("app",app);c.Parameters.AddWithValue("pid",locker.ProcessID);blocked=(bool)(await c.ExecuteScalarAsync())!;
                        if(blocked)break;await Task.Delay(20);
                    }
                    check(blocked,"Default Apply reaches exact native policy lock: "+(cancel?"cancel":"conflict"));
                    if(cancel)deadline.Cancel();else await tx.CommitAsync();
                    bool refused=false;
                    try{await pending;}catch(OperationCanceledException)when(cancel){refused=true;}catch(PostgresException ex)when(!cancel && (ex.SqlState=="40001" || (ex.SqlState=="XX000" && ex.MessageText=="tuple concurrently updated"))){refused=true;}
                    check(refused,"Default Apply refuses blocked write without automatic retry: "+(cancel?"cancel":"native concurrent update"));
                    if(cancel)await tx.RollbackAsync();
                    var preserved=await catalog.InspectAsync(table.Target,owner,schema);
                    check(!preserved.Entries.Any(e=>e.Recipient==role && e.Privilege=="SELECT") && preserved.Entries.Any(e=>e.Recipient==role && e.Privilege=="UPDATE")==!cancel,"Canceled/conflicting default write preserves committed external policy only");
                }
                finally{deadline.Cancel();if(!pending.IsCompleted){try{await tx.RollbackAsync();}catch(InvalidOperationException){}try{await pending;}catch(Exception ex)when(ex is NpgsqlException or OperationCanceledException or InvalidOperationException){}}}
                await Sql((table with{Privilege="INSERT",Revoke=true}).ToSql());
                await Sql((table with{Privilege="UPDATE",Revoke=true}).ToSql());
            }
            await Sql($"DROP SCHEMA {qs};CREATE SCHEMA {qs} AUTHORIZATION {qo}");
            stale=false;try{await service.ApplyInspectedAsync(table,observed,recipient);}catch(InvalidOperationException){stale=true;}
            check(stale,"Recreated default schema rejects stale scope identity");
            using var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;
            try{await new DefaultPrivilegeService("Host=invalid").ApplyInspectedAsync(table,observed,recipient,canceled.Token);}catch(OperationCanceledException){stopped=true;}
            check(stopped,"Pre-canceled default Apply does not connect");
        }
        finally{await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP OWNED BY {qo};DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {qo}");}
    }
}
