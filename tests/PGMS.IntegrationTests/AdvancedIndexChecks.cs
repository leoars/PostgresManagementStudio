using Npgsql;
using PGMS.PostgreSql;

internal static class AdvancedIndexChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_adv_" + Guid.NewGuid().ToString("N");
        string target = ObjectSql.Relation("public", table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        var service = new AdministrativeEditService(connection);
        var edit = new RelationEdit(RelationOperation.CreateIndex, "public", table, table + "_ix", ["id"], true);
        bool Rejects(RelationEdit request) { try { request.ToSql(); return false; } catch (ArgumentException) { return true; } }
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {target}(id int, {SqlIdentifier.Quote("payload\"quoted")} text);");
            var include = edit with { IncludedColumns = ["payload\"quoted"] };
            await service.ApplyAsync(include);
            var index = (await new IndexInspectionService(connection).InspectAsync("public", table)).Single();
            check(index.Index.Unique && index.Index.Definition.Contains("INCLUDE") && index.Index.Definition.Contains("payload"), "INCLUDE creates a unique index with quoted payload");
            await session.ExecuteAsync($"INSERT INTO {target} VALUES(1, 'a');");
            bool duplicate = false;
            try { await session.ExecuteAsync($"INSERT INTO {target} VALUES(1, 'b');"); } catch (PostgresException ex) when (ex.SqlState == "23505") { duplicate = true; }
            check(duplicate, "INCLUDE payload does not participate in uniqueness");
            check(Rejects(edit with { IncludedColumns = ["id"] }) && Rejects(edit with { IncludedColumns = ["x", "x"] }), "INCLUDE rejects overlapping and duplicate names");
            check(Rejects(edit with { IncludedColumns = Enumerable.Range(1,32).Select(i => "c"+i).ToArray() }), "INCLUDE enforces combined column limit");
            await session.ExecuteAsync($"DROP INDEX {ObjectSql.Relation("public", edit.Name)};");
            await service.ApplyAsync(edit);
            await session.ExecuteAsync($"INSERT INTO {target} VALUES(NULL, 'a'),(NULL, 'b');");
            check(true, "default unique index accepts multiple NULLs");
            await session.ExecuteAsync($"DROP INDEX {ObjectSql.Relation("public", edit.Name)};");
            bool existingNulls = false;
            try { await service.ApplyAsync(edit with { NullsNotDistinct = true }); } catch (PostgresException ex) when (ex.SqlState == "23505") { existingNulls = true; }
            check(existingNulls && (await new IndexInspectionService(connection).InspectAsync("public", table)).Count == 0, "NULLS NOT DISTINCT rejects existing duplicates atomically");
            await session.ExecuteAsync($"DELETE FROM {target} WHERE id IS NULL;");
            await service.ApplyAsync(edit with { NullsNotDistinct = true });
            await session.ExecuteAsync($"INSERT INTO {target} VALUES(NULL, 'a');");
            bool newNull = false;
            try { await session.ExecuteAsync($"INSERT INTO {target} VALUES(NULL, 'b');"); } catch (PostgresException ex) when (ex.SqlState == "23505") { newNull = true; }
            check(newNull, "NULLS NOT DISTINCT enforces unique NULL on new writes");
            check(Rejects(edit with { Unique = false, NullsNotDistinct = true }), "NULLS NOT DISTINCT refuses nonunique request");
            await session.ExecuteAsync($"DROP INDEX {ObjectSql.Relation("public", edit.Name)};");
            check(Rejects(edit with { FillFactor = 9 }) && Rejects(edit with { FillFactor = 101 }), "fillfactor rejects values outside PostgreSQL range");
            foreach (int fill in new[] { 10, 75, 100 })
            {
                await service.ApplyAsync(edit with { FillFactor = fill });
                var filled = (await new IndexInspectionService(connection).InspectAsync("public", table)).Single();
                check(filled.Index.Definition.Contains($"fillfactor='{fill}'"), $"fillfactor {fill} persists in catalog");
                await session.ExecuteAsync($"DROP INDEX {ObjectSql.Relation("public", edit.Name)};");
            }
            check(!edit.ToSql().Contains("fillfactor"), "blank fillfactor leaves server default untouched");
            var hash = edit with { Method = IndexMethod.Hash, Unique = false, FillFactor = 75 };
            check(Rejects(hash with { Unique = true }) && Rejects(hash with { IncludedColumns = ["payload"] }) && Rejects(hash with { Columns = ["id", "payload"] }), "hash rejects UNIQUE, INCLUDE and multiple keys before SQL");
            check(Rejects(edit with { Method = (IndexMethod)999 }), "index method uses a closed allowlist");
            await service.ApplyAsync(hash);
            var hashed = (await new IndexInspectionService(connection).InspectAsync("public", table)).Single();
            check(hashed.Index.Method == "hash" && !hashed.Index.Unique && hashed.Index.Definition.Contains("fillfactor='75'"), "hash creation persists method and fillfactor");
            await session.ExecuteAsync($"INSERT INTO {target} VALUES(1, 'duplicate');");
            check(true, "hash index permits duplicate keys");
            await session.ExecuteAsync($"DROP INDEX {ObjectSql.Relation("public", edit.Name)}; DELETE FROM {target} WHERE id = 1;");
            await using var catalog = new NpgsqlConnection(connection); await catalog.OpenAsync();
            foreach (var direction in Enum.GetValues<IndexSortOrder>())
            foreach (var placement in Enum.GetValues<IndexNullPlacement>())
            {
                var ordered = edit with { Unique = false, Columns = ["id", "payload\"quoted"], SortOrder = direction, NullPlacement = placement };
                await service.ApplyAsync(ordered);
                await using var command = new NpgsqlCommand("SELECT i.indoption::text FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relname=@name", catalog);
                command.Parameters.AddWithValue("name", edit.Name);
                bool descending = direction == IndexSortOrder.Descending;
                bool first = placement == IndexNullPlacement.First || (placement == IndexNullPlacement.Default && descending);
                int flags = (descending ? 1 : 0) | (first ? 2 : 0);
                check((string)(await command.ExecuteScalarAsync())! == $"{flags} {flags}", $"catalog key flags match {direction}/{placement} on both quoted keys");
                await session.ExecuteAsync($"DROP INDEX {ObjectSql.Relation("public", edit.Name)};");
            }
            await service.ApplyAsync(edit with { IncludedColumns = ["payload\"quoted"], SortOrder = IndexSortOrder.Descending, NullPlacement = IndexNullPlacement.Last, NullsNotDistinct = true, FillFactor = 75 });
            var combined = (await new IndexInspectionService(connection).InspectAsync("public", table)).Single();
            check(combined.Index.Definition.Contains("DESC NULLS LAST") && combined.Index.Definition.Contains("INCLUDE") && combined.Index.Definition.Contains("NULLS NOT DISTINCT"), "ordering combines with INCLUDE, NULL equality and fillfactor in actual SQL");
            check(Rejects(edit with { SortOrder = (IndexSortOrder)999 }) && Rejects(edit with { NullPlacement = (IndexNullPlacement)999 }), "ordering uses closed allowlists");
            check(Rejects(hash with { SortOrder = IndexSortOrder.Descending }) && Rejects(hash with { NullPlacement = IndexNullPlacement.First }), "hash rejects explicit ordering");
            check((hash with { Operation = RelationOperation.DropIndex, SortOrder = (IndexSortOrder)999 }).ToSql().StartsWith("DROP INDEX"), "Drop ignores creation-only ordering");
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};"); }
    }
}
