using System.Text;
using Npgsql;
using PGMS.PostgreSql;

// M1044: Results to Text/File layout (measured in SSMS 22.11), per-statement counts, File mode without the display budget, Select Top text and Edit Top rows.
internal static class ResultsTextChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await QueryOptionsEngineChecks.RunAsync(connection, check);
        await QueryMenuEngineChecks.RunAsync(connection, check);
        await SqlCompletionChecks.RunAsync(connection, check);
        await ForeignKeyScanChecks.RunAsync(connection, check);
        await ForeignKeyIndexScanChecks.RunAsync(connection, check);
        await ObjectScriptChecks.RunAsync(connection, check);
        await ResultPagesChecks.RunAsync(connection, check);
        // ---- Layout, without a server ----
        var sw = new StringWriter { NewLine = "\n" };
        using (var writer = new TextResultsWriter(sw))
        {
            writer.StartResult(["CodigoLoteria", "Nome", "Obs"]);
            writer.AddRows([["102", "Zoo", null], ["104", "Loto Instantanea", "x"]]);
            writer.Statement(2);
            writer.StartResult(["A", "B"]);
            writer.AddRows([["1", "um"], ["2", null]]);
            writer.Statement(1);
            writer.Message("NOTICE: aviso");
            writer.Statement(0);
            writer.Complete(new DateTimeOffset(2026, 11, 8, 3, 27, 12, TimeSpan.FromHours(-3)).AddTicks(657314));
        }
        string expected = """
            CodigoLoteria Nome             Obs
            ------------- ---------------- ----
            102           Zoo              NULL
            104           Loto Instantanea x

            (2 rows affected)

            A B
            - ----
            1 um
            2 NULL

            (1 row affected)

            NOTICE: aviso

            (0 rows affected)

            Completion time: 2026-11-08T03:27:12.0657314-03:00

            """.Replace("\r\n", "\n");
        check(sw.ToString() == expected, "Results to Text: one-space separators, dashes as wide as the column, last column not padded, NULL, counts and completion as in SSMS");
        if (sw.ToString() != expected) Console.WriteLine("Got:\n" + sw);

        var messages = new StringWriter { NewLine = "\n" };
        using (var writer = new TextResultsWriter(messages, messagesOnly: true))
        {
            writer.StartResult(["A"]); writer.AddRows([["1"]]); writer.Statement(4); writer.Statement(3); writer.Message("aviso"); writer.Statement(0);
            writer.Complete(new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero));
        }
        check(messages.ToString() == "\n(4 rows affected)\n\n(3 rows affected)\naviso\n\n(0 rows affected)\n\nCompletion time: 2026-11-08T00:00:00.0000000+00:00\n",
            "Results to File: Messages holds counts, notices and completion as in SSMS, without the result blocks");

        var ddl = new StringWriter { NewLine = "\n" };
        using (var writer = new TextResultsWriter(ddl)) { writer.Statement(null); writer.Complete(DateTimeOffset.UnixEpoch); }
        check(ddl.ToString().StartsWith("Commands completed successfully.\n\nCompletion time: ", StringComparison.Ordinal), "Statements without a count print Commands completed successfully.");

        var wide = new StringWriter { NewLine = "\n" };
        using (var writer = new TextResultsWriter(wide, maxCharacters: 5)) { writer.StartResult(["c", "d"]); writer.AddRows([["abcdefgh", "z"]]); writer.Statement(1); }
        check(wide.ToString().StartsWith("c     d\n----- -\nabcde z\n", StringComparison.Ordinal), "Values are cut at the maximum characters per column");

        var spooled = new StringWriter { NewLine = "\n" };
        using (var writer = new TextResultsWriter(spooled))
        {
            writer.StartResult(["n", "t"], spool: true);
            writer.AddRows(Enumerable.Range(1, 3).Select(i => new object?[] { i.ToString(), i == 2 ? null : new string('é', i) }));
            writer.Statement(3);
        }
        check(spooled.ToString().StartsWith("n t\n- ----\n1 é\n2 NULL\n3 ééé\n\n(3 rows affected)", StringComparison.Ordinal), "A spooled result (Results to File) is laid out the same, Unicode and NULL included");

        // ---- Streaming: one StatementEnd per statement, File mode keeps every row ----
        string schema = "txt_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        try
        {
            await Sql($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (id integer PRIMARY KEY, name varchar(5) NOT NULL, note text, created timestamp DEFAULT '2026-01-01', serial integer GENERATED ALWAYS AS IDENTITY);
                INSERT INTO {schema}.t (id, name, note) VALUES (1, 'um', NULL), (2, 'dois', 'x');
                CREATE TABLE {schema}.nokey (a integer, b text);
                INSERT INTO {schema}.nokey VALUES (1, 'um'), (1, 'um'), (2, NULL);
                CREATE VIEW {schema}.v AS SELECT id, name FROM {schema}.t;
                """);
            await using var session = new QuerySession(connection);
            await session.OpenAsync();
            var ends = new List<long?>(); var notices = new QueryNotices();
            await session.StreamAsync($"SELECT * FROM {schema}.t; UPDATE {schema}.t SET note = note WHERE false; DO $$BEGIN RAISE NOTICE 'aviso'; END$$; CREATE TEMP TABLE tt(x int); INSERT INTO tt VALUES (1),(2),(3)",
                (batch, _) => { if (batch.StatementEnd) ends.Add(batch.RowsAffected); return ValueTask.CompletedTask; }, new QueryExecutionOptions(0, notices) { ReportStatements = true });
            check(ends.SequenceEqual(new long?[] { 2, 0, null, null, 3 }), $"Each statement ends with its count: SELECT 2, UPDATE 0, DO and CREATE without count, INSERT 3 ({string.Join(",", ends.Select(e => e?.ToString() ?? "-"))})");
            check(notices.Items.Any(n => n.Message == "aviso"), "Notices are still collected while statements are reported");

            var plain = new List<QueryBatch>();
            await session.StreamAsync($"SELECT 1; SELECT 2", (batch, _) => { plain.Add(batch); return ValueTask.CompletedTask; });
            check(plain.All(b => !b.StatementEnd), "Without ReportStatements no StatementEnd batch is sent (grid unchanged)");

            int rows = 0; string? longest = null; bool anyLimited = false;
            await session.StreamAsync($"SELECT g, repeat('y', 300) FROM generate_series(1, {QueryDisplayBudget.MaxRows + 2500}) g",
                (batch, _) => { rows += batch.Rows.Count; if (batch.Rows.Count > 0) longest = (string)batch.Rows[0][1]; anyLimited |= batch.DisplayLimited; return ValueTask.CompletedTask; },
                new QueryExecutionOptions { TextCellLimit = 256, ReportStatements = true });
            check(rows == QueryDisplayBudget.MaxRows + 2500 && longest?.Length == 256 && !anyLimited, $"File mode delivers every row ({rows:N0}) without a display limit and keeps 256 characters per value");

            // ---- Select Top ----
            var service = new TableRowsService(connection);
            var (kind, columns) = await service.ReadColumnsAsync(schema, "t");
            string selectTop = TableRowsService.SelectTopSql(schema, "t", columns.Select(c => c.Name).ToList(), 1000);
            check(kind == "r" && selectTop == $"SELECT \"id\"\r\n      ,\"name\"\r\n      ,\"note\"\r\n      ,\"created\"\r\n      ,\"serial\"\r\n  FROM \"{schema}\".\"t\"\r\n LIMIT 1000\r\n",
                "Select Top text follows the SSMS layout with quoted names and LIMIT");
            var selected = await session.ExecuteAsync(selectTop);
            check(selected.Tables.Single().Rows.Count == 2, "The Select Top text runs");
            check(columns.Single(c => c.Name == "id").InKey && columns.Single(c => c.Name == "serial").ReadOnly && columns.Single(c => c.Name == "name").TypeName == "character varying",
                "Columns know the key, identity GENERATED ALWAYS as read-only and the type without length");

            // ---- Edit Top with a primary key ----
            var snapshot = await service.LoadAsync(schema, "t", 200);
            check(snapshot.Rows.Count == 2 && snapshot.HasKey && snapshot.Rows.Any(r => r[0] == "1" && r[2] is null), "Edit Top reads the rows as text, NULL as null");
            var one = snapshot.Rows.Single(r => r[0] == "1");
            var updated = await service.UpdateAsync(snapshot, one, new Dictionary<int, string?> { [1] = "novo", [2] = "nota" });
            check(updated[1] == "novo" && updated[2] == "nota" && (string?)await Scalar($"SELECT note FROM {schema}.t WHERE id = 1") == "nota", "Update by primary key changes the row and returns it as stored");
            bool tooLong = false;
            try { await service.UpdateAsync(snapshot, updated, new Dictionary<int, string?> { [1] = "longo demais" }); }
            catch (PostgresException ex) when (ex.SqlState == "22001") { tooLong = true; }
            check(tooLong && (string?)await Scalar($"SELECT name FROM {schema}.t WHERE id = 1") == "novo", "A value longer than varchar(5) is refused by the server, not cut");
            var inserted = await service.InsertAsync(snapshot, new Dictionary<int, string?> { [0] = "3", [1] = "tres" });
            check(inserted[0] == "3" && inserted[3] == "2026-01-01 00:00:00" && inserted[4] is not null, "Insert leaves untouched columns to their defaults and identity, and returns them");
            bool readOnly = false;
            try { await service.UpdateAsync(snapshot, inserted, new Dictionary<int, string?> { [4] = "99" }); } catch (TableRowsException) { readOnly = true; }
            check(readOnly, "An identity GENERATED ALWAYS column cannot be changed");
            await service.DeleteAsync(snapshot, [inserted]);
            check((long)(await Scalar($"SELECT count(*) FROM {schema}.t"))! == 2, "Delete removes the row");

            // ---- Edit Top without a key: identical rows are refused, as in SSMS ----
            var nokey = await service.LoadAsync(schema, "nokey", 200);
            var twin = nokey.Rows.First(r => r[0] == "1");
            string? refused = null;
            try { await service.UpdateAsync(nokey, twin, new Dictionary<int, string?> { [1] = "dup" }); } catch (TableRowsException ex) { refused = ex.Message; }
            check(refused?.Contains("(2 rows)", StringComparison.Ordinal) == true && (long)(await Scalar($"SELECT count(*) FROM {schema}.nokey WHERE b = 'um'"))! == 2,
                "Changing one of two identical rows without a key is rolled back with the SSMS message (2 rows)");
            var single = nokey.Rows.Single(r => r[0] == "2");
            var changed = await service.UpdateAsync(nokey, single, new Dictionary<int, string?> { [1] = "dois" });
            check(changed[1] == "dois", "A unique row without a key is found by all its values, NULL included");
            bool deleteRefused = false;
            try { await service.DeleteAsync(nokey, [twin]); } catch (TableRowsException) { deleteRefused = true; }
            check(deleteRefused && (long)(await Scalar($"SELECT count(*) FROM {schema}.nokey"))! == 3, "Deleting one of two identical rows is refused and nothing is deleted");

            // ---- Views ----
            var view = await service.LoadAsync(schema, "v", 200);
            var viaView = await service.UpdateAsync(view, view.Rows.Single(r => r[0] == "2"), new Dictionary<int, string?> { [1] = "Dois" });
            check(view.RelationKind == "v" && viaView[1] == "Dois", "An updatable view is edited through PostgreSQL");
        }
        finally { await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE"); }
    }
}
