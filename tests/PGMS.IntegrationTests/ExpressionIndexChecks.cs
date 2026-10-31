using Npgsql;
using PGMS.PostgreSql;

internal static class ExpressionIndexChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        await f.Owner($"CREATE TABLE {f.QS}.target(id integer,name text,note text,active boolean);INSERT INTO {f.QS}.target VALUES(1,'Alpha','a',true),(2,'Beta','b',false),(3,NULL,'c',true)");
        var catalog=new ExpressionIndexCatalog(f.CreatorConnection);var target=await catalog.InspectAsync(f.Schema,"target");
        var service=new AdministrativeEditService(f.CreatorConnection);
        await using var owner=new NpgsqlConnection(f.CreatorConnection);await owner.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,owner);return await c.ExecuteScalarAsync();}
        void Refuses(Action action,string message){bool refused=false;try{action();}catch(ArgumentException){refused=true;}check(refused,message);}
        check(target.Oid!=0 && target.DatabaseOid!=0 && target.NamespaceOid!=0 && target.OwnerOid!=0 && target.Columns.Count==4,"Expression index inspection captures target and exact typed columns");
        var opclasses=await catalog.InspectOperatorClassesAsync();var pattern=opclasses.Single(o=>o.Name=="text_pattern_ops");
        check(opclasses.All(o=>o.Schema=="pg_catalog" && o.Oid!=0 && o.MethodOid!=0),"Operator class inspection returns named built-in B-tree identities");
        var explicitKey=new ExpressionIndexKey(new("name",IndexTransform.Lower),IndexSortOrder.Descending,IndexNullPlacement.Last){OperatorClass=pattern};
        await service.ApplyAsync(new ExpressionIndexEdit(target,"pattern",[explicitKey]));
        check((bool)(await Scalar($"SELECT i.indclass[0]={pattern.Oid}::oid AND i.indoption[0]=1 FROM pg_index i WHERE i.indexrelid='{f.QS}.pattern'::regclass"))!,"Explicit opclass OID and DESC NULLS LAST survive actual creation");
        Refuses(()=>new ExpressionIndexEdit(target,"wrong_type",[explicitKey with{Expression=new("id")}]).ToSql(),"Incompatible operator class input type refused before SQL");
        bool staleOp=false;try{await service.ApplyAsync(new ExpressionIndexEdit(target,"wrong_op",[explicitKey with{OperatorClass=pattern with{Oid=pattern.Oid+1000000}}]));}catch(InvalidOperationException){staleOp=true;}check(staleOp,"Forged opclass identity cannot create an index by matching name");
        bool immutable=false;try{((IList<IndexColumnIdentity>)target.Columns).Clear();}catch(NotSupportedException){immutable=true;}check(immutable,"Index target columns cannot be mutated");
        Refuses(()=>new IndexExpression("name",(IndexTransform)900).ToSql(),"Unknown expression transform refused");
        Refuses(()=>new ExpressionIndexEdit(target,"bad",[new(new("missing",IndexTransform.Lower))]).ToSql(),"Expression requires exact inspected column");
        Refuses(()=>new ExpressionIndexEdit(target,"bad",[new(new("id",IndexTransform.Lower))]).ToSql(),"Lower refuses numeric input before SQL");
        Refuses(()=>new ExpressionIndexEdit(target,"bad",[new(new("name",IndexTransform.Absolute))]).ToSql(),"Absolute refuses text input before SQL");
        Refuses(()=>new ExpressionIndexEdit(target,"bad",[]).ToSql(),"Empty index keys refused");
        Refuses(()=>new ExpressionIndexEdit(target,"bad",Enumerable.Repeat(new ExpressionIndexKey(new("id")),33)).ToSql(),"Index key count limited before SQL");
        Refuses(()=>new ExpressionIndexEdit(target,"bad",[new(new("id"))],nullsNotDistinct:true).ToSql(),"NULLS NOT DISTINCT requires unique expression index");
        Refuses(()=>new ExpressionIndexEdit(target,"bad",[new(new("id"))],includedColumns:["id"]).ToSql(),"Plain key cannot also be INCLUDE");
        Refuses(()=>new ExpressionIndexEdit(target,"bad",[new(new("id"))],predicate:new(new("name"),IndexComparison.IsNull,"hidden")).ToSql(),"NULL predicate rejects ignored literal value");
        var keys=new List<ExpressionIndexKey>{new(new("name",IndexTransform.Lower))};var include=new List<string>{"note"};
        var proposal=new ExpressionIndexEdit(target,"lower_unique",keys,true,true,include);keys.Clear();include.Clear();
        check(proposal.Keys.Count==1 && proposal.IncludedColumns.Count==1,"Expression index proposal captures mutable input collections");
        await service.ApplyAsync(proposal);
        check((bool)(await Scalar($"SELECT indisvalid AND indisunique AND indnullsnotdistinct AND indexprs IS NOT NULL AND indnatts=2 FROM pg_index WHERE indexrelid='{f.QS}.lower_unique'::regclass"))!,"Real unique lower expression index stores expression, INCLUDE and null semantics");
        bool duplicate=false;try{await Scalar($"INSERT INTO {f.QS}.target VALUES(4,'ALPHA','dup',true)");}catch(PostgresException e)when(e.SqlState=="23505"){duplicate=true;}check(duplicate,"Expression uniqueness rejects differently-cased duplicate data");
        duplicate=false;try{await Scalar($"INSERT INTO {f.QS}.target VALUES(4,NULL,'dupnull',true)");}catch(PostgresException e)when(e.SqlState=="23505"){duplicate=true;}check(duplicate,"Expression unique index enforces NULLS NOT DISTINCT");
        foreach(var transform in new[]{IndexTransform.Upper,IndexTransform.Length,IndexTransform.Absolute})
        {
            string column=transform==IndexTransform.Absolute?"id":"name",name="expr_"+transform;
            await service.ApplyAsync(new ExpressionIndexEdit(target,name,[new(new(column,transform),IndexSortOrder.Descending,IndexNullPlacement.First)]));
            await using var cmd=new NpgsqlCommand("SELECT pg_get_indexdef(c.oid) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name",owner);cmd.Parameters.AddWithValue("schema",f.Schema);cmd.Parameters.AddWithValue("name",name);
            check(((string)(await cmd.ExecuteScalarAsync())!).Contains("DESC"),"Expression and per-key direction are installed: "+transform);
        }
        await service.ApplyAsync(new ExpressionIndexEdit(target,"partial",[new(new("id"))],true,predicate:new(new("active"),IndexComparison.Equal,"true")));
        await Scalar($"INSERT INTO {f.QS}.target VALUES(1,'not active','partial outside',false)");
        duplicate=false;try{await Scalar($"INSERT INTO {f.QS}.target VALUES(1,'another active','partial inside',true)");}catch(PostgresException e)when(e.SqlState=="23505"){duplicate=true;}
        check(duplicate && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.target WHERE id=1"))==2,"Partial uniqueness applies only to matching rows");
        await service.ApplyAsync(new ExpressionIndexEdit(target,"quoted_predicate",[new(new("id"))],predicate:new(new("note"),IndexComparison.Equal,"a'; DROP TABLE target;--\\")));
        check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.target"))==4,"Predicate SQL-looking text remains a literal");
        await Scalar($"ALTER TABLE {f.QS}.target RENAME COLUMN note TO renamed");
        bool stale=false;try{await service.ApplyAsync(new ExpressionIndexEdit(target,"stale",[new(new("id"))]));}catch(InvalidOperationException){stale=true;}check(stale,"Changed column snapshot refuses stale index creation");
        var refreshed=await catalog.InspectAsync(f.Schema,"target");
        await service.ApplyAsync(new ExpressionIndexEdit(refreshed,"refreshed",[new(new("id")),new(new("name",IndexTransform.Lower))]));
        check((bool)(await Scalar($"SELECT indisvalid AND indnkeyatts=2 FROM pg_index WHERE indexrelid='{f.QS}.refreshed'::regclass"))!,"Reinspection permits mixed plain/expression keys");
        await f.Owner($"CREATE TABLE {f.QS}.replace(id integer)");var replace=await catalog.InspectAsync(f.Schema,"replace");await f.Owner($"DROP TABLE {f.QS}.replace;CREATE TABLE {f.QS}.replace(id integer)");
        stale=false;try{await service.ApplyAsync(new ExpressionIndexEdit(replace,"stale_oid",[new(new("id"))]));}catch(InvalidOperationException){stale=true;}check(stale,"Same-name table replacement refuses old inspected index target");
        bool denied=false;try{await new AdministrativeEditService(f.RecipientConnection).ApplyAsync(new ExpressionIndexEdit(refreshed,"denied",[new(new("id"))]));}catch(PostgresException e)when(e.SqlState=="42501"){denied=true;}check(denied,"Expression indexes do not bypass owner authority");
        using var cancel=new CancellationTokenSource();cancel.Cancel();bool canceled=false;try{await service.ApplyAsync(new ExpressionIndexEdit(refreshed,"cancelled",[new(new("id"))]),cancel.Token);}catch(OperationCanceledException){canceled=true;}check(canceled,"Pre-cancelled expression index does not write");
        bool rollback=false;try{await service.ApplyAsync(new FailAfter(new(refreshed,"rollback",[new(new("name",IndexTransform.Lower))])));}catch(InvalidOperationException){rollback=true;}
        check(rollback && (bool)(await Scalar($"SELECT to_regclass('{f.QS}.rollback') IS NULL"))!,"Post-index verification failure rolls DDL back");
    }
    private sealed record FailAfter(ExpressionIndexEdit Inner):IAdministrativeEdit
    {
        public string ToSql()=>Inner.ToSql();
        public Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token=default)=>Inner.ValidateAsync(c,t,token);
        public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token=default){await Inner.VerifyAppliedAsync(c,t,token);throw new InvalidOperationException("Synthetic failure");}
    }
}
