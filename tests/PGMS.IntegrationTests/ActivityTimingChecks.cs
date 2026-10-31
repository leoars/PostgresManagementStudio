using Npgsql;
using PGMS.PostgreSql;

internal static class ActivityTimingChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N")[..12],role="activity_reader_"+suffix;
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        await using var worker=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){ApplicationName="PGMS.ActivityTiming."+suffix}.ConnectionString);await worker.OpenAsync();
        var service=new ActivityService(connection);var filter=new ActivityFilter(Pid:worker.ProcessID);bool roleCreated=false;
        async Task<ActivitySession> WaitState(string state)
        {
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(DateTime.UtcNow<deadline)
            {
                var row=(await service.ReadAsync(filter)).Single();if(row.State==state)return row;await Task.Delay(25);
            }
            throw new Exception("Timed out waiting for synthetic activity state: "+state);
        }
        try
        {
            await using var transaction=await worker.BeginTransactionAsync();
            await using(var command=new NpgsqlCommand("SELECT 1",worker,transaction))await command.ExecuteNonQueryAsync();
            var idle=await WaitState("idle in transaction");
            check(idle.ObservedAt.Kind==DateTimeKind.Utc && idle.QueryStart is not null && idle.TransactionStart is not null,"Activity snapshot captures server UTC query and transaction timestamps");
            check(idle.QueryDuration is null && idle.TransactionDuration>=TimeSpan.Zero,"Idle transaction reports transaction age without inventing running query duration");
            await using var sleep=new NpgsqlCommand("SELECT pg_sleep(15)",worker,transaction);Task pending=sleep.ExecuteNonQueryAsync();
            try
            {
                var active=await WaitState("active");
                check(active.QueryDuration>=TimeSpan.Zero && active.TransactionDuration>=active.QueryDuration,"Active query duration uses server observation and transaction start");
                await Task.Delay(80);var later=await WaitState("active");
                check(later.ObservedAt>active.ObservedAt && later.QueryDuration>active.QueryDuration,"New activity read advances server-based duration");
                await using(var create=new NpgsqlCommand($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN",admin)){await create.ExecuteNonQueryAsync();roleCreated=true;}
                string limited=new NpgsqlConnectionStringBuilder(connection){Username=role,Pooling=false}.ConnectionString;
                await using(var limitedConnection=new NpgsqlConnection(limited))
                {
                    await limitedConnection.OpenAsync();await using var visibility=new NpgsqlCommand("SELECT backend_start IS NULL AND query_start IS NULL AND xact_start IS NULL FROM pg_stat_activity WHERE pid=@pid",limitedConnection);visibility.Parameters.AddWithValue("pid",worker.ProcessID);
                    check((bool)(await visibility.ExecuteScalarAsync())!,"PostgreSQL hides foreign session identity and timing from ordinary reader");
                }
                var restricted=await new ActivityService(limited).ReadAsync(filter);
                check(restricted.Count==0,"Activity service omits a session whose backend identity is hidden");
                check((active with{ObservedAt=active.QueryStart!.Value.AddSeconds(-1)}).QueryDuration is null,"Backward observation does not display negative query duration");
                check((active with{ObservedAt=default}).QueryDuration is null,"Legacy snapshot without observation has unknown duration");
            }
            finally{sleep.Cancel();try{await pending;}catch(PostgresException ex) when(ex.SqlState=="57014"){}catch(OperationCanceledException){}}
            await transaction.RollbackAsync();var finished=await WaitState("idle");
            check(finished.TransactionStart is null && finished.QueryDuration is null && finished.TransactionDuration is null,"Committed or rolled-back idle session clears transaction age");
        }
        finally{if(roleCreated){await using var drop=new NpgsqlCommand($"DROP ROLE {SqlIdentifier.Quote(role)}",admin);await drop.ExecuteNonQueryAsync();}}
        await ActivityBlockingChecks.RunAsync(connection,check);
        await ActivitySnapshotChecks.RunAsync(connection,check);
    }
}
