using System.Text.Json;
using PGMS.PostgreSql;

internal static class QueryPlanChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await QueryPlaceholderChecks.RunAsync(connection,check);
        QueryPlanTreeChecks.Run(check);
        await QueryPlanFileChecks.RunAsync(check);
        await QueryPlanLimitChecks.RunAsync(connection,check);
        await QueryActualPlanChecks.RunAsync(connection,check);
        await PlanIndexAdviceChecks.RunAsync(connection,check);
        await using var session=new QuerySession(connection);await session.OpenAsync();
        await session.ExecuteAsync("CREATE TEMP TABLE plan_fixture(id int); INSERT INTO plan_fixture SELECT generate_series(1,20)");
        var plan=await session.ExplainAsync("SELECT * FROM plan_fixture WHERE id>10;");
        using var document=JsonDocument.Parse(plan.Json);
        var root=document.RootElement[0].GetProperty("Plan");
        check(QueryPlanTree.Parse(plan).Label.Contains("plan_fixture"),"Real PostgreSQL plan is consumable by bounded tree model");
        check(!plan.Analyzed && root.GetProperty("Relation Name").GetString()=="plan_fixture" && root.TryGetProperty("Total Cost",out _),"Estimated plan uses current physical session and real temporary table");
        check(!root.TryGetProperty("Actual Rows",out _),"Estimated plan has no fabricated actual execution rows");
        check(QueryPlanTimings.Read(plan).ExecutionMilliseconds is null,"Missing estimated execution time remains absent, not fabricated zero");
        await session.ExplainAsync("DELETE FROM plan_fixture");
        check((string)(await session.ExecuteAsync("SELECT count(*) FROM plan_fixture")).Tables[0].Rows[0][0]=="20","Estimated DELETE does not execute row deletion");
        bool rejected=false;try{await session.ExplainAsync("SELECT 1; DELETE FROM plan_fixture");}catch(ArgumentException){rejected=true;}
        check(rejected && (string)(await session.ExecuteAsync("SELECT count(*) FROM plan_fixture")).Tables[0].Rows[0][0]=="20","Multiple statements refused before any SQL executes");
        using var cancel=new CancellationTokenSource();cancel.Cancel();bool canceled=false;
        try{await session.ExplainAsync("SELECT 1",token:cancel.Token);}catch(OperationCanceledException){canceled=true;}
        check(canceled,"Plan honors pre-cancellation");
        var actual=await session.AnalyzeAsync("INSERT INTO plan_fixture VALUES(99)");
        using var actualJson=JsonDocument.Parse(actual.Json);
        check(actual.Analyzed && actualJson.RootElement[0].TryGetProperty("Execution Time",out _) && actualJson.RootElement[0].GetProperty("Plan").TryGetProperty("Actual Rows",out _),"Analyzed plan exposes real timing and row metrics");
        check(QueryPlanTimings.Read(actual).ExecutionMilliseconds>=0 && QueryPlanTree.Parse(actual).Label.Contains("/loop"),"Actual timing and per-loop rows retain server units");
        check((string)(await session.ExecuteAsync("SELECT count(*) FROM plan_fixture")).Tables[0].Rows[0][0]=="21","Analyze actually executes INSERT and does not silently roll it back");
        await session.ExecuteAsync("BEGIN");await session.AnalyzeAsync("DELETE FROM plan_fixture");
        check((string)(await session.ExecuteAsync("SELECT count(*) FROM plan_fixture")).Tables[0].Rows[0][0]=="0","Analyze participates in caller's explicit transaction");
        await session.ExecuteAsync("ROLLBACK");
        check((string)(await session.ExecuteAsync("SELECT count(*) FROM plan_fixture")).Tables[0].Rows[0][0]=="21","Caller rollback controls analyzed DML effects");
        using var stop=new CancellationTokenSource(200);bool stopped=false;
        try{await session.AnalyzeAsync("SELECT pg_sleep(10)",token:stop.Token);}catch(OperationCanceledException){stopped=true;}
        check(stopped && (string)(await session.ExecuteAsync("SELECT 1")).Tables[0].Rows[0][0]=="1","Analyzed execution cancels server wait and leaves reusable session");
    }
}
