using Npgsql;
using PGMS.PostgreSql;

internal static class SequenceOwnerJourneyChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string role="pgms_own_journey_"+Guid.NewGuid().ToString("N"), schema="pgms_é'\"_"+Guid.NewGuid().ToString("N");
        string table=ObjectSql.Relation(schema,"tab é'\""), seq=ObjectSql.Relation(schema,"seq é'\"");
        await using var admin=new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Admin(string sql) { await using var c=new NpgsqlCommand(sql,admin); await c.ExecuteNonQueryAsync(); }
        try
        {
            await Admin($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN; CREATE SCHEMA {SqlIdentifier.Quote(schema)} AUTHORIZATION {SqlIdentifier.Quote(role)};");
            string ownedConnection=new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString;
            await using var db=new NpgsqlConnection(ownedConnection); await db.OpenAsync();
            async Task<object?> Sql(string sql) { await using var c=new NpgsqlCommand(sql,db); return await c.ExecuteScalarAsync(); }
            check(Equals(await Sql("SELECT NOT(rolsuper OR rolcreatedb OR rolcreaterole) FROM pg_roles WHERE rolname=current_user"),true),"Ownership journey uses role without cluster administration flags");
            await Sql($"CREATE TABLE {table}({SqlIdentifier.Quote("id é'\"")} bigint DEFAULT 42); CREATE SEQUENCE {seq} START 10;");
            var source=await new SequenceService(ownedConnection).InspectAsync(schema,"seq é'\"");
            var destination=await new SequenceOwnerTargetService(ownedConnection).InspectAsync(schema,"tab é'\"","id é'\"");
            check(source.Owner==role && destination.Owner==role,"Limited owner inspects exact special-name endpoints");
            await new AdministrativeEditService(ownedConnection).ApplyAsync(new SequenceAttachEdit(source,destination));
            check((await new SequenceService(ownedConnection).InspectAsync(schema,"seq é'\"")).Dependency==new SequenceDependency(destination.TableOid,destination.ColumnNumber,"a"),"Limited owner associates Unicode quoted schema table and column");
            check(Equals(await Sql($"INSERT INTO {table} DEFAULT VALUES RETURNING {SqlIdentifier.Quote("id é'\"")}"),42L),"Limited owner association preserves existing default");
            await Sql($"DROP TABLE {table};");
            bool absent=false; try { await new SequenceService(ownedConnection).InspectAsync(schema,"seq é'\""); } catch(InvalidOperationException) { absent=true; }
            check(absent,"Limited owner table drop follows new sequence dependency");
        }
        finally { await Admin($"DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(schema)} CASCADE; DROP ROLE IF EXISTS {SqlIdentifier.Quote(role)};"); }
    }
}
