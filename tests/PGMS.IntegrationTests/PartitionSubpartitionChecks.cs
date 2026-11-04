using Npgsql;
using PGMS.PostgreSql;

internal static class PartitionSubpartitionChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        var service=new AdministrativeEditService(f.CreatorConnection);var catalog=new PartitionCatalog(f.CreatorConnection);var candidates=new PartitionAttachCatalog(f.CreatorConnection);
        await using var owner=new NpgsqlConnection(f.CreatorConnection);await owner.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,owner);return await c.ExecuteScalarAsync();}
        async Task<long> Count(string table)=>Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.{table}"));
        static PartitionValue V(string value)=>new(PartitionValueKind.Literal,value);
        await service.ApplyAsync(new CreateTableEdit(f.Schema,"parent",[new("id",ColumnType.Integer),new("region",ColumnType.Text)]){Partitioning=new(PartitionStrategy.Range,["id"])});
        var top=await catalog.InspectAsync(f.Schema,"parent");var parent=top.Parent;
        check(top.Columns.SequenceEqual(["id","region"]),"Inspection lists parent columns in attribute order");

        var create=new CreatePartitionEdit(parent,f.Schema,"low",PartitionBounds.Range([V("0")],[V("10")])){Partitioning=new(PartitionStrategy.List,["region"])};
        check(create.ToSql().EndsWith("FOR VALUES FROM (E'0') TO (E'10') PARTITION BY LIST (\"region\");",StringComparison.Ordinal),"Sub-partitioned child renders PARTITION BY after its bound");
        await service.ApplyAsync(create);
        var low=(await catalog.InspectAsync(f.Schema,"parent")).Children.Single();
        var lowInspection=await catalog.InspectAsync(f.Schema,"low");
        check(low.Kind=="p" && lowInspection.Parent.Strategy==PartitionStrategy.List && lowInspection.Parent.KeyCount==1,"Created child is a partitioned partition inspectable as a parent");
        bool unrouted=false;try{await Scalar($"INSERT INTO {f.QS}.parent VALUES(5,'north')");}catch(PostgresException e)when(e.SqlState=="23514"){unrouted=true;}
        check(unrouted && await Count("parent")==0,"Sub-partitioned child stores no rows until it has partitions");
        await service.ApplyAsync(new CreatePartitionEdit(lowInspection.Parent,f.Schema,"low_north",PartitionBounds.List([V("north")])));
        await Scalar($"INSERT INTO {f.QS}.parent VALUES(5,'north'),(6,'north')");
        check(await Count("low_north")==2 && await Count("parent")==2,"Rows route through two partition levels");

        bool missing=false;try{await service.ApplyAsync(new CreatePartitionEdit(parent,f.Schema,"bad",PartitionBounds.Range([V("10")],[V("20")])){Partitioning=new(PartitionStrategy.Hash,["absent"])});}catch(PostgresException e)when(e.SqlState=="42703"){missing=true;}
        check(missing && (bool)(await Scalar($"SELECT to_regclass('{f.QS}.bad') IS NULL"))!,"Unknown sub-partition key is rejected atomically");
        bool mismatch=false;try{await service.ApplyAsync(new CreatePartitionEdit(parent,f.Schema,"plain",PartitionBounds.Range([V("10")],[V("20")])){Partitioning=new(PartitionStrategy.List,["region"])}.WithVerifyOf(new(PartitionStrategy.List,["id"])));}catch(InvalidOperationException){mismatch=true;}
        check(mismatch && (bool)(await Scalar($"SELECT to_regclass('{f.QS}.plain') IS NULL"))!,"Created sub-partition key is verified before commit");

        await service.ApplyAsync(new DetachPartitionEdit(parent,low));
        check((await catalog.InspectAsync(f.Schema,"parent")).Children.Count==0 && await Count("low")==2 && (await catalog.InspectAsync(f.Schema,"low")).Children.Single().Name=="low_north",
            "Transactional detach of a partitioned partition keeps its own partitions and data");
        var candidate=await candidates.InspectAsync(f.Schema,"low");
        check(candidate.Kind=="p","Attach inspector accepts a partitioned table with partitions");
        await Scalar($"INSERT INTO {f.QS}.low VALUES(50,'north')");
        bool invalid=false;try{await service.ApplyAsync(new AttachPartitionEdit(parent,candidate,PartitionBounds.Range([V("0")],[V("10")])));}catch(PostgresException e)when(e.SqlState=="23514"){invalid=true;}
        check(invalid && (await catalog.InspectAsync(f.Schema,"parent")).Children.Count==0,"Attach validates rows in the sub-partitions and rolls back");
        await Scalar($"DELETE FROM {f.QS}.low WHERE id=50");
        await service.ApplyAsync(new AttachPartitionEdit(parent,candidate,PartitionBounds.Range([V("0")],[V("10")])));
        low=(await catalog.InspectAsync(f.Schema,"parent")).Children.Single();
        check(low.Oid==candidate.Oid && low.Kind=="p" && await Count("parent")==2,"Attach of a partitioned table exposes its sub-partition rows");
        await Scalar($"CREATE TABLE {f.QS}.inh_parent(id integer,region text);CREATE TABLE {f.QS}.inh_child() INHERITS({f.QS}.inh_parent)");
        bool refused=false;try{await candidates.InspectAsync(f.Schema,"inh_parent");}catch(InvalidOperationException){refused=true;}
        check(refused,"Attach inspector still refuses an ordinary table with inheritance children");

        lowInspection=await catalog.InspectAsync(f.Schema,"low");
        var leafService=new ConcurrentPartitionDetachService(f.CreatorConnection);
        var leafState=await leafService.ApplyAsync(lowInspection.Parent,lowInspection.Children.Single(),true);
        check(leafState==PartitionDetachState.Detached && (await catalog.InspectAsync(f.Schema,"low")).Children.Count==0 && await Count("low_north")==2,"Concurrent detach works at the sub-partition level");
        await Scalar($"DELETE FROM {f.QS}.low_north;ALTER TABLE {f.QS}.low ATTACH PARTITION {f.QS}.low_north FOR VALUES IN ('north');INSERT INTO {f.QS}.parent VALUES(7,'north')");
        var state=await leafService.ApplyAsync(parent,low,true);
        check(state==PartitionDetachState.Detached && (await catalog.InspectAsync(f.Schema,"parent")).Children.Count==0 && await Count("low")==1 && (await catalog.InspectAsync(f.Schema,"low")).Children.Count==1,
            "Concurrent detach of a partitioned partition keeps its sub-partitions");
        check(new DetachPartitionEdit(parent,low).ToSql().Contains("DETACH PARTITION",StringComparison.Ordinal),"Detach accepts partitioned children");
        refused=false;try{new DetachPartitionEdit(parent,low with{Kind="f"}).ToSql();}catch(ArgumentException){refused=true;}check(refused,"Detach still refuses foreign partitions");
    }
    /// <summary>Applies the real statement but verifies a different sub-partition key, simulating a server result that does not match the request.</summary>
    private static IAdministrativeEdit WithVerifyOf(this CreatePartitionEdit edit,TablePartitioning expected)=>new VerifyAs(edit,edit with{Partitioning=expected});
    private sealed record VerifyAs(IAdministrativeEdit Inner,IAdministrativeEdit Verifier):IAdministrativeEdit
    {
        public string ToSql()=>Inner.ToSql();
        public Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token=default)=>Inner.ValidateAsync(c,t,token);
        public Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token=default)=>Verifier.VerifyAppliedAsync(c,t,token);
    }
}
