using Npgsql;
using PGMS.PostgreSql;

/// <summary>The SQL side of the query designers (PGMS-031, M1046). Every statement the panes accept must mean the same after it is written back.</summary>
internal static class QueryDesignChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<string> Rows(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin) { AllResultTypesAreUnknown = true };
            await using var reader = await command.ExecuteReaderAsync();
            var lines = new List<string>();
            while (await reader.ReadAsync()) lines.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetString(i))));
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }
        await Sql("""
            CREATE SCHEMA qd;
            CREATE TABLE qd.parent (id integer PRIMARY KEY, name text NOT NULL, "Mixed Name" text);
            CREATE TABLE qd.child (id integer PRIMARY KEY, parent_id integer REFERENCES qd.parent(id), amount numeric(10,2), "order" integer);
            INSERT INTO qd.parent VALUES (1,'Ana','x'),(2,'Bruno',NULL),(3,'Caio','it''s');
            INSERT INTO qd.child VALUES (10,1,5,1),(11,1,15,2),(12,2,25,3),(13,NULL,35,4);
            CREATE TABLE qd.writes (id integer);
            CREATE FUNCTION qd.write_row() RETURNS integer LANGUAGE sql AS 'INSERT INTO qd.writes VALUES (1) RETURNING id';
            """);

        string[] same =
        [
            "SELECT p.id, p.name FROM qd.parent AS p",
            "SELECT id, name FROM qd.parent",
            "select p.id from qd.parent p where p.id > 1",
            "SELECT DISTINCT c.parent_id FROM qd.child AS c",
            "SELECT p.name, c.amount FROM qd.parent AS p INNER JOIN qd.child AS c ON p.id = c.parent_id",
            "SELECT p.name, c.amount FROM qd.parent AS p LEFT OUTER JOIN qd.child AS c ON p.id = c.parent_id",
            "SELECT p.name, c.amount FROM qd.child AS c RIGHT JOIN qd.parent AS p ON p.id = c.parent_id",
            "SELECT p.name, c.amount FROM qd.parent AS p FULL JOIN qd.child AS c ON p.id = c.parent_id",
            "SELECT p.id, c.id FROM qd.parent AS p CROSS JOIN qd.child AS c",
            "SELECT p.id AS key, p.name AS \"Display Name\" FROM qd.parent AS p",
            "SELECT p.\"Mixed Name\" FROM qd.parent AS p WHERE (p.\"Mixed Name\" = 'it''s')",
            "SELECT c.\"order\" FROM qd.child AS c ORDER BY c.\"order\" DESC",
            "SELECT p.id FROM qd.parent AS p WHERE (p.id = 1) OR (p.id = 3)",
            "SELECT p.id FROM qd.parent AS p WHERE (p.id > 0 AND p.name LIKE 'A%') OR (p.id = 3)",
            "SELECT c.id FROM qd.child AS c WHERE c.amount BETWEEN 10 AND 30",
            "SELECT c.id FROM qd.child AS c WHERE c.parent_id IS NULL",
            "SELECT c.id FROM qd.child AS c WHERE c.parent_id IN (1, 2)",
            "SELECT c.id FROM qd.child AS c WHERE c.amount > 1 AND c.amount < 20",
            "SELECT c.parent_id, SUM(c.amount) AS total FROM qd.child AS c GROUP BY c.parent_id",
            "SELECT c.parent_id, count(*) FROM qd.child AS c GROUP BY c.parent_id HAVING (count(*) > 1)",
            "SELECT c.parent_id, MAX(c.amount) FROM qd.child AS c WHERE c.id > 10 GROUP BY c.parent_id HAVING (MAX(c.amount) > 20)",
            "SELECT p.name, upper(p.name) AS loud, p.id + 1 AS next FROM qd.parent AS p",
            "SELECT p.id FROM qd.parent AS p ORDER BY p.name DESC, p.id",
            "SELECT p.* FROM qd.parent AS p",
            "SELECT * FROM qd.parent",
            "SELECT p.id, c.id FROM qd.parent AS p INNER JOIN qd.child AS c ON p.id = c.parent_id AND p.id = c.id",
        ];
        foreach (string sql in same)
        {
            var design = QueryDesignSql.Parse(sql, out string? error);
            if (design is null) { check(false, $"Readable by the panes: {sql} ({error})"); continue; }
            string written = QueryDesignSql.Generate(design);
            check(await Rows(sql) == await Rows(written), $"Means the same after the panes write it back: {sql}");
            check(QueryDesignSql.Parse(written, out _) is not null, $"What the panes write can be read again: {written.Replace('\n', ' ')}");
        }

        string[] other =
        [
            "SELECT id FROM qd.parent UNION SELECT id FROM qd.child",
            "WITH a AS (SELECT 1 AS x) SELECT x FROM a",
            "SELECT id FROM (SELECT id FROM qd.parent) AS s",
            "SELECT p.id FROM qd.parent AS p, qd.child AS c",
            "SELECT id FROM qd.parent LIMIT 1",
            "SELECT id FROM qd.parent OFFSET 1",
            "SELECT p.id FROM qd.parent AS p INNER JOIN qd.child AS c USING (id)",
            "SELECT p.id FROM qd.parent AS p INNER JOIN qd.child AS c ON p.id > c.id",
            "SELECT DISTINCT ON (id) id FROM qd.parent",
            "SELECT id FROM qd.parent -- note",
            "SELECT id /* note */ FROM qd.parent",
            "SELECT 1",
            "SELECT id FROM qd.parent WHERE id IN (SELECT parent_id FROM qd.child) OR",
            "SELECT id FROM qd.parent WHERE true",
            "SELECT id FROM qd.parent ORDER BY 1",
            "SELECT id FROM qd.parent ORDER BY id NULLS LAST",
            "SELECT id FROM qd.parent FOR UPDATE",
            "SELECT id FROM generate_series(1, 3) AS id",
            "SELECT id FROM qd.parent x y",
        ];
        foreach (string sql in other) check(QueryDesignSql.Parse(sql, out string? why) is null && why is { Length: > 0 }, $"Stays in the SQL pane, with the reason: {sql}");

        var viewDesign = QueryDesignSql.Parse("SELECT p.id FROM qd.parent AS p WHERE (p.id = 1) OR (p.id = 2)", out _)!;
        check(viewDesign.Criteria.Single().Filters.SequenceEqual(["= 1", "= 2"]), "Alternatives go to the Filter and Or columns of one row");
        var between = QueryDesignSql.Parse("SELECT c.id FROM qd.child AS c WHERE c.amount > 1 AND c.amount < 20", out _)!;
        check(between.Criteria.Count == 3 && between.Criteria.Skip(1).All(c => !c.Output), "Two conditions on one column take rows of their own");
        check(QueryDesignSql.Identifier("plain") == "plain" && QueryDesignSql.Identifier("order") == "\"order\"" && QueryDesignSql.Identifier("Mixed") == "\"Mixed\"", "Names are quoted only when they need it");

        check(QueryDesignSql.CheckSingleSelect("SELECT 1; DROP TABLE qd.parent") is { } two && two.Contains("one statement") && QueryDesignSql.CheckSingleSelect("SELECT 1;") is null && QueryDesignSql.CheckSingleSelect("DROP TABLE qd.parent") is not null && QueryDesignSql.CheckSingleSelect("SELECT ';' FROM qd.parent") is null, "Only one SELECT can run or be saved");

        var service = new QueryDesignService(connection);
        var source = await service.ReadSourceAsync("qd", "child");
        check(source is { Schema: "qd", Name: "child" } && source.Columns.Select(c => c.Name).SequenceEqual(["id", "parent_id", "amount", "order"]) && source.Columns[0].IsPrimaryKey && !source.Columns[1].IsPrimaryKey && source.Columns[2].Type == "numeric(10,2)", "A source is read with columns, types and primary key");
        check(await service.ReadSourceAsync("qd", "missing") is null, "A missing source is null");
        var parent = (await service.ReadSourceAsync("qd", "parent"))!;
        check((await service.ForeignKeysAsync([parent.Oid, source!.Oid])).Single() is { Columns: ["parent_id"], ReferencedColumns: ["id"] } link && link.From == source.Oid && link.To == parent.Oid, "Foreign keys between the sources are found");
        var result = await service.ExecuteAsync("SELECT p.id, p.\"Mixed Name\", now()::date - now()::date AS d FROM qd.parent AS p ORDER BY p.id");
        check(result.Table.Rows.Count == 3 && !result.Truncated && result.Table.Rows[1][1] is DBNull && result.Table.Columns[1].Caption == "Mixed Name", "Execute returns rows and NULLs");
        check((await service.ExecuteAsync("SELECT i FROM generate_series(1, 1500) AS i")) is { Truncated: true, Table.Rows.Count: QueryDesignService.MaxRows }, "Execute stops at the row limit and says so");
        string? refused = null;
        try { await service.ExecuteAsync("SELECT qd.write_row()"); } catch (PostgresException ex) { refused = ex.SqlState; }
        check(refused == "25006" && (await Rows("SELECT count(*) FROM qd.writes")) == "0", "Execute cannot write, even through a function");
        await service.VerifyAsync("SELECT id FROM qd.parent");
        string? syntax = null;
        try { await service.VerifyAsync("SELECT nope FROM qd.parent"); } catch (PostgresException ex) { syntax = ex.SqlState; }
        check(syntax == "42703", "Verify reports what PostgreSQL reports");
        // Query Designer (M1047): INSERT, UPDATE, DELETE and SELECT INTO come back meaning the same.
        async Task<string> After(string statement)
        {
            await using var transaction = await admin.BeginTransactionAsync();
            await using (var run = new NpgsqlCommand(statement, admin, transaction)) await run.ExecuteNonQueryAsync();
            string state = await Rows("SELECT 'child', id, parent_id, amount, \"order\" FROM qd.child UNION ALL SELECT 'parent', id, NULL, NULL, NULL FROM qd.parent UNION ALL SELECT 'target', a, b, NULL, NULL FROM qd.target");
            await transaction.RollbackAsync();
            return state;
        }
        await Sql("CREATE TABLE qd.target (a integer, b integer)");
        (string Sql, DesignStatement Kind)[] writes =
        [
            ("UPDATE qd.child SET amount = 1 WHERE id = 10", DesignStatement.Update),
            ("UPDATE qd.child SET amount = amount + 1, \"order\" = 9 WHERE (amount > 10) OR (parent_id IS NULL)", DesignStatement.Update),
            ("UPDATE qd.child AS c SET amount = 2 WHERE c.id > 11", DesignStatement.Update),
            ("UPDATE qd.child SET amount = 3 WHERE child.id = 12", DesignStatement.Update),
            ("update qd.child set amount = (select max(id) from qd.parent)", DesignStatement.Update),
            ("DELETE FROM qd.child WHERE id = 10", DesignStatement.Delete),
            ("DELETE FROM qd.child AS c WHERE c.amount > 20 AND c.id < 13", DesignStatement.Delete),
            ("DELETE FROM qd.child", DesignStatement.Delete),
            ("INSERT INTO qd.target (a, b) VALUES (1, 2)", DesignStatement.InsertValues),
            ("INSERT INTO qd.target (b, a) VALUES (1 + 1, DEFAULT)", DesignStatement.InsertValues),
            ("INSERT INTO qd.target SELECT id, parent_id FROM qd.child", DesignStatement.InsertResults),
            ("INSERT INTO qd.target (a, b) SELECT c.id, c.parent_id FROM qd.child AS c WHERE c.amount > 10 ORDER BY c.id", DesignStatement.InsertResults),
            ("SELECT c.id INTO qd.made FROM qd.child AS c WHERE c.id > 10", DesignStatement.MakeTable),
            ("SELECT id, name INTO made2 FROM qd.parent", DesignStatement.MakeTable),
        ];
        foreach (var (statement, kind) in writes)
        {
            var parsed = QueryDesignSql.Parse(statement, out string? error);
            check(parsed?.Statement == kind, $"{kind} is read: {statement} ({error})");
            if (parsed is null) continue;
            string generated = QueryDesignSql.Generate(parsed);
            check(await After(statement) == await After(generated), $"Written back it does the same: {generated}");
            check(QueryDesignSql.Parse(generated, out _)?.Statement == kind, $"What was written reads back: {generated}");
        }
        string[] notWrites =
        [
            "UPDATE qd.child SET amount = 1 FROM qd.parent WHERE parent.id = child.parent_id",
            "UPDATE qd.child SET amount = 1 RETURNING id",
            "UPDATE qd.child SET (amount, \"order\") = (1, 2)",
            "DELETE FROM qd.child USING qd.parent WHERE parent.id = child.parent_id",
            "DELETE FROM qd.child RETURNING id",
            "INSERT INTO qd.target VALUES (1, 2)",
            "INSERT INTO qd.target (a, b) VALUES (1, 2), (3, 4)",
            "INSERT INTO qd.target (a) VALUES (1, 2)",
            "INSERT INTO qd.target DEFAULT VALUES",
            "INSERT INTO qd.target (a, b) VALUES (1, 2) ON CONFLICT DO NOTHING",
            "INSERT INTO qd.target (a) SELECT id, parent_id FROM qd.child",
            "SELECT id INTO TEMP made3 FROM qd.child",
            "SELECT id FROM qd.child INTO made4",
            "SELECT id FROM qd.child WHERE id > 5\n\nSELECT 1 AS um",
            "SELECT",
            "SELECT id FROM qd.child GROUP",
            "UPDATE",
            "DELETE FROM",
            "INSERT INTO",
        ];
        foreach (string statement in notWrites) check(QueryDesignSql.Parse(statement, out string? why) is null && why is { Length: > 0 }, $"Stays in the SQL pane only: {statement}");
        foreach (string good in new[] { "SELECT 1", "WITH x AS (SELECT 1) SELECT * FROM x", "UPDATE t SET a = 1", "DELETE FROM t", "INSERT INTO t SELECT 1", "INSERT INTO t (a) VALUES (1) ON CONFLICT (a) DO UPDATE SET a = 2", "SELECT a FROM t UNION ALL SELECT b FROM u", "SELECT a FROM t FOR UPDATE", "WITH x AS (SELECT 1) DELETE FROM t", "SELECT 1;" })
            check(QueryDesignSql.CanOpenDesigner(good), "The designer opens on: " + good);
        foreach (string bad in new[] { "", "   ", "WHERE a > 1", "FROM t", "CREATE TABLE t (a int)", "DROP TABLE t", "SELECT 1; SELECT 2", "SELECT a FROM t\n\nSELECT 1 AS um", "UPDATE t SET a = 1 DELETE FROM t" })
            check(!QueryDesignSql.CanOpenDesigner(bad), "The designer refuses: " + bad);
        // The statement under the caret (user's request: no selection needed).
        string Under(string text, int caret) => QueryDesignSql.StatementAt(text, caret) is { } s ? text.Substring(s.Start, s.Length) : "<none>";
        string script = "-- note\nSELECT 1\nFROM a\nWHERE x = ';'\n\nSELECT 2; SELECT 3;\n\nSELECT (\n\n4)\n";
        check(Under(script, script.IndexOf("FROM", StringComparison.Ordinal)) == "SELECT 1\nFROM a\nWHERE x = ';'", "A semicolon in a string does not end the statement, the blank line does");
        check(Under(script, script.IndexOf("SELECT 1", StringComparison.Ordinal)) == "SELECT 1\nFROM a\nWHERE x = ';'", "The comment above is not part of the statement");
        check(Under(script, script.IndexOf("2;", StringComparison.Ordinal)) == "SELECT 2;" && Under(script, script.IndexOf("3;", StringComparison.Ordinal)) == "SELECT 3;", "Statements on one line are told apart by the semicolon");
        check(Under(script, script.IndexOf("4)", StringComparison.Ordinal)) == "SELECT (\n\n4)", "A blank line inside parentheses does not end the statement");
        check(Under(script, script.IndexOf("';'", StringComparison.Ordinal) + 3) == "SELECT 1\nFROM a\nWHERE x = ';'", "At the end of the last line the caret is still on the statement");
        check(Under(script, script.IndexOf("';'", StringComparison.Ordinal) + 4) == "<none>" && Under(script, script.Length) == "<none>" && Under("", 0) == "<none>" && Under("-- only\n", 0) == "<none>", "On an empty line or a comment there is no statement");

        foreach (string good in new[] { "UPDATE qd.child SET amount = 1", "DELETE FROM qd.child", "INSERT INTO qd.target (a, b) VALUES (1, 2)", "SELECT id INTO qd.never FROM qd.child" })
        {
            await service.VerifyAsync(good, allowWrites: true);
            check(await Rows("SELECT count(*), to_regclass('qd.never') FROM qd.child") == "4|NULL", "Verify checks without running: " + good);
        }
        string? notAllowed = null;
        try { await service.VerifyAsync("UPDATE qd.child SET amount = 1"); } catch (ArgumentException ex) { notAllowed = ex.Message; }
        check(notAllowed is not null, "The View Designer's Verify still takes only SELECT");

        await Sql("CREATE TABLE qd.tree (id integer PRIMARY KEY, up integer REFERENCES qd.tree(id)); CREATE TABLE qd.leaf (id integer PRIMARY KEY, child_id integer REFERENCES qd.child(id), other_id integer REFERENCES qd.child(id))");
        var childInfo = (await service.ReadSourceAsync("qd", "child"))!;
        check((await service.RelatedTablesAsync(childInfo.Oid)).Select(r => r.Name).SequenceEqual(["leaf", "parent"]), "Related tables come from foreign keys in both directions, each once");
        check((await service.RelatedTablesAsync((await service.ReadSourceAsync("qd", "tree"))!.Oid)).Count == 0, "A table is not related to itself");
        await Sql("DROP SCHEMA qd CASCADE");
    }
}
