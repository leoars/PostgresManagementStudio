using Npgsql;
using PGMS.PostgreSql;

/// <summary>Database Diagrams (PGMS-028, M1043): pgms.diagrams, layout storage and schema changes saved with the diagram in one transaction.
/// Runs in its own throwaway database, so pgms.diagrams never appears in the databases other checks read.</summary>
internal static class DiagramChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string database = "pgms_diagrams_" + Guid.NewGuid().ToString("N")[..8];
        await using (var server = new NpgsqlConnection(connection))
        {
            await server.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", server); await create.ExecuteNonQueryAsync();
        }
        string target = new NpgsqlConnectionStringBuilder(connection) { Database = database }.ConnectionString;
        try { await RunInDatabaseAsync(target, database, check); }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var server = new NpgsqlConnection(connection);
            await server.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", server); await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task RunInDatabaseAsync(string connection, string database, Action<bool, string> check)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        async Task<string?> Refused(Func<Task> action) { try { await action(); return null; } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or PostgresException) { return ex.Message; } }
        var diagrams = new DiagramService(connection);
        var catalog = new CatalogService(connection);
        var databaseEntry = new CatalogEntry(CatalogKind.Database, database, database);

        // Before Install Diagram Support.
        var folders = await catalog.ChildrenAsync(databaseEntry);
        check(folders.Select(f => f.Kind).SequenceEqual([CatalogKind.DatabaseDiagrams, CatalogKind.Schemas]) && folders[0].Name == "Database Diagrams", "Database Diagrams is the first folder under a database, as in SSMS");
        check(!await diagrams.IsInstalledAsync() && (await diagrams.ListAsync()).Count == 0 && (await catalog.ChildrenAsync(folders[0])).Count == 0, "Without diagram support the folder is empty and nothing is created");
        check((long)(await Scalar("SELECT count(*) FROM pg_namespace WHERE nspname='pgms'"))! == 0, "Reading the folder does not create the pgms schema");
        check(await Refused(() => diagrams.SaveAsync(null, "Diagram_0", new DiagramLayout(), [])) is { } notInstalled && notInstalled.Contains("Install Diagram Support"), "Saving before installing is refused with the way out");

        await diagrams.InstallAsync();
        await diagrams.InstallAsync();
        check(await diagrams.IsInstalledAsync(), "Install Diagram Support creates pgms.diagrams and can run twice");
        check((string?)await Scalar("SELECT string_agg(attname || ':' || format_type(atttypid, atttypmod), ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid='pgms.diagrams'::regclass AND attnum>0") == "diagram_id:integer,name:text,owner:text,version:integer,definition:jsonb,modified:timestamp with time zone", "The diagram table keeps name, owner, version and the layout in jsonb");

        await Sql("""
            CREATE TABLE public.customers (id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE public.orders (id integer PRIMARY KEY, customer_id integer NOT NULL CONSTRAINT fk_orders_customers REFERENCES public.customers(id), placed date);
            CREATE TABLE public.products (id integer PRIMARY KEY, code text NOT NULL);
            CREATE TABLE public.order_lines (order_id integer NOT NULL REFERENCES public.orders(id), product_id integer, product_code text, label text, quantity integer CHECK (quantity > 0));
            CREATE TABLE public.scratch (id integer);
            CREATE TABLE public.sales (at date) PARTITION BY RANGE (at);
            CREATE TABLE public.sales_2026 PARTITION OF public.sales FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
            INSERT INTO public.customers VALUES (1, 'secret customer name');
            """);
        var tables = await diagrams.ListTablesAsync();
        var names = tables.Select(t => t.DisplayName).ToArray();
        check(names.Contains("public.customers") && names.Contains("public.sales") && !names.Contains("public.sales_2026") && !names.Contains("pgms.diagrams"), "Add Table lists tables and partitioned tables, without partitions or the diagram table");

        // Create, read and list.
        var layout = new DiagramLayout
        {
            Tables = tables.Where(t => t.Name is "customers" or "orders").Select((t, i) => new DiagramTablePlacement(t.Schema, t.Name, 20 + 240 * i, 30) { Oid = t.Oid }).ToArray(),
            Zoom = 0.75, ShowRelationshipLabels = true
        };
        var first = await diagrams.SaveAsync(null, " Diagram_0 ", layout, []);
        check(first.Name == "Diagram_0" && first.Version == 1 && first.Owner == new NpgsqlConnectionStringBuilder(connection).Username, "A new diagram is saved with its trimmed name, owner and version 1");
        var read = await diagrams.ReadAsync(first.Id);
        // The save adds the indexes (PGMS-044) and descriptions (PGMS-045) of each table from the catalog; the rest comes back as written.
        check(read.Layout.Tables.Select(t => t with { Indexes = null, UnindexedForeignKeys = null, Descriptions = null }).SequenceEqual(layout.Tables) && read.Layout.Tables.All(t => t.Indexes is not null && t.Descriptions is not null) && read.Layout.Zoom == 0.75 && read.Layout.ShowRelationshipLabels, "Reading a diagram returns the same tables, positions and zoom");
        string definition = (string)(await Scalar($"SELECT definition::text FROM pgms.diagrams WHERE diagram_id={first.Id}"))!;
        check(!definition.Contains("secret") && definition.Contains("\"tables\"") && definition.Contains("\"customers\""), "The stored definition has the layout and never table data");
        var listed = await catalog.ChildrenAsync(folders[0]);
        check(listed.Single() is { Kind: CatalogKind.DatabaseDiagram, Name: "Diagram_0", IsDiagram: true, CanExpand: false } item && item.Oid == (uint)first.Id && item.Database == database, "Object Explorer lists the saved diagram");

        // Layout saves and the version guard.
        var moved = layout with { Tables = [layout.Tables[0] with { X = 400 }, layout.Tables[1]] };
        var second = await diagrams.SaveAsync(first, "Diagram_0", moved, []);
        check(second.Version == 2 && (await diagrams.ReadAsync(first.Id)).Layout.Tables[0].X == 400, "Saving the layout bumps the version");
        check(await Refused(() => diagrams.SaveAsync(first, "Diagram_0", layout, [])) is { } stale && stale.Contains("changed or deleted"), "Saving over a diagram changed elsewhere is refused");
        var other = await diagrams.SaveAsync(null, "Other", new DiagramLayout(), []);
        check(await Refused(() => diagrams.SaveAsync(null, "Other", new DiagramLayout(), [])) is { } duplicate && duplicate.Contains("already exists"), "Two diagrams cannot share a name");
        check(await Refused(() => diagrams.RenameAsync(other.Id, "Diagram_0")) is { } renameDuplicate && renameDuplicate.Contains("already exists"), "Rename refuses a name already used");
        await diagrams.RenameAsync(other.Id, "Renamed");
        check((await diagrams.ListAsync()).Select(d => d.Name).SequenceEqual(["Diagram_0", "Renamed"]), "Rename changes the listed name");
        // An open tab renames with the version it read and gets the new one back.
        var beforeTab = (await diagrams.ListAsync()).Single(d => d.Name == "Renamed");
        var byTab = await diagrams.RenameAsync(beforeTab.Id, "  ByTab  ", beforeTab.Version);
        check(byTab is { Name: "ByTab" } && byTab.Version == beforeTab.Version + 1 && (await diagrams.ListAsync()).Any(d => d.Name == "ByTab"), "Rename with the open version returns the trimmed name and the next version");
        check(await Refused(() => diagrams.RenameAsync(beforeTab.Id, "Stale", beforeTab.Version)) is { } staleRename && staleRename.Contains("changed or deleted") && (await diagrams.ListAsync()).Any(d => d.Name == "ByTab"), "Rename with an outdated version is refused and changes nothing");
        check((await diagrams.SaveAsync(byTab, "ByTab", new DiagramLayout(), [])).Version == byTab.Version + 1, "The tab saves after its rename with the returned version");
        await diagrams.RenameAsync(beforeTab.Id, "Renamed");
        check(await Refused(() => diagrams.SaveAsync(null, "  ", new DiagramLayout(), [])) is not null, "An empty diagram name is refused");

        // Tables of a diagram.
        await Sql("ALTER TABLE public.customers RENAME TO clients");
        var (designs, missing) = await diagrams.ReadTablesAsync([.. layout.Tables, new DiagramTablePlacement("public", "gone", 0, 0)]);
        check(designs.Select(d => d.Name).SequenceEqual(["clients", "orders"]) && missing.SequenceEqual(["public.gone"]), "A table renamed elsewhere is found by its OID and a dropped one is reported");
        await Sql("ALTER TABLE public.clients RENAME TO customers");
        var related = await diagrams.RelatedTablesAsync([tables.Single(t => t.Name == "orders").Oid]);
        check(related.Select(t => t.Name).OrderBy(n => n).SequenceEqual(["customers", "order_lines"]), "Add Related Tables finds referenced and referencing tables");

        // Schema changes with the diagram, in one transaction.
        (designs, _) = await diagrams.ReadTablesAsync([new("public", "products", 0, 0), new("public", "order_lines", 0, 0)]);
        var products = designs[0]; var lines = designs[1];
        var linesWithKey = lines with { ForeignKeys = [.. lines.ForeignKeys, new DesignForeignKey(null, "fk_order_lines_products", ["product_id"], "public", "products", ["id"])] };
        var plan = DiagramSchemaChanges.Build([(products, products), (lines, linesWithKey)], []);
        check(plan.Edits.Count == 1 && plan.Script.Sql.Contains("ADD CONSTRAINT \"fk_order_lines_products\" FOREIGN KEY (\"product_id\") REFERENCES \"public\".\"products\" (\"id\")") && plan.Script.Sql.Contains("-- public.order_lines"), "The script has only the changed table and the new foreign key");
        var withKey = await diagrams.SaveAsync(second, "Diagram_0", moved, plan.Edits);
        check(withKey.Version == 3 && (long)(await Scalar("SELECT count(*) FROM pg_constraint WHERE conname='fk_order_lines_products'"))! == 1, "Save applies the foreign key and writes the diagram");

        // A failing change rolls back the diagram too.
        (designs, _) = await diagrams.ReadTablesAsync([new("public", "order_lines", 0, 0)]);
        var badKey = designs[0] with { ForeignKeys = [.. designs[0].ForeignKeys, new DesignForeignKey(null, "fk_bad", ["label"], "public", "products", ["id"])] };
        var badPlan = DiagramSchemaChanges.Build([(designs[0], badKey)], []);
        check(await Refused(() => diagrams.SaveAsync(withKey, "Diagram_0", layout, badPlan.Edits)) is { } incompatible && incompatible.Contains("42804"), "A foreign key between incompatible types fails in PostgreSQL");
        check((await diagrams.ReadAsync(first.Id)).Info.Version == 3 && (long)(await Scalar("SELECT count(*) FROM pg_constraint WHERE conname='fk_bad'"))! == 0, "A failed save leaves the diagram and the tables as they were");

        // Order: the referenced table gets its unique key before the other table references it, even when listed second.
        (designs, _) = await diagrams.ReadTablesAsync([new("public", "order_lines", 0, 0), new("public", "products", 0, 0)]);
        lines = designs[0]; products = designs[1];
        var keyed = products with { Indexes = [.. products.Indexes, new DesignIndex(null, "products_code_key", DesignIndexKind.UniqueKey, [new("code")]) { Unique = true, ConstraintBacked = true }] };
        var byCode = lines with { ForeignKeys = [.. lines.ForeignKeys, new DesignForeignKey(null, "fk_order_lines_products1", ["product_code"], "public", "products", ["code"])] };
        plan = DiagramSchemaChanges.Build([(lines, byCode), (products, keyed)], []);
        check(plan.Script.Sql.IndexOf("products_code_key", StringComparison.Ordinal) < plan.Script.Sql.IndexOf("fk_order_lines_products1", StringComparison.Ordinal), "A new key is created before the foreign key that uses it");
        await diagrams.SaveAsync(withKey, "Diagram_0", layout, plan.Edits);
        check((long)(await Scalar("SELECT count(*) FROM pg_constraint WHERE conname IN ('products_code_key','fk_order_lines_products1')"))! == 2, "Both tables change in one save");

        // And the reverse: the foreign key goes before the key it uses.
        (designs, _) = await diagrams.ReadTablesAsync([new("public", "products", 0, 0), new("public", "order_lines", 0, 0)]);
        products = designs[0]; lines = designs[1];
        plan = DiagramSchemaChanges.Build([(products, products with { Indexes = products.Indexes.Where(i => i.Name != "products_code_key").ToArray() }), (lines, lines with { ForeignKeys = lines.ForeignKeys.Where(f => f.Name != "fk_order_lines_products1").ToArray() })], []);
        check(plan.Script.Sql.IndexOf("DROP CONSTRAINT \"fk_order_lines_products1\"", StringComparison.Ordinal) < plan.Script.Sql.IndexOf("DROP CONSTRAINT \"products_code_key\"", StringComparison.Ordinal), "A foreign key is dropped before the key it uses");
        var current = await diagrams.SaveAsync((await diagrams.ListAsync()).Single(d => d.Name == "Diagram_0"), "Diagram_0", layout, plan.Edits);
        check((long)(await Scalar("SELECT count(*) FROM pg_constraint WHERE conname IN ('products_code_key','fk_order_lines_products1')"))! == 0, "Removing a relationship and its key saves in one transaction");

        // Delete Tables from Database.
        uint scratchOid = tables.Single(t => t.Name == "scratch").Oid;
        check(await Refused(() => diagrams.SaveAsync(current, "Diagram_0", layout, [new DropTableEdit("public", "scratch", scratchOid + 1)])) is { } wrongTable && wrongTable.Contains("changed since"), "Dropping a table refuses another table under the same name");
        (designs, _) = await diagrams.ReadTablesAsync([new("public", "scratch", 0, 0)]);
        plan = DiagramSchemaChanges.Build([], designs);
        check(plan.Script.Sql == "DROP TABLE \"public\".\"scratch\" RESTRICT;" && plan.Script.Warnings.Single().Contains("data"), "Delete Tables from Database scripts DROP TABLE with a data warning");
        current = await diagrams.SaveAsync(current, "Diagram_0", layout, plan.Edits);
        check(await Scalar("SELECT to_regclass('public.scratch') IS NULL") is true, "The deleted table is dropped on save");

        // Tables created with the diagram (stage 2): every CREATE before the foreign keys, so two new tables can reference each other.
        static TableDesign Fresh(string name, params DesignForeignKey[] keys) => new(0, "public", name, [new(null, "id", "integer", false), new(null, "other_id", "integer", true)])
        {
            Indexes = [new DesignIndex(null, name + "_pkey", DesignIndexKind.PrimaryKey, [new("id")]) { Unique = true, ConstraintBacked = true }], ForeignKeys = keys
        };
        var ping = Fresh("ping", new DesignForeignKey(null, "fk_ping_pong", ["other_id"], "public", "pong", ["id"]));
        var pong = Fresh("pong", new DesignForeignKey(null, "fk_pong_ping", ["other_id"], "public", "ping", ["id"]) { Comment = "back to ping" });
        (designs, _) = await diagrams.ReadTablesAsync([new("public", "products", 0, 0), new("public", "order_lines", 0, 0)]);
        lines = designs[1];
        var toNew = lines with { ForeignKeys = [.. lines.ForeignKeys, new DesignForeignKey(null, "fk_lines_ping", ["product_id"], "public", "ping", ["id"])] };
        plan = DiagramSchemaChanges.Build([(ping, ping), (lines, toNew), (pong, pong)], []);
        string script = plan.Script.Sql;
        int firstKey = script.IndexOf("FOREIGN KEY", StringComparison.Ordinal);
        check(script.IndexOf("CREATE TABLE \"public\".\"ping\"", StringComparison.Ordinal) is >= 0 and var c1 && c1 < firstKey && script.IndexOf("CREATE TABLE \"public\".\"pong\"", StringComparison.Ordinal) is >= 0 and var c2 && c2 < firstKey, "New tables are created before any foreign key");
        check(!script[..firstKey].Contains("fk_ping_pong") && script.IndexOf("fk_lines_ping", StringComparison.Ordinal) < script.IndexOf("fk_ping_pong", StringComparison.Ordinal), "Foreign keys of new tables come after the changes to existing tables");
        current = await diagrams.SaveAsync(current, "Diagram_0", layout, plan.Edits);
        check((long)(await Scalar("SELECT count(*) FROM pg_constraint WHERE conname IN ('fk_ping_pong','fk_pong_ping','fk_lines_ping')"))! == 3 && (string?)await Scalar("SELECT obj_description(oid,'pg_constraint') FROM pg_constraint WHERE conname='fk_pong_ping'") == "back to ping", "Two new tables referencing each other are saved, with the description of the foreign key");
        var taken = Fresh("taken");
        await Sql("CREATE TABLE public.taken (id integer)");
        int versionBefore = (await diagrams.ReadAsync(first.Id)).Info.Version;
        check(await Refused(() => diagrams.SaveAsync(current, "Diagram_0", layout, DiagramSchemaChanges.Build([(taken, taken)], []).Edits)) is { } exists && exists.Contains("42P07") && (await diagrams.ReadAsync(first.Id)).Info.Version == versionBefore, "A new table with a name already in use fails and leaves the diagram as it was");
        check(await Refused(() => Task.FromResult(DiagramSchemaChanges.Build([(Fresh("x", new DesignForeignKey(null, "k", ["nope"], "public", "ping", ["id"])), Fresh("x", new DesignForeignKey(null, "k", ["nope"], "public", "ping", ["id"])))], []))) is { } unknownColumn && unknownColumn.Contains("nope"), "A foreign key of a new table is checked before the script is built");
        await Sql("ALTER TABLE public.order_lines DROP CONSTRAINT fk_lines_ping; DROP TABLE public.ping, public.pong, public.taken CASCADE");
        var full = new DiagramLayout
        {
            Tables = [new("public", "a", 1, 2) { View = "Custom", CustomColumns = ["Default Value"] }], Annotations = [new("note", 3, 4)], ShowPageBreaks = true, PageWidth = 700, PageHeight = 500
        };
        var again = DiagramLayout.FromJson(full.ToJson());
        check(again.Tables[0].View == "Custom" && again.Tables[0].CustomColumns!.SequenceEqual(["Default Value"]) && again.Annotations.Single() == new DiagramAnnotationPlacement("note", 3, 4) && again.ShowPageBreaks && again.PageWidth == 700 && again.PageHeight == 500, "The layout keeps Table View, annotations and page breaks");
        check(DiagramLayout.FromJson("{\"tables\":[{\"schema\":\"public\",\"name\":\"a\",\"x\":1,\"y\":2}],\"zoom\":1}") is { Annotations.Count: 0, ShowPageBreaks: false } old && old.Tables[0].View is null, "A diagram saved before stage 2 still opens");

        await DiagramIndexChecks.RunAsync(connection, check);
        await DiagramDescriptionChecks.RunAsync(connection, check);

        // Delete.
        await diagrams.DeleteAsync(other.Id);
        check((await diagrams.ListAsync()).Single().Id == first.Id && await Refused(() => diagrams.DeleteAsync(other.Id)) is not null, "Delete removes the diagram; deleting it again is refused");
        check(await Refused(() => diagrams.ReadAsync(other.Id)) is { } gone && gone.Contains("not found"), "Opening a deleted diagram says it was not found");
        check(DiagramLayout.FromJson(new DiagramLayout { Tables = [new("a b", "c\"d", 1.5, -2) { Oid = 7 }] }.ToJson()).Tables.Single() == new DiagramTablePlacement("a b", "c\"d", 1.5, -2) { Oid = 7 }, "Layout JSON keeps names with spaces and quotes");
        _ = current;
        await QueryDesignChecks.RunAsync(connection, check);
    }
}
