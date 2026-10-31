using Npgsql;
using PGMS.PostgreSql;

internal static class SequenceTypeChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string name = "pgms_type_" + Guid.NewGuid().ToString("N"), target = ObjectSql.Relation("public", name);
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        async Task<object?> Sql(string sql) { await using var command = new NpgsqlCommand(sql, db); return await command.ExecuteScalarAsync(); }
        var service = new SequenceService(connection); var edits = new AdministrativeEditService(connection);
        try
        {
            await Sql($"CREATE SEQUENCE {target} AS smallint START 10; CREATE TABLE {ObjectSql.Relation("public", name + "_owner")}(id integer); ALTER SEQUENCE {target} OWNED BY {ObjectSql.Relation("public", name + "_owner")}.id;");
        }
        catch { await Sql($"DROP SEQUENCE IF EXISTS {target};"); throw; }
        try
        {
            var before = await service.InspectAsync("public", name);
            check(before.OwnedBy is not null, "Type fixture has an actual OWNED BY dependency");
            var edit = new SequenceParameterEdit("public", name, SequenceOperation.SetDataType) { Expected = before, DataType = SequenceDataType.BigInt };
            check(edit.ToSql().Contains("AS bigint MINVALUE 1 MAXVALUE 32767"), "Type SQL preserves default bounds explicitly during widening");
            bool missing = false; try { (edit with { Expected = null }).ToSql(); } catch (ArgumentException) { missing = true; }
            check(missing, "Type SQL requires an inspected snapshot");
            object? state = await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}");
            await edits.ApplyAsync(edit);
            check(await service.InspectAsync("public", name) == before with { DataType = "bigint" }, "Type widening changes only inspected datatype and preserves bounds");
            check(Equals(state, await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}")), "Type change preserves current value and is_called");
            bool stale = false; try { await edits.ApplyAsync(edit); } catch (InvalidOperationException) { stale = true; }
            check(stale, "Type change rejects stale datatype snapshot");
            before = await service.InspectAsync("public", name);
            await edits.ApplyAsync(edit with { Expected = before, DataType = SequenceDataType.Integer });
            check((await service.InspectAsync("public", name)).DataType == "integer", "Type narrows when bounds fit new range");
            await Sql($"ALTER SEQUENCE {target} MAXVALUE 100000;");
            before = await service.InspectAsync("public", name);
            bool outside = false; try { (edit with { Expected = before, DataType = SequenceDataType.SmallInt }).ToSql(); } catch (ArgumentException) { outside = true; }
            check(outside && await service.InspectAsync("public", name) == before, "Type narrowing refuses out-of-range bounds without mutation");
            await Sql($"ALTER SEQUENCE {target} CACHE 2;");
            bool conflict = false; try { await edits.ApplyAsync(edit with { Expected = before }); } catch (InvalidOperationException) { conflict = true; }
            check(conflict && (await service.InspectAsync("public", name)).Cache == 2, "Type Apply protects concurrent parameter changes");
            before = await service.InspectAsync("public", name);
            string role = "pgms_type_reader_" + Guid.NewGuid().ToString("N");
            try
            {
                await Sql($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN; GRANT SELECT ON SEQUENCE {target} TO {SqlIdentifier.Quote(role)};");
                var limited = new AdministrativeEditService(new NpgsqlConnectionStringBuilder(connection) { Username = role }.ConnectionString);
                bool denied = false;
                try { await limited.ApplyAsync(edit with { Expected = before }); } catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
                check(denied && await service.InspectAsync("public", name) == before, "Type Apply requires ownership even when SELECT is granted");
            }
            finally { await Sql($"REVOKE ALL ON SEQUENCE {target} FROM {SqlIdentifier.Quote(role)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote(role)};"); }
            await Sql($"DROP SEQUENCE {target}; CREATE SEQUENCE {target} AS integer MINVALUE 1 MAXVALUE 100000 CACHE 2 START 10;");
            bool replaced = false; try { await edits.ApplyAsync(edit with { Expected = before }); } catch (InvalidOperationException) { replaced = true; }
            check(replaced, "Type Apply rejects replacement sequence identity");
        }
        finally { await Sql($"DROP SEQUENCE IF EXISTS {target}; DROP TABLE IF EXISTS {ObjectSql.Relation("public", name + "_owner")};"); }
    }
}
