using Npgsql;
using PGMS.PostgreSql;

internal static class QueryTimeoutChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        foreach(int invalid in new[]{-1,86401,int.MaxValue})
        {
            bool refused=false;try{_ = new QueryExecutionOptions(invalid);}catch(ArgumentOutOfRangeException){refused=true;}
            check(refused,"Invalid timeout rejected before execution: "+invalid);
        }
        check(new QueryExecutionOptions().CommandTimeoutSeconds==0 && new QueryExecutionOptions(86400).CommandTimeoutSeconds==86400,"Timeout defaults unlimited with explicit upper boundary");
        await using var session=new QuerySession(connection);await session.OpenAsync();
        bool timedOut=false;
        try{await session.ExecuteAsync("SELECT pg_sleep(5)",new QueryExecutionOptions(1));}
        catch(NpgsqlException ex) when(ex.InnerException is TimeoutException){timedOut=true;}
        check(timedOut,"Npgsql command timeout interrupts sleeping query");
        if(session.IsConnected)
            check((string)(await session.ExecuteAsync("SELECT 42")).Tables[0].Rows[0][0]=="42","Connected session remains usable after timeout");
        else check(true,"Timeout may close physical connection; disconnected state is exposed");
        await using var independent=new QuerySession(connection);await independent.OpenAsync();
        var result=await independent.ExecuteAsync("SELECT pg_sleep(1.2), 7",new QueryExecutionOptions(0));
        check((string)result.Tables[0].Rows[0][1]=="7","Zero timeout permits query longer than one second");
        using var cancel=new CancellationTokenSource(200);bool canceled=false;
        try{await independent.ExecuteAsync("SELECT pg_sleep(10)",new QueryExecutionOptions(0),cancel.Token);}catch(OperationCanceledException){canceled=true;}
        check(canceled,"Explicit cancellation remains effective with timeout disabled");
    }
}
