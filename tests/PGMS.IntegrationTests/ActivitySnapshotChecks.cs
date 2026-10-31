using Npgsql;
using PGMS.PostgreSql;

internal static class ActivitySnapshotChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string app="PGMS.Limit."+Guid.NewGuid().ToString("N")[..10];
        string target=new NpgsqlConnectionStringBuilder(connection){ApplicationName=app,Pooling=false}.ConnectionString;
        await using var a=new NpgsqlConnection(target);await a.OpenAsync();
        await using var b=new NpgsqlConnection(target);await b.OpenAsync();
        await using var c=new NpgsqlConnection(target);await c.OpenAsync();
        var service=new ActivityService(connection);var filter=new ActivityFilter(Application:app);
        var limited=await service.ReadSnapshotAsync(filter,2);
        check(limited.IsTruncated && limited.Rows.Count==2 && limited.MaximumRows==2,"Extra activity row proves truncation without exposing sentinel");
        check(limited.Rows.Select(x=>x.Pid).SequenceEqual(new[]{a.ProcessID,b.ProcessID,c.ProcessID}.Order().Take(2)),"Bounded activity snapshot retains deterministic PID order");
        var exact=await service.ReadSnapshotAsync(filter,3);
        check(!exact.IsTruncated && exact.Rows.Count==3,"Exactly the activity limit is complete, not truncated");
        var fewer=await service.ReadSnapshotAsync(filter,4);
        check(!fewer.IsTruncated && fewer.Rows.Count==3,"Fewer sessions than limit reports complete snapshot");
        var empty=await service.ReadSnapshotAsync(new ActivityFilter(Application:app+"missing"),1);
        check(!empty.IsTruncated && empty.Rows.Count==0,"Empty filtered snapshot is complete");
        foreach(int invalid in new[]{0,-1,1001})
        {
            bool rejected=false;try{await service.ReadSnapshotAsync(filter,invalid);}catch(ArgumentOutOfRangeException){rejected=true;}
            check(rejected,"Invalid activity limit rejected before connection: "+invalid);
        }
        using var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;
        try{await service.ReadSnapshotAsync(filter,2,canceled.Token);}catch(OperationCanceledException){stopped=true;}
        check(stopped,"Canceled activity snapshot does not return partial rows");
        check((await service.ReadAsync(filter)).Count==3,"Legacy activity reader preserves complete bounded rows");
        var selected=exact.Rows.Single(x=>x.Pid==c.ProcessID);
        check(!await service.SignalAsync(selected with{BackendStart=selected.BackendStart.AddSeconds(-1)},false),"Stale PID plus start refuses cancel after snapshot change");
        await using var sleep=new NpgsqlCommand("SELECT pg_sleep(30)",c);var pending=sleep.ExecuteNonQueryAsync();
        try
        {
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(DateTime.UtcNow<deadline && !(await service.ReadAsync(filter)).Any(x=>x.Pid==c.ProcessID && x.State=="active"))await Task.Delay(25);
            check(await service.SignalAsync(selected,false),"Snapshot identity can cancel real running query");
            bool canceledQuery=false;try{await pending;}catch(PostgresException ex) when(ex.SqlState=="57014"){canceledQuery=true;}
            check(canceledQuery && c.State==System.Data.ConnectionState.Open,"Cancel Query keeps target connection open");
            check(await service.SignalAsync(selected,true),"Terminate confirms isolated session exit with guarded identity");
        }
        finally{if(!pending.IsCompleted){sleep.Cancel();try{await pending;}catch(OperationCanceledException){}catch(PostgresException){}}}
    }
}
