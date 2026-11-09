using Npgsql;
using PGMS.PostgreSql;

// PGMS-034: Parse never runs the SQL and never harms an open transaction; client statistics are counted and laid out as trials.
internal static class QueryMenuEngineChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "qm_" + Guid.NewGuid().ToString("N")[..8];
        await using var session = new QuerySession(connection);
        await session.OpenAsync();
        async Task<string?> Error(string sql, bool inside = false) { try { await session.ParseAsync(sql, inside); return null; } catch (PostgresException ex) { return ex.SqlState; } }
        async Task<bool> Exists() => (await session.ExecuteAsync($"SELECT to_regclass('public.{table}')::text")).Tables[0].Rows[0][0] is string;

        check(await Error("SELECT 1; SELECT 2") is null, "Parse accepts valid statements");
        check(await Error($"CREATE TABLE public.{table}(id int)") is null && !await Exists(), "Parse does not run the statement");
        check(await Error("SELEC 1") == "42601", "Parse reports a syntax error");
        check(await Error("SELECT * FROM public.qm_missing_table") == "42P01", "Parse reports a missing object: PostgreSQL resolves names when parsing");
        check(await Error("BEGIN; COMMIT;") is null && !(await session.InspectLossRiskAsync()).OpenTransaction, "Parse of BEGIN opens no transaction");
        check((await session.ExecuteAsync("SELECT 1")).Tables.Count == 1, "The session keeps working after failed parses");

        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync($"CREATE TABLE public.{table}(id int)");
        check(await Error("SELEC 1", true) == "42601", "Parse inside a transaction reports the error");
        var after = await session.InspectLossRiskAsync();
        check(after is { OpenTransaction: true, Aborted: false } && await Exists(), "A parse error does not abort the open transaction, and its work is intact");
        check(await Error("SELECT 1", true) is null, "Parse inside a transaction accepts valid SQL");
        bool released = false;
        try { await session.ExecuteAsync("ROLLBACK TO SAVEPOINT pgms_parse"); } catch (PostgresException ex) { released = ex.SqlState == "3B001"; }
        check(released, "Parse leaves no savepoint behind");
        await session.ExecuteAsync("ROLLBACK");

        static ClientTrial Trial(int select, double total) => new(new DateTime(2026, 11, 8, 12, 0, select), 0, 0, select, select * 10, 5, 7, 1, 2, total);
        var log = new ClientStatisticsLog();
        log.Add(Trial(1, 10)); log.Add(Trial(3, 10)); log.Add(Trial(2, 40));
        var (headers, rows) = log.Table();
        check(headers.SequenceEqual(["", "Trial 3", "Trial 2", "Trial 1", "Average"]), "Trials are shown newest first, with the average last");
        string[] Row(string label) => rows.Single(r => r[0].Trim() == label);
        check(Row("Number of SELECT statements").SequenceEqual(["  Number of SELECT statements", "2 ↓", "3 ↑", "1", "2.0"]), "Arrows compare with the previous trial; the first has none");
        check(Row("Total execution time")[1] == "40 ↑" && Row("Total execution time")[2] == "10 →", "An equal value shows the level arrow");
        check(Row("Client Execution Time")[1] == "12:00:02" && Row("Client Execution Time")[4] == "", "The time of each trial is shown, without an average");
        for (int i = 0; i < 12; i++) log.Add(Trial(1, 1));
        check(log.Count == ClientStatisticsLog.MaxTrials && log.Table().Headers[1] == "Trial 15" && log.Table().Headers[^2] == "Trial 6", "Only the last ten trials are kept and numbering continues");
        log.Reset();
        log.Add(Trial(1, 1));
        check(log.Table().Headers.SequenceEqual(["", "Trial 1", "Average"]), "Reset starts again from Trial 1");

        var meter = new ClientTrialMeter("SELECT é");
        await session.StreamAsync($"CREATE TEMP TABLE {table}_t(id int); INSERT INTO {table}_t VALUES (1),(2); SELECT 'abc' FROM generate_series(1,3); UPDATE {table}_t SET id = 3 WHERE id = 1;", meter.Wrap((_, _) => ValueTask.CompletedTask), new QueryExecutionOptions { ReportStatements = true });
        var measured = meter.Finish(DateTime.Now);
        check(measured is { DmlStatements: 2, DmlRows: 3, SelectStatements: 1, SelectRows: 3, CharactersReceived: 9, BytesSent: 9 } && measured.TotalMilliseconds >= measured.WaitMilliseconds, $"The meter counts statements, rows and characters: {measured}");
    }
}
