using Npgsql;
using PGMS.PostgreSql;

internal static class IndexInspectionChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_idx_" + Guid.NewGuid().ToString("N");
        string target = ObjectSql.Relation("public", table);
        string index = table + "\"ix";
        await using var session = new QuerySession(connection); await session.OpenAsync();
        var service = new IndexInspectionService(connection);
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {target}(id int, other int);");
            check((await service.InspectAsync("public", table)).Count == 0, "index inspector distinguishes empty table");
            await session.ExecuteAsync($"CREATE UNIQUE INDEX {SqlIdentifier.Quote(index)} ON {target} ((id+1)) INCLUDE(other) WHERE id>0;");
            var row = (await service.InspectAsync("public", table)).Single();
            check(row.Name == index && row.TableOid > 0 && row.Index.Oid > 0 && row.Index.Unique && row.Index.Valid && row.Index.Ready, "index inspector preserves exact name, OIDs and flags");
            check(row.Index.Definition.Contains("INCLUDE") && row.Index.Definition.Contains("WHERE") && row.Index.Definition.Contains("id + 1"), "index inspector retains expression, INCLUDE and predicate");
            bool rejected = false;
            try { await service.InspectAsync("public", table + "_missing"); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "index inspector reports missing target instead of empty result");
            await session.ExecuteAsync($"DROP INDEX {ObjectSql.Relation("public", index)};");
            check((await service.InspectAsync("public", table)).Count == 0, "index reinspection reflects deletion");
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};"); }
    }
}
