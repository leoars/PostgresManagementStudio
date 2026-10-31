using PGMS.PostgreSql;

internal static class QuerySessionInfoChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await QueryApplicationNameChecks.RunAsync(connection,check);
        await QuerySessionGuardChecks.RunAsync(connection,check);
        await QuerySessionSettingChecks.RunAsync(connection,check);
        await QuerySessionTimeoutChecks.RunAsync(connection,check);
        await using var session=new QuerySession(connection);await session.OpenAsync();
        var first=await session.InspectSessionAsync();
        check(first.BackendPid==int.Parse((string)(await session.ExecuteAsync("SELECT pg_backend_pid()")).Tables[0].Rows[0][0]) && first.Database=="postgres","Session snapshot identifies actual editor backend and database");
        check(first.SessionUser=="pgms_test" && first.CurrentUser=="pgms_test" && first.Settings.ContainsKey("effective_schemas"),"Snapshot distinguishes login identity and effective role with resolved schemas");
        await session.ExecuteAsync("CREATE TEMP TABLE session_fixture(id int); INSERT INTO session_fixture VALUES(7); SET search_path=pg_catalog; SET application_name='PGMS session fixture'");
        var next=await session.InspectSessionAsync();
        check(next.SessionIdentity==first.SessionIdentity && next.BackendPid==first.BackendPid && next.Settings["search_path"]=="pg_catalog" && next.Settings["application_name"]=="PGMS session fixture","Inspection observes current settings without replacing physical session");
        check((string)(await session.ExecuteAsync("SELECT id FROM pg_temp.session_fixture")).Tables[0].Rows[0][0]=="7","Inspection preserves session temporary data");
        await using var other=new QuerySession(connection);await other.OpenAsync();var separate=await other.InspectSessionAsync();
        check(separate.SessionIdentity!=first.SessionIdentity && separate.BackendPid!=first.BackendPid && separate.Settings["application_name"]!=next.Settings["application_name"],"Independent editor sessions have distinct identities and settings");
        using var cancel=new CancellationTokenSource();cancel.Cancel();bool canceled=false;try{await session.InspectSessionAsync(cancel.Token);}catch(OperationCanceledException){canceled=true;}
        check(canceled,"Session inspection honors cancellation before execution");
    }
}
