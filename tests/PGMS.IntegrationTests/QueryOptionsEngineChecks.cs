using Npgsql;
using PGMS.PostgreSql;

// PGMS-033: text output formats, NOCOUNT, completion time, discarded results, session options, commit at close, and JSON detection.
internal static class QueryOptionsEngineChecks
{
    private static string Write(Action<TextResultsWriter> act, int max = TextResultsWriter.DefaultMaxCharacters, TextResultsFormat? format = null, bool noCount = false, bool completion = true)
    {
        var text = new StringWriter { NewLine = "\n" };
        using (var writer = new TextResultsWriter(text, false, max, format) { NoCount = noCount, ShowCompletionTime = completion }) act(writer);
        return text.ToString();
    }

    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static void Two(TextResultsWriter w) { w.StartResult(["a", "name"], types: ["integer", "text"]); w.AddRows([["1", "x y"], ["22", null]]); w.Statement(2); }

        check(Write(Two, format: new(TextOutputFormat.CommaDelimited), completion: false) == "a,name\n--,----\n1,x y\n22,NULL\n\n(2 rows affected)\n", "Comma delimited has no padding and keeps the line of dashes");
        check(Write(Two, format: new(TextOutputFormat.TabDelimited), completion: false).StartsWith("a\tname\n--\t----\n1\tx y\n", StringComparison.Ordinal), "Tab delimited separates with tabs");
        check(Write(Two, format: new(TextOutputFormat.SpaceDelimited), completion: false).StartsWith("a name\n-- ----\n1 x y\n22 NULL\n", StringComparison.Ordinal), "Space delimited does not pad");
        check(Write(Two, format: new(TextOutputFormat.CustomDelimited, '|'), completion: false).StartsWith("a|name\n--|----\n1|x y\n", StringComparison.Ordinal), "Custom delimiter is used");
        check(Write(Two, format: new(IncludeHeaders: false), completion: false).StartsWith("1  x y\n22 NULL\n", StringComparison.Ordinal), "Without column headers there are neither names nor dashes");
        check(Write(Two, format: new(RightAlignNumeric: true), completion: false).StartsWith(" a name\n-- ----\n 1 x y\n22 NULL\n", StringComparison.Ordinal), "Numeric columns are padded on the left, text columns are not");
        check(Write(Two, format: new(TextOutputFormat.CommaDelimited, RightAlignNumeric: true), completion: false).Contains("\n1,x y\n", StringComparison.Ordinal), "Right alignment does not pad delimited output");
        check(!Write(Two, noCount: true, completion: false).Contains("rows affected", StringComparison.Ordinal), "NOCOUNT hides the row counts");
        check(Write(w => { w.Statement(3); w.Complete(DateTimeOffset.UnixEpoch); }, noCount: true, completion: false) == "Commands completed successfully.\n", "NOCOUNT with only counted statements says the commands completed");
        static void Finished(TextResultsWriter w) { Two(w); w.Complete(DateTimeOffset.UnixEpoch); }
        check(Write(Finished).Contains("\n\nCompletion time: 1970-", StringComparison.Ordinal) && !Write(Finished, completion: false).Contains("Completion", StringComparison.Ordinal), "Completion time can be turned off");
        check(Write(w => { w.StartResult(["c"]); w.AddRows([["abcdefgh"]]); w.EndResult(); }, max: 3, completion: false).Contains("\nabc\n", StringComparison.Ordinal), "The maximum characters per column cuts the value");
        check(TextResultsFormat.IsNumericType("numeric(10,2)") && TextResultsFormat.IsNumericType("double precision") && !TextResultsFormat.IsNumericType("integer[]") && !TextResultsFormat.IsNumericType("text"), "Numeric types are recognized with modifiers, arrays are not numeric");

        check(ResultCopyText.FlattenLineBreaks("a\r\nb\nc\rd") == "a b c d", "Each line break becomes one space");
        check(ResultCopyText.Format(["h"], [["a\nb"]], true) == "\"h\"\r\n\"a\nb\"\r\n" && ResultCopyText.Format(["h"], [["a\nb"]], true, retainLineBreaks: false) == "\"h\"\r\n\"a b\"\r\n", "Copy keeps line breaks unless Retain CR/LF is off");

        check(ResultJson.IsDocument(" {\"a\":1} ") && ResultJson.IsDocument("[1,2]") && !ResultJson.IsDocument("{not json}") && !ResultJson.IsDocument("12") && !ResultJson.IsDocument("\"s\"") && !ResultJson.IsDocument(null), "Only JSON objects and arrays are links");
        check(ResultJson.MayHoldJson("jsonb") && ResultJson.MayHoldJson("character varying(50)") && !ResultJson.MayHoldJson("text[]") && !ResultJson.MayHoldJson("integer") && !ResultJson.MayHoldJson(null), "Links are limited to json and text columns");
        check(ResultJson.TryFormat("{\"pi\":3.10,\"a\":1,\"a\":2,\"nome\":\"João\"}", out string json) && json.Contains("3.10", StringComparison.Ordinal) && json.Contains("João", StringComparison.Ordinal) && json.Split("\"a\"").Length == 3 && json.Contains('\n'), "Formatting keeps number spelling, repeated keys and accents");

        await using var session = new QuerySession(connection);
        await session.OpenAsync();
        async Task<string> Value(string sql) => (string)(await session.ExecuteAsync(sql)).Tables[0].Rows[0][0];

        // Types still arrive when values are read as text.
        IReadOnlyList<string>? types = null;
        await session.StreamAsync("SELECT 1::int, 1.5::numeric(10,2), '{}'::jsonb, 't'::text, 'v'::varchar(5), ARRAY[1]::int[]", (batch, _) => { types ??= batch.ColumnTypes; return ValueTask.CompletedTask; });
        check(types is { Count: 6 } && TextResultsFormat.IsNumericType(types[0]) && TextResultsFormat.IsNumericType(types[1]) && ResultJson.MayHoldJson(types[2]) && ResultJson.MayHoldJson(types[3]) && ResultJson.MayHoldJson(types[4]) && !ResultJson.MayHoldJson(types[5]) && !TextResultsFormat.IsNumericType(types[5]),
            "Column types are reported: " + string.Join(", ", types ?? []));

        // Discard: past the display budget, nothing delivered but the counts are right.
        int delivered = 0; var counts = new List<long?>();
        var finished = await session.StreamAsync("SELECT g, repeat('x', 500) FROM generate_series(1, 20000) g; SELECT 1 WHERE false;", (batch, _) => { delivered += batch.Rows.Count + (batch.Columns is null ? 0 : 1); if (batch.StatementEnd) counts.Add(batch.RowsAffected); return ValueTask.CompletedTask; }, new QueryExecutionOptions { ReportStatements = true, DiscardResults = true });
        check(delivered == 0 && finished.DisplayedRows == 20000 && !finished.Truncated && counts.SequenceEqual(new long?[] { 20000, 0 }), "Discard keeps no rows, reads them all and still reports counts");

        // Session options.
        await session.ApplySessionOptionsAsync(QueryIsolationLevel.RepeatableRead, 1234);
        check(await Value("SHOW transaction_isolation") == "repeatable read" && await Value("SHOW lock_timeout") == "1234ms", "The isolation level and lock timeout reach the session");
        await session.ApplySessionOptionsAsync(QueryIsolationLevel.Serializable, 0);
        check(await Value("SHOW default_transaction_isolation") == "serializable" && await Value("SHOW lock_timeout") == "0", "Zero means no lock limit");
        await session.ApplySessionOptionsAsync(QueryIsolationLevel.ServerDefault, -1);
        check(await Value("SHOW default_transaction_isolation") == "read committed" && await Value("SHOW lock_timeout") == "0", "Defaults go back to the server's values");
        await using (var tuned = new QuerySession(new NpgsqlConnectionStringBuilder(connection) { Options = "-c default_transaction_isolation=repeatable\\ read -c lock_timeout=777" }.ConnectionString))
        {
            await tuned.OpenAsync();
            await tuned.ApplySessionOptionsAsync(QueryIsolationLevel.Serializable, 5);
            await tuned.ApplySessionOptionsAsync(QueryIsolationLevel.ServerDefault, -1);
            check((string)(await tuned.ExecuteAsync("SHOW default_transaction_isolation")).Tables[0].Rows[0][0] == "repeatable read" && (string)(await tuned.ExecuteAsync("SHOW lock_timeout")).Tables[0].Rows[0][0] == "777ms", "Defaults respect a value the session started with, not read committed");
        }

        // Commit at close.
        string table = "qo_commit_" + Guid.NewGuid().ToString("N")[..8];
        await session.ExecuteAsync($"BEGIN; CREATE TABLE public.{table}(id int)");
        var open = await session.InspectLossRiskAsync();
        await session.CommitAsync();
        await using (var other = new QuerySession(connection))
        {
            await other.OpenAsync();
            check(open is { OpenTransaction: true, Aborted: false } && (await other.ExecuteAsync($"SELECT to_regclass('public.{table}')::text")).Tables[0].Rows[0][0] is string, "Commit makes the open transaction permanent");
            await other.ExecuteAsync($"DROP TABLE public.{table}");
        }
        await session.ExecuteAsync("BEGIN");
        try { await session.ExecuteAsync("SELECT 1/0"); } catch (PostgresException) { }
        var failed = await session.InspectLossRiskAsync();
        await session.CommitAsync();
        check(failed is { OpenTransaction: true, Aborted: true } && !(await session.InspectLossRiskAsync()).OpenTransaction, "A failed transaction is reported as aborted and COMMIT ends it");
    }
}
