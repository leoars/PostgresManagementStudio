using Npgsql;
using PGMS.PostgreSql;

internal static class CreateUserTypeChecks
{
    private sealed class Interleave(IAdministrativeEdit edit,Func<Task> action):IAdministrativeEdit
    {
        public string ToSql()=>edit.ToSql();
        public async Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token){await edit.ValidateAsync(c,tx,token);await action();}
        public Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)=>edit.VerifyAppliedAsync(c,tx,token);
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        var catalog=new TypeCreationCatalog(f.CreatorConnection);var schema=await catalog.InspectSchemaAsync(f.Schema);var service=new AdministrativeEditService(f.CreatorConnection);
        check(schema.Name==f.Schema && schema.Oid!=0 && schema.CreatorOid==schema.OwnerOid,"Type creation captures schema and ordinary creator identity");
        string[] labels={"ação","漢字'\\",""};var request=new CreateEnumEdit(schema,"mood",labels);labels[0]="mutated";
        await service.ApplyAsync(request);
        var mood=await new EnumCatalog(f.CreatorConnection).InspectAsync(f.Schema,"mood");
        check(mood.Labels.Select(x=>x.Value).SequenceEqual(new[]{"ação","漢字'\\",""}),"Enum creation freezes input order and preserves Unicode and empty label");
        var types=await new DataTypeCatalog(f.CreatorConnection).InspectAsync();var numeric=types.Single(x=>x.Oid==1700);var text=types.Single(x=>x.Oid==25);
        await service.ApplyAsync(new CreateDomainEdit(schema,"amount",new(numeric,Precision:8,Scale:2),"4.25",true,"positive",new(IndexComparison.Greater,"0")));
        var amount=await new DomainCatalog(f.CreatorConnection).InspectAsync(f.Schema,"amount");
        check(amount.BaseTypeSql=="numeric(8,2)" && amount.NotNull && amount.Constraints.Single().Validated,"Domain creation persists modifier, NOT NULL and named typed constraint");
        await f.Owner($"CREATE TABLE {f.QS}.values(v {f.QS}.amount);INSERT INTO {f.QS}.values DEFAULT VALUES");
        await using var c=new NpgsqlConnection(f.CreatorConnection);await c.OpenAsync();
        async Task<object?> Scalar(string sql){await using var cmd=new NpgsqlCommand(sql,c);return await cmd.ExecuteScalarAsync();}
        check(Convert.ToDecimal(await Scalar($"SELECT v FROM {f.QS}.values"))==4.25m,"Created domain default populates real rows");
        bool rejected=false;try{await f.Owner($"INSERT INTO {f.QS}.values VALUES(-1)");}catch(PostgresException e)when(e.SqlState=="23514"){rejected=true;}
        check(rejected,"Created domain rule enforces against actual values");
        rejected=false;try{await f.Owner($"INSERT INTO {f.QS}.values VALUES(NULL)");}catch(PostgresException e)when(e.SqlState=="23502"){rejected=true;}
        check(rejected,"Created domain NOT NULL rejects direct NULL assignment");
        await service.ApplyAsync(new CreateDomainEdit(schema,"word",new(text),""));
        check((string)(await Scalar($"SELECT typdefault FROM pg_catalog.pg_type WHERE oid='{f.QS}.word'::regtype"))! =="''::text","Empty literal domain default is distinct from no default");
        await service.ApplyAsync(new CreateDomainEdit(schema,"state",new(mood.Type),"ação"));
        await f.Owner($"CREATE TABLE {f.QS}.states(v {f.QS}.state);INSERT INTO {f.QS}.states DEFAULT VALUES");
        check((string)(await Scalar($"SELECT v::text FROM {f.QS}.states"))! =="ação","Domain over selected user enum uses its inspected identity and default");
        await f.Owner($"ALTER TYPE {f.QS}.mood ADD VALUE 'new'");
        bool stale=false;try{await service.ApplyAsync(new CreateDomainEdit(schema,"stale",new(mood.Type)));}catch(InvalidOperationException){stale=true;}
        check(stale && await Scalar($"SELECT to_regtype('{f.QS}.stale')::text") is DBNull,"Changed base enum refuses new domain before DDL");
        bool duplicate=false;try{await service.ApplyAsync(request);}catch(PostgresException e)when(e.SqlState=="42710"){duplicate=true;}
        check(duplicate && (await new EnumCatalog(f.CreatorConnection).InspectAsync(f.Schema,"mood")).Labels.Count==4,"Name collision never replaces an existing enum");
        foreach(string[] invalid in new[]{Array.Empty<string>(),new[]{"a","a"},Enumerable.Range(0,1001).Select(x=>x.ToString()).ToArray()})
        {
            bool bad=false;try{new CreateEnumEdit(schema,"invalid",invalid).ToSql();}catch(ArgumentException){bad=true;}
            check(bad,"Enum creation rejects empty, duplicate or oversized label collection");
        }
        bool denied=false;try{await new TypeCreationCatalog(f.RecipientConnection).InspectSchemaAsync(f.Schema);}catch(InvalidOperationException){denied=true;}
        check(denied,"Type creation inspection refuses missing schema USAGE/CREATE");
        await f.Owner($"GRANT USAGE ON SCHEMA {f.QS} TO {f.QR}");
        denied=false;try{await new TypeCreationCatalog(f.RecipientConnection).InspectSchemaAsync(f.Schema);}catch(InvalidOperationException){denied=true;}
        check(denied,"Schema USAGE alone does not authorize type creation");
        var fresh=await catalog.InspectSchemaAsync(f.Schema);
        bool changed=false;try{await service.ApplyAsync(new CreateEnumEdit(schema,"old_schema_acl",new[]{"a"}));}catch(InvalidOperationException){changed=true;}
        check(changed,"Changed schema row invalidates old creation review");
        using var canceled=new CancellationTokenSource();canceled.Cancel();bool cancel=false;
        try{await service.ApplyAsync(new CreateEnumEdit(fresh,"canceled",new[]{"a"}),canceled.Token);}catch(OperationCanceledException){cancel=true;}
        check(cancel && await Scalar($"SELECT to_regtype('{f.QS}.canceled')::text") is DBNull,"Canceled type creation leaves no object");
        bool concurrent=false;
        try{await service.ApplyAsync(new Interleave(new CreateEnumEdit(fresh,"concurrent",new[]{"a"}),()=>f.Owner($"REVOKE USAGE ON SCHEMA {f.QS} FROM {f.QR}")));}catch(InvalidOperationException){concurrent=true;}
        check(concurrent && await Scalar($"SELECT to_regtype('{f.QS}.concurrent')::text") is DBNull,"Schema change between validation and creation triggers rollback of new type");
    }
}
