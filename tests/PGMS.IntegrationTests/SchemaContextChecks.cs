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
            var small = await service.BuildAsync("show open_orders", maximumCharacters: 400);
            check(small.Text.Contains($"VIEW {schema}.open_orders", StringComparison.Ordinal) && small.OmittedRelations > 0 && small.Text.Contains("more relations omitted for size", StringComparison.Ordinal), "Over budget, relations named in the request come first and the rest are listed by name");

            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {schema}.customers", admin);
            check(await count.ExecuteScalarAsync() is 1L, "Building the context changes nothing in the database");
        }
        finally { await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE"); }
    }
}
