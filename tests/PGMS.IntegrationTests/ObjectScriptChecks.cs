using System.Diagnostics;
using Npgsql;
using PGMS.PostgreSql;

// PGMS-026 (M1054): Script … as. The scripts are applied for real to an empty database and pg_dump must see the same objects on both sides.
internal static class ObjectScriptChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8], source = "qscr_src_" + suffix, target = "qscr_dst_" + suffix, role = "qscr_role_" + suffix, group = "qscr_grp_" + suffix, copy = "qscr_db_" + suffix, offline = "qscr_off_" + suffix;
        const string S = "\"sc ript\"";
        var root = new NpgsqlConnectionStringBuilder(connection);
        string On(string database) => new NpgsqlConnectionStringBuilder(connection) { Database = database }.ConnectionString;
        async Task Admin(string sql) { await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var command = new NpgsqlCommand(sql, c); await command.ExecuteNonQueryAsync(); }
        await Admin($"CREATE DATABASE {source} TEMPLATE template0");
        await Admin($"CREATE DATABASE {target} TEMPLATE template0");
        try
        {
            await using (var c = new NpgsqlConnection(On(source)))
            {
                await c.OpenAsync();
                await using var command = new NpgsqlCommand($$"""
                    CREATE SCHEMA {{S}};
                    COMMENT ON SCHEMA {{S}} IS 'schema''s note';
                    CREATE TYPE {{S}}.mood AS ENUM ('sad', 'o''k', 'happy');
                    CREATE DOMAIN {{S}}.pos AS integer DEFAULT 1 NOT NULL CONSTRAINT pos_chk CHECK (VALUE > 0);
                    CREATE SEQUENCE {{S}}.free_seq AS integer START 5 INCREMENT 2 MINVALUE 1 MAXVALUE 100 CACHE 3 CYCLE;
                    CREATE FUNCTION {{S}}.touch() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN RETURN NEW; END $f$;
                    CREATE TABLE {{S}}."Parent T" (
                        id bigint GENERATED ALWAYS AS IDENTITY (START WITH 10) PRIMARY KEY,
                        code text COLLATE "C" NOT NULL UNIQUE,
                        qty {{S}}.pos,
                        m {{S}}.mood DEFAULT 'sad',
                        twice integer GENERATED ALWAYS AS (qty * 2) STORED,
                        ser serial,
                        note text,
                        CONSTRAINT qty_big CHECK (qty < 1000)
                    ) WITH (fillfactor = 70);
                    ALTER TABLE {{S}}."Parent T" ALTER COLUMN note SET STORAGE EXTERNAL, ALTER COLUMN note SET STATISTICS 50;
                    ALTER TABLE {{S}}."Parent T" ADD CONSTRAINT late CHECK (qty <> 7) NOT VALID;
                    CREATE INDEX lower_code ON {{S}}."Parent T" (lower(code)) WHERE qty > 1;
                    CREATE UNLOGGED TABLE {{S}}.loose (a integer);
                    CREATE TABLE {{S}}.child (id integer PRIMARY KEY, parent_id bigint REFERENCES {{S}}."Parent T"(id) ON DELETE CASCADE, other bigint);
                    ALTER TABLE {{S}}.child ADD CONSTRAINT other_fk FOREIGN KEY (other) REFERENCES {{S}}."Parent T"(id) NOT VALID;
                    CREATE TRIGGER t_off BEFORE INSERT ON {{S}}.child FOR EACH ROW EXECUTE FUNCTION {{S}}.touch();
                    ALTER TABLE {{S}}.child DISABLE TRIGGER t_off;
                    CREATE RULE r_quiet AS ON DELETE TO {{S}}.loose DO INSTEAD NOTHING;
                    ALTER TABLE {{S}}.child ENABLE ROW LEVEL SECURITY;
                    CREATE POLICY p_all ON {{S}}.child AS RESTRICTIVE FOR SELECT TO PUBLIC USING (id > 0);
                    COMMENT ON TABLE {{S}}.child IS 'the child';
                    COMMENT ON COLUMN {{S}}.child.other IS 'o''ther';
                    COMMENT ON CONSTRAINT other_fk ON {{S}}.child IS 'fk note';
                    COMMENT ON INDEX {{S}}.lower_code IS 'index note';
                    CREATE TABLE {{S}}.base (a integer NOT NULL);
                    CREATE TABLE {{S}}.derived (b integer) INHERITS ({{S}}.base);
                    CREATE TABLE {{S}}.sales (d date NOT NULL, v integer) PARTITION BY RANGE (d);
                    CREATE TABLE {{S}}.sales_2026 PARTITION OF {{S}}.sales FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
                    CREATE VIEW {{S}}.v_parent WITH (security_barrier) AS SELECT id, code FROM {{S}}."Parent T" WHERE qty > 1;
                    COMMENT ON VIEW {{S}}.v_parent IS 'view note';
                    CREATE MATERIALIZED VIEW {{S}}.mv AS SELECT id FROM {{S}}."Parent T" WITH NO DATA;
                    CREATE INDEX mv_id ON {{S}}.mv (id);
                    CREATE FUNCTION {{S}}.series(n integer, step integer DEFAULT 1, OUT v integer) RETURNS SETOF integer LANGUAGE sql AS $f$ SELECT generate_series(1, n, step) $f$;
                    CREATE FUNCTION {{S}}.plus(a integer, VARIADIC more integer[]) RETURNS integer LANGUAGE sql AS 'SELECT a';
                    CREATE PROCEDURE {{S}}.work(IN a integer, OUT b integer) LANGUAGE sql AS 'SELECT a';
                    COMMENT ON FUNCTION {{S}}.plus(integer, integer[]) IS 'adds';
                    """, c);
                await command.ExecuteNonQueryAsync();
            }

            var service = new ObjectScriptService(On(source));
            async Task<uint> Oid(string sql)
            {
                await using var c = new NpgsqlConnection(On(source)); await c.OpenAsync();
                await using var command = new NpgsqlCommand(sql, c);
                return (uint)(await command.ExecuteScalarAsync())!;
            }
            Task<uint> Rel(string name) => Oid($"SELECT '{S}.{name}'::regclass::oid");
            async Task<CatalogEntry> Relation(string name, string kind) => new(CatalogKind.Relation, name, source, "sc ript", await Rel(name)) { RelationType = kind };
            async Task<CatalogEntry> Type(string name, string kind) => new(CatalogKind.DataType, name, source, "sc ript", await Oid($"SELECT '{S}.{name}'::regtype::oid")) { DataTypeKind = kind };
            async Task<CatalogEntry> Routine(string signature, string kind) => new(CatalogKind.Routine, signature, source, "sc ript", await Oid($"SELECT '{S}.{signature}'::regprocedure::oid")) { RoutineKind = kind };
            var schema = new CatalogEntry(CatalogKind.Schema, "sc ript", source, "sc ript", await Oid("SELECT oid FROM pg_namespace WHERE nspname='sc ript'"));
            CatalogEntry parent = await Relation("\"Parent T\"", "r"), child = await Relation("child", "r"), view = await Relation("v_parent", "v"), matview = await Relation("mv", "m"), sequence = await Relation("free_seq", "S");
            CatalogEntry mood = await Type("mood", "e"), pos = await Type("pos", "d"), touch = await Routine("touch()", "f"), series = await Routine("series(integer, integer)", "f"), plus = await Routine("plus(integer, integer[])", "f"), work = await Routine("work(integer)", "p");

            // Apply every CREATE to an empty database, dependencies first.
            var order = new[] { schema, mood, pos, sequence, touch, parent, await Relation("loose", "r"), child, await Relation("base", "r"), await Relation("derived", "r"), await Relation("sales", "p"), await Relation("sales_2026", "r"), view, matview, series, plus, work };
            await using (var apply = new QuerySession(On(target)))
            {
                await apply.OpenAsync();
                foreach (var entry in order)
                {
                    string script = await service.ScriptAsync(entry, ScriptVerb.Create);
                    try { await apply.ExecuteAsync(script); }
                    catch (Exception ex) { check(false, $"CREATE script of {entry.Name} runs: {ex.Message}\n{script}"); throw; }
                }
            }
            string Normalize(string dump) => string.Join("\n", dump.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal) && !l.StartsWith("\\", StringComparison.Ordinal) && !l.StartsWith("SET ", StringComparison.Ordinal) && !l.StartsWith("SELECT pg_catalog.set_config", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            string before = Normalize(await Dump(root, source)), after = Normalize(await Dump(root, target));
            if (before != after)
            {
                var a = before.Split('\n'); var b = after.Split('\n');
                Console.WriteLine("Only in source:\n  " + string.Join("\n  ", a.Except(b)));
                Console.WriteLine("Only in target:\n  " + string.Join("\n  ", b.Except(a)));
            }
            check(before == after && before.Contains("CREATE TABLE", StringComparison.Ordinal), "pg_dump sees the same objects after applying every CREATE script to an empty database");

            // Text the user sees.
            string create = await service.ScriptAsync(parent, ScriptVerb.Create);
            check(create.StartsWith($"-- Database: {source}\r\n\r\n/****** Object:  Table {S}.\"Parent T\"    Script Date: ", StringComparison.Ordinal), "CREATE opens with the database and the SSMS-style object header");
            check(create.Contains("GENERATED ALWAYS AS IDENTITY (START WITH 10", StringComparison.Ordinal) && create.Contains($"\"qty\" {S}.pos", StringComparison.Ordinal) && create.Contains("COLLATE pg_catalog.\"C\"", StringComparison.Ordinal), "Identity, qualified types and collations are in the column list");
            string drop = await service.ScriptAsync(parent, ScriptVerb.Drop);
            check(drop.EndsWith($"DROP TABLE IF EXISTS {S}.\"Parent T\";\r\n", StringComparison.Ordinal) && !drop.Contains("CASCADE", StringComparison.Ordinal), "DROP To uses IF EXISTS, as observed, and never CASCADE");
            string both = await service.ScriptAsync(child, ScriptVerb.DropAndCreate);
            check(both.IndexOf("DROP TABLE IF EXISTS", StringComparison.Ordinal) < both.IndexOf("CREATE TABLE", StringComparison.Ordinal) && both.Split("/****** Object:").Length == 3, "DROP And CREATE repeats the object header, as observed");
            check(await service.ScriptAsync(child, ScriptVerb.Select) == $"-- Database: {source}\r\n\r\nSELECT \"id\"\r\n      ,\"parent_id\"\r\n      ,\"other\"\r\n  FROM {S}.\"child\";\r\n", "SELECT To has the observed layout");
            check((await service.ScriptAsync(child, ScriptVerb.Insert)).EndsWith($"INSERT INTO {S}.\"child\"\r\n           (\"id\"\r\n           ,\"parent_id\"\r\n           ,\"other\")\r\n     VALUES\r\n           (<id, integer,>\r\n           ,<parent_id, bigint,>\r\n           ,<other, bigint,>);\r\n", StringComparison.Ordinal), "INSERT To has the observed layout and placeholders");
            check((await service.ScriptAsync(child, ScriptVerb.Update)).EndsWith($"UPDATE {S}.\"child\"\r\n   SET \"id\" = <id, integer,>\r\n      ,\"parent_id\" = <parent_id, bigint,>\r\n      ,\"other\" = <other, bigint,>\r\n WHERE <Search Conditions,,>;\r\n", StringComparison.Ordinal), "UPDATE To has the observed layout");
            check((await service.ScriptAsync(child, ScriptVerb.Delete)).EndsWith($"DELETE FROM {S}.\"child\"\r\n      WHERE <Search Conditions,,>;\r\n", StringComparison.Ordinal), "DELETE To keeps the search condition to fill in");
            string insertGenerated = await service.ScriptAsync(parent, ScriptVerb.Insert);
            check(!insertGenerated.Contains("\"id\"", StringComparison.Ordinal) && !insertGenerated.Contains("\"twice\"", StringComparison.Ordinal) && insertGenerated.Contains("\"code\"", StringComparison.Ordinal), "INSERT To leaves out identity ALWAYS and generated columns");
            string viewCreate = await service.ScriptAsync(view, ScriptVerb.Create), viewReplace = await service.ScriptAsync(view, ScriptVerb.CreateOrReplace);
            check(viewCreate.Contains($"CREATE VIEW {S}.\"v_parent\" WITH (security_barrier=true) AS", StringComparison.Ordinal) && viewReplace.Contains("CREATE OR REPLACE VIEW", StringComparison.Ordinal) && viewCreate.Contains($"{S}.\"Parent T\"", StringComparison.Ordinal), "Views keep options and qualify their tables");
            string routine = await service.ScriptAsync(plus, ScriptVerb.Create), routineReplace = await service.ScriptAsync(plus, ScriptVerb.CreateOrReplace);
            check(routine.Contains("\nCREATE FUNCTION", StringComparison.Ordinal) && routineReplace.Contains("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal), "CREATE and CREATE OR REPLACE differ only where they should for routines");
            check((await service.ScriptAsync(plus, ScriptVerb.Drop)).EndsWith($"DROP FUNCTION IF EXISTS {S}.\"plus\"(a integer, VARIADIC more integer[]);\r\n", StringComparison.Ordinal), "DROP keeps the argument list of an overloadable routine");
            check((await service.ScriptAsync(plus, ScriptVerb.Execute)).EndsWith($"SELECT {S}.\"plus\"(\r\n    <a, integer,>\r\n   ,VARIADIC <more, integer[],>);\r\n", StringComparison.Ordinal), "EXECUTE on a function is a SELECT with placeholders");
            check((await service.ScriptAsync(series, ScriptVerb.Execute)).Contains("SELECT *", StringComparison.Ordinal), "EXECUTE on a set-returning function reads FROM it");
            check((await service.ScriptAsync(work, ScriptVerb.Execute)).EndsWith($"CALL {S}.\"work\"(\r\n    <a, integer,>\r\n   ,NULL);\r\n", StringComparison.Ordinal), "EXECUTE on a procedure is a CALL, with NULL for its OUT argument");
            check((await service.ScriptAsync(touch, ScriptVerb.Drop)).Contains("DROP FUNCTION IF EXISTS", StringComparison.Ordinal), "A function without arguments drops");

            // Index nodes
            uint Plain = await Oid($"SELECT '{S}.lower_code'::regclass::oid");
            uint Backing = await Oid($"SELECT conindid FROM pg_constraint WHERE conrelid='{S}.\"Parent T\"'::regclass AND contype='p'");
            var plainIndex = new CatalogEntry(CatalogKind.Index, "lower_code", source, "sc ript", Plain);
            var backingIndex = new CatalogEntry(CatalogKind.Index, "pk", source, "sc ript", Backing);
            string plainCreate = await service.ScriptAsync(plainIndex, ScriptVerb.Create);
            check(plainCreate.Contains("CREATE INDEX lower_code ON", StringComparison.Ordinal) && plainCreate.Contains("COMMENT ON INDEX", StringComparison.Ordinal), "A plain index is scripted with CREATE INDEX");
            check((await service.ScriptAsync(backingIndex, ScriptVerb.Create)).Contains("ADD CONSTRAINT", StringComparison.Ordinal) && (await service.ScriptAsync(backingIndex, ScriptVerb.Drop)).Contains("DROP CONSTRAINT IF EXISTS", StringComparison.Ordinal), "An index that backs a constraint is scripted through the constraint");

            var maintenance = await new IndexMaintenanceCatalog(On(source)).InspectAsync(Plain);
            check(maintenance.Name == "lower_code" && maintenance.Table.Name == "Parent T", "Rebuild finds the index and its table from the node's OID");

            // Keys, Constraints and Triggers folders
            var browse = new CatalogService(On(source));
            var folders = await browse.ChildrenAsync(parent with { Name = "Parent T" });
            check(folders.Select(f => f.Name).SequenceEqual(["Columns", "Keys", "Constraints", "Triggers", "Indexes"]), "A table lists Columns, Keys, Constraints, Triggers, Indexes, as in SSMS");
            check((await browse.ChildrenAsync(view)).Select(f => f.Name).SequenceEqual(["Columns", "Triggers", "Indexes"]), "A view has Triggers but no Keys or Constraints");
            async Task<IReadOnlyList<CatalogEntry>> In(CatalogEntry table, string folder) => await browse.ChildrenAsync((await browse.ChildrenAsync(table)).Single(f => f.Name == folder));
            var parentKeys = await In(parent, "Keys"); var childKeys = await In(child, "Keys");
            check(parentKeys.Select(k => k.Name).SequenceEqual(["Parent T_pkey", "Parent T_code_key"]) && childKeys.Select(k => k.Name).SequenceEqual(["child_pkey", "child_parent_id_fkey", "other_fk"]), "Keys lists the primary key first, then unique and foreign keys");
            var checks = await In(parent, "Constraints"); var triggers = await In(child, "Triggers");
            check(checks.Select(k => k.Name).SequenceEqual(["late", "qty_big"]) && checks.All(k => k.Kind == CatalogKind.Constraint && !k.CanExpand), "Constraints lists the checks");
            check(triggers.Select(t => t.Name).SequenceEqual(["t_off"]) && triggers[0].Kind == CatalogKind.Trigger && !triggers[0].CanExpand, "Triggers lists user triggers and leaves out the internal ones of foreign keys");
            var otherFk = childKeys.Single(k => k.Name == "other_fk"); var late = checks.Single(k => k.Name == "late");
            check(ObjectScripts.Noun(otherFk) == "Key" && ObjectScripts.Noun(late) == "Constraint" && ObjectScripts.Noun(triggers[0]) == "Trigger" && ObjectScripts.Verbs(otherFk).Count == 3, "The new nodes have definition scripts");
            string keyScript = await service.ScriptAsync(otherFk, ScriptVerb.DropAndCreate);
            check(keyScript.Contains($"DROP CONSTRAINT IF EXISTS \"other_fk\";", StringComparison.Ordinal) && keyScript.Contains("NOT VALID", StringComparison.Ordinal) && keyScript.Contains("COMMENT ON CONSTRAINT", StringComparison.Ordinal), "A key is scripted through ALTER TABLE, keeping NOT VALID and its comment");
            string triggerScript = await service.ScriptAsync(triggers[0], ScriptVerb.DropAndCreate);
            check(triggerScript.Contains("DROP TRIGGER IF EXISTS \"t_off\" ON", StringComparison.Ordinal) && triggerScript.Contains("DISABLE TRIGGER", StringComparison.Ordinal), "A disabled trigger stays disabled");
            await using (var apply = new QuerySession(On(target)))
            {
                await apply.OpenAsync();
                foreach (var entry in new[] { otherFk, late, triggers[0] }) await apply.ExecuteAsync(await service.ScriptAsync(entry, ScriptVerb.DropAndCreate));
            }
            check(Normalize(await Dump(root, target)) == before, "DROP And CREATE of a key, constraint and trigger leaves the same objects");

            // Take Offline and Bring Online
            await Admin($"CREATE DATABASE {offline} TEMPLATE template0");
            var edits = new DatabaseEditService(connection);
            var catalogRoot = new CatalogService(connection);
            async Task<CatalogEntry> Listed() => (await catalogRoot.ChildrenAsync(new(CatalogKind.Databases, "Databases"))).Single(d => d.Database == offline);
            check(await Listed() is { IsOffline: false, CanExpand: true } online && online.Name == offline, "An online database is listed as before");
            await edits.SetOnlineAsync(offline, false);
            var off = await Listed();
            check(off.IsOffline && !off.CanExpand && off.Name == offline + " (Offline)" && off.Database == offline && ObjectScripts.Noun(off) is null, "An offline database stays in the tree, marked, without children or scripts");
            string? refusal = null;
            try { await using var c = new NpgsqlConnection(On(offline)); await c.OpenAsync(); } catch (PostgresException ex) { refusal = ex.SqlState; }
            check(refusal == "55000", "New connections are refused while it is offline");
            await edits.SetOnlineAsync(offline, true);
            await using (var c = new NpgsqlConnection(On(offline))) { await c.OpenAsync(); check(c.State == System.Data.ConnectionState.Open && !(await Listed()).IsOffline, "Bring Online accepts connections again"); }
            NpgsqlConnection.ClearAllPools();
            check(!DatabaseEditService.CanTakeOffline("postgres", "x") && !DatabaseEditService.CanTakeOffline("loja", "loja") && DatabaseEditService.CanTakeOffline("loja", "x"), "The maintenance database and the connected one cannot go offline");
            bool protectedRefused = false;
            try { await edits.SetOnlineAsync("postgres", false); } catch (ArgumentException) { protectedRefused = true; }
            check(protectedRefused, "The service also refuses the maintenance database");

            // Availability, as in the SSMS menus.
            check(ObjectScripts.Verbs(parent).SequenceEqual([ScriptVerb.Create, ScriptVerb.Drop, ScriptVerb.DropAndCreate, ScriptVerb.Select, ScriptVerb.Insert, ScriptVerb.Update, ScriptVerb.Delete]), "Table: the verbs observed in SSMS");
            check(ObjectScripts.Verbs(schema).SequenceEqual([ScriptVerb.Create, ScriptVerb.Drop, ScriptVerb.DropAndCreate]) && ObjectScripts.Verbs(plainIndex).Count == 3, "Schema and index: only the definition verbs");
            check(ObjectScripts.Verbs(work).Contains(ScriptVerb.Execute) && !ObjectScripts.Verbs(work).Contains(ScriptVerb.Select) && ObjectScripts.Noun(work) == "Procedure" && ObjectScripts.Noun(plus) == "Function", "Routines: EXECUTE and the right noun");
            check(ObjectScripts.Verbs(new CatalogEntry(CatalogKind.Relation, "f", source, "x", 1) { RelationType = "f" }).Count == 0 && ObjectScripts.Noun(new CatalogEntry(CatalogKind.Column, "c")) is null, "Nodes without a script offer none");
            check(ObjectScripts.Noun(new CatalogEntry(CatalogKind.Role, "pg_monitor") { RoleName = "pg_monitor" }) is null, "Predefined roles have no script");
            bool refused = false;
            try { await service.ScriptAsync(schema, ScriptVerb.Select); } catch (ObjectScriptException) { refused = true; }
            check(refused, "A verb the object does not offer is refused");
            bool gone = false;
            try { await service.ScriptAsync(parent with { Oid = 4000000000 }, ScriptVerb.Create); } catch (ObjectScriptException ex) { gone = ex.Message.Contains("no longer exists", StringComparison.Ordinal); }
            check(gone, "An object dropped since the tree loaded says so");
            check(ObjectScripts.FileName(parent) == "sc ript.\"Parent T\".sql".Replace('"', '_') && ObjectScripts.FileName(plus) == "sc ript.plus.sql", "The suggested file name is valid and leaves out the arguments");

            // A hostile name cannot end the header comment and run as SQL.
            await using (var c = new NpgsqlConnection(On(source)))
            {
                await c.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE TABLE {S}.\"x*/ DROP TABLE y;\nz\" (a integer)", c);
                await command.ExecuteNonQueryAsync();
            }
            var hostile = new CatalogEntry(CatalogKind.Relation, "h", source, "sc ript", await Oid($"SELECT oid FROM pg_class WHERE relname LIKE 'x*/%'")) { RelationType = "r" };
            string hostileScript = await service.ScriptAsync(hostile, ScriptVerb.Create);
            string header = hostileScript.Split("\r\n")[2];
            check(header.StartsWith("/****** Object:", StringComparison.Ordinal) && header.EndsWith("******/", StringComparison.Ordinal) && !header[..^7].Contains("*/", StringComparison.Ordinal), "A name with the end of a comment or a line break stays inside the header");

            // Roles: no password, and memberships and settings survive.
            await Admin($"CREATE ROLE {group} NOLOGIN");
            await Admin($"CREATE ROLE {role} LOGIN PASSWORD 'qscr-secret' CONNECTION LIMIT 5");
            await Admin($"ALTER ROLE {role} SET statement_timeout = '5s'");
            await Admin($"ALTER ROLE {role} SET search_path = a, b");
            await Admin($"GRANT {group} TO {role}");
            await Admin($"COMMENT ON ROLE {role} IS 'note'");
            uint roleOid; { await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var command = new NpgsqlCommand($"SELECT oid FROM pg_roles WHERE rolname='{role}'", c); roleOid = (uint)(await command.ExecuteScalarAsync())!; }
            var roleEntry = new CatalogEntry(CatalogKind.Role, role, null, null, roleOid) { RoleName = role };
            var roleService = new ObjectScriptService(connection);
            string roleScript = await roleService.ScriptAsync(roleEntry, ScriptVerb.Create);
            check(!roleScript.Contains("qscr-secret", StringComparison.Ordinal) && !roleScript.Contains("md5", StringComparison.Ordinal) && !roleScript.Contains("SCRAM", StringComparison.Ordinal) && roleScript.Contains("password is not scripted", StringComparison.Ordinal), "A role script holds no password and says so");
            await Admin($"DROP ROLE {role}");
            await using (var apply = new QuerySession(connection)) { await apply.OpenAsync(); await apply.ExecuteAsync(roleScript); }
            { await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var command = new NpgsqlCommand($"SELECT rolcanlogin AND rolconnlimit=5 AND array_length(rolconfig,1)=2 AND 'search_path=a, b'=ANY(rolconfig) AND pg_has_role(oid, '{group}', 'member') AND shobj_description(oid,'pg_authid')='note' FROM pg_roles WHERE rolname='{role}'", c); check((bool)(await command.ExecuteScalarAsync())!, "The role script recreates login, limit, settings, membership and comment"); }

            // Database: the whole script runs through the query path, from another database.
            await Admin($"CREATE DATABASE {copy} TEMPLATE template0 CONNECTION LIMIT 7");
            await Admin($"COMMENT ON DATABASE {copy} IS 'db note'");
            uint databaseOid; { await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var command = new NpgsqlCommand($"SELECT oid FROM pg_database WHERE datname='{copy}'", c); databaseOid = (uint)(await command.ExecuteScalarAsync())!; }
            var databaseEntry = new CatalogEntry(CatalogKind.Database, copy, copy, null, databaseOid);
            string dropAndCreate = await new ObjectScriptService(On(copy)).ScriptAsync(databaseEntry, ScriptVerb.DropAndCreate);
            check(dropAndCreate.Contains($"DROP DATABASE IF EXISTS \"{copy}\";", StringComparison.Ordinal) && dropAndCreate.Contains("TEMPLATE = template0", StringComparison.Ordinal) && dropAndCreate.Contains("CONNECTION LIMIT = 7", StringComparison.Ordinal), "The database script names its settings");
            await using (var apply = new QuerySession(connection)) { await apply.OpenAsync(); await apply.ExecuteAsync(dropAndCreate); }
            { await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var command = new NpgsqlCommand($"SELECT datconnlimit=7 AND oid<>{databaseOid} AND shobj_description(oid,'pg_database')='db note' FROM pg_database WHERE datname='{copy}'", c); check((bool)(await command.ExecuteScalarAsync())!, "DROP And CREATE of a database runs statement by statement and replaces it"); }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            foreach (var database in new[] { source, target, copy, offline }) await Admin($"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
            foreach (var name in new[] { role, group }) await Admin($"DROP ROLE IF EXISTS {name}");
        }
    }

    private static async Task<string> Dump(NpgsqlConnectionStringBuilder root, string database)
    {
        string tools = Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN") ?? @"C:\Program Files\PostgreSQL\17\bin";
        var start = new ProcessStartInfo(Path.Combine(tools, "pg_dump.exe")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var argument in new[] { "-h", root.Host!, "-p", root.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", root.Username!, "--schema-only", "--no-owner", "--no-privileges", "--schema=sc ript", database }) start.ArgumentList.Add(argument);
        if (root.Password is { } password) start.Environment["PGPASSWORD"] = password;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("pg_dump failed: " + await errors);
        return await output;
    }
}
