using PGMS.PostgreSql;

internal static class QueryApplicationNameChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var session=new QuerySession(connection);await session.OpenAsync();
        await using var observer=new QuerySession(connection);await observer.OpenAsync();
        var before=await observer.InspectSessionAsync();var snapshot=await session.InspectSessionAsync();
        string literal="PGMS 'quoted' \\ observer";
        await session.SetApplicationNameAsync(snapshot,literal);
        var activity=await observer.ExecuteAsync($"SELECT application_name FROM pg_catalog.pg_stat_activity WHERE pid={snapshot.BackendPid}");
        check((string)activity.Tables[0].Rows[0][0]==literal,"Reviewed application name is literal and visible in actual backend activity");
        check((await observer.InspectSessionAsync()).Settings["application_name"]==before.Settings["application_name"],"Application name change does not alter another session");
        await session.SetApplicationNameAsync(await session.InspectSessionAsync(),new string('x',63));
        check((await session.InspectSessionAsync()).Settings["application_name"].Length==63,"Application name accepts 63 ASCII characters without truncation");
        foreach(string invalid in new[]{new string('x',64),"não ASCII","line\nname","nul\0name"})
        {bool rejected=false;try{await session.SetApplicationNameAsync(await session.InspectSessionAsync(),invalid);}catch(ArgumentException){rejected=true;}check(rejected,"Application name rejects truncation or server escaping");}
        await session.SetApplicationNameAsync(await session.InspectSessionAsync(),"");
        check((await session.InspectSessionAsync()).Settings["application_name"]=="","Empty application name is an explicit supported value");
    }
}
