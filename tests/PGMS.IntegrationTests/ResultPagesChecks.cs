using System.Reflection;
using PGMS.PostgreSql;

// M1053: pages of a grid result kept in a temporary file, and the option that lifts the display budget.
internal static class ResultPagesChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static object[] Row(int i) => [i.ToString(), i % 3 == 0 ? DBNull.Value : "ação " + i];
        using (var pages = new ResultPages(10, 2))
        {
            check(pages.PageCount == 1 && pages.TotalRows == 0, "An empty result has one page");
            var kept = Enumerable.Range(1, 25).Select(i => pages.Add(Row(i))).ToArray();
            check(kept.Take(10).All(k => k) && kept.Skip(10).All(k => !k), "The first page stays with the caller and the rest goes to the file");
            check(pages.TotalRows == 25 && pages.PageCount == 3 && pages.RowsOf(0) == 10 && pages.RowsOf(1) == 10 && pages.RowsOf(2) == 5, "Pages are cut at the page size");
            check(pages.FirstRowOf(0) == 0 && pages.FirstRowOf(1) == 10 && pages.FirstRowOf(2) == 20, "Each page knows its first row");
            var second = pages.ReadPage(1);
            check(second.Count == 10 && (string)second[0][0] == "11" && (string)second[0][1] == "ação 11" && second[1][1] == DBNull.Value && (string)second[9][0] == "20", "A page comes back with its values, accents and NULLs");
            check((string)pages.ReadPage(2)[4][0] == "25" && (string)pages.ReadPage(1)[0][0] == "11", "Pages can be read in any order, and again");
            pages.Add(Row(26));
            check(pages.ReadPage(2).Count == 6 && (string)pages.ReadPage(2)[5][0] == "26", "A page can be read while rows are still arriving");
            bool threw = false;
            try { pages.ReadPage(0); } catch (ArgumentOutOfRangeException) { threw = true; }
            check(threw, "The first page is not in the file");
            var file = (string)typeof(ResultPages).GetField("path", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pages)!;
            check(File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.Hidden), "The file exists and is hidden while the result is open");
            pages.Dispose();
            check(!File.Exists(file), "Disposing removes the file");
        }
        using (var small = new ResultPages(1000, 1))
        {
            small.Add(["x"]);
            check(small.PageCount == 1 && typeof(ResultPages).GetField("path", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(small) is null, "A result that fits one page never touches the disk");
        }
        using (var huge = new ResultPages(100, 1, 100))
        {
            string big = new('z', 60);
            huge.Add([big]); huge.Add([big]); huge.Add([big]);
            check(huge.PageCount == 3 && huge.RowsOf(0) == 1 && huge.RowsOf(1) == 1, "A page closes early when its text is too large");
        }
        using (var longValues = new ResultPages(1, 1))
        {
            string big = new('z', 40 * 1024 * 1024);
            longValues.Add(["x"]); longValues.Add([big]);
            check(((string)longValues.ReadPage(1)[0][0]).Length == big.Length, "A very long value survives the file");
        }
        check(ResultPages.MaxPageCharacters / 100000 >= 2000, "The text guard does not cut a page of 100,000 ordinary wide rows");

        await using var session = new QuerySession(connection);
        await session.OpenAsync();
        foreach (int delay in new[] { 0, 1, 5, 20 })
        {
            await using var s = new QuerySession(connection);
            await s.OpenAsync();
            using var cts = new CancellationTokenSource();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string outcome = "none";
            try
            {
                await s.StreamAsync("SELECT i, repeat('x',256) FROM generate_series(1,600)i; SELECT pg_sleep(30);", (b, _) => { if (b.Rows.Count > 0 && !cts.IsCancellationRequested) { if (delay == 0) cts.Cancel(); else cts.CancelAfter(delay); } return default; }, new QueryExecutionOptions { PagedDisplay = true }, cts.Token);
            }
            catch (OperationCanceledException) { outcome = "canceled"; }
            check(outcome == "canceled" && watch.ElapsedMilliseconds < 10000 && s.IsConnected, $"Cancel {delay} ms after the first rows stops a later statement and keeps the connection ({outcome}, {watch.ElapsedMilliseconds} ms)");
            var after = new List<QueryBatch>();
            await s.StreamAsync("SELECT 'still works'", (b, _) => { after.Add(b); return default; });
            check(after.Sum(b => b.Rows.Count) == 1, $"The session works after a cancel {delay} ms after the first rows");
        }
        int Rows(List<QueryBatch> batches) => batches.Sum(b => b.Rows.Count);
        var all = new List<QueryBatch>();
        string wide = "SELECT " + string.Join(",", Enumerable.Range(1, 40).Select(i => $"g AS c{i}")) + $" FROM generate_series(1, {QueryDisplayBudget.MaxRows + 2000}) g";
        var result = await session.StreamAsync(wide, (b, _) => { all.Add(b); return default; }, new QueryExecutionOptions { PagedDisplay = true });
        check(!result.Truncated && result.DisplayedRows == QueryDisplayBudget.MaxRows + 2000 && Rows(all) == result.DisplayedRows, "Paged display delivers every row, past the old row and cell limits");
        check(all.Where(b => b.Rows.Count > 0).Max(b => b.Rows.Sum(r => r.Length)) <= QueryDisplayBudget.MaxBatchCells, "Paged display keeps batches bounded");
        var limited = new List<QueryBatch>();
        var budget = await session.StreamAsync(wide, (b, _) => { limited.Add(b); return default; });
        check(budget.Truncated && budget.LimitReasons == QueryLimitReason.Cells && Rows(limited) == QueryDisplayBudget.MaxCells / 40, "Without paged display the budget still applies");
        var longValue = new List<QueryBatch>();
        var oversized = await session.StreamAsync($"SELECT repeat('a', {QueryDisplayBudget.MaxCellCharacters + 1})", (b, _) => { longValue.Add(b); return default; }, new QueryExecutionOptions { PagedDisplay = true });
        check(oversized.Truncated && oversized.LimitReasons == QueryLimitReason.Text, "A cell over its limit is still refused");
    }
}
