using Npgsql;
using PGMS.PostgreSql;

internal static class QuerySessionTimeoutChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var session=new QuerySession(connection);await session.OpenAsync();
        var before=await session.InspectSessionAsync();await session.SetStatementTimeoutAsync(before,300);
        check((await session.InspectSessionAsync()).StatementTimeoutMilliseconds==300,"Statement timeout snapshot normalizes server units to milliseconds");
        bool timedOut=false;try{await session.ExecuteAsync("SELECT pg_sleep(1)");}catch(PostgresException ex)when(ex.SqlState=="57014"){timedOut=true;}
        check(timedOut,"Server statement timeout interrupts query with client read timeout disabled");
        await session.SetStatementTimeoutAsync(await session.InspectSessionAsync(),0);
        check((await session.InspectSessionAsync()).StatementTimeoutMilliseconds==0,"Zero restores disabled server statement timeout");
        await using var holder=new QuerySession(connection);await holder.OpenAsync();long key=Random.Shared.NextInt64(1,long.MaxValue);
        await holder.ExecuteAsync($"SELECT pg_advisory_lock({key})");
        await session.SetLockTimeoutAsync(await session.InspectSessionAsync(),300);
        bool lockTimedOut=false;try{await session.ExecuteAsync($"SELECT pg_advisory_lock({key})");}catch(PostgresException ex)when(ex.SqlState=="55P03"){lockTimedOut=true;}
        check(lockTimedOut && (await session.InspectSessionAsync()).LockTimeoutMilliseconds==300,"Server lock timeout interrupts real contested advisory lock");
        check((string)(await session.ExecuteAsync("SELECT 1")).Tables[0].Rows[0][0]=="1","Lock timeout leaves session usable for nonwaiting work");
        foreach(int invalid in new[]{-1,86400001})
        {bool refused=false;try{await session.SetStatementTimeoutAsync(await session.InspectSessionAsync(),invalid);}catch(ArgumentOutOfRangeException){refused=true;}check(refused,"Out-of-range server timeout rejected before mutation: "+invalid);}
    }
}
