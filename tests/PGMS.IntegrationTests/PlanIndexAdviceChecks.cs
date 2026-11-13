using PGMS.PostgreSql;

/// <summary>PGMS-041: index suggestions read from analyzed plans and checked against the catalog.</summary>
internal static class PlanIndexAdviceChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await using var session = new QuerySession(connection); await session.OpenAsync();
        async Task<string> Scalar(string sql) => (string)(await session.ExecuteAsync(sql)).Tables[0].Rows[0][0];
        await session.ExecuteAsync("""
            DROP SCHEMA IF EXISTS advice_a CASCADE; DROP SCHEMA IF EXISTS advice_b CASCADE;
            CREATE SCHEMA advice_a; CREATE SCHEMA advice_b;
            CREATE TABLE advice_a.orders(id int PRIMARY KEY, customer_id int NOT NULL, status varchar(10) NOT NULL, created_at timestamp NOT NULL, note text);
            INSERT INTO advice_a.orders SELECT g, g % 500, CASE WHEN g % 10 = 0 THEN 'open' ELSE 'done' END, timestamp '2026-01-01' + g * interval '1 minute', 'n' FROM generate_series(1, 20000) g;
            CREATE TABLE advice_a.twin(id int, v int); CREATE TABLE advice_b.twin(id int, v int);
            INSERT INTO advice_a.twin SELECT g, g FROM generate_series(1, 5000) g;
            CREATE TABLE advice_a."Mixed Case"("KeyCol" int, other int);
            INSERT INTO advice_a."Mixed Case" SELECT g, g FROM generate_series(1, 5000) g;
            CREATE TABLE advice_a.parts(id int, k int) PARTITION BY RANGE (id);
            CREATE TABLE advice_a.parts_1 PARTITION OF advice_a.parts FOR VALUES FROM (0) TO (3000);
            CREATE TABLE advice_a.parts_2 PARTITION OF advice_a.parts FOR VALUES FROM (3000) TO (100000);
            INSERT INTO advice_a.parts SELECT g, g FROM generate_series(1, 6000) g;
            CREATE TEMP TABLE advice_tmp AS SELECT g AS id, g % 100 AS k FROM generate_series(1, 5000) g;
            ANALYZE advice_a.orders; ANALYZE advice_a.twin; ANALYZE advice_a."Mixed Case"; ANALYZE advice_a.parts; ANALYZE advice_tmp;
            SET max_parallel_workers_per_gather = 0;
            """);
        try
        {
            async Task<ExecutionPlanStatement> Analyze(string sql)
            {
                var plan = await session.AnalyzeAsync(sql);
                return (await session.AdviseIndexesAsync(ExecutionPlanBuilder.Build([plan], sql)))[0];
            }
            static List<ExecutionPlanOperator> All(ExecutionPlanOperator op) => [op, .. op.Children.SelectMany(All)];
            static string Describe(ExecutionPlanStatement s) => s.MissingIndexes.Count + " suggestion(s): " + string.Join(" | ", s.MissingIndexes.Select(m => m.HeaderLine)) + " / candidates: " + All(s.Root).Count(o => o.IndexCandidate is not null) + " / warnings: " + string.Join(" ", All(s.Root).SelectMany(o => o.Warnings));

            var simple = await Analyze("SELECT id FROM advice_a.orders WHERE customer_id = 42");
            check(simple.MissingIndexes is [{ Schema: "advice_a", Table: "orders", IndexName: "ix_orders_customer_id", Partitioned: false, KeyList: "customer_id" }], "An equality filter that discards rows suggests an index on its column: " + Describe(simple));
            check(simple.MissingIndexes.Count == 1 && simple.MissingIndexes[0].HeaderLine == "Missing Index (heuristic, not verified): CREATE INDEX ON advice_a.orders (customer_id)", "The header line says it is a heuristic");
            check(simple.MissingIndexes.Count == 1 && simple.MissingIndexes[0].Source is { RemovedRows: 19960, ReturnedRows: 40, NodeType: "Seq Scan" }, "The suggestion keeps the rows discarded and returned");

            var estimated = (await session.AdviseIndexesAsync(ExecutionPlanBuilder.Build([await session.ExplainAsync("SELECT id FROM advice_a.orders WHERE customer_id = 42")])))[0];
            check(estimated.MissingIndexes.Count == 0 && All(estimated.Root).All(o => o.IndexCandidate is null), "An estimated plan suggests nothing");

            var top = await Analyze("SELECT id FROM advice_a.orders WHERE customer_id = 42 ORDER BY created_at DESC LIMIT 20");
            check(top.MissingIndexes is [{ KeyList: "customer_id, created_at DESC", IndexName: "ix_orders_customer_id_created_at" }], "WHERE = … ORDER BY … LIMIT puts the ordering after the equality column: " + Describe(top));

            var topRange = await Analyze("SELECT id FROM advice_a.orders WHERE customer_id = 42 AND created_at >= '2026-01-05' ORDER BY status DESC LIMIT 5");
            check(topRange.MissingIndexes is [{ KeyList: "customer_id, status DESC", Source.RangeLeftOut: true }] && topRange.MissingIndexes[0].Source.Ignored.Contains("range on created_at"), "Under a LIMIT the ordering wins over a range on another column, and the script says so: " + Describe(topRange));

            var range = await Analyze("SELECT id FROM advice_a.orders WHERE customer_id = 42 AND created_at >= '2026-01-05' ORDER BY note");
            check(range.MissingIndexes is [{ KeyList: "customer_id, created_at", Source.RangeLeftOut: false }], "Without LIMIT the range column follows the equality column and the ordering is left out: " + Describe(range));

            var between = await Analyze("SELECT id FROM advice_a.orders WHERE created_at BETWEEN '2026-01-02' AND '2026-01-02 01:00' AND note = 'n'");
            check(between.MissingIndexes is [{ KeyList: "note, created_at" }], "Equality goes first whatever the order in the filter, and BETWEEN is one range: " + Describe(between));

            var varchar = await Analyze("SELECT id FROM advice_a.orders WHERE status = 'open' AND customer_id = 7");
            check(varchar.MissingIndexes.Count == 1 && varchar.MissingIndexes[0].Source.Equality.Order().SequenceEqual(["customer_id", "status"]) && varchar.MissingIndexes[0].Source.CastToText.SequenceEqual(["status"]), "A varchar column, shown as (status)::text, is accepted: " + Describe(varchar));

            var inList = await Analyze("SELECT id FROM advice_a.orders WHERE customer_id IN (3, 4)");
            check(inList.MissingIndexes is [{ KeyList: "customer_id" }], "IN with constants counts as equality: " + Describe(inList));

            var relative = await Analyze("SELECT id FROM advice_a.orders WHERE created_at > now() - interval '1 day' AND customer_id = 1");
            check(relative.MissingIndexes is [{ KeyList: "customer_id, created_at" }], "A comparison with now() is a range: " + Describe(relative));

            foreach (var (sql, why) in new[]
            {
                ("SELECT id FROM advice_a.orders WHERE customer_id = 42 OR note = 'x'", "OR"),
                ("SELECT id FROM advice_a.orders WHERE lower(note) = 'zzz'", "an expression on the column"),
                ("SELECT id FROM advice_a.orders WHERE customer_id = id + 1", "a comparison between columns"),
                ("SELECT id FROM advice_a.orders WHERE customer_id <> 5 AND note LIKE 'z%'", "<> and LIKE"),
            })
            {
                var none = await Analyze(sql);
                check(none.MissingIndexes.Count == 0 && All(none.Root).Any(o => o.Warnings.Count > 0), "No suggestion for " + why + ", though the scan is warned: " + Describe(none));
            }

            var mixed = await Analyze("SELECT id FROM advice_a.orders WHERE customer_id = 42 AND note LIKE 'z%'");
            check(mixed.MissingIndexes is [{ KeyList: "customer_id" }] && mixed.MissingIndexes[0].Source.Ignored.Any(t => t.Contains("note", StringComparison.Ordinal)), "Conditions that are not understood are listed, and the rest still gives the index: " + Describe(mixed));

            var quoted = await Analyze("SELECT other FROM advice_a.\"Mixed Case\" WHERE \"KeyCol\" = 3");
            check(quoted.MissingIndexes is [{ QualifiedTable: "advice_a.\"Mixed Case\"", KeyList: "\"KeyCol\"" }], "Names that need quotes are quoted: " + Describe(quoted));

            var partition = await Analyze("SELECT id FROM advice_a.parts WHERE k = 5");
            check(partition.MissingIndexes is [{ Table: "parts", Partitioned: true, IndexName: "ix_parts_k" }] && All(partition.Root).Count(o => o.IndexCandidate is not null) == 2, "Scans of partitions give one suggestion, on the partitioned table: " + Describe(partition));
            check(partition.MissingIndexes.Count == 1 && PlanIndexAdvisor.Script("t", partition.MissingIndexes) is var partitionScript && partitionScript.Contains("\nCREATE INDEX ix_parts_k\nON advice_a.parts (k);", StringComparison.Ordinal) && partitionScript.Contains("partitioned", StringComparison.Ordinal), "On a partitioned table the script has no CONCURRENTLY and says why");

            var temporary = await Analyze("SELECT id FROM advice_tmp WHERE k = 5");
            check(temporary.MissingIndexes is [{ QualifiedTable: "pg_temp.advice_tmp" }], "A temporary table of the session is found: " + Describe(temporary));

            await session.ExecuteAsync("SET search_path = advice_a, public");
            var twins = await Analyze("SELECT id FROM twin WHERE v = 3");
            check(twins.MissingIndexes.Count == 0 && All(twins.Root).Any(o => o.IndexCandidate is not null), "A name that two schemas hold gives no suggestion, because the plan does not say which: " + Describe(twins));
            await session.ExecuteAsync("DROP TABLE advice_b.twin");
            twins = await Analyze("SELECT id FROM twin WHERE v = 3");
            check(twins.MissingIndexes is [{ Schema: "advice_a", Table: "twin" }], "Once the name is unique the schema is found: " + Describe(twins));
            await session.ExecuteAsync("RESET search_path");

            var capture = new ActualPlanCapture();
            await session.ExecuteAsync("SELECT id FROM advice_a.orders WHERE customer_id = 42 ORDER BY created_at DESC LIMIT 20", new QueryExecutionOptions(0, new QueryNotices()) { ActualPlans = capture });
            var actual = capture.Plans.Count == 1 ? (await session.AdviseIndexesAsync(ExecutionPlanBuilder.Build(capture.Plans)))[0] : null;
            check(actual?.MissingIndexes is [{ Schema: "advice_a", KeyList: "customer_id, created_at DESC" }], "An actual plan, which qualifies its columns, gives the same suggestion: " + (actual is null ? capture.Unavailable : Describe(actual)));
            check(await Scalar("SHOW auto_explain.log_min_duration") == "-1", "The catalog check leaves plan capture off");

            // Script
            string script = PlanIndexAdvisor.Script("SQLQuery1.sql - database postgres", top.MissingIndexes);
            check(script.StartsWith("/*\nMissing Index Details from SQLQuery1.sql - database postgres\n", StringComparison.Ordinal) && script.Contains("heuristic", StringComparison.Ordinal) && script.Contains("did not verify", StringComparison.Ordinal), "The script opens by saying where it came from and that it was not verified");
            check(script.Contains("Seq Scan on advice_a.orders discarded 19960 rows to return 40 (Rows Removed by Filter).\nUsed: equality on customer_id; ordering by created_at DESC.\n", StringComparison.Ordinal), "The script says what the index was read from");
            check(script.Contains("\nCREATE INDEX CONCURRENTLY ix_orders_customer_id_created_at\nON advice_a.orders (customer_id, created_at DESC);\n*/\n", StringComparison.Ordinal), "The script holds the CREATE INDEX inside a comment");
            await session.ExecuteAsync(script + "SELECT 1");
            check(await Scalar("SELECT count(*) FROM pg_indexes WHERE schemaname = 'advice_a' AND tablename = 'orders'") == "1", "Executing the script as it opens creates nothing");
            var hostile = top.MissingIndexes[0] with { Source = top.MissingIndexes[0].Source with { Ignored = ["note ~~ '*/ DROP TABLE x; /*'::text"] } };
            string hostileScript = PlanIndexAdvisor.Script("a */ b", [hostile]);
            await session.ExecuteAsync(hostileScript + "SELECT 1");
            check(!hostileScript.Contains("*/ DROP", StringComparison.Ordinal) && !hostileScript.Contains("a */ b", StringComparison.Ordinal), "Text taken from the plan cannot close the comment");

            // Catalog check
            await session.ExecuteAsync("CREATE TABLE advice_a.ix_orders_note(x int); CREATE INDEX advice_partial ON advice_a.orders(note) WHERE id < 0");
            var named = await Analyze("SELECT id FROM advice_a.orders WHERE note = 'zzz'");
            check(named.MissingIndexes is [{ IndexName: "ix_orders_note_1" }], "A partial index does not count as existing, and a taken name gets a number: " + Describe(named));

            string uncommented = string.Join("\n", PlanIndexAdvisor.Script("t", top.MissingIndexes).Split('\n').SkipWhile(l => !l.StartsWith("CREATE INDEX", StringComparison.Ordinal)).TakeWhile(l => l != "*/"));
            await session.ExecuteAsync(uncommented);
            check(await Scalar("SELECT count(*) FROM pg_indexes WHERE schemaname = 'advice_a' AND indexname = 'ix_orders_customer_id_created_at'") == "1", "Without the comment marks the statement creates the index: " + uncommented);
            var after = await Analyze("SELECT id FROM advice_a.orders WHERE customer_id = 42 ORDER BY created_at DESC LIMIT 20");
            check(after.MissingIndexes.Count == 0 && All(after.Root).All(o => o.Warnings.Count == 0) && All(after.Root).Any(o => o.Properties.GetValueOrDefault("Index Name") == "ix_orders_customer_id_created_at"), "With the index the planner uses it and the warning is gone: " + Describe(after));
            await session.ExecuteAsync("SET enable_indexscan = off; SET enable_bitmapscan = off; SET enable_indexonlyscan = off");
            var existing = await Analyze("SELECT id FROM advice_a.orders WHERE customer_id = 42");
            check(existing.MissingIndexes.Count == 0 && All(existing.Root).Any(o => o.IndexCandidate is not null), "A candidate that a valid index already starts with is dropped: " + Describe(existing));
            await session.ExecuteAsync("RESET enable_indexscan; RESET enable_bitmapscan; RESET enable_indexonlyscan");

            try { await session.ExecuteAsync("BEGIN; SELECT 1/0"); } catch (Npgsql.PostgresException) { }
            var planBefore = ExecutionPlanBuilder.Build([simple.Source]);
            bool failed = false;
            try { await session.AdviseIndexesAsync(planBefore); } catch (Npgsql.PostgresException) { failed = true; }
            await session.ExecuteAsync("ROLLBACK");
            check(failed, "In an aborted transaction the catalog check fails, for the caller to show the plan without suggestions");
        }
        finally
        {
            try { await session.ExecuteAsync("ROLLBACK"); } catch (Npgsql.NpgsqlException) { }
            await session.ExecuteAsync("DROP SCHEMA IF EXISTS advice_a CASCADE; DROP SCHEMA IF EXISTS advice_b CASCADE");
        }
    }
}
