using Npgsql;
using PGMS.PostgreSql;

internal static class ActivityMetricsChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        HistoryRates(check);
        await ServerSampleAsync(connection,check);
        await RecentQueriesAsync(connection,check);
    }
    private static ActivityMetricsSample Sample(DateTime at,int active,int waiting,Dictionary<string,int> waiters,long io,params ActivityDatabaseCounters[] databases)
        =>new(at,active,waiting,waiters,io,null,true,8192,databases){IoSource="pg_stat_io"};
    private static void HistoryRates(Action<bool,string> check)
    {
        var t0=new DateTime(2026,11,3,12,0,0,DateTimeKind.Utc);var history=new ActivityMetricsHistory();
        var db=new ActivityDatabaseCounters(5,"app",100,900,0,0,1000,null);
        var first=history.Add(Sample(t0,2,1,new(){["Lock"]=1},10_000_000,db));
        check(first.IoMegabytesPerSecond is null && first.TransactionsPerSecond is null && history.DataFileIo.Single().ReadMegabytesPerSecond is null,"First reading has no rates");
        check(history.ResourceWaits.Select(r=>r.Category).SequenceEqual(ActivityMetricsHistory.Categories),"Resource Waits lists the PostgreSQL wait categories in a fixed order");
        var lockRow=history.ResourceWaits.Single(r=>r.Category=="Lock");
        check(lockRow is {WaitingNow:1,WaitMsPerSecond:1000,AverageWaiters:1,CumulativeWaitSeconds:0},"One waiter in the only reading reads as 1000 ms/sec with no interval yet");
        // 10 s later: 1 MiB/s of I/O, 50 transactions/s, 128 blocks read of 8 KiB in 10 s, 40 ms read time.
        var second=history.Add(Sample(t0.AddSeconds(10),3,0,new(){["Lock"]=1,["IO"]=2},10_000_000+10*1024*1024,db with{BlocksRead=228,BlocksHit=1284,TempBytes=2*1024*1024,ReadTimeMs=40,Transactions=1500}));
        check(Math.Abs(second.IoMegabytesPerSecond!.Value-1)<1e-9 && Math.Abs(second.TransactionsPerSecond!.Value-50)<1e-9,"Overview rates divide counter deltas by the server interval");
        var io=history.DataFileIo.Single();
        check(io.Database=="app" && Math.Abs(io.ReadMegabytesPerSecond!.Value-0.1)<1e-9 && Math.Abs(io.TempMegabytesPerSecond!.Value-0.2)<1e-9 && Math.Abs(io.AverageReadMs!.Value-40.0/128)<1e-9 && Math.Abs(io.HitRatio!.Value-384.0/512)<1e-9,"Data File I/O derives read, temp, read time and hit ratio for the interval");
        lockRow=history.ResourceWaits.Single(r=>r.Category=="Lock");var ioRow=history.ResourceWaits.Single(r=>r.Category=="IO");
        check(lockRow.CumulativeWaitSeconds==10 && lockRow.AverageWaiters==1 && ioRow.CumulativeWaitSeconds==10 && ioRow.WaitMsPerSecond==2000 && ioRow.RecentWaitMsPerSecond==1000 && ioRow.AverageWaiters==1,"Waits accumulate by trapezoid between readings and average over readings");
        var reset=history.Add(Sample(t0.AddSeconds(20),0,0,new(){["InjectionPoint"]=1},5,db with{BlocksRead=1,BlocksHit=2,Transactions=3,StatsReset=t0.AddSeconds(15)}));
        check(reset.IoMegabytesPerSecond is null && reset.TransactionsPerSecond is null && history.DataFileIo.Single().ReadMegabytesPerSecond is null,"Counters that went back after a reset give no rate instead of a negative one");
        check(history.ResourceWaits.Last().Category=="InjectionPoint" && history.ResourceWaits.Single(r=>r.Category=="Lock").WaitingNow==0,"A wait category outside the known list is still shown");
        var added=new ActivityDatabaseCounters(6,"new",1,1,0,0,1,null);history.Add(Sample(t0.AddSeconds(30),0,0,new(),5,db with{BlocksRead=1,BlocksHit=2,Transactions=3,StatsReset=t0.AddSeconds(15)},added));
        check(history.DataFileIo.Single(r=>r.Database=="new").ReadMegabytesPerSecond is null && history.DataFileIo.Single(r=>r.Database=="app").ReadMegabytesPerSecond==0,"A database created between readings waits for its second reading");
        bool refused=false;try{history.Add(Sample(t0,0,0,new(),5));}catch(ArgumentException){refused=true;}
        check(refused,"History refuses a reading older than the last one");
        for(int i=0;i<70;i++)history.Add(Sample(t0.AddMinutes(1+i),i,0,new(),5));
        check(history.Points.Count==ActivityMetricsHistory.Capacity && history.Points[^1].ActiveSessions==69,"History keeps only the last 60 readings");
        var same=new ActivityMetricsHistory();same.Add(Sample(t0,0,0,new(),5));
        check(same.Add(Sample(t0,0,0,new(),5)).IoMegabytesPerSecond is null,"Two readings at the same server instant give no rate");
        var window=new QueryStatisticsSnapshot([new(1,"app","u",true,30,600,20,double.NaN,double.NaN,0,30,70,30,0,0,0,"SELECT 1",null)],false,20,t0.AddSeconds(30),new(true,true,"1.11","1.11","public",true,"app")){WindowStart=t0};
        var recent=RecentExpensiveQuery.FromWindow(window).Single();
        check(recent.ExecutionsPerMinute==60 && recent.ExecutionMsPerSecond==20 && recent.PhysicalReadsPerSecond==1 && Math.Abs(recent.LogicalReadsPerSecond-100.0/30)<1e-9 && recent.AverageDurationMs==20,"Recent Expensive Queries turns window counters into per-second and per-minute rates");
        check(RecentExpensiveQuery.FromWindow(window with{WindowStart=null}).Count==0,"Cumulative counters are not shown as recent activity");
    }
    private static async Task ServerSampleAsync(string connection,Action<bool,string> check)
    {
        var service=new ActivityMetricsService(connection);long key=Random.Shared.NextInt64(1,long.MaxValue);
        string app="PGMS.Metrics."+Guid.NewGuid().ToString("N")[..10];string target=new NpgsqlConnectionStringBuilder(connection){ApplicationName=app,Pooling=false}.ConnectionString;
        await using var holder=new NpgsqlConnection(target);await holder.OpenAsync();
        await using var waiter=new NpgsqlConnection(target);await waiter.OpenAsync();
        await using var sleeper=new NpgsqlConnection(target);await sleeper.OpenAsync();
        var before=await service.ReadSampleAsync();
        check(before.BlockSize==8192 && before.IoSource=="pg_stat_io" && before.Databases.Any(d=>d.Database==holder.Database) && before.Databases.Any(d=>d.Oid==0),"Sample reads block size, pg_stat_io and every pg_stat_database row including shared catalogs");
        await using(var hold=new NpgsqlCommand("SELECT pg_advisory_lock(@key)",holder)){hold.Parameters.AddWithValue("key",key);await hold.ExecuteNonQueryAsync();}
        await using var wait=new NpgsqlCommand("SELECT pg_advisory_lock(@key)",waiter);wait.Parameters.AddWithValue("key",key);Task blocked=wait.ExecuteNonQueryAsync();
        await using var sleep=new NpgsqlCommand("SELECT pg_sleep(30) /* metrics sleeper */",sleeper);Task sleeping=sleep.ExecuteNonQueryAsync();
        try
        {
            ActivityMetricsSample during=before;var deadline=DateTime.UtcNow.AddSeconds(10);
            // Sample only once the sleeper is seen inside pg_sleep, so its absence from the waits is the exclusion and not a race.
            while(DateTime.UtcNow<deadline)
            {
                bool inSleep=(await service.ReadActiveQueriesAsync()).Any(a=>a.Pid==sleeper.ProcessID && a.Wait=="Timeout: PgSleep");
                if(inSleep){during=await service.ReadSampleAsync();if(during.Waiters.GetValueOrDefault("Lock")>=1)break;}
                await Task.Delay(25);
            }
            check(during.Waiters.GetValueOrDefault("Lock")>=1 && during.ActiveSessions>=2 && during.WaitingSessions>=1,"A real lock waiter counts as an active waiting session and a Lock wait");
            check(during.Waiters.GetValueOrDefault("Timeout")==0 && during.WaitingSessions<=during.ActiveSessions-1,"A session inside pg_sleep stays active but is not a waiting session or a Timeout wait, like WAITFOR in SSMS (M1030)");
            check(ActivityText.SingleLine("SELECT 1,\r\n\t  2\n  FROM  t ")=="SELECT 1, 2 FROM t","Query text collapses to one line for the expensive query grids");
            check(!during.Waiters.ContainsKey("Activity") && !during.Waiters.ContainsKey("Client"),"Idle background loops and idle client reads are not counted as waits");
            var history=new ActivityMetricsHistory();history.Add(before);var point=history.Add(during);
            check(point.TransactionsPerSecond>=0 && point.IoMegabytesPerSecond>=0 && history.ResourceWaits.Single(r=>r.Category=="Lock").WaitingNow>=1,"Rates between two real readings are measurable and non-negative");
            var active=await service.ReadActiveQueriesAsync();
            var row=active.SingleOrDefault(a=>a.Pid==sleeper.ProcessID);
            check(row is {Wait:"Timeout: PgSleep"} && row.Query.Contains("metrics sleeper",StringComparison.Ordinal) && row.Elapsed>=TimeSpan.Zero && active.Any(a=>a.Pid==waiter.ProcessID && a.Wait.StartsWith("Lock",StringComparison.Ordinal)),"Active Expensive Queries lists real running queries with elapsed time and wait");
            check(active.Count<=ActiveExpensiveQuery.MaximumRows && active.Zip(active.Skip(1)).All(p=>p.First.Elapsed is null || p.Second.Elapsed is null || p.First.Elapsed>=p.Second.Elapsed),"Running queries come oldest first and are capped");
        }
        finally
        {
            wait.Cancel();sleep.Cancel();
            foreach(var task in new[]{blocked,sleeping}){try{await task;}catch(OperationCanceledException){}catch(PostgresException ex) when(ex.SqlState=="57014"){}}
            await using var release=new NpgsqlCommand("SELECT pg_advisory_unlock_all()",holder);await release.ExecuteNonQueryAsync();
        }
    }
    private static async Task RecentQueriesAsync(string connection,Action<bool,string> check)
    {
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();bool created=false;
        string marker="pgms_recent_"+Guid.NewGuid().ToString("N")[..10];
        try
        {
            await using(var exists=new NpgsqlCommand("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname='pg_stat_statements'",admin))
                if((long)(await exists.ExecuteScalarAsync())! == 0){await using var create=new NpgsqlCommand("CREATE EXTENSION pg_stat_statements",admin);await create.ExecuteNonQueryAsync();created=true;}
            await using(var table=new NpgsqlCommand($"CREATE TABLE {SqlIdentifier.Quote(marker)}(id int)",admin))await table.ExecuteNonQueryAsync();
            var statements=new QueryStatisticsService(connection);var baseline=await statements.CaptureBaselineAsync();
            for(int i=0;i<4;i++)await using(var count=new NpgsqlCommand($"SELECT count(*), pg_sleep(0.02) FROM {SqlIdentifier.Quote(marker)}",admin))await count.ExecuteScalarAsync();
            var window=await statements.ReadSnapshotAsync(new QueryStatisticsFilter(),baseline,RecentExpensiveQuery.MaximumRows);
            var recent=RecentExpensiveQuery.FromWindow(window).SingleOrDefault(r=>r.Query.Contains(marker,StringComparison.Ordinal) && r.Query.Contains("pg_sleep",StringComparison.Ordinal));
            check(recent is not null && recent.ExecutionsPerMinute>0 && recent.AverageDurationMs>=20 && recent.Database==admin.Database,"Recent Expensive Queries measures a real statement between two readings");
        }
        finally
        {
            await using(var drop=new NpgsqlCommand($"DROP TABLE IF EXISTS {SqlIdentifier.Quote(marker)}",admin))await drop.ExecuteNonQueryAsync();
            if(created){await using var remove=new NpgsqlCommand("DROP EXTENSION pg_stat_statements",admin);await remove.ExecuteNonQueryAsync();}
        }
    }
}
