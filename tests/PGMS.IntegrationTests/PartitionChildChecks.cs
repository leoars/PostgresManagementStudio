using Npgsql;
using PGMS.PostgreSql;

internal static class PartitionChildChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        static PartitionValue V(string value)=>new(PartitionValueKind.Literal,value);
        void Refuses(Action action,string label){bool refused=false;try{action();}catch(ArgumentException){refused=true;}check(refused,label);}
        Refuses(()=>PartitionBounds.Range([V("1")],[]).ToSql(PartitionStrategy.Range,1),"RANGE requires both tuple arities");
        Refuses(()=>PartitionBounds.Range([new(PartitionValueKind.Null)],[V("1")]).ToSql(PartitionStrategy.Range,1),"RANGE rejects NULL marker");
        Refuses(()=>PartitionBounds.Range([new(PartitionValueKind.Minimum),V("2")],[V("3"),V("4")]).ToSql(PartitionStrategy.Range,2),"RANGE enforces consistent infinite suffix");
        Refuses(()=>PartitionBounds.List([]).ToSql(PartitionStrategy.List,1),"LIST rejects empty values");
        Refuses(()=>PartitionBounds.List([new(PartitionValueKind.Maximum)]).ToSql(PartitionStrategy.List,1),"LIST rejects MAXVALUE marker");
        foreach(var pair in new[]{(0,0),(2,-1),(2,2)})Refuses(()=>PartitionBounds.Hash(pair.Item1,pair.Item2).ToSql(PartitionStrategy.Hash,1),"HASH rejects invalid modulus/remainder");
        Refuses(()=>PartitionBounds.Default(PartitionStrategy.Hash),"HASH rejects DEFAULT");
        Refuses(()=>PartitionBounds.List([V("1")]).ToSql(PartitionStrategy.Range,1),"Bound strategy must match inspected parent");
        Refuses(()=>PartitionBounds.List(Enumerable.Repeat(V("1"),1001)),"LIST refuses more than 1000 values");
        Refuses(()=>new PartitionValue(PartitionValueKind.Null,"NULL").ToSql(),"Special markers cannot contain hidden literal text");
        var source=new List<PartitionValue>{V("0")};var captured=PartitionBounds.Range(source,[V("10")]);source.Clear();
        check(captured.Lower.Count==1,"Bounds copy caller collections");
        bool immutable=false;try{((IList<PartitionValue>)captured.Lower).Clear();}catch(NotSupportedException){immutable=true;}check(immutable,"Bounds expose read-only values");
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        await using var owner=new NpgsqlConnection(f.CreatorConnection);await owner.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,owner);return await c.ExecuteScalarAsync();}
        var service=new AdministrativeEditService(f.CreatorConnection);var catalog=new PartitionCatalog(f.CreatorConnection);
        async Task<PartitionParent> Parent(string name,PartitionStrategy strategy,ColumnType type=ColumnType.Integer)
        {
            await service.ApplyAsync(new CreateTableEdit(f.Schema,name,[new("key",type),new("note",ColumnType.Text)]){Partitioning=new(strategy,["key"])});
            return (await catalog.InspectAsync(f.Schema,name)).Parent;
        }
        async Task<bool> Missing(string name)=>(bool)(await Scalar($"SELECT to_regclass('{f.QS}.{name}') IS NULL"))!;
        async Task<bool> ServerReject(IAdministrativeEdit edit,string state)
        {try{await service.ApplyAsync(edit);return false;}catch(PostgresException e)when(e.SqlState==state){return true;}}
        var range=await Parent("r",PartitionStrategy.Range);
        check(range.DatabaseOid!=0 && range.NamespaceOid!=0 && range.Oid!=0 && range.KeyCount==1 && range.Definition.Contains("RANGE"),"Inspection captures database/namespace/parent and strategy");
        await service.ApplyAsync(new CreatePartitionEdit(range,f.Schema,"r0",captured));
        await service.ApplyAsync(new CreatePartitionEdit(range,f.Schema,"r1",PartitionBounds.Range([V("10")],[new(PartitionValueKind.Maximum)])));
        await service.ApplyAsync(new CreatePartitionEdit(range,f.Schema,"rneg",PartitionBounds.Range([new(PartitionValueKind.Minimum)],[V("0")])));
        await Scalar($"INSERT INTO {f.QS}.r VALUES(-1,'negative'),(0,'lower'),(9,'inside'),(10,'upper')");
        check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.r0"))==2 && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.r1"))==1,"RANGE lower inclusive/upper exclusive routing is real");
        check(await ServerReject(new CreatePartitionEdit(range,f.Schema,"overlap",PartitionBounds.Range([V("5")],[V("12")])),"42P17") && await Missing("overlap"),"Overlapping RANGE rolls back without a child");
        check(await ServerReject(new CreatePartitionEdit(range,f.Schema,"empty_range",PartitionBounds.Range([V("5")],[V("5")])),"42P17") && await Missing("empty_range"),"Empty RANGE is rejected by server");
        check(await ServerReject(new CreatePartitionEdit(range,f.Schema,"bad_value",PartitionBounds.Range([V("0); DROP TABLE r;--")],[V("1")])),"22P02") && await Missing("bad_value"),"Bound SQL-looking text stays a literal and cannot execute");
        await service.ApplyAsync(new CreatePartitionEdit(range,f.Schema,"rdefault",PartitionBounds.Default(PartitionStrategy.Range)));
        await Scalar($"INSERT INTO {f.QS}.r VALUES(NULL,'null')");
        check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.rdefault"))==1,"RANGE DEFAULT receives NULL outside explicit ranges");
        var inspected=await catalog.InspectAsync(f.Schema,"r");
        check(inspected.Children.Count==4 && inspected.Children.All(c=>c.Oid!=0 && c.NamespaceOid==range.NamespaceOid && c.Kind=="r" && !c.DetachPending && c.Bound.Length>0),"Read-only inspection captures direct links and native bound definitions");
        immutable=false;try{((IList<PartitionChild>)inspected.Children).Clear();}catch(NotSupportedException){immutable=true;}check(immutable,"Partition child collection is read-only");
        var list=await Parent("l",PartitionStrategy.List,ColumnType.Text);
        string unusual="ação'\\;--";
        await service.ApplyAsync(new CreatePartitionEdit(list,f.Schema,"lvalues",PartitionBounds.List([V(unusual),V(""),new(PartitionValueKind.Null)])));
        await service.ApplyAsync(new CreatePartitionEdit(list,f.Schema,"ldefault",PartitionBounds.Default(PartitionStrategy.List)));
        await using(var insert=new NpgsqlCommand($"INSERT INTO {f.QS}.l VALUES(@v,'exact'),('', 'empty'),(NULL,'null'),('other','default')",owner)){insert.Parameters.AddWithValue("v",unusual);await insert.ExecuteNonQueryAsync();}
        check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.lvalues"))==3 && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.ldefault"))==1,"LIST preserves Unicode quotes backslash empty literal and NULL separately");
        check(await ServerReject(new CreatePartitionEdit(list,f.Schema,"lduplicate",PartitionBounds.List([V(unusual)])),"42P17") && await Missing("lduplicate"),"Overlapping LIST value leaves no child");
        check(await ServerReject(new CreatePartitionEdit(list,f.Schema,"lblocked",PartitionBounds.List([V("other")])),"23514") && await Missing("lblocked"),"Existing DEFAULT rows prevent conflicting new partition without moving data");
        check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.l"))==4,"Rejected LIST creation preserves all rows");
        var hash=await Parent("h",PartitionStrategy.Hash);
        await service.ApplyAsync(new CreatePartitionEdit(hash,f.Schema,"h0",PartitionBounds.Hash(2,0)));
        await service.ApplyAsync(new CreatePartitionEdit(hash,f.Schema,"h1",PartitionBounds.Hash(2,1)));
        await Scalar($"INSERT INTO {f.QS}.h SELECT g,'hash' FROM generate_series(1,40) g");
        check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.h0"))>0 && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.h1"))>0 && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.h"))==40,"HASH routes actual rows across both remainders");
        check(await ServerReject(new CreatePartitionEdit(hash,f.Schema,"hdup",PartitionBounds.Hash(2,0)),"42P17") && await Missing("hdup"),"Duplicate HASH remainder rolls back");
        var fresh=await Parent("changed",PartitionStrategy.Range);
        await Scalar($"ALTER TABLE {f.QS}.changed RENAME COLUMN key TO renamed");
        bool stale=false;try{await service.ApplyAsync(new CreatePartitionEdit(fresh,f.Schema,"stale",captured));}catch(InvalidOperationException){stale=true;}
        check(stale && await Missing("stale"),"Changed parent definition refuses stale inspection");
        var replaced=await Parent("replaced",PartitionStrategy.Range);
        await Scalar($"DROP TABLE {f.QS}.replaced;CREATE TABLE {f.QS}.replaced(key integer,note text) PARTITION BY RANGE(key)");
        stale=false;try{await service.ApplyAsync(new CreatePartitionEdit(replaced,f.Schema,"stale_oid",captured));}catch(InvalidOperationException){stale=true;}
        check(stale && await Missing("stale_oid"),"Same-name replacement cannot inherit inspected parent identity");
        bool denied=false;try{await new AdministrativeEditService(f.RecipientConnection).ApplyAsync(new CreatePartitionEdit(range,f.Schema,"denied",captured));}catch(PostgresException e)when(e.SqlState=="42501"){denied=true;}
        check(denied && await Missing("denied"),"Partition creation respects ordinary owner authority");
        using var cancel=new CancellationTokenSource();cancel.Cancel();bool cancelled=false;
        try{await catalog.InspectAsync(f.Schema,"r",cancel.Token);}catch(OperationCanceledException){cancelled=true;}check(cancelled,"Partition inspection honors pre-cancellation");
        var rollback=await Parent("rollback",PartitionStrategy.Range);bool failed=false;
        try{await service.ApplyAsync(new FailAfterCreate(new(rollback,f.Schema,"rolled_back",captured)));}catch(InvalidOperationException){failed=true;}
        check(failed && await Missing("rolled_back"),"Post-DDL failure rolls partition and link back atomically");
    }
    private sealed record FailAfterCreate(CreatePartitionEdit Inner):IAdministrativeEdit
    {
        public string ToSql()=>Inner.ToSql();
        public Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token=default)=>Inner.ValidateAsync(c,t,token);
        public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token=default){await Inner.VerifyAppliedAsync(c,t,token);throw new InvalidOperationException("Synthetic post-DDL failure");}
    }
}
