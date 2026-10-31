using Npgsql;
using PGMS.PostgreSql;

internal static class SequenceOwnerTargetChecks
{
    public static async Task RunAsync(string connection, Action<bool,string> check)
    {
        string name="pgms_owner_target_"+Guid.NewGuid().ToString("N"), table=ObjectSql.Relation("public",name);
        await using var db=new NpgsqlConnection(connection); await db.OpenAsync();
        async Task Sql(string sql) { await using var c=new NpgsqlCommand(sql,db); await c.ExecuteNonQueryAsync(); }
        var service=new SequenceOwnerTargetService(connection);
        try
        {
            await Sql($"CREATE TABLE {table}(id bigint, old text, ident bigint GENERATED ALWAYS AS IDENTITY, calc bigint GENERATED ALWAYS AS (id+1) STORED);");
            var candidates=await service.ListColumnsAsync("public",name);
            check(candidates.Select(x=>x.Column).SequenceEqual(new[]{"id","old"}),"Destination list excludes identity/generated columns and keeps ordinal order");
            check((await service.ListColumnsAsync("public",name+"_absent")).Count==0,"Missing destination table returns empty candidate list");
            var first=await service.InspectAsync("public",name,"id");
            check(first.TableOid!=0 && first.ColumnNumber==1 && first.OwnerOid!=0 && first.Kind=="r" && first.Persistence=="p", "Owner target inspection retains relation and owner identity");
            check(first.Identity=="" && first.Generated=="" && first.TypeOid!=0, "Owner target records column type and flags");
            check((await service.InspectAsync("public",name,"ident")).Identity=="a", "Owner target identifies identity column");
            check((await service.InspectAsync("public",name,"calc")).Generated=="s", "Owner target identifies generated column");
            using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel(); bool stopped=false;
                try { await service.ListColumnsAsync("public",name,canceled.Token); } catch(OperationCanceledException) { stopped=true; }
                check(stopped,"Destination listing honors pre-canceled token");
                stopped=false; try { await service.InspectAsync("public",name,"id",canceled.Token); } catch(OperationCanceledException) { stopped=true; }
                check(stopped,"Destination inspection honors pre-canceled token");
            }
            string role="pgms_target_reader_"+Guid.NewGuid().ToString("N");
            try
            {
                await Sql($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN;");
                string limitedConnection=new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString;
                var limited=new SequenceOwnerTargetService(limitedConnection);
                check((await limited.InspectAsync("public",name,"id")).TableOid==first.TableOid,"Destination metadata is inspectable without table SELECT");
                await using var limitedDb=new NpgsqlConnection(limitedConnection); await limitedDb.OpenAsync();
                bool denied=false; try { await using var c=new NpgsqlCommand($"SELECT * FROM {table}",limitedDb); await c.ExecuteNonQueryAsync(); } catch(PostgresException ex) when(ex.SqlState=="42501") { denied=true; }
                check(denied,"Metadata inspection does not grant table data access");
            }
            finally { await Sql($"DROP ROLE IF EXISTS {SqlIdentifier.Quote(role)};"); }
            bool missing=false; try { await service.InspectAsync("public",name,"absent"); } catch(InvalidOperationException) { missing=true; }
            check(missing,"Owner target refuses absent column");
            await Sql($"ALTER TABLE {table} DROP COLUMN old; ALTER TABLE {table} ADD COLUMN old text;");
            check((await service.InspectAsync("public",name,"old")).ColumnNumber==5,"Recreated column has a different attnum");
            await Sql($"DROP TABLE {table}; CREATE TABLE {table}(id bigint);");
            check((await service.InspectAsync("public",name,"id")).TableOid!=first.TableOid,"Recreated table has a different target OID");
            string specialName="é'\"_"+Guid.NewGuid().ToString("N"), special=ObjectSql.Relation("public",specialName);
            try
            {
                await Sql($"CREATE TABLE {special}({SqlIdentifier.Quote("c'\"é")} bigint);");
                var exact=await service.InspectAsync("public",specialName,"c'\"é");
                check(exact.Table==specialName && exact.Column=="c'\"é","Target inspection preserves Unicode quotes and apostrophes");
                check((await service.ListColumnsAsync("public",specialName)).Single()==exact,"Target list retains exact special identifiers");
                await Sql($"DROP TABLE {special}; CREATE VIEW {special} AS SELECT 1 AS id;");
                check((await service.ListColumnsAsync("public",specialName)).Count==0,"Views are not eligible OWNED BY destinations");
                await Sql($"DROP VIEW {special};");
            }
            finally { await Sql($"DROP TABLE IF EXISTS {special};"); }

        }
        finally { await Sql($"DROP TABLE IF EXISTS {table};"); }
    }
}
