using Npgsql;
using PGMS.PostgreSql;

// PGMS-039: foreign keys without an index, read from the catalog, and the statements that create the indexes.
internal static class ForeignKeyIndexScanChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static ScanIndex I(string name, string?[] columns, bool valid = true, bool partial = false, string method = "btree") => new(name, columns, valid, partial, method);
        static ScanForeignKey K(string table, string[] columns, params ScanIndex[] indexes) => new("public", table, "fk", columns, "public", "parent", false, 0, 0, null, null, indexes);
        string[] one = ["a"], two = ["a", "b"];
        check(ForeignKeyIndexScan.Covers(one, I("i", ["a"])) && ForeignKeyIndexScan.Covers(one, I("i", ["a", "z"])) && ForeignKeyIndexScan.Covers(one, I("i", ["a"], method: "hash")), "An index that starts with the column covers the key");
        check(ForeignKeyIndexScan.Covers(two, I("i", ["b", "a"])) && ForeignKeyIndexScan.Covers(two, I("i", ["b", "a", "z"])), "The columns of a composite key may come in any order, as long as they come first");
        check(!ForeignKeyIndexScan.Covers(one, I("i", ["z", "a"])) && !ForeignKeyIndexScan.Covers(two, I("i", ["a"])) && !ForeignKeyIndexScan.Covers(two, I("i", ["a", "z", "b"])) && !ForeignKeyIndexScan.Covers(two, I("i", ["a", "a"])), "Columns that are not first, or only part of the key, do not cover it");
        check(!ForeignKeyIndexScan.Covers(one, I("i", ["a"], valid: false)) && !ForeignKeyIndexScan.Covers(one, I("i", ["a"], partial: true)) && !ForeignKeyIndexScan.Covers(one, I("i", ["a"], method: "gin")) && !ForeignKeyIndexScan.Covers(one, I("i", [null])), "Invalid, partial, non b-tree and expression indexes do not cover it");
        check(ForeignKeyIndexScan.NearReason(one, I("i", ["a"], partial: true)) == "i is partial" && ForeignKeyIndexScan.NearReason(one, I("i", ["a"], valid: false)) == "i is invalid" && ForeignKeyIndexScan.NearReason(one, I("i", ["a"], method: "gist")) == "i is gist, not b-tree"
            && ForeignKeyIndexScan.NearReason(one, I("i", ["z", "a"])) == "i has the columns, but not first" && ForeignKeyIndexScan.NearReason(two, I("i", ["a"])) == "i covers only part of the key" && ForeignKeyIndexScan.NearReason(one, I("i", ["z"])) is null, "An index that almost covers the key says why it does not");

        var none = new HashSet<(string, string)>();
        var found = ForeignKeyIndexScan.FindUnindexed([K("t1", one, I("i", ["a"])), K("t2", one, I("p", ["a"], partial: true), I("o", ["z"])), K("t3", two), K("t3", two)], none);
        check(found.Count == 3 && found[0] is { Key.Table: "t2", IndexName: "ix_t2_a", NearIndexes: ["p is partial"] } && found[1].IndexName == "ix_t3_a_b" && found[2].IndexName == "ix_t3_a_b", "Covered keys are left out; two keys on the same columns share one index");
        check(ForeignKeyIndexScan.Statements(found, true).SequenceEqual(["CREATE INDEX CONCURRENTLY \"ix_t2_a\" ON \"public\".\"t2\" (\"a\");", "CREATE INDEX CONCURRENTLY \"ix_t3_a_b\" ON \"public\".\"t3\" (\"a\", \"b\");"]) && ForeignKeyIndexScan.Statements(found, false)[0] == "CREATE INDEX \"ix_t2_a\" ON \"public\".\"t2\" (\"a\");", "One statement per index, with and without CONCURRENTLY");
        var taken = ForeignKeyIndexScan.FindUnindexed([K("t2", one), K("t2", ["b"])], new HashSet<(string, string)> { ("public", "ix_t2_a"), ("public", "ix_t2_a_1"), ("other", "ix_t2_b") });
        check(taken[0].IndexName == "ix_t2_a_2" && taken[1].IndexName == "ix_t2_b", "A name already used in the schema gets a number");
        var longKeys = ForeignKeyIndexScan.FindUnindexed([K(new string('t', 60), [new string('c', 60)]), K(new string('t', 60), [new string('c', 59) + "d"])], none);
        check(longKeys.All(k => System.Text.Encoding.UTF8.GetByteCount(k.IndexName) <= 63) && longKeys[0].IndexName != longKeys[1].IndexName, "Long index names fit and stay distinct");
        var quoted = ForeignKeyIndexScan.FindUnindexed([new("a b", "t\"x", "fk", ["c 1"], "s", "p", true, 0, 0, null, null, [])], none)[0];
        check(ForeignKeyIndexScan.Statement(quoted, true) == "CREATE INDEX \"ix_t\"\"x_c 1\" ON \"a b\".\"t\"\"x\" (\"c 1\");", "Names are quoted and a partitioned table never gets CONCURRENTLY: " + ForeignKeyIndexScan.Statement(quoted, true));

        string schema = "qfki_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<long> Count(string sql) { await using var command = new NpgsqlCommand(sql, admin); return (long)(await command.ExecuteScalarAsync())!; }
        await Sql($"""
            CREATE SCHEMA {schema};
            CREATE TABLE {schema}.pai(id int PRIMARY KEY, a int, b int, UNIQUE (a, b));
            CREATE TABLE {schema}.sem(id int PRIMARY KEY, pai_id int REFERENCES {schema}.pai(id), apagada int, outro int);
            ALTER TABLE {schema}.sem DROP COLUMN apagada;
            CREATE TABLE {schema}.com(id int PRIMARY KEY, pai_id int REFERENCES {schema}.pai(id)); CREATE INDEX ON {schema}.com(pai_id);
            CREATE TABLE {schema}.pk_serve(pai_id int REFERENCES {schema}.pai(id), n int, PRIMARY KEY (pai_id, n));
            CREATE TABLE {schema}.composta(id int PRIMARY KEY, a int, b int, FOREIGN KEY (a, b) REFERENCES {schema}.pai(a, b)); CREATE INDEX composta_b_a ON {schema}.composta(b, a);
            CREATE TABLE {schema}.composta_meia(id int PRIMARY KEY, a int, b int, CONSTRAINT meia_fk FOREIGN KEY (a, b) REFERENCES {schema}.pai(a, b)); CREATE INDEX meia_a ON {schema}.composta_meia(a);
            CREATE TABLE {schema}.parcial(id int PRIMARY KEY, pai_id int REFERENCES {schema}.pai(id)); CREATE INDEX parcial_ix ON {schema}.parcial(pai_id) WHERE pai_id > 0;
            CREATE TABLE {schema}.depois(id int PRIMARY KEY, pai_id int REFERENCES {schema}.pai(id)); CREATE INDEX depois_ix ON {schema}.depois(id, pai_id);
            CREATE TABLE {schema}.expressao(id int PRIMARY KEY, pai_id int REFERENCES {schema}.pai(id)); CREATE INDEX expressao_ix ON {schema}.expressao((pai_id + 0));
            CREATE TABLE {schema}.incluida(id int PRIMARY KEY, pai_id int REFERENCES {schema}.pai(id)); CREATE INDEX incluida_ix ON {schema}.incluida(id) INCLUDE (pai_id);
            CREATE TABLE {schema}.parts(id int, pai_id int REFERENCES {schema}.pai(id), PRIMARY KEY (id)) PARTITION BY RANGE (id);
            CREATE TABLE {schema}.parts_1 PARTITION OF {schema}.parts FOR VALUES FROM (0) TO (1000);
            CREATE TABLE {schema}.parts_ok(id int, pai_id int REFERENCES {schema}.pai(id)) PARTITION BY RANGE (id);
            CREATE TABLE {schema}.parts_ok_1 PARTITION OF {schema}.parts_ok FOR VALUES FROM (0) TO (1000); CREATE INDEX ON {schema}.parts_ok(pai_id);
            CREATE TABLE {schema}.dupla(id int PRIMARY KEY, pai_id int, CONSTRAINT d1 FOREIGN KEY (pai_id) REFERENCES {schema}.pai(id), CONSTRAINT d2 FOREIGN KEY (pai_id) REFERENCES {schema}.com(id));
            CREATE TABLE {schema}.ix_sem_outro(id int);
            ALTER TABLE {schema}.sem ADD CONSTRAINT sem_outro FOREIGN KEY (outro) REFERENCES {schema}.pai(id);
            INSERT INTO {schema}.pai SELECT g, g, g FROM generate_series(1, 50) g;
            INSERT INTO {schema}.sem SELECT g, 1 + g % 50, NULL FROM generate_series(1, 500) g;
            INSERT INTO {schema}.parts SELECT g, 1 FROM generate_series(1, 300) g;
            ANALYZE {schema}.sem; ANALYZE {schema}.parts_1;
            """);
        try
        {
            var (keys, names) = await ForeignKeyIndexScan.ReadAsync(connection);
            var mine = keys.Where(k => k.Schema == schema).ToList();
            check(mine.Count == 14 && !mine.Any(k => k.Table is "parts_1" or "parts_ok_1") && !keys.Any(k => k.Schema.StartsWith("pg_", StringComparison.Ordinal)), $"Every foreign key is read once; the copies on partitions are left out: {mine.Count}");
            check(names.Contains((schema, "composta_b_a")) && names.Contains((schema, "pai")), "Relation names are read, to keep new index names free");
            var unindexed = ForeignKeyIndexScan.FindUnindexed(mine, names);
            UnindexedForeignKey Of(string table, string? name = null) => unindexed.Single(k => k.Key.Table == table && (name is null || k.Key.Name == name));
            check(unindexed.Select(k => k.Key.Table).Distinct().Order(StringComparer.Ordinal).SequenceEqual(["composta_meia", "depois", "dupla", "expressao", "incluida", "parcial", "parts", "sem"]), "Keys with an index (plain, primary key prefix, composite in another order, partitioned) are left out: " + string.Join(",", unindexed.Select(k => k.Key.Table)));
            var sem = Of("sem", "sem_pai_id_fkey");
            check(sem is { Key.Columns: ["pai_id"], Key.TargetTable: "pai", Key.Partitioned: false, IndexName: "ix_sem_pai_id", NearIndexes.Count: 0 } && sem.Key.TableBytes > 0 && sem.Key.EstimatedRows == 500, $"A key without an index shows its table, size and row estimate: {sem.Key.TableBytes} bytes, {sem.Key.EstimatedRows} rows");
            check(Of("sem", "sem_outro").IndexName == "ix_sem_outro_1", "A relation with the wanted name makes the index name change");
            check(Of("parcial").NearIndexes.SequenceEqual(["parcial_ix is partial"]) && Of("depois").NearIndexes.SequenceEqual(["depois_ix has the columns, but not first"]) && Of("composta_meia").NearIndexes.SequenceEqual(["meia_a covers only part of the key"]), "Indexes that almost cover the key are named with the reason");
            check(Of("expressao").NearIndexes.Count == 0 && Of("incluida").NearIndexes.Count == 0, "An expression and an INCLUDE column are not key columns");
            var parts = Of("parts");
            check(parts is { Key.Partitioned: true } && parts.Key.TableBytes > 0 && parts.Key.EstimatedRows == 300, $"A partitioned table shows the size and rows of its partitions: {parts.Key.TableBytes} bytes, {parts.Key.EstimatedRows} rows");
            check(Of("dupla", "d1").IndexName == Of("dupla", "d2").IndexName && ForeignKeyIndexScan.Statements([Of("dupla", "d1"), Of("dupla", "d2")], true).Count == 1, "Two keys on the same column ask for one index");

            // In one transaction: a failure creates nothing.
            await Sql($"CREATE TABLE {schema}.\"ix_depois_pai_id\"(x int)");
            var refused = await ForeignKeyIndexScan.ApplyAsync(connection, [sem, Of("depois")], false);
            check(refused is { Created: 0, Error: not null, InvalidIndex: null } && await Count($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{schema}' AND indexname = 'ix_sem_pai_id'") == 0, $"In one transaction a failure creates nothing: {refused.Error}");
            // One at a time: the ones before the failure stay.
            var partly = await ForeignKeyIndexScan.ApplyAsync(connection, [sem, Of("depois"), Of("parcial")], true);
            check(partly is { Created: 1, Error: not null } && partly.Error.StartsWith("42P07", StringComparison.Ordinal) && await Count($"SELECT count(*) FROM pg_index WHERE indexrelid = '{schema}.ix_sem_pai_id'::regclass AND indisvalid") == 1 && await Count($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{schema}' AND indexname = 'ix_parcial_pai_id'") == 0, $"One at a time, the indexes before a failure stay and the ones after are not tried: {partly}");
            await Sql($"DROP TABLE {schema}.\"ix_depois_pai_id\"");

            // A concurrent build that fails leaves an invalid index, and the result names it.
            await Sql($"CREATE TABLE {schema}.lenta(id int PRIMARY KEY, pai_id int REFERENCES {schema}.pai(id)); INSERT INTO {schema}.lenta VALUES (1, 1)");
            var lenta = ForeignKeyIndexScan.FindUnindexed((await ForeignKeyIndexScan.ReadAsync(connection)).Keys.Where(k => k.Schema == schema && k.Table == "lenta").ToList(), names).Single();
            await using (var blocker = new NpgsqlConnection(connection))
            {
                await blocker.OpenAsync();
                await using (var hold = new NpgsqlCommand($"BEGIN; INSERT INTO {schema}.lenta VALUES (2, 1)", blocker)) await hold.ExecuteNonQueryAsync();
                var building = ForeignKeyIndexScan.ApplyAsync(connection, [lenta], true);
                for (int i = 0; i < 100 && await Count($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{schema}' AND indexname = 'ix_lenta_pai_id'") == 0; i++) await Task.Delay(50);
                // The build waits for the open transaction; ending its session is how it fails halfway.
                await Sql($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query LIKE 'CREATE INDEX CONCURRENTLY \"ix_lenta_pai_id\"%' AND pid <> pg_backend_pid()");
                var cut = await building;
                check(cut is { Created: 0, Error: not null, InvalidIndex: "ix_lenta_pai_id" } && cut.InvalidIndexSchema == schema && await Count($"SELECT count(*) FROM pg_index WHERE indexrelid = '{schema}.ix_lenta_pai_id'::regclass AND NOT indisvalid") == 1, $"A concurrent build cut halfway leaves an invalid index, and the result names it: {cut}");
                await using (var back = new NpgsqlCommand("ROLLBACK", blocker)) await back.ExecuteNonQueryAsync();
            }
            var afterBreak = (await ForeignKeyIndexScan.ReadAsync(connection));
            var lentaAgain = ForeignKeyIndexScan.FindUnindexed(afterBreak.Keys.Where(k => k.Schema == schema && k.Table == "lenta").ToList(), afterBreak.RelationNames).Single();
            check(lentaAgain.NearIndexes.SequenceEqual(["ix_lenta_pai_id is invalid"]) && lentaAgain.IndexName == "ix_lenta_pai_id_1", "The invalid index does not cover the key, and the scan says so");
            await Sql(ForeignKeyIndexScan.DropInvalid(schema, "ix_lenta_pai_id"));
            check(await Count($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{schema}' AND indexname = 'ix_lenta_pai_id'") == 0, "The suggested DROP INDEX removes the invalid index");

            // Everything that is left, concurrently; the partitioned table goes without CONCURRENTLY.
            var rest = ForeignKeyIndexScan.FindUnindexed((await ForeignKeyIndexScan.ReadAsync(connection)).Keys.Where(k => k.Schema == schema).ToList(), (await ForeignKeyIndexScan.ReadAsync(connection)).RelationNames);
            var all = await ForeignKeyIndexScan.ApplyAsync(connection, rest, true);
            check(all.Error is null && all.Created == ForeignKeyIndexScan.Statements(rest, true).Count && await Count($"SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE c.relnamespace = '{schema}'::regnamespace AND NOT i.indisvalid") == 0, $"The rest is created, all valid: {all}");
            check(await Count($"SELECT count(*) FROM pg_index WHERE indrelid = '{schema}.parts_1'::regclass AND NOT indisprimary") == 1, "The index on a partitioned table reaches its partitions");
            var final = await ForeignKeyIndexScan.ReadAsync(connection);
            check(ForeignKeyIndexScan.FindUnindexed(final.Keys.Where(k => k.Schema == schema).ToList(), final.RelationNames).Count == 0, "After that, no key of the schema is without an index");
        }
        finally { await Sql($"DROP SCHEMA {schema} CASCADE"); }
    }
}
