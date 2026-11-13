using Npgsql;
using PGMS.PostgreSql;

internal static class SchemaContextChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string schema = "ai_ctx_" + Guid.NewGuid().ToString("N")[..8];
        const string Secret = "row-value-that-must-never-leave-7f3a";
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        try
        {
            await Sql($"""
                CREATE SCHEMA {schema};
                CREATE TYPE {schema}.order_status AS ENUM ('open', 'paid', 'void');
                CREATE TABLE {schema}.customers (id integer PRIMARY KEY, name text NOT NULL, email varchar(200) UNIQUE);
                COMMENT ON TABLE {schema}.customers IS 'People who buy';
                COMMENT ON COLUMN {schema}.customers.email IS 'Contact address';
                CREATE TABLE {schema}.orders (id bigint PRIMARY KEY, customer_id integer NOT NULL REFERENCES {schema}.customers(id), status {schema}.order_status NOT NULL, total numeric(12,2));
                CREATE TABLE {schema}."Mixed Case" ("Some Column" int);
                CREATE VIEW {schema}.open_orders AS SELECT id, total FROM {schema}.orders WHERE status = 'open';
                INSERT INTO {schema}.customers VALUES (1, '{Secret}', '{Secret}@example.com');
                """);
            var service = new SchemaContextService(connection);
            var context = await service.BuildAsync("total by customer");
            string text = context.Text;
            check(text.Contains($"TABLE {schema}.customers -- People who buy", StringComparison.Ordinal) && text.Contains("email character varying(200) -- Contact address", StringComparison.Ordinal)
                && text.Contains("name text NOT NULL", StringComparison.Ordinal), "Schema context lists tables, columns, types, NOT NULL and comments");
            check(text.Contains("PRIMARY KEY (id)", StringComparison.Ordinal) && text.Contains($"FOREIGN KEY (customer_id) REFERENCES {schema}.customers(id)", StringComparison.Ordinal) && text.Contains("UNIQUE (email)", StringComparison.Ordinal), "Schema context includes primary, unique and foreign keys");
            check(text.Contains($"ENUM {schema}.order_status ('open', 'paid', 'void')", StringComparison.Ordinal) && text.Contains($"status {schema}.order_status NOT NULL", StringComparison.Ordinal), "Schema context includes enum labels and enum-typed columns");
            check(text.Contains($"VIEW {schema}.open_orders", StringComparison.Ordinal) && text.Contains($"TABLE {schema}.\"Mixed Case\"", StringComparison.Ordinal) && text.Contains("\"Some Column\" integer", StringComparison.Ordinal), "Schema context includes views and quotes only identifiers that need it");
            check(!text.Contains(Secret, StringComparison.Ordinal), "Schema context never contains table data");
            check(!text.Contains("pg_catalog.pg_class", StringComparison.Ordinal) && !text.Contains("information_schema.", StringComparison.Ordinal) && text.StartsWith("-- PostgreSQL ", StringComparison.Ordinal), "System catalogs are excluded and the server version is stated");

            // A tight budget keeps relations named in the request and lists the rest by name.
            var small = await service.BuildAsync("show open_orders", maximumCharacters: 600);
            check(small.Text.Contains($"VIEW {schema}.open_orders", StringComparison.Ordinal) && small.OmittedRelations > 0 && small.Text.Contains("more relations omitted for size", StringComparison.Ordinal), "Over budget, relations named in the request come first and the rest are listed by name");

            // A database that does not fit: 200 filler tables sort before the ones the requests are about.
            await Sql($"""
                DO $$ BEGIN FOR i IN 0..199 LOOP
                  EXECUTE format('CREATE TABLE {schema}.filler_%s (id int, code text, label text, amount numeric, created date, flag boolean)', lpad(i::text, 3, '0'));
                END LOOP; END $$;
                CREATE TABLE {schema}.zz_ledger (id integer PRIMARY KEY, "valorTotal" numeric, memo text);
                COMMENT ON TABLE {schema}.zz_ledger IS 'Lançamentos contábeis';
                CREATE TABLE {schema}.zz_chart (id integer PRIMARY KEY, title text);
                CREATE TABLE {schema}.zz_ledger_lines (id integer PRIMARY KEY, chart_id integer REFERENCES {schema}.zz_chart(id));
                INSERT INTO {schema}.zz_chart VALUES (1, '{Secret}');
                """);
            var catalog = await service.LoadAsync();
            const int Tight = 3000;
            bool Detailed(SchemaContext c, string table) => c.Text.Contains($"TABLE {schema}.{table}\n", StringComparison.Ordinal) || c.Text.Contains($"TABLE {schema}.{table} -- ", StringComparison.Ordinal);
            check(!catalog.Fits(Tight) && !catalog.Fits(16_000) && catalog.Fits(SchemaContext.MaximumCharacters) && catalog.Relations >= 207, "The catalog says whether the whole structure fits a limit");
            var unrelated = catalog.Build("xyzzy", Tight);
            check(!Detailed(unrelated, "zz_ledger") && !Detailed(unrelated, "zz_chart") && unrelated.OmittedRelations > 0 && unrelated.Text.Length <= Tight, "Without shared words the context follows catalog order and stays within the limit");
            var byComment = catalog.Build("soma dos lancamentos contábeis", Tight);
            check(Detailed(byComment, "zz_ledger") && !Detailed(byComment, "zz_chart"), "A request sharing words with a table comment brings that table, ignoring accents and plural");
            check(Detailed(catalog.Build("valor total", Tight), "zz_ledger"), "Words of a camelCase column name count");
            var byReference = catalog.Build("ledger lines", Tight);
            check(Detailed(byReference, "zz_ledger_lines") && Detailed(byReference, "zz_chart") && byReference.Text.IndexOf($"TABLE {schema}.zz_ledger_lines", StringComparison.Ordinal) < byReference.Text.IndexOf($"TABLE {schema}.zz_chart", StringComparison.Ordinal),
                "A table referenced by a relevant one comes along for the join");
            var chosen = catalog.Build("xyzzy", Tight, [$"{schema}.zz_chart", "nope.missing", $"{schema.ToUpperInvariant()}.MIXED CASE", $"{schema}.zz_chart"]);
            check(chosen.ChosenRelations == 2 && Detailed(chosen, "zz_chart") && chosen.Text.Contains($"TABLE {schema}.\"Mixed Case\"", StringComparison.Ordinal) && !chosen.Text.Contains("nope.missing", StringComparison.Ordinal)
                && chosen.Text.IndexOf($"TABLE {schema}.zz_chart", StringComparison.Ordinal) < chosen.Text.IndexOf($"TABLE {schema}.\"Mixed Case\"", StringComparison.Ordinal),
                "Chosen relations come first in the order given; unknown and repeated names are ignored");
            var roomy = catalog.Build("xyzzy", 16_000);
            check(roomy.OmittedRelations > 0 && roomy.ListedByName == roomy.OmittedRelations && roomy.Text.Length <= 16_000 && !roomy.Text.Contains("--   …", StringComparison.Ordinal), "Room is kept so every relation left out still appears by name");
            check(unrelated.ListedByName < unrelated.OmittedRelations && unrelated.Text.Contains("--   …", StringComparison.Ordinal), "When even the names do not fit, the list says it was cut");
            string names = catalog.NameList("lançamentos", 100_000);
            check(names.StartsWith($"{schema}.zz_ledger -- Lançamentos contábeis\n", StringComparison.Ordinal) && names.Contains($"{schema}.customers -- People who buy\n", StringComparison.Ordinal) && names.Contains($"{schema}.filler_199\n", StringComparison.Ordinal)
                && names.Contains($"{schema}.\"Mixed Case\"\n", StringComparison.Ordinal) && !names.Contains("valorTotal", StringComparison.Ordinal) && !names.Contains(Secret, StringComparison.Ordinal),
                "The name list has every relation with its comment, most similar first, without columns or data");
            check(catalog.NameList("x", 300).TrimEnd().EndsWith("more relations not listed for size", StringComparison.Ordinal), "A name list over the limit says how many names are missing");
            check(SchemaContext.DefaultMaximumCharacters == 200_000, "The default context limit is 200,000 characters");

            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {schema}.customers", admin);
            check(await count.ExecuteScalarAsync() is 1L, "Building the context changes nothing in the database");
        }
        finally { await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE"); }
    }
}
