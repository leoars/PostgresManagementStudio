using System.Text.Json;
using PGMS.PostgreSql;

/// <summary>PGMS-084 (M1119): the plan as JSON and as text, and the comparison of two plans, on plans from the real server.</summary>
internal static class QueryPlanTextChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync("CREATE TEMP TABLE pt_orders(id int primary key, c text); CREATE TEMP TABLE pt_items(id int, o int, q int); INSERT INTO pt_orders SELECT g, 'c'||(g%9) FROM generate_series(1,2000) g; INSERT INTO pt_items SELECT g, (g%2000)+1, g%7 FROM generate_series(1,8000) g; ANALYZE pt_orders; ANALYZE pt_items;");
        const string join = "SELECT o.c, sum(i.q) FROM pt_orders o JOIN pt_items i ON i.o = o.id WHERE o.id < 1500 GROUP BY o.c ORDER BY 2 DESC";
        var estimated = await session.ExplainAsync(join);

        string json = QueryPlanText.Json(estimated);
        using (var reparsed = JsonDocument.Parse(json))
            check(reparsed.RootElement.ValueKind == JsonValueKind.Array && json.Contains(Environment.NewLine + "  ") && JsonSerializer.Serialize(reparsed.RootElement) == JsonSerializer.Serialize(JsonDocument.Parse(estimated.Json).RootElement), "The JSON is the server's, indented and unchanged");

        string text = QueryPlanText.Text(estimated);
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        check(lines[0].StartsWith("Sort  (cost=", StringComparison.Ordinal) && lines[0].Contains(" rows=") && lines[0].Contains(" width="), "The first line is the root with cost, rows and width: " + lines[0]);
        check(lines.Any(l => l.TrimStart().StartsWith("->  ", StringComparison.Ordinal) && l.Contains("Join")) && lines.Any(l => l.Contains("Seq Scan on pt_items i") || l.Contains("Scan on pt_items i") || l.Contains("on pt_items i")), "Children follow with arrows, scans name the relation and alias:\n" + text);
        check(lines.Any(l => l.Trim().StartsWith("Sort Key: ", StringComparison.Ordinal)) && lines.Any(l => l.Trim().StartsWith("Hash Cond: ", StringComparison.Ordinal) || l.Trim().StartsWith("Merge Cond: ", StringComparison.Ordinal) || l.Trim().StartsWith("Join Filter: ", StringComparison.Ordinal)), "Keys and conditions are listed under their node");
        check(!text.Contains("actual", StringComparison.Ordinal) && !text.Contains("Execution Time"), "An estimated plan has no actual values in the text");

        // The text agrees with the server's own EXPLAIN (FORMAT TEXT) line by line, apart from the details PGMS leaves out.
        var serverText = (await session.ExecuteAsync("EXPLAIN " + join)).Tables[0].Rows.Cast<System.Data.DataRow>().Select(r => (string)r[0]).ToList();
        var nodeLines = lines.Where(l => l.Contains("(cost=", StringComparison.Ordinal)).ToList();
        var serverNodes = serverText.Where(l => l.Contains("(cost=", StringComparison.Ordinal)).ToList();
        check(nodeLines.SequenceEqual(serverNodes), "Every node line equals the server's EXPLAIN text:\nPGMS:\n" + string.Join("\n", nodeLines) + "\nserver:\n" + string.Join("\n", serverNodes));

        var analyzed = await session.AnalyzeAsync(join);
        string actualText = QueryPlanText.Text(analyzed);
        check(actualText.Contains("(actual time=") && actualText.Contains(" loops=") && actualText.Contains("Planning Time: ") && actualText.Contains("Execution Time: "), "An analyzed plan shows actual time, rows, loops and the timings");
        check(actualText.Contains("Buffers: shared hit="), "Analyzed plans list the buffers");

        var same = QueryPlanComparison.Compare(estimated, estimated);
        check(same.All(d => d.Difference == QueryPlanComparison.Same) && same.Count(d => d.Area == "Operator") == nodeLines.Count, "A plan compared with itself has no difference");

        await session.ExecuteAsync("CREATE INDEX pt_items_o ON pt_items(o); ANALYZE pt_items;");
        var indexed = await session.ExplainAsync("SELECT * FROM pt_items WHERE o = 5");
        await session.ExecuteAsync("DROP INDEX pt_items_o;");
        var scanned = await session.ExplainAsync("SELECT * FROM pt_items WHERE o = 5");
        var diff = QueryPlanComparison.Compare(scanned, indexed);
        check(diff.Any(d => d.Area == "Statement" && d.Item == "Total cost" && d.Difference == QueryPlanComparison.Changed), "The total cost difference is listed");
        check(diff.Any(d => d.Difference == QueryPlanComparison.OnlyFirst && d.Item.Contains("Seq Scan on pt_items")) && diff.Any(d => d.Difference == QueryPlanComparison.OnlySecond && d.Item.Contains("pt_items_o")), "A sequential scan replaced by an index scan shows as only in plan 1 and only in plan 2: " + string.Join(" | ", diff.Select(d => d.Item + "=" + d.Difference)));

        var actualDiff = QueryPlanComparison.Compare(estimated, analyzed);
        check(actualDiff.Any(d => d.Item == "Execution time (ms)" && d.First == "" && d.Second.Length > 0) && actualDiff.Where(d => d.Area == "Operator").All(d => d.Difference is not (QueryPlanComparison.OnlyFirst or QueryPlanComparison.OnlySecond)) && actualDiff.Any(d => d.Area == "Operator" && d.Difference == QueryPlanComparison.Changed), "Estimated against analyzed: the operators match and actual values show as differences");

        bool refused = false; try { QueryPlanText.Text(new QueryPlan("{\"not\":\"a plan\"}", false)); } catch (InvalidDataException) { refused = true; }
        check(refused, "Something that is not a plan is refused");
    }
}
