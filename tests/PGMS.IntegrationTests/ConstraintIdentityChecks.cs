using PGMS.PostgreSql;

internal static class ConstraintIdentityChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_ci_" + Guid.NewGuid().ToString("N");
        string target = "public." + SqlIdentifier.Quote(table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        var edits = new AdministrativeEditService(connection);
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {target}(id int, other int);");
            foreach (string kind in new[] { "PRIMARY KEY", "UNIQUE" })
            {
                await session.ExecuteAsync($"ALTER TABLE {target} ADD CONSTRAINT inspected {kind}(id);");
                var snapshot = (await new TableConstraintService(connection).InspectAsync("public", table)).Single();
                check(snapshot.Identity is { TableOid: > 0, ConstraintOid: > 0 }, kind + " inspection includes object identities");
                var request = new TableConstraintEdit(TableConstraintOperation.Drop, "public", table, "inspected", []) { ExpectedIdentity = snapshot.Identity };
                await session.ExecuteAsync($"ALTER TABLE {target} DROP CONSTRAINT inspected; ALTER TABLE {target} ADD CONSTRAINT inspected {kind}(other);");
                bool rejected = false;
                try { await edits.ApplyAsync(request); } catch (InvalidOperationException ex) when (ex.Message.Contains("identity")) { rejected = true; }
                var current = (await new TableConstraintService(connection).InspectAsync("public", table)).Single();
                check(rejected && current.Definition.Contains("other"), kind + " replacement with same name survives stale Drop");
                await edits.ApplyAsync(request with { ExpectedIdentity = current.Identity });
                check((await new TableConstraintService(connection).InspectAsync("public", table)).Count == 0, kind + " refreshed identity permits real Drop");
            }

            await session.ExecuteAsync($"ALTER TABLE {target} ADD CONSTRAINT reference_key UNIQUE(id); ALTER TABLE {target} ADD CONSTRAINT inspected CHECK(id > 0);");
            var snapshot54 = (await new CheckConstraintService(connection).InspectAsync("public", table)).Single();
            check(snapshot54.Identity is { TableOid: > 0, ConstraintOid: > 0 }, "CHECK snapshot includes identities");
            var request54 = new CheckConstraintEdit(CheckConstraintOperation.Drop, "public", table, "inspected", "") { ExpectedIdentity = snapshot54.Identity };
            await session.ExecuteAsync($"ALTER TABLE {target} DROP CONSTRAINT inspected; ALTER TABLE {target} ADD CONSTRAINT inspected CHECK(id > 0);");
            bool rejected54 = false;
            try { await edits.ApplyAsync(request54); } catch (InvalidOperationException ex) when (ex.Message.Contains("identity")) { rejected54 = true; }
            check(rejected54 && (await new CheckConstraintService(connection).InspectAsync("public", table)).Count == 1, "CHECK stale Drop preserves same-name replacement");
            var current54 = (await new CheckConstraintService(connection).InspectAsync("public", table)).Single();
            await session.ExecuteAsync($"DROP TABLE {target}; CREATE TABLE {target}(id int UNIQUE, other int); ALTER TABLE {target} ADD CONSTRAINT inspected CHECK(id > 0);");
            rejected54 = false;
            try { await edits.ApplyAsync(request54 with { ExpectedIdentity = current54.Identity }); } catch (InvalidOperationException ex) when (ex.Message.Contains("identity")) { rejected54 = true; }
            check(rejected54, "CHECK stale Drop refuses replacement table");
            current54 = (await new CheckConstraintService(connection).InspectAsync("public", table)).Single();
            await edits.ApplyAsync(request54 with { ExpectedIdentity = current54.Identity });
            check((await new CheckConstraintService(connection).InspectAsync("public", table)).Count == 0, "CHECK refreshed snapshot permits Drop");
            await session.ExecuteAsync($"DROP TABLE {target}; CREATE TABLE {target}(id int, other int);");

            await session.ExecuteAsync($"ALTER TABLE {target} ADD CONSTRAINT reference_key UNIQUE(id); ALTER TABLE {target} ADD CONSTRAINT inspected FOREIGN KEY(other) REFERENCES {target}(id);");
            var snapshot55 = (await new ForeignKeyService(connection).InspectAsync("public", table)).Single();
            check(snapshot55.Identity is { TableOid: > 0, ConstraintOid: > 0 }, "FOREIGN KEY snapshot includes identities");
            var request55 = new ForeignKeyEdit(ForeignKeyOperation.Drop, "public", table, "inspected", [], "public", table, [], ReferentialAction.NoAction, ReferentialAction.NoAction) { ExpectedIdentity = snapshot55.Identity };
            await session.ExecuteAsync($"ALTER TABLE {target} DROP CONSTRAINT inspected; ALTER TABLE {target} ADD CONSTRAINT inspected FOREIGN KEY(other) REFERENCES {target}(id);");
            bool rejected55 = false;
            try { await edits.ApplyAsync(request55); } catch (InvalidOperationException ex) when (ex.Message.Contains("identity")) { rejected55 = true; }
            check(rejected55 && (await new ForeignKeyService(connection).InspectAsync("public", table)).Count == 1, "FOREIGN KEY stale Drop preserves same-name replacement");
            var current55 = (await new ForeignKeyService(connection).InspectAsync("public", table)).Single();
            await session.ExecuteAsync($"DROP TABLE {target}; CREATE TABLE {target}(id int UNIQUE, other int); ALTER TABLE {target} ADD CONSTRAINT inspected FOREIGN KEY(other) REFERENCES {target}(id);");
            rejected55 = false;
            try { await edits.ApplyAsync(request55 with { ExpectedIdentity = current55.Identity }); } catch (InvalidOperationException ex) when (ex.Message.Contains("identity")) { rejected55 = true; }
            check(rejected55, "FOREIGN KEY stale Drop refuses replacement table");
            current55 = (await new ForeignKeyService(connection).InspectAsync("public", table)).Single();
            await edits.ApplyAsync(request55 with { ExpectedIdentity = current55.Identity });
            check((await new ForeignKeyService(connection).InspectAsync("public", table)).Count == 0, "FOREIGN KEY refreshed snapshot permits Drop");
            await session.ExecuteAsync($"DROP TABLE {target}; CREATE TABLE {target}(id int, other int);");

            foreach (string inspectedTable in new[] { table, table + "_missing", table + "_view", table + "_partitioned" })
            {
                if (inspectedTable.EndsWith("_view")) await session.ExecuteAsync($"CREATE VIEW public.{SqlIdentifier.Quote(inspectedTable)} AS SELECT 1 AS id;");
                if (inspectedTable.EndsWith("_partitioned")) await session.ExecuteAsync($"CREATE TABLE public.{SqlIdentifier.Quote(inspectedTable)}(id int) PARTITION BY RANGE(id);");
                foreach (var inspect in new Func<string, Task<int>>[] {
                    async name => (await new TableConstraintService(connection).InspectAsync("public", name)).Count,
                    async name => (await new CheckConstraintService(connection).InspectAsync("public", name)).Count,
                    async name => (await new ForeignKeyService(connection).InspectAsync("public", name)).Count })
                {
                    bool invalidTarget = inspectedTable.EndsWith("_missing") || inspectedTable.EndsWith("_view");
                    bool rejected = false; int count = -1;
                    try { count = await inspect(inspectedTable); } catch (InvalidOperationException ex) when (ex.Message.Contains("not found")) { rejected = true; }
                    check(invalidTarget ? rejected : !rejected && count == 0, "constraint inspection distinguishes missing/view targets from empty ordinary/partitioned tables");
                }
            }
            // Additional constraint families.
        }
        finally { await session.ExecuteAsync($"DROP VIEW IF EXISTS public.{SqlIdentifier.Quote(table + "_view")}; DROP TABLE IF EXISTS public.{SqlIdentifier.Quote(table + "_partitioned")}; DROP TABLE IF EXISTS {target} CASCADE;"); }
    }
}
