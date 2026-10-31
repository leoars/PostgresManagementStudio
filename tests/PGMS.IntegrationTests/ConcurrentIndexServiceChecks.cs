using Npgsql;
using PGMS.PostgreSql;

internal static class ConcurrentIndexServiceChecks
{
    private sealed class Journal : IConcurrentIndexJournal
    {
        internal ConcurrentIndexReceipt? Pending;
        internal Func<Task>? BeforeRecord;
        public async Task RecordAsync(ConcurrentIndexReceipt receipt,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(Pending is not null)throw new IOException("Pending operation exists.");
            if(BeforeRecord is not null)await BeforeRecord();
            Pending=receipt;
        }
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        await f.Owner($"CREATE TABLE {f.QS}.target(id integer,label text);INSERT INTO {f.QS}.target VALUES(1,'A'),(1,'B')");
        var target=await new ExpressionIndexCatalog(f.CreatorConnection).InspectAsync(f.Schema,"target");
        var proposal=new ExpressionIndexEdit(target,"created",[new(new("label",IndexTransform.Lower))],true);
        var journal=new Journal();var service=new ConcurrentIndexService(f.CreatorConnection,journal);
        check((await service.InspectAsync(target,"created")).Kind==ConcurrentIndexObservationKind.Absent,"Concurrent residue inspection reports absent index");
        bool refused=false;try{await service.CreateAsync(proposal,false);}catch(InvalidOperationException){refused=true;}
        check(refused && journal.Pending is null,"Nominal resolution must be explicitly reviewed before standalone command");
        var result=await service.CreateAsync(proposal,true);
        check(result.Observation.Kind==ConcurrentIndexObservationKind.Valid && result.Observation.IndexOid!=0,"Concurrent service creates and verifies actual valid index outside transaction");
        check(journal.Pending==result.Receipt && result.Receipt.TableOid==target.Oid && result.Receipt.OperationId!=Guid.Empty,"Receipt precedes execution and retains original target identity");
        check(result.Observation.Definition?.Contains("lower") is true,"Residue inspection includes actual server definition");
        bool pending=false;try{await service.CreateAsync(new(target,"second",[new(new("id"))]),true);}catch(IOException){pending=true;}
        check(pending && (await service.InspectAsync(target,"second")).Kind==ConcurrentIndexObservationKind.Absent,"Unresolved journal prevents a second DDL submission");
        var failedJournal=new Journal{BeforeRecord=()=>throw new IOException("disk fixture")};
        bool disk=false;try{await new ConcurrentIndexService(f.CreatorConnection,failedJournal).CreateAsync(new(target,"disk_fail",[new(new("id"))]),true);}catch(IOException){disk=true;}
        check(disk && (await service.InspectAsync(target,"disk_fail")).Kind==ConcurrentIndexObservationKind.Absent,"Journal failure prevents database creation");
        var duplicateJournal=new Journal();var duplicateService=new ConcurrentIndexService(f.CreatorConnection,duplicateJournal);
        bool uncertain=false;try{await duplicateService.CreateAsync(new(target,"bad_unique",[new(new("id"))],true),true);}catch(ConcurrentIndexUncertainException e){uncertain=e.Receipt==duplicateJournal.Pending && e.InnerException is PostgresException{SqlState:"23505"};}
        var invalid=await service.InspectAsync(target,"bad_unique");
        check(uncertain && invalid.Kind==ConcurrentIndexObservationKind.Invalid,"Concurrent uniqueness error preserves receipt and reports invalid residue without rollback claim");
        await f.Owner($"CREATE SEQUENCE {f.QS}.occupied;CREATE TABLE {f.QS}.other(id integer);CREATE INDEX wrong_target ON {f.QS}.other(id)");
        check((await service.InspectAsync(target,"occupied")).Kind==ConcurrentIndexObservationKind.NameOccupied,"Residue inspection distinguishes non-index name collision");
        check((await service.InspectAsync(target,"wrong_target")).Kind==ConcurrentIndexObservationKind.NameOccupied,"Residue inspection refuses index belonging to another table");
        var raceJournal=new Journal{BeforeRecord=()=>f.Owner($"CREATE SEQUENCE {f.QS}.raced")};
        bool raced=false;try{await new ConcurrentIndexService(f.CreatorConnection,raceJournal).CreateAsync(new(target,"raced",[new(new("id"))]),true);}catch(InvalidOperationException){raced=true;}
        check(raced && raceJournal.Pending is not null && (await service.InspectAsync(target,"raced")).Kind==ConcurrentIndexObservationKind.NameOccupied,"Preconditions refreshed after journal write refuse newly occupied name");
        using(var ambient=new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            bool ambientRefused=false;try{await service.CreateAsync(proposal,true);}catch(InvalidOperationException){ambientRefused=true;}
            check(ambientRefused,"Standalone service refuses ambient transaction before any submission");
        }
        var cancellationJournal=new Journal();var cancellationService=new ConcurrentIndexService(f.CreatorConnection,cancellationJournal);
        await using(var blocker=new NpgsqlConnection(f.CreatorConnection))
        {
            await blocker.OpenAsync();await using var tx=await blocker.BeginTransactionAsync();
            await using(var hold=new NpgsqlCommand($"LOCK TABLE {f.QS}.target IN ROW EXCLUSIVE MODE",blocker,tx))await hold.ExecuteNonQueryAsync();
            using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var operation=cancellationService.CreateAsync(new(target,"canceled",[new(new("id"))]),true,cancel.Token);
            bool observed=false;
            for(int attempt=0;attempt<100 && !operation.IsCompleted;attempt++)
            {
                observed=(await service.InspectAsync(target,"canceled")).Kind==ConcurrentIndexObservationKind.Invalid;
                if(observed)break;await Task.Delay(25);
            }
            cancel.Cancel();bool canceled=false;
            try{await operation;}catch(ConcurrentIndexUncertainException){canceled=true;}
            await tx.RollbackAsync();
            check(observed && canceled && cancellationJournal.Pending is not null,"Cancellation after actual concurrent phase keeps pending receipt and uncertain result");
            check((await service.InspectAsync(target,"canceled")).Kind==ConcurrentIndexObservationKind.Invalid,"Canceled standalone command leaves explicit invalid residue without cleanup");
        }
        await f.Owner($"ALTER TABLE {f.QS}.target ADD COLUMN extra integer");
        check((await service.InspectAsync(target,"created")).Kind==ConcurrentIndexObservationKind.TargetChanged,"Residue inspection refuses stale target shape");
    }
}
