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
        foreach(var invalid in new[]{new ActivityFilter(Pid:0),new(Pid:-1),new(Database:null!)})
        {bool refused=false;try{invalid.Validate();}catch(ArgumentException){refused=true;}check(refused,"Invalid activity filter is refused before connecting");}
        bool nullRefused=false;try{await service.ReadAsync(null!);}catch(ArgumentNullException){nullRefused=true;}check(nullRefused,"Null activity filter is explicitly refused");
    }
}
