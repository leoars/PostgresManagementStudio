using Npgsql;
using PGMS.PostgreSql;

internal static class ExclusionChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);var catalog=new ExpressionIndexCatalog(f.CreatorConnection);var service=new AdministrativeEditService(f.CreatorConnection);
        await using var owner=new NpgsqlConnection(f.CreatorConnection);await owner.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,owner);return await c.ExecuteScalarAsync();}
        var samples=new[]{("int4range","[1,5)","[3,6)","[5,8)"),("int8range","[1,5)","[3,6)","[5,8)"),("numrange","[1,5)","[3,6)","[5,8)"),("daterange","[2026-01-01,2026-01-05)","[2026-01-03,2026-01-06)","[2026-01-05,2026-01-08)"),("tsrange","[2026-01-01 00:00,2026-01-05 00:00)","[2026-01-03 00:00,2026-01-06 00:00)","[2026-01-05 00:00,2026-01-08 00:00)"),("tstzrange","[2026-01-01 00:00+00,2026-01-05 00:00+00)","[2026-01-03 00:00+00,2026-01-06 00:00+00)","[2026-01-05 00:00+00,2026-01-08 00:00+00)")};
        foreach(var (type,first,overlap,adjacent) in samples)
        {
            string table=f.QS+"."+SqlIdentifier.Quote(type);await Scalar($"CREATE TABLE {table}(during pg_catalog.{type},note text);INSERT INTO {table} VALUES('{first}','first')");
            var target=await catalog.InspectAsync(f.Schema,type);var edit=new RangeExclusionEdit(target,"no_overlap_"+type,[new("during")]);await service.ApplyAsync(edit);
            bool refused=false;try{await Scalar($"INSERT INTO {table} VALUES('{overlap}','overlap')");}catch(PostgresException e)when(e.SqlState=="23P01"){refused=true;}
            check(refused,"Range EXCLUDE rejects actual overlap: "+type);
            await Scalar($"INSERT INTO {table} VALUES('{adjacent}','adjacent'),('empty','empty'),(NULL,'null')");
            check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {table}"))==4,"Range EXCLUDE preserves adjacency, empty and NULL semantics: "+type);
        }
        await Scalar($"CREATE TABLE {f.QS}.deferred(during int4range);INSERT INTO {f.QS}.deferred VALUES('[1,5)')");
        var deferred=await catalog.InspectAsync(f.Schema,"deferred");await service.ApplyAsync(new RangeExclusionEdit(deferred,"deferred_exclusion",[new("during")],true,true));
        bool commitRefused=false;await using(var tx=await owner.BeginTransactionAsync())
        {
            await using(var cmd=new NpgsqlCommand($"INSERT INTO {f.QS}.deferred VALUES('[3,6)')",owner,tx))await cmd.ExecuteNonQueryAsync();
            check(true,"Initially deferred exclusion permits temporary overlap within transaction");
            try{await tx.CommitAsync();}catch(PostgresException e)when(e.SqlState=="23P01"){commitRefused=true;}
        }
        check(commitRefused && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.deferred"))==1,"Deferred exclusion rejects commit and restores rows");
        await Scalar($"CREATE TABLE {f.QS}.equal_range(during int4range)");var equal=await catalog.InspectAsync(f.Schema,"equal_range");await service.ApplyAsync(new RangeExclusionEdit(equal,"no_equal",[new("during",RangeExclusionOperator.Equal)]));
        await Scalar($"INSERT INTO {f.QS}.equal_range VALUES('[1,5)'),('[3,6)')");bool duplicate=false;try{await Scalar($"INSERT INTO {f.QS}.equal_range VALUES('[1,5)')");}catch(PostgresException e)when(e.SqlState=="23P01"){duplicate=true;}check(duplicate,"Equality exclusion allows overlap but rejects equal ranges");
        await Scalar($"CREATE TABLE {f.QS}.existing_bad(during int4range);INSERT INTO {f.QS}.existing_bad VALUES('[1,5)'),('[3,6)')");var bad=await catalog.InspectAsync(f.Schema,"existing_bad");bool rejected=false;try{await service.ApplyAsync(new RangeExclusionEdit(bad,"invalid",[new("during")]));}catch(PostgresException e)when(e.SqlState=="23P01"){rejected=true;}
        check(rejected && (bool)(await Scalar($"SELECT NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='{f.QS}.existing_bad'::regclass AND conname='invalid')"))! && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {f.QS}.existing_bad"))==2,"Existing overlap rejects constraint atomically and preserves data");
        bool invalid=false;try{new RangeExclusionEdit(bad,"bad",[new("during")],false,true).ToSql();}catch(ArgumentException){invalid=true;}check(invalid,"Initially deferred cannot be requested without DEFERRABLE");
        await Scalar($"ALTER TABLE {f.QS}.existing_bad RENAME COLUMN during TO changed");bool stale=false;try{await service.ApplyAsync(new RangeExclusionEdit(bad,"stale",[new("during")]));}catch(InvalidOperationException){stale=true;}check(stale,"Changed range column refuses stale exclusion proposal");
        bool denied=false;try{await new AdministrativeEditService(f.RecipientConnection).ApplyAsync(new RangeExclusionEdit(equal,"denied",[new("during")]));}catch(PostgresException e)when(e.SqlState=="42501"){denied=true;}check(denied,"Range exclusion requires table owner authority");
    }
}
