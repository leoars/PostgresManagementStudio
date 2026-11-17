using Npgsql;
using PGMS.PostgreSql;

/// <summary>PGMS-075 (M1102): Schema Compare between two databases created here, in the disposable cluster. The comparison only reads; the test itself
/// runs the generated script on the target (the application never does) and checks that a second comparison finds nothing and the rows survived.</summary>
internal static class SchemaCompareChecks
{
    private const string Source = """
        CREATE SCHEMA app; CREATE SCHEMA only_src;
        CREATE TYPE app.mood AS ENUM ('sad', 'ok', 'happy');
        CREATE TYPE app.pair AS (a integer, b text);
        CREATE DOMAIN app.positive AS integer CHECK (VALUE > 0);
        CREATE SEQUENCE app.counter INCREMENT BY 5;
        CREATE TABLE app.customers (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL, email varchar(200), mood app.mood DEFAULT 'ok', score app.positive, created timestamptz DEFAULT now());
        CREATE TABLE app.orders (id bigserial PRIMARY KEY, customer integer NOT NULL REFERENCES app.customers(id), total numeric(10,2) CHECK (total >= 0));
        CREATE INDEX orders_customer ON app.orders (customer);
        CREATE TABLE app.events (id integer, at date NOT NULL) PARTITION BY RANGE (at);
        CREATE TABLE app.events_2026 PARTITION OF app.events FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
        CREATE VIEW app.big_orders AS SELECT id, total FROM app.orders WHERE total > 100;
        CREATE MATERIALIZED VIEW app.totals AS SELECT customer, sum(total) AS total FROM app.orders GROUP BY customer;
        CREATE FUNCTION app.touch() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN NEW.created := now(); RETURN NEW; END$$;
        CREATE TRIGGER customers_touch BEFORE UPDATE ON app.customers FOR EACH ROW EXECUTE FUNCTION app.touch();
        CREATE FUNCTION app.double(x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS 'SELECT x * 2';
        CREATE PROCEDURE app.reset() LANGUAGE sql AS 'SELECT 1';
        CREATE TABLE only_src.notes (body text);
        """;
    private const string Target = """
        CREATE SCHEMA app; CREATE SCHEMA only_tgt;
        CREATE TYPE app.mood AS ENUM ('sad', 'happy');
        CREATE TYPE app.pair AS (a integer);
        CREATE DOMAIN app.positive AS integer CHECK (VALUE >= 0);
        CREATE SEQUENCE app.counter INCREMENT BY 1;
        CREATE TABLE app.customers (id integer PRIMARY KEY, name text, email varchar(100), legacy text, created timestamptz);
        CREATE TABLE app.orders (id bigserial PRIMARY KEY, customer integer NOT NULL, total numeric(10,2));
        CREATE TABLE app.old_stuff (x integer);
        CREATE INDEX old_stuff_x ON app.old_stuff (x);
        CREATE VIEW app.big_orders AS SELECT id, total FROM app.orders WHERE total > 50;
        CREATE FUNCTION app.double(x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS 'SELECT x + x';
        CREATE FUNCTION app.gone() RETURNS integer LANGUAGE sql AS 'SELECT 1';
        CREATE TABLE only_tgt.stuff (a integer);
        INSERT INTO app.customers VALUES (1, 'kept', 'kept@example.invalid', 'old', now());
        INSERT INTO app.orders (customer, total) VALUES (1, 5);
        """;

    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8], source = "pgms_cmp_src_" + suffix, target = "pgms_cmp_tgt_" + suffix;
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(NpgsqlConnection c, string sql) { await using var command = new NpgsqlCommand(sql, c); await command.ExecuteNonQueryAsync(); }
        string On(string db) => new NpgsqlConnectionStringBuilder(connection) { Database = db, Pooling = false }.ConnectionString;
        async Task In(string db, string sql) { await using var c = new NpgsqlConnection(On(db)); await c.OpenAsync(); await Sql(c, sql); }
        async Task<object?> Scalar(string db, string sql) { await using var c = new NpgsqlConnection(On(db)); await c.OpenAsync(); await using var command = new NpgsqlCommand(sql, c); return await command.ExecuteScalarAsync(); }
        var created = new List<string>();
        try
        {
            foreach (var db in new[] { source, target }) { await Sql(admin, $"CREATE DATABASE {db}"); created.Add(db); }
            await In(source, Source); await In(target, Target);

            var same = SchemaCompare.Compare(await SchemaCompare.ReadAsync(On(source)), await SchemaCompare.ReadAsync(On(source)));
            check(same.Count == 0 && SchemaCompare.Summary(same) == "Comparison completed. No differences detected.", "A database compared with itself has no differences");

            var left = await SchemaCompare.ReadAsync(On(source)); var right = await SchemaCompare.ReadAsync(On(target));
            check(left.Database == source && left.Objects.All(o => o.Schema is not ("pg_catalog" or "information_schema")), "The snapshot names the database and leaves the system schemas out");
            var differences = SchemaCompare.Compare(left, right);
            string Row(SchemaDifference d) => $"{d.Action} {d.Type} {(d.Source ?? d.Target)!.DisplayName}";
            var rows = differences.Select(Row).ToHashSet();
            string[] expected =
            [
                "Add Schema only_src", "Add Table only_src.notes", "Add Table app.events", "Add Table app.events_2026", "Add MaterializedView app.totals", "Add Function app.touch()",
                "Add Trigger app.customers.customers_touch", "Add Procedure app.reset()", "Add Index app.orders.orders_customer", "Add Constraint app.orders.orders_customer_fkey", "Add Constraint app.orders.orders_total_check",
                "Change Type app.mood", "Change Type app.pair", "Change Domain app.positive", "Change Sequence app.counter", "Change Table app.customers", "Change View app.big_orders", "Change Function app.double(x integer)",
                "Delete Schema only_tgt", "Delete Table only_tgt.stuff", "Delete Table app.old_stuff", "Delete Index app.old_stuff.old_stuff_x", "Delete Function app.gone()",
            ];
            check(rows.SetEquals(expected), "Every Add, Change and Delete is found, and nothing else: missing " + string.Join(", ", expected.Except(rows)) + "; extra " + string.Join(", ", rows.Except(expected)));
            check(SchemaCompare.Summary(differences) == $"Comparison completed. Found {expected.Length} differences (11 Add, 7 Change, 5 Delete).", "The summary counts like SSMS: " + SchemaCompare.Summary(differences));
            check(differences.Select(d => (int)d.Type).SequenceEqual(differences.Select(d => (int)d.Type).Order()), "The differences are ordered by type");
            var customers = differences.Single(d => d.Type == SchemaObjectType.Table && d.Action == SchemaDifferenceAction.Change);
            check(customers.Source!.Definition.StartsWith("CREATE TABLE app.customers (\r\n    id integer GENERATED ALWAYS AS IDENTITY,", StringComparison.Ordinal) && customers.Target!.Definition.Contains("legacy text", StringComparison.Ordinal),
                "A table is shown as CREATE TABLE with its columns in order: " + customers.Source.Definition);

            string script = SchemaCompare.GenerateScript(left, right, differences, new DateTime(2026, 11, 16, 12, 0, 0));
            Directory.CreateDirectory("artifacts/schema-compare");
            await File.WriteAllTextAsync("artifacts/schema-compare/last-script.sql", script); // kept for a failure to be read
            check(script.StartsWith("/*\r\nSchema Compare script for ", StringComparison.Ordinal) && script.Contains("PGMS did not run it", StringComparison.Ordinal) && script.Contains("Generated by PGMS on 2026-11-16 12:00:00", StringComparison.Ordinal), "The script starts with a header that says it was not run");
            int enumAt = script.IndexOf("ALTER TYPE app.mood ADD VALUE 'ok' BEFORE 'happy';", StringComparison.Ordinal), beginAt = script.IndexOf("\r\nBEGIN;\r\n", StringComparison.Ordinal);
            check(enumAt > 0 && enumAt < beginAt && script.TrimEnd().EndsWith("COMMIT;", StringComparison.Ordinal), "The new enum value comes before BEGIN, in its place, and the script ends with COMMIT");
            int At(string text) { int i = script.IndexOf(text, StringComparison.Ordinal); check(i > 0, "The script has: " + text); return i; }
            check(At("DROP INDEX app.old_stuff_x;") < At("DROP TABLE app.old_stuff;") && At("DROP TABLE only_tgt.stuff;") < At("DROP SCHEMA only_tgt;"), "Drops go from the dependent objects to the schema");
            check(At("-- Data loss: the values of column legacy go with it.") > 0 && At("ALTER TABLE app.customers DROP COLUMN legacy;") > 0, "A dropped column is marked as data loss");
            check(At("ALTER TABLE app.customers ALTER COLUMN email TYPE character varying(200);") > 0 && At("ALTER TABLE app.customers ALTER COLUMN name SET NOT NULL;") > 0
                && At("ALTER TABLE app.customers ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY;") > 0 && At("ALTER TABLE app.customers ALTER COLUMN created SET DEFAULT now();") > 0
                && At("ALTER TABLE app.customers ADD COLUMN mood app.mood DEFAULT 'ok'::app.mood;") > 0, "Table changes are column by column, keeping the rows");
            check(At("ALTER SEQUENCE app.counter AS bigint INCREMENT BY 5") > 0 && At("DROP DOMAIN app.positive;") < At("CREATE DOMAIN app.positive AS integer") && At("CREATE OR REPLACE VIEW app.big_orders AS") > 0, "Sequences are altered, domains recreated and views replaced");
            check(At("CREATE TABLE app.events (") < At("CREATE TABLE app.events_2026 PARTITION OF app.events") && At("CREATE OR REPLACE FUNCTION app.touch()") < At("CREATE TRIGGER customers_touch") && At("ADD CONSTRAINT orders_total_check") < At("ADD CONSTRAINT orders_customer_fkey"),
                "Creates go in dependency order: parent before partition, function before trigger, foreign keys last");
            check(At("REFRESH MATERIALIZED VIEW app.totals") > At("CREATE MATERIALIZED VIEW app.totals AS"), "A new materialized view is created empty, with a hint to fill it");
            check(Convert.ToInt32(await Scalar(target, "SELECT count(*) FROM pg_namespace WHERE nspname = 'only_src'")) == 0, "Comparing and generating the script changed nothing in the target");

            // Exclude one row: its statement is left out.
            var old = differences.Single(d => Row(d) == "Delete Table app.old_stuff");
            old.Include = false;
            string partial = SchemaCompare.GenerateScript(left, right, differences);
            check(!partial.Contains("DROP TABLE app.old_stuff;", StringComparison.Ordinal) && partial.Contains("DROP INDEX app.old_stuff_x;", StringComparison.Ordinal), "An excluded row is not scripted; the others still are");
            old.Include = true;
            foreach (var d in differences) d.Include = false;
            check(SchemaCompare.GenerateScript(left, right, differences).Contains("-- No differences are included.", StringComparison.Ordinal), "With nothing included the script says so and has no statement");
            foreach (var d in differences) d.Include = true;

            // Switched direction: what was added is now deleted, and the other way round.
            var back = SchemaCompare.Compare(right, left).Select(Row).ToHashSet();
            check(back.Contains("Add Schema only_tgt") && back.Contains("Delete Schema only_src") && back.Contains("Change Table app.customers") && back.Count == expected.Length, "Switching the direction swaps Add and Delete");

            // The test runs the script, as the user would after reviewing it: the part before BEGIN on its own, then the transaction.
            await In(target, script[..beginAt]);
            await In(target, script[beginAt..]);
            var after = SchemaCompare.Compare(left, await SchemaCompare.ReadAsync(On(target)));
            check(after.Count == 0, "After the script the target matches the source: " + string.Join(", ", after.Select(Row)));
            check((string?)await Scalar(target, "SELECT name FROM app.customers WHERE id = 1") == "kept" && Convert.ToInt32(await Scalar(target, "SELECT count(*) FROM app.orders")) == 1, "The rows of the changed tables are kept");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            foreach (var db in created) await Sql(admin, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
        }
    }
}
