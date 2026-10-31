using Npgsql;
using PGMS.PostgreSql;

internal static class IndexIdentityChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_idx_guard_" + Guid.NewGuid().ToString("N");
        string target = ObjectSql.Relation("public", table);
        string name = table + "_ix", alternate = table + "_other", away = table + "_away";
        string index = ObjectSql.Relation("public", name);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        var service = new IndexInspectionService(connection); var edits = new AdministrativeEditService(connection);
        async Task<RelationEdit> Request()
        {
            var row = (await service.InspectAsync("public", table)).Single(i => i.Name == name);
            return new(RelationOperation.DropIndex, "public", table, name, []) { ExpectedIdentity = new(row.TableOid, row.Index.Oid) };
        }
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {target}(id int PRIMARY KEY); CREATE INDEX {SqlIdentifier.Quote(name)} ON {target}(id);");
            var request = await Request();
            await session.ExecuteAsync($"DROP INDEX {index}; CREATE INDEX {SqlIdentifier.Quote(name)} ON {target}(id);");
            bool rejected = false;
            try { await edits.ApplyAsync(request); } catch (InvalidOperationException ex) when (ex.Message.Contains("identity")) { rejected = true; }
            check(rejected && (await service.InspectAsync("public", table)).Any(i => i.Name == name), "index stale identity preserves same-name replacement");
            request = await Request();
            await edits.ApplyAsync(request);
            check(!(await service.InspectAsync("public", table)).Any(i => i.Name == name), "fresh index identity commits standard Drop");
            var primary = (await service.InspectAsync("public", table)).Single();
            rejected = false;
            try { await edits.ApplyAsync(request with { Name = primary.Name, ExpectedIdentity = new(primary.TableOid, primary.Index.Oid) }); }
            catch (PostgresException ex) when (ex.SqlState == "2BP01") { rejected = true; }
            check(rejected && (await service.InspectAsync("public", table)).Count == 1, "guarded Drop preserves constraint-backed index via RESTRICT");
            await session.ExecuteAsync($"CREATE INDEX {SqlIdentifier.Quote(name)} ON {target}(id); CREATE INDEX {SqlIdentifier.Quote(alternate)} ON {target}(id);");
            request = await Request();
            rejected = false;
            try
            {
                await edits.ApplyAsync(new AfterPreflight(request, async () =>
                {
                    await session.ExecuteAsync($"ALTER INDEX {index} RENAME TO {SqlIdentifier.Quote(away)}; ALTER INDEX {ObjectSql.Relation("public", alternate)} RENAME TO {SqlIdentifier.Quote(name)};");
                }));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("identity")) { rejected = true; }
            var afterRace = await service.InspectAsync("public", table);
            check(rejected && afterRace.Any(i => i.Name == name) && afterRace.Any(i => i.Name == away), "post-Drop verification rolls back wrong deletion after concurrent name swap");
            await edits.ApplyAsync(await Request());
            check(!(await service.InspectAsync("public", table)).Any(i => i.Name == name), "reinspection permits deletion of reviewed replacement after race");
            await session.ExecuteAsync($"ALTER INDEX {ObjectSql.Relation("public", away)} RENAME TO {SqlIdentifier.Quote(name)};");
            request = await Request();
            await session.ExecuteAsync($"DROP TABLE {target}; CREATE TABLE {target}(id int); CREATE INDEX {SqlIdentifier.Quote(name)} ON {target}(id);");
            rejected = false;
            try { await edits.ApplyAsync(request); } catch (InvalidOperationException ex) when (ex.Message.Contains("identity")) { rejected = true; }
            check(rejected && (await service.InspectAsync("public", table)).Count == 1, "index identity rejects replaced owning table");
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};"); }
    }

    private sealed record AfterPreflight(RelationEdit Edit, Func<Task> Change) : IAdministrativeEdit
    {
        public string ToSql() => Edit.ToSql();
        public async Task ValidateAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken token = default)
        { await Edit.ValidateAsync(c, tx, token); await Change(); }
        public Task VerifyAppliedAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken token = default) => Edit.VerifyAppliedAsync(c, tx, token);
    }
}
