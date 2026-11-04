using Npgsql;
using PGMS.PostgreSql;

internal static class ActivityBlockingChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var blocker=new NpgsqlConnection(connection);await blocker.OpenAsync();
        await using var waiter=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){ApplicationName="PGMS.Blocked.Literal_%_",Pooling=false}.ConnectionString);await waiter.OpenAsync();
        long key=Random.Shared.NextInt64(1,long.MaxValue);var service=new ActivityService(connection);
        await using(var acquire=new NpgsqlCommand("SELECT pg_advisory_lock(@key)",blocker)){acquire.Parameters.AddWithValue("key",key);await acquire.ExecuteNonQueryAsync();}
        try
        {
            await using var waiting=new NpgsqlCommand("SELECT pg_advisory_lock(@key)",waiter);waiting.Parameters.AddWithValue("key",key);Task pending=waiting.ExecuteNonQueryAsync();
            try
            {
                IReadOnlyList<ActivitySession> rows=[];var deadline=DateTime.UtcNow.AddSeconds(10);
                var filter=new ActivityFilter(Application:"  %  ",State:"active",Pid:waiter.ProcessID){BlockedOnly=true};
                while(DateTime.UtcNow<deadline){rows=await service.ReadAsync(filter);if(rows.Count>0)break;await Task.Delay(25);}
                check(rows.Count==1 && rows[0].BlockingPids.Contains(blocker.ProcessID),"Blocked-only filter finds actual advisory-lock waiter with literal application filter");
                check((await service.ReadAsync(new ActivityFilter(Pid:blocker.ProcessID){BlockedOnly=true})).Count==0,"Blocked-only filter excludes the unblocked lock holder");
                check((await service.ReadAsync(filter with{Application="PGMS.Blocked.*"})).Count==0,"Application wildcard remains literal with blocked-only filter");
                check((await service.ReadAsync(new ActivityFilter(Pid:blocker.ProcessID))).Count==1,"Default activity filter preserves unblocked sessions");
            }
            finally{waiting.Cancel();try{await pending;}catch(OperationCanceledException){}catch(PostgresException ex) when(ex.SqlState=="57014"){}}
            check((await service.ReadAsync(new ActivityFilter(Pid:waiter.ProcessID){BlockedOnly=true})).Count==0,"Canceled waiter disappears from blocked-only snapshot");
        }
        finally{await using var release=new NpgsqlCommand("SELECT pg_advisory_unlock(@key)",blocker);release.Parameters.AddWithValue("key",key);await release.ExecuteNonQueryAsync();}
        await HeadBlockerChainAsync(connection,service,check);
        foreach(var invalid in new[]{new ActivityFilter(Pid:0),new(Pid:-1),new(Database:null!)})
        {bool refused=false;try{invalid.Validate();}catch(ArgumentException){refused=true;}check(refused,"Invalid activity filter is refused before connecting");}
        bool nullRefused=false;try{await service.ReadAsync(null!);}catch(ArgumentNullException){nullRefused=true;}check(nullRefused,"Null activity filter is explicitly refused");
    }
    // Chain head -> middle -> tail on two advisory locks: only the head blocks without waiting itself.
    private static async Task HeadBlockerChainAsync(string connection,ActivityService service,Action<bool,string> check)
    {
        string app="PGMS.HeadBlocker."+Guid.NewGuid().ToString("N")[..8];string target=new NpgsqlConnectionStringBuilder(connection){ApplicationName=app,Pooling=false}.ConnectionString;
        await using var head=new NpgsqlConnection(target);await head.OpenAsync();
        await using var middle=new NpgsqlConnection(target);await middle.OpenAsync();
        await using var tail=new NpgsqlConnection(target);await tail.OpenAsync();
        long first=Random.Shared.NextInt64(1,long.MaxValue/2),second=first+1;
        async Task Lock(NpgsqlConnection c,long key){await using var cmd=new NpgsqlCommand("SELECT pg_advisory_lock(@key)",c);cmd.Parameters.AddWithValue("key",key);await cmd.ExecuteNonQueryAsync();}
        await Lock(head,first);await Lock(middle,second);
        var waits=new List<(NpgsqlCommand Command,Task Pending)>();
        try
        {
            foreach(var (c,key) in new[]{(middle,first),(tail,second)}){var cmd=new NpgsqlCommand("SELECT pg_advisory_lock(@key)",c);cmd.Parameters.AddWithValue("key",key);waits.Add((cmd,cmd.ExecuteNonQueryAsync()));}
            IReadOnlyList<ActivitySession> rows=[];var deadline=DateTime.UtcNow.AddSeconds(10);
            while(DateTime.UtcNow<deadline){rows=await service.ReadAsync(new ActivityFilter(Application:app));if(rows.Count(r=>r.BlockingPids.Length>0)==2)break;await Task.Delay(25);}
            ActivitySession Row(NpgsqlConnection c)=>rows.Single(r=>r.Pid==c.ProcessID);
            check(Row(head).IsHeadBlocker && Row(head).HeadBlocker=="Yes" && Row(head).BlockingPids.Length==0,"Head blocker marks the lock holder at the root of a real chain");
            check(!Row(middle).IsHeadBlocker && Row(middle).BlockingPids.SequenceEqual([head.ProcessID]),"A session that blocks others while waiting is not a head blocker");
            check(!Row(tail).IsHeadBlocker && Row(tail).BlockingPids.SequenceEqual([middle.ProcessID]) && Row(tail).HeadBlocker=="","Chain tail is neither head blocker nor blocked by the head directly");
            var alone=await service.ReadAsync(new ActivityFilter(Pid:head.ProcessID));
            check(alone.Single().IsHeadBlocker,"Head blocker is computed over all server waiters even when filters exclude them");
            check(rows.All(r=>r.Host=="127.0.0.1"),"Host shows the loopback client address of TCP sessions");
            var reread=await service.ReadSessionAsync(Row(tail));
            check(reread is {IsHeadBlocker:false} && reread.Pid==tail.ProcessID && reread.BackendStart==Row(tail).BackendStart && reread.Wait.StartsWith("Lock:",StringComparison.Ordinal),"ReadSessionAsync rereads one session by PID and backend start");
            check(await service.ReadSessionAsync(Row(tail) with{BackendStart=Row(tail).BackendStart.AddSeconds(-1)}) is null,"ReadSessionAsync refuses a reused PID with another backend start");
        }
        finally
        {
            foreach(var (cmd,pending) in waits){cmd.Cancel();try{await pending;}catch(OperationCanceledException){}catch(PostgresException ex) when(ex.SqlState=="57014"){}await cmd.DisposeAsync();}
        }
        var after=await service.ReadAsync(new ActivityFilter(Pid:head.ProcessID));
        check(!after.Single().IsHeadBlocker,"Head blocker clears once its waiters stop waiting");
    }
}
