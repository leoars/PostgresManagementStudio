using Npgsql;
using PGMS.PostgreSql;

internal static class QuerySessionGuardChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var admin=new QuerySession(connection);await admin.OpenAsync();
        string role="session_guard_"+Guid.NewGuid().ToString("N"),schema="session_shadow_"+Guid.NewGuid().ToString("N");
        await admin.ExecuteAsync($"CREATE ROLE {role} LOGIN; CREATE SCHEMA {schema}");
        try
        {
            var builder=new NpgsqlConnectionStringBuilder(connection){Username=role};
            await using(var ordinary=new QuerySession(builder.ConnectionString))
            {
                await ordinary.OpenAsync();var snapshot=await ordinary.InspectSessionAsync();
                await ordinary.SetApplicationNameAsync(snapshot,"ordinary reviewed user");
                check((await ordinary.InspectSessionAsync()).CurrentUser==role,"Ordinary LOGIN can inspect and change permitted session settings");
            }
            var before=await admin.InspectSessionAsync();await admin.ExecuteAsync($"SET ROLE {role}");
            bool refused=false;try{await admin.SetApplicationNameAsync(before,"wrong actor");}catch(InvalidOperationException){refused=true;}
            check(refused,"Role changed after inspection refuses reviewed mutation");await admin.ExecuteAsync("RESET ROLE");
            await admin.ExecuteAsync($"CREATE FUNCTION {schema}.pg_backend_pid() RETURNS integer LANGUAGE sql AS 'SELECT -1'; CREATE FUNCTION {schema}.current_setting(text) RETURNS text LANGUAGE sql AS 'SELECT ''shadow''::text'; SET search_path={schema},pg_catalog");
            var protectedSnapshot=await admin.InspectSessionAsync();
            check(protectedSnapshot.BackendPid==before.BackendPid && protectedSnapshot.Settings["search_path"].Contains(schema),"Session inspection ignores search-path shadow functions");
            await admin.SetApplicationNameAsync(protectedSnapshot,"qualified setter");
            check((await admin.InspectSessionAsync()).Settings["application_name"]=="qualified setter","Guarded setter uses pg_catalog even under hostile search path");
            var fresh=await admin.InspectSessionAsync();using var cancel=new CancellationTokenSource();
            var busy=admin.ExecuteAsync("SELECT pg_catalog.pg_sleep(0.4)");
            var queued=admin.SetApplicationNameAsync(fresh,"must not apply",cancel.Token);cancel.Cancel();
            bool canceled=false;try{await queued;}catch(OperationCanceledException){canceled=true;}await busy;
            check(canceled && (await admin.InspectSessionAsync()).Settings["application_name"]=="qualified setter","Canceled queued setting sends no mutation after active command");
            await admin.ExecuteAsync($"CREATE FUNCTION {schema}.always_equal(text,text) RETURNS boolean LANGUAGE sql AS 'SELECT true'; CREATE OPERATOR {schema}.= (PROCEDURE={schema}.always_equal,LEFTARG=text,RIGHTARG=text)");
            var underOperator=await admin.InspectSessionAsync();
            check(underOperator.Settings["application_name"]=="qualified setter","Inspection remains exact with shadow equality operator in search path");
            check((string)(await admin.ExecuteAsync("SELECT 'a'::pg_catalog.text = 'b'::pg_catalog.text")).Tables[0].Rows[0][0]=="t","Fixture proves custom equality is selected for unqualified operator");
            await admin.ExecuteAsync("SET application_name='changed after snapshot'");
            bool staleOperator=false;try{await admin.SetApplicationNameAsync(underOperator,"must not overwrite");}catch(InvalidOperationException){staleOperator=true;}
            check(staleOperator && (await admin.InspectSessionAsync()).Settings["application_name"]=="changed after snapshot","Catalog-qualified equality rejects stale review despite malicious equality operator");
            await admin.SetApplicationNameAsync(await admin.InspectSessionAsync(),"fresh guarded value");
            check((await admin.InspectSessionAsync()).Settings["application_name"]=="fresh guarded value","Fresh reviewed setting still applies under shadow equality operator");
        }
        finally{await admin.ExecuteAsync($"RESET ROLE; RESET search_path; DROP SCHEMA {schema} CASCADE; DROP ROLE {role}");}
    }
}
