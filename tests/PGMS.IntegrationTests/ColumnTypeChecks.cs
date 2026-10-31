using Npgsql;
using PGMS.PostgreSql;

internal static class ColumnTypeChecks
{
    private sealed class FailAfter(IAdministrativeEdit edit) : IAdministrativeEdit
    {
        public string ToSql()=>edit.ToSql();
        public Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token)=>edit.ValidateAsync(c,t,token);
        public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token){await edit.VerifyAppliedAsync(c,t,token);throw new IOException("post-DDL fixture");}
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        await f.Owner($"CREATE TYPE {f.QS}.mood AS ENUM ('ação','漢字'); CREATE DOMAIN {f.QS}.positive AS numeric(8,2) CHECK(VALUE>0); CREATE TYPE {f.QS}.pair AS (x int,y int); CREATE TABLE {f.QS}.target(value text,other int); INSERT INTO {f.QS}.target VALUES(' 12 ',7),(' 15 ',8)");
        var catalog=new DataTypeCatalog(f.CreatorConnection);var types=await catalog.InspectAsync();
        var mood=types.Single(x=>x.Schema==f.Schema && x.Name=="mood");var domain=types.Single(x=>x.Schema==f.Schema && x.Name=="positive");
        var integer=types.Single(x=>x.Oid==23);var text=types.Single(x=>x.Oid==25);var numeric=types.Single(x=>x.Oid==1700);var varchar=types.Single(x=>x.Oid==1043);
        var explorer = new CatalogService(f.CreatorConnection);
        var folders = await explorer.ChildrenAsync(new(CatalogKind.Schema, f.Schema, explorer.DefaultDatabase, f.Schema));
        var enums = await explorer.ChildrenAsync(folders.Single(x => x.Kind == CatalogKind.Enums));
        var domains = await explorer.ChildrenAsync(folders.Single(x => x.Kind == CatalogKind.Domains));
        check(enums.Count == 1 && enums[0].Oid == mood.Oid && enums[0].Schema == f.Schema && enums[0].Database == explorer.DefaultDatabase, "Explorer enum folder preserves exact identity and database context");
        check(domains.Count == 1 && domains[0].Oid == domain.Oid && domains[0].Kind == CatalogKind.DataType, "Explorer domain folder excludes enums, composite and generated array types");
        check(!enums[0].CanExpand && (await explorer.ChildrenAsync(enums[0])).Count == 0 && !enums[0].CanEditTable, "Type leaves have no spurious table expansion or editing commands");
        check(mood.Oid!=0 && mood.Kind=="e" && domain.BaseTypeOid==1700,"Type catalog captures owner enum and numeric domain with exact OIDs");
        check(!types.Any(x=>x.Schema==f.Schema && x.Name=="pair") && types.All(x=>x.Kind is "b" or "e" or "d" or "r"),"Type picker excludes unsupported composite and array types");
        check(!(await new DataTypeCatalog(f.RecipientConnection).InspectAsync()).Any(x=>x.Schema==f.Schema),"Type picker excludes schema without USAGE");
        await f.Owner($"GRANT USAGE ON SCHEMA {f.QS} TO {f.QR}; REVOKE USAGE ON TYPE {f.QS}.mood FROM PUBLIC");
        check(!(await new DataTypeCatalog(f.RecipientConnection).InspectAsync()).Any(x=>x.Oid==mood.Oid),"Type picker excludes enum without USAGE");
        await f.Owner($"GRANT USAGE ON TYPE {f.QS}.mood TO {f.QR}");
        check((await new DataTypeCatalog(f.RecipientConnection).InspectAsync()).Any(x=>x.Oid==mood.Oid),"Explicit type USAGE enables catalog selection");
        // ACL changes modify xmin, so re-inspect before reviewed operations.
        mood=(await catalog.InspectAsync()).Single(x=>x.Oid==mood.Oid);
        var service=new AdministrativeEditService(f.CreatorConnection);
        await service.ApplyAsync(new CreateTableEdit(f.Schema,"typed",[
            new("state",ColumnType.Text){InspectedType=new(mood)},new("amount",ColumnType.Numeric){InspectedType=new(domain)},
            new("label",ColumnType.Text){InspectedType=new(varchar,Length:12)},new("ratio",ColumnType.Numeric){InspectedType=new(numeric,Precision:9,Scale:3)}]));
        await f.Owner($"INSERT INTO {f.QS}.typed VALUES('漢字',12.34,'ação',1.234)");
        bool domainEnforced=false;try{await f.Owner($"INSERT INTO {f.QS}.typed(amount) VALUES(-1)");}catch(PostgresException e)when(e.SqlState=="23514"){domainEnforced=true;}
        check(domainEnforced,"New table uses real domain constraint and accepts Unicode enum");
        await using var owner=new NpgsqlConnection(f.CreatorConnection);await owner.OpenAsync();
        async Task<object?> Scalar(string sql){await using var command=new NpgsqlCommand(sql,owner);return await command.ExecuteScalarAsync();}
        check(Convert.ToInt32(await Scalar($"SELECT atttypmod FROM pg_attribute WHERE attrelid='{f.QS}.typed'::regclass AND attname='label'"))==16,"Varchar modifier persists on new table");
        check(Convert.ToInt32(await Scalar($"SELECT atttypmod FROM pg_attribute WHERE attrelid='{f.QS}.typed'::regclass AND attname='ratio'"))==((9<<16)|3)+4,"Numeric precision and scale persist on new table");
        bool invalid=false;try{new SelectedColumnType(numeric,Precision:4,Scale:5).ToSql();}catch(ArgumentException){invalid=true;}
        check(invalid,"Unsupported numeric modifier is refused before SQL");
        var targetCatalog=new ExpressionIndexCatalog(f.CreatorConnection);
        var target=await targetCatalog.InspectAsync(f.Schema,"target");
        await service.ApplyAsync(new InspectedColumnTypeEdit(target,"value",new(integer),conversion:ColumnTypeConversion.TrimText));
        check(Convert.ToInt64(await Scalar($"SELECT sum(value) FROM {f.QS}.target"))==27,"Reviewed trim USING converts stored text to real integer values");
        check(Convert.ToInt64(await Scalar($"SELECT sum(other) FROM {f.QS}.target"))==15,"Type conversion preserves unrelated column values");
        bool stale=false;try{await service.ApplyAsync(new InspectedColumnTypeEdit(target,"value",new(text)));}catch(InvalidOperationException){stale=true;}
        check(stale,"Changed column shape refuses stale type operation");
        target=await targetCatalog.InspectAsync(f.Schema,"target");
        bool rollback=false;try{await service.ApplyAsync(new FailAfter(new InspectedColumnTypeEdit(target,"value",new(text))));}catch(IOException){rollback=true;}
        check(rollback && (await targetCatalog.InspectAsync(f.Schema,"target")).Columns.Single(x=>x.Name=="value").TypeOid==23,"Post-DDL verification failure rolls back type and stored data");
        await service.ApplyAsync(new InspectedColumnTypeEdit(target,"amount",new(domain),add:true));
        check((await targetCatalog.InspectAsync(f.Schema,"target")).Columns.Single(x=>x.Name=="amount").TypeOid==domain.Oid,"Inspected ADD COLUMN uses real domain identity");
        await f.Owner($"ALTER TYPE {f.QS}.mood ADD VALUE 'novo'");
        bool changed=false;try{await service.ApplyAsync(new CreateTableEdit(f.Schema,"stale_enum",[new("v",ColumnType.Text){InspectedType=new(mood)}]));}catch(InvalidOperationException){changed=true;}
        check(changed && await Scalar($"SELECT to_regclass('{f.QS}.stale_enum')::text") is DBNull,"Enum label change invalidates reviewed selection before table creation");
        await f.Owner($"ALTER DOMAIN {f.QS}.positive ADD CONSTRAINT upper_limit CHECK(VALUE<100)");
        changed=false;try{await service.ApplyAsync(new InspectedColumnTypeEdit(await targetCatalog.InspectAsync(f.Schema,"target"),"second",new(domain),add:true));}catch(InvalidOperationException){changed=true;}
        check(changed,"Domain constraint change invalidates selected definition");
        await f.Owner($"CREATE TABLE {f.QS}.bad(value text);INSERT INTO {f.QS}.bad VALUES('abc')");
        bool bad=false;try{await service.ApplyAsync(new InspectedColumnTypeEdit(await targetCatalog.InspectAsync(f.Schema,"bad"),"value",new(integer)));}catch(PostgresException e)when(e.SqlState=="22P02"){bad=true;}
        check(bad && Convert.ToString(await Scalar($"SELECT value FROM {f.QS}.bad"))=="abc","Failed conversion rolls back without losing original text");
        await f.Owner($"CREATE TABLE {f.QS}.identity_test(id int GENERATED ALWAYS AS IDENTITY)");
        bool identity=false;try{await service.ApplyAsync(new InspectedColumnTypeEdit(await targetCatalog.InspectAsync(f.Schema,"identity_test"),"id",new(text)));}catch(InvalidOperationException){identity=true;}
        check(identity,"Generic type operation refuses identity column");
        await f.Owner($"CREATE TYPE {f.QS}.replaceable AS ENUM('a')");
        var replaceable=(await catalog.InspectAsync()).Single(x=>x.Schema==f.Schema && x.Name=="replaceable");
        await f.Owner($"DROP TYPE {f.QS}.replaceable; CREATE TYPE {f.QS}.replaceable AS ENUM('a')");
        bool replacement=false;try{await service.ApplyAsync(new InspectedColumnTypeEdit(await targetCatalog.InspectAsync(f.Schema,"target"),"replacement",new(replaceable),add:true));}catch(InvalidOperationException){replacement=true;}
        check(replacement,"Same-name recreated enum cannot replace the selected type OID");
        await f.Owner($"CREATE TABLE {f.QS}.empty_value(value text); INSERT INTO {f.QS}.empty_value VALUES(''),('42')");
        await service.ApplyAsync(new InspectedColumnTypeEdit(await targetCatalog.InspectAsync(f.Schema,"empty_value"),"value",new(integer),conversion:ColumnTypeConversion.EmptyTextToNull));
        check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.empty_value WHERE value IS NULL"))==1 && Convert.ToInt64(await Scalar($"SELECT sum(value) FROM {f.QS}.empty_value"))==42,"Empty-to-NULL USING preserves null distinction and converts numeric text");
        await f.Owner($"CREATE TABLE {f.QS}.lower_value(value text); INSERT INTO {f.QS}.lower_value VALUES('ABC')");
        await service.ApplyAsync(new InspectedColumnTypeEdit(await targetCatalog.InspectAsync(f.Schema,"lower_value"),"value",new(varchar,Length:10),conversion:ColumnTypeConversion.LowerText));
        check(Convert.ToString(await Scalar($"SELECT value FROM {f.QS}.lower_value"))=="abc","Lower USING converts to selected varchar with length modifier");
        await f.Owner($"CREATE TABLE {f.QS}.generated_value(value int GENERATED ALWAYS AS (1) STORED)");
        bool generated=false;try{await service.ApplyAsync(new InspectedColumnTypeEdit(await targetCatalog.InspectAsync(f.Schema,"generated_value"),"value",new(text)));}catch(InvalidOperationException){generated=true;}
        check(generated,"Generic type operation refuses generated columns");
        using var canceled=new CancellationTokenSource();canceled.Cancel();bool cancel=false;try{await catalog.InspectAsync(canceled.Token);}catch(OperationCanceledException){cancel=true;}
        check(cancel,"Type catalog honors cancellation before connecting");
        await f.Owner($"CREATE TYPE {f.QS}.base_mood AS ENUM('one');CREATE DOMAIN {f.QS}.nested_mood AS {f.QS}.base_mood;CREATE DOMAIN {f.QS}.outer_mood AS {f.QS}.nested_mood");
        var outer=(await catalog.InspectAsync()).Single(x=>x.Schema==f.Schema && x.Name=="outer_mood");
        await f.Owner($"ALTER TYPE {f.QS}.base_mood ADD VALUE 'two'");
        bool baseChanged=false;try{await service.ApplyAsync(new CreateTableEdit(f.Schema,"stale_base",[new("v",ColumnType.Text){InspectedType=new(outer)}]));}catch(InvalidOperationException){baseChanged=true;}
        check(baseChanged,"Nested domain selection detects changed base enum labels");
        outer=(await catalog.InspectAsync()).Single(x=>x.Schema==f.Schema && x.Name=="outer_mood");
        await f.Owner($"ALTER DOMAIN {f.QS}.nested_mood ADD CONSTRAINT one_only CHECK(VALUE::text='one')");
        baseChanged=false;try{await service.ApplyAsync(new CreateTableEdit(f.Schema,"stale_constraint",[new("v",ColumnType.Text){InspectedType=new(outer)}]));}catch(InvalidOperationException){baseChanged=true;}
        check(baseChanged,"Nested domain selection detects changed intermediate domain constraints");
        outer=(await catalog.InspectAsync()).Single(x=>x.Schema==f.Schema && x.Name=="outer_mood");
        await service.ApplyAsync(new CreateTableEdit(f.Schema,"fresh_nested",[new("v",ColumnType.Text){InspectedType=new(outer)}]));
        await f.Owner($"INSERT INTO {f.QS}.fresh_nested VALUES('one')");
        bool nestedRule=false;try{await f.Owner($"INSERT INTO {f.QS}.fresh_nested VALUES('two')");}catch(PostgresException e)when(e.SqlState=="23514"){nestedRule=true;}
        check(nestedRule,"Reinspected nested domain enforces intermediate constraint on actual data");
        // Verify the bounded recursion refuses a chain that would hide a deeper dependency.
        await f.Owner($"CREATE DOMAIN {f.QS}.depth0 AS int");
        for(int i=1;i<=17;i++)await f.Owner($"CREATE DOMAIN {f.QS}.depth{i} AS {f.QS}.depth{i-1}");
        bool depth=false;try{await catalog.InspectAsync();}catch(InvalidOperationException){depth=true;}
        check(depth,"Domain chain beyond sixteen base types is refused rather than truncated");
    }
}
