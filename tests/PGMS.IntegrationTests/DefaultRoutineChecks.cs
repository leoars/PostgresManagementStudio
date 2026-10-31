using Npgsql;
using PGMS.PostgreSql;

internal static class DefaultRoutineChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        await f.Admin($"GRANT USAGE ON SCHEMA {f.QS} TO {f.QR}");
        await f.Owner($"CREATE FUNCTION {f.QS}.old() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
        check(!await f.Apply(DefaultPrivilegeTarget.Routines,"EXECUTE",revoke:true,isPublic:true),"Schema-only PUBLIC revoke cannot remove built-in global routine EXECUTE");
        await f.Owner($"CREATE FUNCTION {f.QS}.still_public() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
        check(await f.ReaderCount($"SELECT {f.QS}.still_public()")==1,"Future routine retains global PUBLIC after ineffective schema revoke");
        check(await f.Apply(DefaultPrivilegeTarget.Routines,"EXECUTE",revoke:true,global:true,isPublic:true),"Global routine default can revoke built-in PUBLIC");
        async Task Create(string prefix)
        {
            await f.Owner($"CREATE FUNCTION {f.QS}.{prefix}(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1+1';CREATE FUNCTION {f.QS}.{prefix}(text) RETURNS integer LANGUAGE sql AS 'SELECT length($1)';CREATE PROCEDURE {f.QS}.{prefix}_proc(INOUT value integer) LANGUAGE plpgsql AS 'BEGIN value:=value+1; END';CREATE AGGREGATE {f.QS}.{prefix}_agg(integer)(SFUNC=pg_catalog.int4pl,STYPE=integer,INITCOND='0')");
        }
        await Create("denied");
        check(await f.Denied($"SELECT {f.QS}.denied(1)") && await f.Denied($"SELECT {f.QS}.denied('x'::text)"),"Global default removes EXECUTE from both future function overloads");
        check(await f.Denied($"CALL {f.QS}.denied_proc(1)"),"Global routine default also controls future procedures");
        check(await f.Denied($"SELECT {f.QS}.denied_agg(value) FROM(VALUES(1),(2))v(value)"),"Global routine default also controls future aggregates");
        check(await f.ReaderCount($"SELECT {f.QS}.old()")==1,"Global default revoke preserves PUBLIC on previously existing routine");
        check(await f.Apply(DefaultPrivilegeTarget.Routines,"EXECUTE"),"Schema routine policy adds recipient EXECUTE to restricted global default");
        await Create("allowed");
        check(await f.ReaderCount($"SELECT {f.QS}.allowed(1)")==2 && await f.ReaderCount($"SELECT {f.QS}.allowed('text'::text)")==4,"Future schema EXECUTE authorizes both new overloads");
        await f.Reader($"CALL {f.QS}.allowed_proc(1)");check(true,"Future schema EXECUTE authorizes actual CALL");
        check(await f.ReaderCount($"SELECT {f.QS}.allowed_agg(value) FROM(VALUES(1),(2))v(value)")==3,"Future schema EXECUTE authorizes actual aggregation");
        check(await f.Denied($"SELECT {f.QS}.denied(1)"),"New recipient template does not rewrite earlier restricted routine ACL");
        await using(var c=new NpgsqlConnection(f.CreatorConnection))
        {
            await c.OpenAsync();await using var oidCommand=new NpgsqlCommand("SELECT @signature::regprocedure::oid",c);oidCommand.Parameters.AddWithValue("signature",f.QS+".allowed_agg(integer)");
            var selected=await new RoutinePrivilegeCatalog(f.CreatorConnection).InspectAsync((uint)(await oidCommand.ExecuteScalarAsync())!);
            check(selected.Kind=="a" && selected.InputTypeOids=="23","Routine privilege catalog captures exact aggregate identity and input type");
            var request=new PrivilegeRequest(PrivilegeTarget.Routine,selected.Name,f.Recipient,"EXECUTE",Revoke:true,Schema:f.Schema){Routine=selected};
            check(await new PrivilegeService(f.CreatorConnection).ApplyCurrentAsync(request) && await f.Denied($"SELECT {f.QS}.allowed_agg(value) FROM(VALUES(1))v(value)"),"Guarded routine revoke also protects aggregate EXECUTE by exact identity");
            await new PrivilegeService(f.CreatorConnection).ApplyCurrentAsync(request with{Revoke=false});
        }
        await f.Apply(DefaultPrivilegeTarget.Routines,"EXECUTE",global:true,isPublic:true);
        await f.Owner($"CREATE FUNCTION {f.QS}.public_again() RETURNS integer LANGUAGE sql AS 'SELECT 9'");
        check(await f.ReaderCount($"SELECT {f.QS}.public_again()")==9 && await f.Denied($"SELECT {f.QS}.denied(1)"),"Restoring global PUBLIC changes future routines without retroactive grants");
        await f.Apply(DefaultPrivilegeTarget.Routines,"EXECUTE",revoke:true);
        await f.Owner($"CREATE FUNCTION {f.QS}.public_after_local_revoke() RETURNS integer LANGUAGE sql AS 'SELECT 8'");
        check(await f.ReaderCount($"SELECT {f.QS}.public_after_local_revoke()")==8,"Revoking schema recipient rule cannot negate restored global PUBLIC");
    }
}
