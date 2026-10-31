using PGMS.PostgreSql;

internal static class QueryStreamingChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await QueryTimeoutChecks.RunAsync(connection,check);
        await QuerySessionInfoChecks.RunAsync(connection,check);
        await QueryPlanChecks.RunAsync(connection,check);
        await QueryNoticeChecks.RunAsync(connection,check);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        var batches = new List<QueryBatch>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = session.StreamAsync("SELECT i AS same, NULL::text AS same, ''::text AS empty FROM generate_series(1,600) i; SELECT 1 WHERE false; SELECT 'last';", async (batch, token) =>
        {
            batches.Add(batch);
            if (batch.Rows.Count > 0 && !received.Task.IsCompleted)
            { received.SetResult(); await resume.Task.WaitAsync(token); }
        });
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(!pending.IsCompleted && batches.Count == 2, "M98 first row arrives before completion and consumer backpressure stops producer");
        resume.SetResult();
        var result = await pending;
        check(result.DisplayedRows == 601 && !result.Truncated, "M98 streamed row count is exact");
        check(batches.Where(b => b.Columns is not null).Count() == 3 && batches.Any(b => b.ResultIndex == 1 && b.Columns is not null), "M98 empty and multiple results retain their schemas");
        check(batches[0].Columns![0] == batches[0].Columns![1] && batches[1].Rows[0][1] is DBNull && (string)batches[1].Rows[0][2] == "", "M98 duplicate headers NULL and empty text remain distinct");
        check(batches.All(b => b.Rows.Count <= 128), "M98 row batches are bounded");
        var rows = batches.Where(b => b.ResultIndex == 0).SelectMany(b => b.Rows).ToArray();
        check(rows.Select(r => int.Parse((string)r[0])).SequenceEqual(Enumerable.Range(1,600)), "M98 batches preserve row order without mutation");
        result = await session.StreamAsync("SELECT i FROM generate_series(1,12000)i; CREATE TEMP TABLE stream_tail(id int); INSERT INTO stream_tail VALUES(7);", (_, _) => ValueTask.CompletedTask);
        check(result.Truncated && result.DisplayedRows == 10000, "M98 display row budget is global");
        check((string)(await session.ExecuteAsync("SELECT id FROM stream_tail")).Tables[0].Rows[0][0] == "7", "M98 display cap drains script and preserves trailing effects");
        using var cancel = new CancellationTokenSource();
        bool canceled = false;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await session.StreamAsync("SELECT i, repeat('x',256) FROM generate_series(1,600)i; SELECT pg_sleep(30);", (batch, token) =>
            {
                if (batch.Rows.Count > 0) { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
                return ValueTask.CompletedTask;
            }, cancel.Token).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled && timer.Elapsed < TimeSpan.FromSeconds(5), "M100 consumer cancellation cancels server before reader disposal");
        check((string)(await session.ExecuteAsync("SELECT 9")).Tables[0].Rows[0][0] == "9", "M100 session remains usable after consumer cancellation");
        batches.Clear();
        result = await session.StreamAsync("SELECT repeat('x',65536), NULL::text, ''::text;", (batch, _) => { batches.Add(batch); return ValueTask.CompletedTask; });
        check(!result.Truncated && ((string)batches.SelectMany(b => b.Rows).Single()[0]).Length == 65536, "M101 exact cell boundary is retained intact");
        batches.Clear();
        result = await session.StreamAsync("SELECT 'safe'; SELECT repeat('x',2097152); SELECT 'not displayed'; INSERT INTO stream_tail VALUES(8);", (batch, _) => { batches.Add(batch); return ValueTask.CompletedTask; });
        check(result.Truncated && result.DisplayedRows == 1 && batches.SelectMany(b => b.Rows).Count() == 1, "M101 oversized cell omits entire row and subsequent rows");
        check(batches.Any(b => b.DisplayLimited) && (await session.ExecuteAsync("SELECT * FROM stream_tail")).Tables[0].Rows.Count == 2, "M101 limit event arrives and trailing script effects still execute");
        result = await session.StreamAsync("SELECT repeat('x',65536),repeat('x',65536),repeat('x',65536),repeat('x',65536), 'overflow';", (_, _) => ValueTask.CompletedTask);
        check(result.Truncated && result.DisplayedRows == 0, "M101 total row text is bounded across columns");
        long retained = 0;
        result = await session.StreamAsync("SELECT repeat('x',1024) FROM generate_series(1,8193);", (batch, _) =>
        { retained += batch.Rows.SelectMany(r => r).OfType<string>().Sum(v => (long)v.Length); return ValueTask.CompletedTask; });
        check(result.Truncated && retained == QueryDisplayBudget.MaxTextCharacters && result.DisplayedRows == 8192, "M101 global retained text budget spans all row batches");
        batches.Clear();
        result = await session.StreamAsync("SELECT repeat(chr(128512),32768);", (batch, _) => { batches.Add(batch); return ValueTask.CompletedTask; });
        check(!result.Truncated && ((string)batches.SelectMany(b => b.Rows).Single()[0]).Length == 65536, "M101 Unicode uses UTF-16 units without splitting retained values");
        int schemas = 0;
        result = await session.StreamAsync(string.Join(";", Enumerable.Repeat("SELECT 1 WHERE false",101)), (batch, _) =>
        { if (batch.Columns is not null) schemas++; return ValueTask.CompletedTask; });
        check(result.Truncated && schemas == 100 && result.DisplayedRows == 0, "M102 empty result schemas obey the 100-set budget");
        bool consumerFailed = false;
        try
        {
            await session.StreamAsync("SELECT i, repeat('x',256) FROM generate_series(1,600)i; SELECT pg_sleep(30);", (batch, _) =>
            { if (batch.Rows.Count > 0) throw new InvalidOperationException("consumer failure"); return ValueTask.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException ex) when (ex.Message == "consumer failure") { consumerFailed = true; }
        check(consumerFailed && (string)(await session.ExecuteAsync("SELECT 11")).Tables[0].Rows[0][0] == "11", "M102 consumer failure cancels reader and preserves reusable session");
        await session.ExecuteAsync("BEGIN");
        using var transactionCancel = new CancellationTokenSource();
        try
        {
            await session.StreamAsync("SELECT i, repeat('x',256) FROM generate_series(1,600)i; SELECT pg_sleep(30);", (batch, token) =>
            { if (batch.Rows.Count > 0) { transactionCancel.Cancel(); token.ThrowIfCancellationRequested(); } return ValueTask.CompletedTask; }, transactionCancel.Token).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) { }
        bool aborted = false;
        try { await session.ExecuteAsync("SELECT 1"); } catch (Npgsql.PostgresException ex) when (ex.SqlState == "25P02") { aborted = true; }
        check(aborted, "M102 canceled explicit transaction is not silently rolled back");
        await session.ExecuteAsync("ROLLBACK");
        result = await session.StreamAsync("SELECT 42::numeric, '{\"a\":1}'::jsonb, decode('abcd','hex');", (_, _) => ValueTask.CompletedTask);
        check(!result.Truncated && result.DisplayedRows == 1, "M102 text representations work after explicit rollback");
        string columns256 = string.Join(",", Enumerable.Range(1, 256).Select(i => $"NULL::text AS c{i}"));
        batches.Clear();
        result = await session.StreamAsync($"SELECT {columns256} WHERE false", (batch, _) => { batches.Add(batch); return ValueTask.CompletedTask; });
        check(!result.Truncated && batches.Single().Columns!.Count == 256, "Exact column boundary preserves empty schema");
        batches.Clear();
        result = await session.StreamAsync($"SELECT {columns256}, NULL::text AS extra; INSERT INTO stream_tail VALUES(99); SELECT 1", (batch, _) => { batches.Add(batch); return ValueTask.CompletedTask; });
        check(result.Truncated && batches.All(b => b.Columns is null && b.Rows.Count == 0), "Oversize schema is omitted whole with no later result index gaps");
        check((string)(await session.ExecuteAsync("SELECT count(*) FROM stream_tail WHERE id=99")).Tables[0].Rows[0][0] == "1", "Metadata cap preserves trailing script effects");
        schemas = 0;
        result = await session.StreamAsync(string.Join(";", Enumerable.Repeat($"SELECT {columns256} WHERE false", 17)), (batch, _) =>
        { if (batch.Columns is not null) schemas++; return ValueTask.CompletedTask; });
        check(result.Truncated && result.LimitReasons == QueryLimitReason.Columns && schemas == 16, "Total column budget spans empty results");
        long cells = 0;
        string columns32 = string.Join(",", Enumerable.Range(1, 32).Select(i => $"NULL::text AS c{i}"));
        result = await session.StreamAsync($"SELECT {columns32} FROM generate_series(1,10000); INSERT INTO stream_tail VALUES(100); SELECT 1", (batch, _) =>
        { cells += batch.Rows.Sum(r => (long)r.Length); return ValueTask.CompletedTask; });
        check(result.Truncated && result.LimitReasons == QueryLimitReason.Cells && result.DisplayedRows == QueryDisplayBudget.MaxCells / 32 && cells <= QueryDisplayBudget.MaxCells,
            "Cell budget includes NULL and omits whole excess rows across results");
        check((string)(await session.ExecuteAsync("SELECT count(*) FROM stream_tail WHERE id=100")).Tables[0].Rows[0][0] == "1", "Cell cap drains trailing statements without changing SQL semantics");
        var materialized = await session.ExecuteAsync("SELECT 'safe' AS same, NULL::text AS same; SELECT repeat('x',65537); INSERT INTO stream_tail VALUES(101)");
        check(materialized.Truncated && materialized.LimitReasons == QueryLimitReason.Text && materialized.Tables[0].Rows.Count == 1 && materialized.Tables[1].Rows.Count == 0,
            "Materialized API applies identical cell text budget without shortened values");
        check(materialized.Tables[0].Columns[0].Caption == materialized.Tables[0].Columns[1].Caption && materialized.Tables[0].Rows[0][1] is DBNull,
            "Materialized API preserves duplicate labels and NULL through shared pipeline");
        check((string)(await session.ExecuteAsync("SELECT count(*) FROM stream_tail WHERE id=101")).Tables[0].Rows[0][0] == "1", "Materialized cap still executes trailing SQL");
        batches.Clear();
        result = await session.StreamAsync($"SELECT {columns256} FROM generate_series(1,40)", (batch, _) => { batches.Add(batch); return ValueTask.CompletedTask; });
        check(!result.Truncated && result.DisplayedRows == 40 && batches.All(b => b.Rows.Sum(r => r.Length) <= QueryDisplayBudget.MaxBatchCells),
            "Wide result batches bound cell work without reducing retained rows");
        var activeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeQuery = session.StreamAsync("SELECT 123", async (batch, token) =>
        {
            if (batch.Rows.Count > 0) { activeEntered.SetResult(); await releaseActive.Task.WaitAsync(token); }
        });
        try
        {
            await activeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var waitingCancellation = new CancellationTokenSource();
            var waitingQuery = session.ExecuteAsync("CREATE TEMP TABLE canceled_waiter(id int)", waitingCancellation.Token);
            waitingCancellation.Cancel();
            bool waitingCanceled = false;
            try { await waitingQuery.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { waitingCanceled = true; }
            check(waitingCanceled && !activeQuery.IsCompleted, "Canceling queued execution leaves active reader untouched");
        }
        finally { releaseActive.TrySetResult(); await activeQuery; }
        check((await session.ExecuteAsync("SELECT to_regclass('pg_temp.canceled_waiter')")).Tables[0].Rows[0][0] is DBNull,
            "Canceled queued SQL never reaches server and session remains usable");
    }
}
