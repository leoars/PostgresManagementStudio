using Npgsql;
using PGMS.PostgreSql;

internal static class PartitionConcurrentDetachChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        var catalog=new PartitionCatalog(f.CreatorConnection);var service=new ConcurrentPartitionDetachService(f.CreatorConnection);
        await using var owner=new NpgsqlConnection(f.CreatorConnection);await owner.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,owner);return await c.ExecuteScalarAsync();}
        async Task<long> Count(string table)=>Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.{table}"));
        await Scalar($"""
            CREATE TABLE {f.QS}.parent(id integer) PARTITION BY RANGE(id);
            CREATE TABLE {f.QS}.p1 PARTITION OF {f.QS}.parent FOR VALUES FROM (0) TO (10);
            CREATE TABLE {f.QS}.p2 PARTITION OF {f.QS}.parent FOR VALUES FROM (10) TO (20);
            CREATE TABLE {f.QS}.p3 PARTITION OF {f.QS}.parent FOR VALUES FROM (20) TO (30);
            INSERT INTO {f.QS}.parent VALUES(1),(2),(11),(12),(21)
            """);
        var inspection=await catalog.InspectAsync(f.Schema,"parent");var parent=inspection.Parent;
        PartitionChild Child(PartitionInspection i,string name)=>i.Children.Single(c=>c.Name==name);
        var p1=Child(inspection,"p1");var p2=Child(inspection,"p2");var p3=Child(inspection,"p3");
        check(ConcurrentPartitionDetachService.ToSql(parent,p1).EndsWith("DETACH PARTITION "+ObjectSql.Relation(f.Schema,"p1")+" CONCURRENTLY;",StringComparison.Ordinal)
            && ConcurrentPartitionDetachService.ToSql(parent,p1 with{DetachPending=true}).EndsWith(" FINALIZE;",StringComparison.Ordinal),"Concurrent detach and finalize render standalone statements");
        bool refused=false;try{await service.ApplyAsync(parent,p1,false);}catch(InvalidOperationException){refused=true;}
        check(refused && (await catalog.InspectAsync(f.Schema,"parent")).Children.Count==3,"Concurrent detach requires nominal-resolution acknowledgement");
        check(await service.ApplyAsync(parent,p1,true)==PartitionDetachState.Detached && (await catalog.InspectAsync(f.Schema,"parent")).Children.Count==2
            && await Count("p1")==2 && await Count("parent")==3,"DETACH CONCURRENTLY preserves child data and removes it from parent");
        check(await service.InspectAsync(parent,p1)==PartitionDetachState.Detached,"Detached child is observed by OID");
        refused=false;try{await service.ApplyAsync(parent,p1,true);}catch(InvalidOperationException){refused=true;}check(refused,"Repeated concurrent detach refuses stale link");

        await using var blocker=new NpgsqlConnection(connection);await blocker.OpenAsync();
        var hold=await blocker.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await using(var read=new NpgsqlCommand($"SELECT count(*) FROM {f.QS}.parent",blocker,hold))await read.ExecuteScalarAsync();
        using var cancel=new CancellationTokenSource();
        var running=service.ApplyAsync(parent,p2,true,cancel.Token);
        bool pendingSeen=false;
        for(int i=0;i<200 && !pendingSeen && !running.IsCompleted;i++)
        {
            await Task.Delay(50);
            pendingSeen=Child(await catalog.InspectAsync(f.Schema,"parent"),"p2").DetachPending;
        }
        check(pendingSeen && !running.IsCompleted,"Second phase waits for an older transaction while the link is pending");
        cancel.Cancel();PartitionDetachState? observed=null;
        try{await running.WaitAsync(TimeSpan.FromSeconds(15));}catch(PartitionDetachUncertainException e){observed=e.Observed;}
        await hold.RollbackAsync();
        check(observed==PartitionDetachState.DetachPending,"Cancelled second phase reports the pending detach instead of rollback");
        inspection=await catalog.InspectAsync(f.Schema,"parent");var pending=Child(inspection,"p2");
        check(pending.DetachPending && await service.InspectAsync(parent,pending)==PartitionDetachState.DetachPending && await Count("parent")==1 && await Count("p2")==2,
            "Pending partition is visible in catalog inspection and no longer exposes rows through parent");
        refused=false;try{new DetachPartitionEdit(parent,pending).ToSql();}catch(ArgumentException){refused=true;}check(refused,"Transactional detach still refuses a pending link");
        var blockers=await service.BlockersAsync(parent,Child(inspection,"p3"));
        check(blockers.Any(b=>b.Contains("pending detach",StringComparison.Ordinal)),"Second concurrent detach is blocked while another link is pending");
        refused=false;try{await service.ApplyAsync(parent,pending with{DetachPending=false},true);}catch(InvalidOperationException){refused=true;}check(refused,"Concurrent detach refuses a stale attached snapshot of a pending link");

        PostgresException? denied=null;
        try{await new ConcurrentPartitionDetachService(f.RecipientConnection).ApplyAsync(parent,pending,true);}
        catch(PartitionDetachUncertainException e){denied=e.InnerException as PostgresException;}
        check(denied?.SqlState=="42501" && Child(await catalog.InspectAsync(f.Schema,"parent"),"p2").DetachPending,"FINALIZE respects owner authority and keeps the pending link");
        check(await service.ApplyAsync(parent,pending,true)==PartitionDetachState.Detached && (await catalog.InspectAsync(f.Schema,"parent")).Children.Count==1 && await Count("p2")==2,
            "FINALIZE completes the pending detach and preserves data");
        refused=false;try{await service.ApplyAsync(parent,pending,true);}catch(InvalidOperationException){refused=true;}check(refused,"Repeated FINALIZE refuses a completed detach");

        await Scalar($"CREATE TABLE {f.QS}.other PARTITION OF {f.QS}.parent DEFAULT");
        blockers=await service.BlockersAsync(parent,p3);
        check(blockers.Any(b=>b.Contains("DEFAULT",StringComparison.Ordinal)),"DEFAULT partition blocks concurrent detach before submission");
        refused=false;try{await service.ApplyAsync(parent,p3,true);}catch(InvalidOperationException){refused=true;}
        check(refused && (await catalog.InspectAsync(f.Schema,"parent")).Children.Count==2,"Blocked concurrent detach sends nothing");
        await Scalar($"DROP TABLE {f.QS}.other");
        using var early=new CancellationTokenSource();early.Cancel();bool cancelled=false;
        try{await service.ApplyAsync(parent,p3,true,early.Token);}catch(OperationCanceledException){cancelled=true;}
        check(cancelled && !Child(await catalog.InspectAsync(f.Schema,"parent"),"p3").DetachPending,"Pre-cancelled concurrent detach preserves link");
        await Scalar($"ALTER TABLE {f.QS}.parent RENAME TO old_parent;CREATE TABLE {f.QS}.parent(id integer) PARTITION BY RANGE(id)");
        check(await service.InspectAsync(parent,p3)==PartitionDetachState.Changed,"Replaced parent is observed as changed");
        refused=false;try{await service.ApplyAsync(parent,p3,true);}catch(InvalidOperationException){refused=true;}check(refused,"Replaced parent refuses concurrent detach");
    }
}
