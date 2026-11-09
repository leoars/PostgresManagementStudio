using PGMS.PostgreSql;

internal static class QueryActualPlanChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await using var session = new QuerySession(connection); await session.OpenAsync();
        async Task<string> Scalar(string sql) => (string)(await session.ExecuteAsync(sql)).Tables[0].Rows[0][0];
        await session.ExecuteAsync("CREATE TEMP TABLE actual_fixture(id int); INSERT INTO actual_fixture SELECT generate_series(1,20)");

        var capture = new ActualPlanCapture(); var notices = new QueryNotices();
        var result = await session.ExecuteAsync("SELECT count(*) FROM actual_fixture WHERE id>10; UPDATE actual_fixture SET id=id+100 WHERE id=1;", new QueryExecutionOptions(0, notices) { ActualPlans = capture });
        check(capture.Unavailable is null && capture.Plans.Count == 2 && capture.Plans.All(p => p.Analyzed), "One execution reports the actual plan of each statement: " + capture.Unavailable);
        check((string)result.Tables[0].Rows[0][0] == "10", "Rows still come back while the plan is captured");
        check(notices.Items.All(n => !n.Message.Contains("plan:", StringComparison.Ordinal)), "Plans do not appear as server messages");
        check(await Scalar("SELECT count(*) FROM actual_fixture WHERE id=101") == "1" && await Scalar("SELECT count(*) FROM actual_fixture WHERE id=201") == "0", "The UPDATE ran exactly once");

        var statements = ExecutionPlanBuilder.Build(capture.Plans);
        check(statements[0].QueryText.StartsWith("SELECT count(*)", StringComparison.Ordinal) && statements[1].QueryText.StartsWith("UPDATE", StringComparison.Ordinal), "Each plan carries the text of its own statement: " + statements[0].QueryText);
        check(statements[0].Root.Title == "SELECT" && statements[1].Root.Title == "UPDATE" && statements[0].Root.IsStatement && statements[0].Root.Lines.SequenceEqual(["SELECT", "Cost: 0 %"]), "A statement box leads each plan, as in SSMS");
        check(Math.Abs(statements.Sum(s => s.BatchPercent) - 100) < 0.01 && statements[0].Header.StartsWith("Query 1: Query cost (relative to the batch): ", StringComparison.Ordinal), "Statement costs add up to the batch");
        var aggregate = statements[0].Root.Children[0];
        double Sum(ExecutionPlanOperator op) => op.CostPercent + op.Children.Sum(Sum);
        check(Math.Abs(Sum(aggregate) - 100) < 0.01, "Operator costs add up to the statement");
        var scan = aggregate.Children[0];
        check(aggregate.Lines.Contains("Cost: " + aggregate.CostPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %") && aggregate.Lines.Any(l => l.EndsWith('s')) && aggregate.Lines.Any(l => l.EndsWith(" of", StringComparison.Ordinal)) && scan.ActualRows == 10, "Actual plan boxes show cost, time and rows of estimate");
        check(scan.ObjectName?.EndsWith("actual_fixture", StringComparison.Ordinal) == true && scan.Details.Any(d => d.Label == "Filter") && scan.Summary.Any(s => s.Key == "Physical Operation" && s.Value == "Seq Scan"), "Scan names its table and details");
        check(capture.Plans.All(p => QueryPlanTree.Parse(p).Label.Length > 0), "Captured plans use the same format as EXPLAIN");

        check(await Scalar("SHOW auto_explain.log_min_duration") == "-1", "Capture is turned off after the execution");
        var quiet = new QueryNotices();
        await session.ExecuteAsync("SELECT 1", new QueryExecutionOptions(0, quiet));
        check(quiet.Items.Count == 0, "Later executions produce no plan messages");

        var failing = new ActualPlanCapture(); bool failed = false;
        try { await session.ExecuteAsync("BEGIN; SELECT 1/0", new QueryExecutionOptions(0) { ActualPlans = failing }); } catch (Npgsql.PostgresException) { failed = true; }
        await session.ExecuteAsync("ROLLBACK");
        quiet = new QueryNotices();
        await session.ExecuteAsync("SELECT 2", new QueryExecutionOptions(0, quiet));
        check(failed && quiet.Items.Count == 0 && await Scalar("SHOW auto_explain.log_min_duration") == "-1", "A statement left in an aborted transaction does not leave capture on");

        await session.ExecuteAsync("CREATE ROLE actual_limited");
        try
        {
            await session.ExecuteAsync("BEGIN"); await session.ExecuteAsync("SET LOCAL ROLE actual_limited");
            var refused = new ActualPlanCapture();
            var kept = await session.ExecuteAsync("SELECT 7", new QueryExecutionOptions(0) { ActualPlans = refused });
            check(refused.Unavailable is not null && refused.Plans.Count == 0 && (string)kept.Tables[0].Rows[0][0] == "7", "Without superuser the SQL runs and the reason is reported: " + refused.Unavailable);
            check(await Scalar("SELECT 8") == "8", "A refused capture does not abort the open transaction");
        }
        finally { await session.ExecuteAsync("ROLLBACK"); await session.ExecuteAsync("DROP ROLE actual_limited"); }
    }
}
