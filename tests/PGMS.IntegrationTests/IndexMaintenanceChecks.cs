using Npgsql;
using PGMS.PostgreSql;

internal static class IndexMaintenanceChecks
{
    private sealed class Journal:IConcurrentIndexJournal
    {
        internal ConcurrentIndexReceipt? Receipt;
        internal Func<Task>? Hook;
        public async Task RecordAsync(ConcurrentIndexReceipt receipt,CancellationToken token)
        {token.ThrowIfCancellationRequested();if(Receipt is not null)throw new IOException("pending");if(Hook is not null)await Hook();Receipt=receipt;}
    }
    private sealed class FailAfterReindex(ReindexEdit edit):IAdministrativeEdit
    {
        public string ToSql()=>edit.ToSql();
        public Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)=>edit.ValidateAsync(c,tx,token);
        public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token){await edit.VerifyAppliedAsync(c,tx,token);throw new IOException("post-DDL fixture");}
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        await f.Owner($"CREATE TABLE {f.QS}.target(id integer PRIMARY KEY,label text);INSERT INTO {f.QS}.target VALUES(1,'a'),(2,'a');CREATE INDEX ordinary ON {f.QS}.target(label)");
        var catalog=new IndexMaintenanceCatalog(f.CreatorConnection);
        var original=await catalog.InspectAsync(f.Schema,"target","ordinary");
        check(original.Oid!=0 && original.Valid && !original.SupportsConstraint && original.Table.OwnerOid==original.OwnerOid,"Maintenance inspection captures real index identity and constraint status");
        await using var owner=new NpgsqlConnection(f.CreatorConnection);await owner.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,owner);return await c.ExecuteScalarAsync();}
        async Task<uint> FileNode(string name)=>Convert.ToUInt32(await Scalar($"SELECT relfilenode FROM pg_class WHERE oid='{f.QS}.{name}'::regclass"));
        uint before=await FileNode("ordinary");
        await new AdministrativeEditService(f.CreatorConnection).ApplyAsync(new ReindexEdit(original));
        check(await FileNode("ordinary")!=before && (await catalog.InspectAsync(f.Schema,"target","ordinary")).Oid==original.Oid,"Ordinary REINDEX replaces physical file while retaining inspected index OID");
        uint rollbackNode=await FileNode("ordinary");bool rolled=false;
        try{await new AdministrativeEditService(f.CreatorConnection).ApplyAsync(new FailAfterReindex(new(original)));}catch(IOException){rolled=true;}
        check(rolled && await FileNode("ordinary")==rollbackNode,"Failure after ordinary REINDEX rolls back physical replacement");
        var primary=await catalog.InspectAsync(f.Schema,"target","target_pkey");
        bool constraint=false;try{ConcurrentIndexMaintenanceService.ToSql(primary,IndexMaintenanceOperation.DropConcurrently);}catch(ArgumentException){constraint=true;}
        check(constraint,"DROP CONCURRENTLY refuses primary-key constraint index before submission");
        var rebuildJournal=new Journal();
        var rebuilt=await new ConcurrentIndexMaintenanceService(f.CreatorConnection,rebuildJournal).ApplyAsync(original,IndexMaintenanceOperation.ReindexConcurrently,true);
        check(rebuilt.Observation.Kind==ConcurrentIndexObservationKind.Valid && rebuilt.Observation.IndexOid!=original.Oid && rebuilt.Observation.Definition==original.Definition,"Concurrent REINDEX creates new OID and preserves inspected definition");
        check(rebuilt.Receipt.OriginalIndexOid==original.Oid && rebuilt.Receipt.Operation=="ReindexConcurrently" && rebuildJournal.Receipt==rebuilt.Receipt,"Concurrent rebuild records original index and operation before submission");
        bool stale=false;try{await new ConcurrentIndexMaintenanceService(f.CreatorConnection,new Journal()).ApplyAsync(original,IndexMaintenanceOperation.DropConcurrently,true);}catch(InvalidOperationException){stale=true;}
        check(stale,"Old snapshot cannot drop replacement produced by concurrent REINDEX");
        var fresh=await catalog.InspectAsync(f.Schema,"target","ordinary");
        var dropJournal=new Journal();var dropped=await new ConcurrentIndexMaintenanceService(f.CreatorConnection,dropJournal).ApplyAsync(fresh,IndexMaintenanceOperation.DropConcurrently,true);
        check(dropped.Observation.Kind==ConcurrentIndexObservationKind.Absent && dropJournal.Receipt?.OriginalIndexOid==fresh.Oid,"DROP INDEX CONCURRENTLY removes exact inspected ordinary index with durable-journal contract");
        check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.target"))==2,"Concurrent index drop preserves table rows");
        await new ConcurrentIndexMaintenanceService(f.CreatorConnection,new Journal()).ApplyAsync(primary,IndexMaintenanceOperation.ReindexConcurrently,true);
        bool primaryEnforced=false;try{await f.Owner($"INSERT INTO {f.QS}.target VALUES(1,'duplicate')");}catch(PostgresException e)when(e.SqlState=="23505"){primaryEnforced=true;}
        check(primaryEnforced,"Concurrent primary-key rebuild preserves constraint enforcement");
        bool duplicate=false;try{await Scalar($"CREATE UNIQUE INDEX CONCURRENTLY invalid ON {f.QS}.target(label)");}catch(PostgresException e)when(e.SqlState=="23505"){duplicate=true;}
        var invalid=await catalog.InspectAsync(f.Schema,"target","invalid");
        check(duplicate && !invalid.Valid,"Maintenance catalog identifies actual failed-build invalid index");
        await f.Owner($"UPDATE {f.QS}.target SET label='b' WHERE id=2");
        var recovered=await new ConcurrentIndexMaintenanceService(f.CreatorConnection,new Journal()).ApplyAsync(invalid,IndexMaintenanceOperation.ReindexConcurrently,true);
        check(recovered.Observation.Kind==ConcurrentIndexObservationKind.Valid,"REINDEX CONCURRENTLY repairs invalid unique index after conflicting data corrected");
        await f.Owner($"CREATE INDEX race ON {f.QS}.target(label)");var race=await catalog.InspectAsync(f.Schema,"target","race");
        var changedJournal=new Journal{Hook=()=>f.Owner($"DROP INDEX {f.QS}.race;CREATE INDEX race ON {f.QS}.target(id)")};
        bool changed=false;try{await new ConcurrentIndexMaintenanceService(f.CreatorConnection,changedJournal).ApplyAsync(race,IndexMaintenanceOperation.DropConcurrently,true);}catch(InvalidOperationException){changed=true;}
        check(changed && (await catalog.InspectAsync(f.Schema,"target","race")).Oid!=race.Oid,"Refresh after journal prevents dropping same-name replacement index");
        var failedJournal=new Journal{Hook=()=>throw new IOException("journal unavailable")};bool journalFailed=false;
        var stillThere=await catalog.InspectAsync(f.Schema,"target","race");
        try{await new ConcurrentIndexMaintenanceService(f.CreatorConnection,failedJournal).ApplyAsync(stillThere,IndexMaintenanceOperation.DropConcurrently,true);}catch(IOException){journalFailed=true;}
        check(journalFailed && (await catalog.InspectAsync(f.Schema,"target","race")).Oid==stillThere.Oid,"Journal failure prevents concurrent index removal");
        await f.Owner($"CREATE TABLE {f.QS}.ranges(value int4range,EXCLUDE USING gist(value WITH &&))");
        var exclusion=await catalog.InspectAsync(f.Schema,"ranges","ranges_value_excl");bool excluded=false;
        try{ConcurrentIndexMaintenanceService.ToSql(exclusion,IndexMaintenanceOperation.ReindexConcurrently);}catch(ArgumentException){excluded=true;}
        check(excluded,"Concurrent reindex refuses exclusion index explicitly");
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();bool canceled=false;
        try{await new ConcurrentIndexMaintenanceService(f.CreatorConnection,new Journal()).ApplyAsync(stillThere,IndexMaintenanceOperation.DropConcurrently,true,cancellation.Token);}catch(OperationCanceledException){canceled=true;}
        check(canceled,"Pre-canceled maintenance never submits DDL");
    }
}
