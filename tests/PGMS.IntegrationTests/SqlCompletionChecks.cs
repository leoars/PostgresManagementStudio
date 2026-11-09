using PGMS.PostgreSql;

// PGMS-035: what IntelliSense offers at each position, and the catalog it reads.
internal static class SqlCompletionChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var catalog = new SqlCompletionCatalog
        {
            Database = "db", SearchPath = ["pg_catalog", "public"], Schemas = ["pg_catalog", "public", "sales", "Mixed", "financeiro", "usuarios_legado"],
            Relations = [new("public", "customers", false), new("public", "customer_notes", false), new("public", "v_totals", true), new("sales", "items", false), new("public", "Order", false), new("pg_catalog", "pg_class", false), new("public", "employees", false), new("public", "lines", false), new("public", "Usuários", false), new("financeiro", "Usuários", false), new("financeiro", "customers", false), new("financeiro", "resumo", true), new("public", "shared", false), new("sales", "shared", false), new("information_schema", "tables", true)],
            Columns = new Dictionary<(string, string), IReadOnlyList<SqlCatalogColumn>>
            {
                [("public", "customers")] = [new("id", "integer", false), new("city", "character varying(50)", true), new("Full Name", "text", true), new("order", "text", true)],
                [("public", "customer_notes")] = [new("note", "text", true)],
                [("sales", "items")] = [new("sku", "text", true)],
                [("public", "employees")] = [new("id", "integer", false), new("manager_id", "integer", true)],
                [("public", "lines")] = [new("order_id", "integer", false), new("order_no", "integer", false)],
                [("public", "Order")] = [new("id", "integer", false), new("no", "integer", false)],
                [("public", "v_totals")] = [new("total", "numeric", true)],
            },
            Functions = [new("public", "customer_total", "p integer", "numeric"), new("sales", "price", "", "numeric"), new("pg_catalog", "count", "\"any\"", "bigint"), new("public", "customer_orders", "p integer", "TABLE(id integer)", true), new("sales", "open_items", "", "SETOF text", true), new("pg_catalog", "generate_series", "integer, integer", "SETOF integer", true), new("pg_catalog", "pg_ls_dir", "text", "SETOF text", true)],
            ForeignKeys = [new("fk_notes", "public", "customer_notes", ["customer_id"], "public", "customers", ["id"]), new("fk_items", "sales", "items", ["customer_id"], "public", "customers", ["id"]), new("fk_boss", "public", "employees", ["manager_id"], "public", "employees", ["id"]), new("fk_lines", "public", "lines", ["order_id", "order_no"], "public", "Order", ["id", "no"])],
        };
        SqlCompletionResult? At(string marked) { int caret = marked.IndexOf('|'); return SqlCompletionEngine.Complete(marked.Remove(caret, 1), caret, catalog); }
        string[] Names(string marked) => At(marked)?.Items.Select(i => i.Text).ToArray() ?? [];
        bool Has(string marked, params string[] wanted) => wanted.All(Names(marked).Contains);
        bool Lacks(string marked, params string[] unwanted) => !unwanted.Any(Names(marked).Contains);

        check(Has("SELECT * FROM |", "customers", "customer_notes", "v_totals", "sales", "\"Order\"") && Lacks("SELECT * FROM |", "SELECT", "id", "pg_class", "count", "items"), "After FROM: schemas and visible tables, without keywords, columns or the system catalog");
        check(Has("SELECT * FROM |", "customer_orders", "generate_series") && Lacks("SELECT * FROM |", "customer_total", "pg_ls_dir", "open_items"), "After FROM: table functions of the path, without scalar functions or internal pg_* ones");
        check(Has("SELECT * FROM a JOIN |", "customer_orders", "v_totals") && Has("SELECT * FROM a, LATERAL |", "customer_orders") && Lacks("SELECT * FROM a JOIN |", "customer_total", "count", "SELECT"), "JOIN and LATERAL offer the same as FROM");
        check(Has("SELECT * FROM public.customers JOIN |", "customer_notes") && Has("UPDATE |", "customers") && Has("INSERT INTO |", "customers"), "JOIN, UPDATE and INTO also want a table");
        foreach (string target in new[] { "UPDATE |", "INSERT INTO |", "DELETE FROM |", "TRUNCATE |", "TRUNCATE TABLE |", "SELECT * FROM ONLY |" })
            check(Has(target, "customers", "sales") && Lacks(target, "customer_orders", "generate_series", "customer_total", "SELECT", "id"), "A write target or ONLY offers tables and no functions: " + target);
        check(Has("UPDATE sales.|", "items") && Lacks("UPDATE sales.|", "open_items", "price"), "A write target after a schema has no functions");
        check(Has("Select * from Usuar|", "\"Usuários\"", "financeiro.\"Usuários\"", "usuarios_legado", "financeiro"), "After FROM a table is listed from every schema that has it, and the schemas stay");
        var everySchema = At("Select * from Usuar|")!.Items;
        check(everySchema.Where(i => i.Text.EndsWith("\"Usuários\"", StringComparison.Ordinal)).All(i => i.Filter == "\"Usuários\"") && everySchema.Single(i => i.Text == "usuarios_legado").Filter is null, "Typing is matched against the table's name, not its schema");
        check(everySchema.Single(i => i.Text == "financeiro.\"Usuários\"").Description == "table db.financeiro.Usuários" && Has("SELECT * FROM |", "sales.items", "financeiro.resumo") && everySchema.Single(i => i.Text == "financeiro.resumo").Kind == SqlCompletionKind.View, "A table off the search path is written with its schema");
        check(Has("SELECT * FROM |", "customers", "financeiro.customers") && Has("SELECT * FROM |", "shared", "sales.shared") && Lacks("SELECT * FROM |", "public.shared", "public.customers", "public.\"Usuários\""), "Only a name a bare name would not reach gets its schema");
        var order = everySchema.Select(i => i.Text).ToList();
        check(order.IndexOf("financeiro.\"Usuários\"") == order.IndexOf("\"Usuários\"") + 1, "Tables of the same name stand together, the one on the path first");
        check(Lacks("SELECT * FROM |", "pg_class", "pg_catalog.pg_class", "information_schema.tables", "tables"), "System catalogs stay out of the search");
        check(Has("DELETE FROM |", "financeiro.\"Usuários\"") && Has("UPDATE |", "sales.items") && Has("SELECT * FROM a JOIN |", "sales.items"), "The same search applies wherever a table is expected");
        check(Lacks("SELECT | FROM customers c", "sales.items", "financeiro.\"Usuários\""), "An expression does not search other schemas");
        const string Notes = "customer_notes cn ON cn.customer_id = c.id";
        check(Has("SELECT * FROM customers c JOIN |", Notes, "sales.items i ON i.customer_id = c.id", "customer_notes", "v_totals"), "After JOIN: the whole join for tables linked by a key, and the plain tables too");
        check(At("SELECT * FROM customers c JOIN |")!.Items[0] is { Kind: SqlCompletionKind.Join, Description: "join db.public.customer_notes by foreign key fk_notes" } && At("SELECT * FROM customers c JOIN |")!.Items.Take(2).All(i => i.Kind == SqlCompletionKind.Join) && At("SELECT * FROM customers c JOIN |")!.Items[2].Kind != SqlCompletionKind.Join, "Whole joins come first and say which key they use");
        check(Has("SELECT * FROM customers JOIN |", "customer_notes ON customer_notes.customer_id = customers.id"), "A statement without aliases gets a join without one");
        check(Has("SELECT * FROM customer_notes n JOIN |", "customers c ON c.id = n.customer_id"), "A key works in both directions");
        check(Has("SELECT * FROM customers c LEFT OUTER JOIN cust|", Notes) && Has("SELECT * FROM customers c JOIN sales.|", "items i ON i.customer_id = c.id") && Lacks("SELECT * FROM customers c JOIN sales.|", Notes), "Any kind of JOIN, a name being typed, and a schema that narrows the list");
        check(Has("SELECT * FROM employees e JOIN |", "employees e2 ON e2.manager_id = e.id", "employees e2 ON e2.id = e.manager_id") && Has("SELECT * FROM employees JOIN |", "employees e ON e.manager_id = employees.id"), "A self join gets a new alias, in each direction of the key");
        check(Has("SELECT * FROM lines l JOIN |", "\"Order\" o ON o.id = l.order_id AND o.no = l.order_no"), "A composite key joins on every column, and names are quoted when needed");
        check(Has("SELECT * FROM customers c JOIN customer_notes cn ON true JOIN |", "customer_notes cn2 ON cn2.customer_id = c.id", "customers c2 ON c2.id = cn.customer_id"), "An alias already in use is not repeated");
        check(!At("SELECT * FROM v_totals v JOIN | JOIN customers c ON true")!.Items.Any(i => i.Kind == SqlCompletionKind.Join), "Only tables before the JOIN count");
        check(!At("SELECT * FROM customers c, |")!.Items.Any(i => i.Kind == SqlCompletionKind.Join) && !At("SELECT * FROM |")!.Items.Any(i => i.Kind == SqlCompletionKind.Join), "A whole join is only offered after JOIN");
        check(Has("SELECT * FROM customers c JOIN customer_notes n ON |", "n.customer_id = c.id", "id", "note") && At("SELECT * FROM customers c JOIN customer_notes n ON |")!.Items[0].Kind == SqlCompletionKind.Join, "After ON: the condition from the key comes first, then the usual list");
        check(Has("SELECT * FROM customers c JOIN customer_notes n ON n.|", "note") && !At("SELECT * FROM customers c JOIN v_totals v ON |")!.Items.Any(i => i.Kind == SqlCompletionKind.Join), "After ON no condition is invented without a key");
        check(At("SELECT * FROM customers.|") is { Items.Count: 0 }, "After FROM a name that is not a schema offers nothing, not its columns");
        check(Has("SELECT | FROM customers c", "c", "id", "customer_total", "SELECT") && Lacks("SELECT | FROM customers c", "customers", "customer_notes", "v_totals", "sales"), "In the SELECT list: the statement's alias, columns, functions and keywords, not every table");
        check(Has("SELECT * FROM customers WHERE |", "customers", "city") && Lacks("SELECT * FROM customers WHERE |", "customer_notes", "v_totals", "sales", "note"), "In WHERE only the statement's tables are named");
        check(At("SELECT | FROM customers c")!.Items.Single(i => i.Text == "c") is { Kind: SqlCompletionKind.Table, Description: "alias c of db.public.customers" }, "An alias says what it stands for");
        check(Has("|", "SELECT", "INSERT", "WITH") && Lacks("|", "customers", "customer_total", "FROM", "sales") && Has("SELECT 1; SEL|", "SELECT") && Lacks("SELECT 1; SEL|", "customers", "id"), "At the start of a statement only the words that begin one");
        check(Has("SELECT * FROM customers, |", "customer_notes") && Lacks("SELECT * FROM customers, |", "SELECT"), "After a comma in FROM a table is wanted");
        check(Has("SELECT a, | FROM customers", "id", "city", "SELECT", "customer_total", "count"), "After a comma in SELECT: columns of the statement's tables, functions and keywords");
        check(Has("SELECT * FROM sales.|", "items", "open_items") && Lacks("SELECT * FROM sales.|", "price", "customers", "customer_total", "customer_orders"), "After FROM and a schema: its tables and table functions only");
        check(Has("SELECT sales.|", "price", "open_items", "items"), "After a schema in an expression its functions are all offered");
        check(Has("SELECT c.| FROM customers c", "id", "city") && Lacks("SELECT c.| FROM customers c", "note", "customers"), "A dot after an alias, written later in the statement, lists its columns");
        check(Has("SELECT i.| FROM sales.items AS i", "sku") && Has("SELECT customers.| FROM customers", "city") && Has("SELECT public.customers.| FROM customers", "city"), "AS aliases, table names and schema.table all resolve");
        check(Has("SELECT n.| FROM customers c JOIN customer_notes n ON true", "note") && Lacks("SELECT n.| FROM customers c JOIN customer_notes n ON true", "city"), "Each alias of a join has its own columns");
        check(Has("SELECT a.| FROM customers a, customer_notes b WHERE true", "id") && Has("SELECT b.| FROM customers a, customer_notes b", "note"), "Every table of a FROM list has its alias");
        check(Names("SELECT c.| FROM customers c; SELECT 1 FROM customer_notes c").Contains("id") && Names("SELECT 1 FROM customers c; SELECT c.| FROM customer_notes c").SequenceEqual(["note"]), "Only the statement at the caret counts");
        check(At("SELECT c.| FROM missing c") is { Items.Count: 0 }, "An unknown table offers nothing");
        check(Has("SELECT c.| FROM CUSTOMERS C", "id"), "Unquoted names fold to lower case");
        check(Has("SELECT c.| FROM \"customers\" \"c\"", "id"), "Quoted names and aliases are understood");
        check(Has("SELECT c.| FROM customers c", "\"Full Name\"", "\"order\""), "Names that need quotes are inserted quoted");
        check(SqlCompletionEngine.Quote("plain_1") == "plain_1" && SqlCompletionEngine.Quote("select") == "\"select\"" && SqlCompletionEngine.Quote("Ab") == "\"Ab\"" && SqlCompletionEngine.Quote("a\"b") == "\"a\"\"b\"", "Quoting follows PostgreSQL's rules");

        var word = At("SELECT * FROM cu|");
        check(word is { ReplaceStart: 14, Prefix: "cu" }, "A word being typed is what gets replaced");
        var middle = At("SELECT * FROM cu|stomers");
        check(middle is { ReplaceStart: 14, Prefix: "cu" }, "The caret inside a word uses the part before it");
        var quoted = At("SELECT * FROM \"Or|");
        check(quoted is { ReplaceStart: 14, Prefix: "Or" }, "A quote being typed is part of what gets replaced");
        var afterDot = At("SELECT c.ci| FROM customers c");
        check(afterDot is { ReplaceStart: 9, Prefix: "ci" } && afterDot.Items.Any(i => i.Text == "city"), "A partial name after the dot keeps the context");

        foreach (string inside in new[] { "SELECT 'a|b'", "SELECT 'it''s |", "-- c|", "SELECT /* a| */ 1", "SELECT /* /* a */ b| */ 1", "SELECT $$ a| $$", "SELECT $x$ a| $x$", "SELECT E'a\\'b|'" })
            check(At(inside) is null, "Nothing is offered inside: " + inside);
        check(At("SELECT 'a' |") is not null && At("SELECT 1 -- x\n|") is not null && At("SELECT /* a */ |") is not null && At("SELECT $$a$$ |") is not null, "Right after strings and comments the list works");
        check(At("SELECT $1, c.| FROM customers c") is { Items.Count: > 0 }, "A $1 parameter is not taken for a dollar quote");

        var described = At("SELECT c.| FROM customers c")!.Items;
        check(described.Single(i => i.Text == "city").Description == "column city(character varying(50), null)" && described.Single(i => i.Text == "id").Description == "column id(integer, not null)", "Column tips show type and nullability");
        var tables = At("SELECT * FROM |")!.Items;
        check(tables.Single(i => i.Text == "customers").Description == "table db.public.customers" && tables.Single(i => i.Text == "v_totals") is { Kind: SqlCompletionKind.View, Description: "view db.public.v_totals" }, "Table and view tips show the full name");
        check(At("SELECT |")!.Items.Single(i => i.Text == "customer_total").Description == "function db.public.customer_total(p integer) RETURNS numeric", "Function tips show arguments and result");
        var ordered = At("SELECT |")!.Items.Select(i => i.Text).ToArray();
        check(ordered.SequenceEqual(ordered.Order(StringComparer.OrdinalIgnoreCase)), "The list is alphabetical");

        string schema = "qcpl_" + Guid.NewGuid().ToString("N")[..8];
        await using var session = new QuerySession(connection);
        await session.OpenAsync();
        await session.ExecuteAsync($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.t(id serial PRIMARY KEY, note varchar(9)); CREATE VIEW {schema}.v AS SELECT id FROM {schema}.t; CREATE FUNCTION {schema}.f(a int) RETURNS int LANGUAGE sql AS 'SELECT a'; CREATE FUNCTION {schema}.ft() RETURNS TABLE(a int) LANGUAGE sql AS 'SELECT 1'; CREATE FUNCTION {schema}.fs() RETURNS SETOF int LANGUAGE sql AS 'SELECT 1'; CREATE TABLE {schema}.pai(a int, b int, PRIMARY KEY (b, a)); CREATE TABLE {schema}.filho(x int, y int, CONSTRAINT fk_pai FOREIGN KEY (y, x) REFERENCES {schema}.pai(b, a)); ALTER TABLE {schema}.t DROP COLUMN note; ALTER TABLE {schema}.t ADD COLUMN kept varchar(9) NOT NULL;");
        try
        {
            var real = await SqlCompletionCatalog.LoadAsync(connection);
            check(real.SearchPath.Contains("pg_catalog") && real.Schemas.Contains(schema) && !real.Schemas.Any(s => s.StartsWith("pg_toast", StringComparison.Ordinal)), "The real catalog has the search path and hides internal schemas");
            check(real.Relations.Contains(new(schema, "t", false)) && real.Relations.Contains(new(schema, "v", true)), "Tables and views are read");
            check(real.Columns[(schema, "t")].SequenceEqual([new SqlCatalogColumn("id", "integer", false), new SqlCatalogColumn("kept", "character varying(9)", false)]), "Columns come in order with their types, without dropped ones");
            check(real.Functions.Contains(new(schema, "f", "a integer", "integer")) && real.Functions.Any(f => f.Schema == "pg_catalog" && f.Name == "count"), "Functions and aggregates are read");
            check(real.Functions.Single(f => f.Schema == schema && f.Name == "ft") is { ReturnsRows: true, Result: "TABLE(a integer)" } && real.Functions.Single(f => f.Schema == schema && f.Name == "fs").ReturnsRows && real.Functions.Any(f => f.Schema == "pg_catalog" && f.Name == "generate_series" && f.ReturnsRows) && !real.Functions.Single(f => f.Schema == schema && f.Name == "f").ReturnsRows && !real.Functions.First(f => f.Schema == "pg_catalog" && f.Name == "count").ReturnsRows, "Table functions are told apart from scalar ones and aggregates");
            var key = real.ForeignKeys.Single(k => k.Schema == schema);
            check(key is { Name: "fk_pai", Table: "filho", TargetTable: "pai" } && key.TargetSchema == schema && key.Columns.SequenceEqual(["y", "x"]) && key.TargetColumns.SequenceEqual(["b", "a"]), "Foreign keys are read with their columns paired in the key's order");
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA {schema} CASCADE"); }
    }
}
