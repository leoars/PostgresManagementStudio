using Npgsql;
using PGMS.PostgreSql;

internal static class DefaultTableChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        await f.Owner($"CREATE TABLE {f.QS}.old(id integer PRIMARY KEY)");
        foreach(string privilege in DefaultPrivilegeRequest.Allowed(DefaultPrivilegeTarget.Tables))
            check(await f.Apply(DefaultPrivilegeTarget.Tables,privilege),"Guarded table default accepts future privilege: "+privilege);
        await f.Owner($"CREATE TABLE {f.QS}.future(id integer PRIMARY KEY);CREATE VIEW {f.QS}.future_view AS SELECT 7 AS id");
        foreach(string privilege in DefaultPrivilegeRequest.Allowed(DefaultPrivilegeTarget.Tables))
            check(!await f.Has("has_table_privilege",f.QS+".old",privilege) && await f.Has("has_table_privilege",f.QS+".future",privilege),"Creation-time table ACL receives default without rewriting old table: "+privilege);
        check(await f.Denied($"SELECT * FROM {f.QS}.future"),"Table default SELECT does not bypass missing schema USAGE");
        await f.Admin($"GRANT USAGE,CREATE ON SCHEMA {f.QS} TO {f.QR}");
        await f.Reader($"INSERT INTO {f.QS}.future VALUES(1),(2);UPDATE {f.QS}.future SET id=3 WHERE id=2;DELETE FROM {f.QS}.future WHERE id=3");
        check(await f.ReaderCount($"SELECT count(*) FROM {f.QS}.future")==1,"Future table defaults authorize actual INSERT/UPDATE/DELETE/SELECT");
        await f.Owner($"ALTER TABLE {f.QS}.future ADD COLUMN later text;ALTER TABLE {f.QS}.old ADD COLUMN later text");
        await f.Reader($"SELECT later FROM {f.QS}.future");check(await f.Denied($"SELECT later FROM {f.QS}.old"),"Table-level default ACL covers later columns only on the granted table");
        check(await f.ReaderCount($"SELECT id FROM {f.QS}.future_view")==7,"TABLES default SELECT also reaches a future view");
        await f.Reader($"CREATE TABLE {f.QS}.reader_child(parent_id integer REFERENCES {f.QS}.future(id))");check(true,"Future REFERENCES default authorizes a real foreign key");
        await f.Owner($"CREATE FUNCTION {f.QS}.trigger_fn() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END'");
        await f.Reader($"CREATE TRIGGER reader_trigger BEFORE INSERT ON {f.QS}.future FOR EACH ROW EXECUTE FUNCTION {f.QS}.trigger_fn()");check(true,"Future TRIGGER default authorizes actual trigger creation");
        await f.Reader($"VACUUM {f.QS}.future");check(true,"Future MAINTAIN default authorizes VACUUM by recipient");
        await f.Reader($"TRUNCATE {f.QS}.future,{f.QS}.reader_child");check(await f.ReaderCount($"SELECT count(*) FROM {f.QS}.future")==0,"Future TRUNCATE default authorizes actual table truncation");
        bool rejected=false;try{await f.Owner($"ALTER DEFAULT PRIVILEGES FOR ROLE {f.QO} IN SCHEMA {f.QS} GRANT SELECT(id) ON TABLES TO {f.QR}");}catch(PostgresException e)when(e.SqlState=="0LP01"){rejected=true;}
        check(rejected,"PostgreSQL refuses column-specific default privileges; table policy is explicit");
        await f.Apply(DefaultPrivilegeTarget.Tables,"SELECT",revoke:true);
        await f.Owner($"CREATE TABLE {f.QS}.after_revoke(id integer)");
        check(!await f.Has("has_table_privilege",f.QS+".after_revoke","SELECT") && await f.Has("has_table_privilege",f.QS+".future","SELECT"),"Revoking table default affects next creation but preserves previously materialized ACL");
        await f.Owner($"INSERT INTO {f.QS}.future(id) VALUES(1),(2);ALTER TABLE {f.QS}.future ENABLE ROW LEVEL SECURITY");
        check(await f.ReaderCount($"SELECT count(*) FROM {f.QS}.future")==0,"Stored default SELECT does not bypass RLS with no policy");
        await f.Owner($"CREATE POLICY only_one ON {f.QS}.future FOR SELECT TO {f.QR} USING(id=1)");
        check(await f.ReaderCount($"SELECT count(*) FROM {f.QS}.future")==1,"RLS still filters rows independently of creation-time default ACL");
    }
}
