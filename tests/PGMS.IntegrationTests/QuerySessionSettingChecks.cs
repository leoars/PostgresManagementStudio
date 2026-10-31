using PGMS.PostgreSql;

internal static class QuerySessionSettingChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var session=new QuerySession(connection);await session.OpenAsync();
        await session.ExecuteAsync("CREATE TEMP TABLE path_fixture(id int); INSERT INTO path_fixture VALUES(7)");
        var original=await session.InspectSessionAsync();string value=await session.SetSearchPathAsync(original,"pg_catalog, pg_temp");
        check(value=="pg_catalog, pg_temp" && (string)(await session.ExecuteAsync("SELECT id FROM path_fixture")).Tables[0].Rows[0][0]=="7","Guarded search path applies to same session with temporary data intact");
        bool stale=false;try{await session.SetSearchPathAsync(original,"public");}catch(InvalidOperationException){stale=true;}
        check(stale && (await session.InspectSessionAsync()).Settings["search_path"]==value,"Stale search path inspection cannot overwrite current setting");
        var fresh=await session.InspectSessionAsync();string hostile="\"missing'; DROP TABLE pg_temp.path_fixture; --\"";
        await session.SetSearchPathAsync(fresh,hostile);
        check((string)(await session.ExecuteAsync("SELECT id FROM pg_temp.path_fixture")).Tables[0].Rows[0][0]=="7" && (await session.InspectSessionAsync()).Settings["search_path"]==hostile,"Quoted hostile path stays a parameter and never becomes SQL");
        await using var other=new QuerySession(connection);await other.OpenAsync();bool wrong=false;try{await other.SetSearchPathAsync(await session.InspectSessionAsync(),"public");}catch(InvalidOperationException){wrong=true;}
        check(wrong,"Snapshot from another query session is refused");
        foreach(string invalid in new[]{new string('x',2049),"nul\0path","\ud800"})
        {bool rejected=false;try{await session.SetSearchPathAsync(await session.InspectSessionAsync(),invalid);}catch(ArgumentException){rejected=true;}check(rejected,"Invalid search path is refused before mutation");}
        var before=await session.InspectSessionAsync();await session.ExecuteAsync("BEGIN");await session.SetSearchPathAsync(before,"pg_catalog");await session.ExecuteAsync("ROLLBACK");
        check((await session.InspectSessionAsync()).Settings["search_path"]==before.Settings["search_path"],"Caller transaction rollback restores session setting; setter never commits it");
    }
}
