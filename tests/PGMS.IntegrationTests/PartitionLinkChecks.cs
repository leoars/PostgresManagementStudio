using Npgsql;
using PGMS.PostgreSql;

internal static class PartitionLinkChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        var service=new AdministrativeEditService(f.CreatorConnection);var catalog=new PartitionCatalog(f.CreatorConnection);var candidates=new PartitionAttachCatalog(f.CreatorConnection);
        await using var owner=new NpgsqlConnection(f.CreatorConnection);await owner.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,owner);return await c.ExecuteScalarAsync();}
        var bounds=PartitionBounds.Range([new(PartitionValueKind.Literal,"0")],[new(PartitionValueKind.Literal,"10")]);
        await service.ApplyAsync(new CreateTableEdit(f.Schema,"parent",[new("id",ColumnType.Integer)]){Partitioning=new(PartitionStrategy.Range,["id"])});
        var parent=(await catalog.InspectAsync(f.Schema,"parent")).Parent;
        await Scalar($"CREATE TABLE {f.QS}.child(id integer);INSERT INTO {f.QS}.child VALUES(1),(2)");
        var child=await candidates.InspectAsync(f.Schema,"child");
        await service.ApplyAsync(new AttachPartitionEdit(parent,child,bounds));
        var attached=(await catalog.InspectAsync(f.Schema,"parent")).Children.Single();
        check(attached.Oid==child.Oid && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.parent"))==2,"ATTACH preserves child OID and exposes existing rows through parent");
        await Scalar($"INSERT INTO {f.QS}.parent VALUES(3)");
        check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.child"))==3,"New rows route into attached table");
        bool refused=false;try{await candidates.InspectAsync(f.Schema,"child");}catch(InvalidOperationException){refused=true;}check(refused,"Attach inspector refuses already attached child");
        bool rolledBack=false;try{await service.ApplyAsync(new FailAfter(new DetachPartitionEdit(parent,attached)));}catch(InvalidOperationException){rolledBack=true;}
        check(rolledBack && (await catalog.InspectAsync(f.Schema,"parent")).Children.Single().Oid==child.Oid && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.parent"))==3,"Failed DETACH verification restores link and rows in parent");
        await service.ApplyAsync(new DetachPartitionEdit(parent,attached));
        check((await catalog.InspectAsync(f.Schema,"parent")).Children.Count==0 && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.child"))==3 && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.parent"))==0,"DETACH preserves table and data without dropping rows");
        refused=false;try{await service.ApplyAsync(new DetachPartitionEdit(parent,attached));}catch(InvalidOperationException){refused=true;}check(refused,"Repeated detach refuses stale link");
        var detached=await candidates.InspectAsync(f.Schema,"child");
        rolledBack=false;try{await service.ApplyAsync(new FailAfter(new AttachPartitionEdit(parent,detached,bounds)));}catch(InvalidOperationException){rolledBack=true;}
        check(rolledBack && (await catalog.InspectAsync(f.Schema,"parent")).Children.Count==0 && (await candidates.InspectAsync(f.Schema,"child")).Oid==child.Oid,"Failed ATTACH verification rolls back link without losing table");
        await Scalar($"INSERT INTO {f.QS}.child VALUES(50)");
        bool invalidRows=false;try{await service.ApplyAsync(new AttachPartitionEdit(parent,detached,bounds));}catch(PostgresException e)when(e.SqlState=="23514"){invalidRows=true;}
        check(invalidRows && (await catalog.InspectAsync(f.Schema,"parent")).Children.Count==0 && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.child"))==4,"ATTACH scans existing data and rejects out-of-bound rows atomically");
        await Scalar($"DELETE FROM {f.QS}.child WHERE id=50;ALTER TABLE {f.QS}.child ADD COLUMN note text");
        refused=false;try{await service.ApplyAsync(new AttachPartitionEdit(parent,detached,bounds));}catch(InvalidOperationException){refused=true;}check(refused,"Changed candidate shape refuses stale attach");
        await Scalar($"ALTER TABLE {f.QS}.child DROP COLUMN note");
        detached=await candidates.InspectAsync(f.Schema,"child");
        await Scalar($"ALTER TABLE {f.QS}.child RENAME TO old_child;CREATE TABLE {f.QS}.child(id integer)");
        refused=false;try{await service.ApplyAsync(new AttachPartitionEdit(parent,detached,bounds));}catch(InvalidOperationException){refused=true;}check(refused,"Same-name candidate replacement refuses old OID");
        var replacement=await candidates.InspectAsync(f.Schema,"child");
        bool denied=false;try{await new AdministrativeEditService(f.RecipientConnection).ApplyAsync(new AttachPartitionEdit(parent,replacement,bounds));}catch(PostgresException e)when(e.SqlState=="42501"){denied=true;}check(denied,"ATTACH requires owner authority");
        await service.ApplyAsync(new AttachPartitionEdit(parent,replacement,bounds));
        var current=(await catalog.InspectAsync(f.Schema,"parent")).Children.Single();
        await service.ApplyAsync(new DetachPartitionEdit(parent,current));
        var nextBounds=PartitionBounds.Range([new(PartitionValueKind.Literal,"10")],[new(PartitionValueKind.Literal,"20")]);
        await service.ApplyAsync(new AttachPartitionEdit(parent,await candidates.InspectAsync(f.Schema,"child"),nextBounds));
        refused=false;try{await service.ApplyAsync(new DetachPartitionEdit(parent,current));}catch(InvalidOperationException){refused=true;}check(refused,"Reattached same-OID partition with different bound refuses stale detach");
        var fresh=(await catalog.InspectAsync(f.Schema,"parent")).Children.Single();
        denied=false;try{await new AdministrativeEditService(f.RecipientConnection).ApplyAsync(new DetachPartitionEdit(parent,fresh));}catch(PostgresException e)when(e.SqlState=="42501"){denied=true;}check(denied,"DETACH respects owner authority and preserves link");
        using var cancel=new CancellationTokenSource();cancel.Cancel();bool cancelled=false;try{await service.ApplyAsync(new DetachPartitionEdit(parent,fresh),cancel.Token);}catch(OperationCanceledException){cancelled=true;}check(cancelled && (await catalog.InspectAsync(f.Schema,"parent")).Children.Count==1,"Pre-cancelled DETACH preserves link");
        refused=false;try{new DetachPartitionEdit(parent,fresh with{DetachPending=true}).ToSql();}catch(ArgumentException){refused=true;}check(refused,"Pending concurrent detach is not silently finalized");
        refused=false;try{new AttachPartitionEdit(parent,replacement with{DatabaseOid=parent.DatabaseOid+1},bounds).ToSql();}catch(ArgumentException){refused=true;}check(refused,"ATTACH refuses cross-database snapshot");
    }
    private sealed record FailAfter(IAdministrativeEdit Inner):IAdministrativeEdit
    {
        public string ToSql()=>Inner.ToSql();
        public Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token=default)=>Inner.ValidateAsync(c,t,token);
        public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token=default){await Inner.VerifyAppliedAsync(c,t,token);throw new InvalidOperationException("Synthetic post-DDL failure");}
    }
}
