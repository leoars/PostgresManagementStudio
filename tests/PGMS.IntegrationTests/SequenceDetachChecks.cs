using Npgsql;
using PGMS.PostgreSql;

internal static class SequenceDetachChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string name = "pgms_detach_" + Guid.NewGuid().ToString("N"), target = ObjectSql.Relation("public", name);
        string table = ObjectSql.Relation("public", name + "_table");
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        async Task<object?> Sql(string sql) { await using var c = new NpgsqlCommand(sql, db); return await c.ExecuteScalarAsync(); }
        var service = new SequenceService(connection); var edits = new AdministrativeEditService(connection);
        try
        {
            await Sql($"CREATE SEQUENCE {target} START 10; CREATE TABLE {table}(id bigint DEFAULT nextval('{target}'), other bigint, ident bigint GENERATED ALWAYS AS IDENTITY); ALTER SEQUENCE {target} OWNED BY {table}.id;");
            var before = await service.InspectAsync("public", name);
            check(before.Dependency is { ColumnNumber: 1, Kind: "a" } && before.Dependency.TableOid != 0, "Inspect retains owned column identity and dependency kind");
            var edit = new SequenceDetachEdit("public", name) { Expected = before };
            bool missing = false; try { (edit with { Expected = null }).ToSql(); } catch (ArgumentException) { missing = true; }
            check(missing, "Detach requires inspected snapshot");
            await using (var tx = await db.BeginTransactionAsync())
            {
                await edit.ValidateAsync(db, tx); await using var c = new NpgsqlCommand(edit.ToSql(), db, tx); await c.ExecuteNonQueryAsync();
                await edit.VerifyAppliedAsync(db, tx); await tx.RollbackAsync();
            }
            check(await service.InspectAsync("public", name) == before, "Detach rollback restores association");
            await Sql($"ALTER SEQUENCE {target} OWNED BY {table}.other;");
            bool conflict = false; try { await edits.ApplyAsync(edit); } catch (InvalidOperationException) { conflict = true; }
            check(conflict && (await service.InspectAsync("public", name)).Dependency?.ColumnNumber == 2, "Detach rejects a concurrently reassigned column");
            before = await service.InspectAsync("public", name);
            string role = "pgms_detach_reader_" + Guid.NewGuid().ToString("N");
            try
            {
                await Sql($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN; GRANT SELECT ON SEQUENCE {target} TO {SqlIdentifier.Quote(role)};");
                bool denied = false;
                var limited = new AdministrativeEditService(new NpgsqlConnectionStringBuilder(connection) { Username = role }.ConnectionString);
                try { await limited.ApplyAsync(edit with { Expected = before }); } catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
                check(denied && await service.InspectAsync("public", name) == before, "Detach requires ownership and preserves association on denial");
            }
            finally { await Sql($"REVOKE ALL ON SEQUENCE {target} FROM {SqlIdentifier.Quote(role)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote(role)};"); }
            await Sql($"ALTER SEQUENCE {target} CACHE 2;");
            bool stale = false; try { await edits.ApplyAsync(edit with { Expected = before }); } catch (InvalidOperationException) { stale = true; }
            check(stale && (await service.InspectAsync("public", name)).Dependency == before.Dependency, "Detach protects concurrent configuration changes");
            await Sql($"ALTER SEQUENCE {target} CACHE 1;");
            object? state = await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}");
            await edits.ApplyAsync(edit with { Expected = before });
            check(await service.InspectAsync("public", name) == before with { OwnedBy = null, Dependency = null }, "Detach changes only association metadata");
            check(Equals(state, await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}")), "Detach preserves current value and is_called");
            check(Equals(await Sql($"INSERT INTO {table}(other) VALUES (1) RETURNING id"), 10L), "Detach leaves existing column default functional");
            bool absent = false; try { (edit with { Expected = await service.InspectAsync("public", name) }).ToSql(); } catch (ArgumentException) { absent = true; }
            check(absent, "Detach refuses an already free-standing sequence");
            string identityName = (string)(await Sql($"SELECT c.relname FROM pg_class c WHERE c.oid=pg_get_serial_sequence('{table}','ident')::regclass"))!;
            var identity = await service.InspectAsync("public", identityName);
            bool rejected = false; try { new SequenceDetachEdit("public", identityName) { Expected = identity }.ToSql(); } catch (ArgumentException) { rejected = true; }
            check(identity.Dependency?.Kind == "i" && rejected, "Detach refuses identity dependency with explicit guidance");
            await Sql($"DROP TABLE {table};");
            check((await service.InspectAsync("public", name)).Oid == before.Oid, "Detached sequence survives dropping former owning table");
            await Sql($"CREATE TABLE {table}(id bigint); ALTER SEQUENCE {target} OWNED BY {table}.id;");
            before = await service.InspectAsync("public", name);
            await Sql($"DROP SEQUENCE {target}; CREATE SEQUENCE {target} START 10 OWNED BY {table}.id;");
            bool replaced = false; try { await edits.ApplyAsync(edit with { Expected = before }); } catch (InvalidOperationException) { replaced = true; }
            check(replaced && (await service.InspectAsync("public", name)).Dependency is not null, "Detach refuses replacement sequence even with matching association");

        }
        finally { await Sql($"DROP TABLE IF EXISTS {table}; DROP SEQUENCE IF EXISTS {target};"); }
    }
}
