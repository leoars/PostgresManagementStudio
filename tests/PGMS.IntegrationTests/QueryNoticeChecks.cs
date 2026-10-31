using Npgsql;
using PGMS.PostgreSql;

internal static class QueryNoticeChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var session=new QuerySession(connection);await session.OpenAsync();
        var notices=new QueryNotices();
        await session.ExecuteAsync("DO $$ BEGIN RAISE NOTICE 'Olá 😀'; RAISE WARNING 'warning'; END $$",new QueryExecutionOptions(notices:notices));
        check(notices.Items.Count==2 && notices.Items[0].Message=="Olá 😀" && notices.Items[0].SqlState=="00000" && notices.Items[1].Severity=="WARNING","Server notices preserve order, Unicode, SQLSTATE and severity");
        var failed=new QueryNotices();bool error=false;
        try{await session.ExecuteAsync("DO $$ BEGIN RAISE NOTICE 'before failure'; RAISE EXCEPTION 'failure'; END $$",new QueryExecutionOptions(notices:failed));}catch(PostgresException){error=true;}
        check(error && failed.Items.Single().Message=="before failure","Notices preceding server error remain available");
        await session.ExecuteAsync("DO $$ BEGIN RAISE NOTICE 'unobserved'; END $$");
        check(notices.Items.Count==2 && failed.Items.Count==1,"Notice handlers are detached after success and error");
        var bounded=new QueryNotices();
        await session.ExecuteAsync("DO $$ BEGIN FOR i IN 1..150 LOOP RAISE NOTICE '%', i; END LOOP; END $$",new QueryExecutionOptions(notices:bounded));
        check(bounded.Items.Count==100 && bounded.Omitted==50,"Notice flood retains first100 with exact omission count");
        var text=new QueryNotices();
        await session.ExecuteAsync("DO $$ BEGIN RAISE NOTICE '%', repeat('x',5000); FOR i IN 1..40 LOOP RAISE NOTICE '%', repeat('á',3000); END LOOP; END $$",new QueryExecutionOptions(notices:text));
        check(text.Items.All(n=>n.Message.Length==3000) && text.Items.Sum(n=>n.Message.Length+n.Severity.Length+n.SqlState.Length)<=65536 && text.Omitted+text.Items.Count==41,"Oversize notices omitted whole and total UTF16 retention bounded");
        var first=new QueryNotices();var queued=new QueryNotices();
        var arrived=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active=session.StreamAsync("DO $$ BEGIN RAISE NOTICE 'active'; END $$; SELECT 1",async(batch,token)=>
        {if(batch.Rows.Count>0){arrived.TrySetResult();await release.Task.WaitAsync(token);}},new QueryExecutionOptions(notices:first));
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var queuedCancel=new CancellationTokenSource();
        var waiting=session.ExecuteAsync("DO $$ BEGIN RAISE NOTICE 'queued'; END $$",new QueryExecutionOptions(notices:queued),queuedCancel.Token);
        queuedCancel.Cancel();bool queueCanceled=false;try{await waiting;}catch(OperationCanceledException){queueCanceled=true;}
        check(queueCanceled && queued.Items.Count==0 && first.Items.Single().Message=="active","Canceled queued execution never subscribes to active query notices");
        release.SetResult();await active;
        var canceledNotices=new QueryNotices();using var cancel=new CancellationTokenSource(500);bool canceled=false;
        try{await session.ExecuteAsync("DO $$ BEGIN RAISE NOTICE 'before cancel'; END $$; SELECT pg_sleep(10)",new QueryExecutionOptions(notices:canceledNotices),cancel.Token);}catch(OperationCanceledException){canceled=true;}
        check(canceled && canceledNotices.Items.Single().Message=="before cancel","Cancellation retains notices already received");
        await session.ExecuteAsync("DO $$ BEGIN RAISE NOTICE 'after cancel'; END $$");
        check(canceledNotices.Items.Count==1 && first.Items.Count==1 && queued.Items.Count==0,"Cancellation removes handler without contaminating other captures");
    }
}
