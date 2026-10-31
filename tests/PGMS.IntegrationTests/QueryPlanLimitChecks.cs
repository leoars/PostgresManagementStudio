using Npgsql;
using PGMS.PostgreSql;

internal static class QueryPlanLimitChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var session=new QuerySession(connection);await session.OpenAsync();
        await session.ExecuteAsync("CREATE TEMP TABLE plan_limit(id int); INSERT INTO plan_limit VALUES(1)");
        bool limited=false;
        try{await session.ExplainAsync("SELECT * FROM plan_limit WHERE id::text='"+new string('x',QuerySession.MaximumPlanCharacters)+"'");}catch(InvalidDataException){limited=true;}
        check(limited,"Oversize server JSON is refused without a partial plan");
        check((string)(await session.ExecuteAsync("SELECT count(*) FROM plan_limit")).Tables[0].Rows[0][0]=="1","Session and fixture survive oversize plan refusal");
        bool conservative=false;try{await session.ExplainAsync("SELECT 'a;b'");}catch(ArgumentException){conservative=true;}
        check(conservative,"Embedded literal semicolon follows documented conservative refusal");
        var arrived=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active=session.StreamAsync("SELECT 1",async(batch,token)=>{if(batch.Rows.Count>0){arrived.TrySetResult();await release.Task.WaitAsync(token);}});
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));using var queuedCancel=new CancellationTokenSource();
        var queued=session.AnalyzeAsync("INSERT INTO plan_limit VALUES(2)",token:queuedCancel.Token);queuedCancel.Cancel();bool canceled=false;
        try{await queued;}catch(OperationCanceledException){canceled=true;}finally{release.TrySetResult();}
        await active;
        check(canceled && (string)(await session.ExecuteAsync("SELECT count(*) FROM plan_limit")).Tables[0].Rows[0][0]=="1","Canceled queued analysis never executes its INSERT");
        bool timedOut=false;try{await session.AnalyzeAsync("SELECT pg_sleep(5)",new QueryExecutionOptions(1));}catch(NpgsqlException ex)when(ex.InnerException is TimeoutException){timedOut=true;}
        check(timedOut,"Analyzed plan honors command read timeout");
    }
}
