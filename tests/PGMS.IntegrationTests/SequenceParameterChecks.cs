using Npgsql;
using PGMS.PostgreSql;

internal static class SequenceParameterChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await SequenceOwnerTargetChecks.RunAsync(connection, check);
        await SequenceAttachChecks.RunAsync(connection, check);
        await SequenceOwnerJourneyChecks.RunAsync(connection, check);
        await SequenceTypeChecks.RunAsync(connection, check);
        await SequenceRestartChecks.RunAsync(connection, check);
        await SequenceDetachChecks.RunAsync(connection, check);
        await SequenceIdentifierChecks.RunAsync(connection, check);
        string name = "pgms_params_" + Guid.NewGuid().ToString("N"), schema = "public";
        string target = ObjectSql.Relation(schema, name);
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        async Task<object?> Sql(string sql) { await using var c = new NpgsqlCommand(sql, db); return await c.ExecuteScalarAsync(); }
        var service = new SequenceService(connection); var edits = new AdministrativeEditService(connection);
        try
        {
            await Sql($"CREATE SEQUENCE {target} AS integer MINVALUE 1 MAXVALUE 100 START 10;");
            await Sql($"CREATE TABLE {ObjectSql.Relation(schema, name + "_owner")}(id integer); ALTER SEQUENCE {target} OWNED BY {ObjectSql.Relation(schema, name + "_owner")}.id;");
            var before = await service.InspectAsync(schema, name);
            var edit = new SequenceParameterEdit(schema, name, SequenceOperation.SetIncrement) { Expected = before, Value = 3 };
            bool invalid = false; try { (edit with { Value = 0 }).ToSql(); } catch (ArgumentException) { invalid = true; }
            check(invalid, "parameter edit rejects zero increment");
            bool missing = false; try { await edits.ApplyAsync(edit with { Expected = null }); } catch (InvalidOperationException) { missing = true; }
            check(missing, "parameter Apply requires matching inspected target");
            var state = await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}");
            await edits.ApplyAsync(edit);
            var after = await service.InspectAsync(schema, name);
            check(after == before with { Increment = 3 }, "increment Apply preserves other configuration and OID");
            check(Equals(state, await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}")), "increment alteration does not advance sequence");
            bool stale = false; try { await edits.ApplyAsync(edit with { Value = 4 }); } catch (InvalidOperationException) { stale = true; }
            check(stale && (await service.InspectAsync(schema, name)).Increment == 3, "parameter Apply refuses stale configuration without overwriting");
            bool locked = false;
            await edits.ApplyAsync(new AfterSequenceGuard(edit with { Expected = after, Value = -2 }, async () =>
            {
                await using var concurrent = new NpgsqlConnection(connection); await concurrent.OpenAsync();
                await using var command = new NpgsqlCommand($"SET lock_timeout='150ms'; ALTER SEQUENCE {target} CACHE 9;", concurrent);
                try { await command.ExecuteNonQueryAsync(); } catch (PostgresException ex) when (ex.SqlState == "55P03") { locked = true; }
            }));
            check(locked && (await service.InspectAsync(schema, name)).Increment == -2, "parameter guard blocks concurrent ALTER through commit");
            check((long)(await Sql($"SELECT nextval('{target}')"))! == 10 && (long)(await Sql($"SELECT nextval('{target}')"))! == 8, "changed increment governs real generation without restart");
            before = await service.InspectAsync(schema, name);
            var cache = new SequenceParameterEdit(schema, name, SequenceOperation.SetCache) { Expected = before, Value = 5 };
            bool badCache = false; try { (cache with { Value = 0 }).ToSql(); } catch (ArgumentException) { badCache = true; }
            check(badCache, "cache change rejects nonpositive values");
            state = await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}");
            await edits.ApplyAsync(cache);
            check(await service.InspectAsync(schema, name) == before with { Cache = 5 }, "cache change preserves all other settings and OWNED BY");
            check(Equals(state, await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}")), "cache change does not consume values");
            bool staleCache = false; try { await edits.ApplyAsync(cache with { Value = 1 }); } catch (InvalidOperationException) { staleCache = true; }
            check(staleCache && (await service.InspectAsync(schema, name)).Cache == 5, "stale cache change preserves current setting");
            before = await service.InspectAsync(schema, name);
            var cycle = new SequenceParameterEdit(schema, name, SequenceOperation.SetCycle) { Expected = before, Cycle = true };
            await edits.ApplyAsync(cycle);
            check(await service.InspectAsync(schema, name) == before with { Cycle = true }, "CYCLE preserves inspected parameters and dependency");
            await Sql($"ALTER SEQUENCE {target} CACHE 1; SELECT setval('{target}',1,true);");
            check((long)(await Sql($"SELECT nextval('{target}')"))! == 100, "enabled descending CYCLE wraps at actual minimum");
            before = await service.InspectAsync(schema, name);
            await edits.ApplyAsync(cycle with { Expected = before, Cycle = false });
            await Sql($"SELECT setval('{target}',1,true);");
            bool exhausted = false; try { await Sql($"SELECT nextval('{target}')"); } catch (PostgresException ex) when (ex.SqlState == "2200H") { exhausted = true; }
            check(exhausted && !(await service.InspectAsync(schema, name)).Cycle, "NO CYCLE stops generation at the limit");
            before = await service.InspectAsync(schema, name);
            var start = new SequenceParameterEdit(schema, name, SequenceOperation.SetStart) { Expected = before, Value = 50 };
            state = await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}");
            await edits.ApplyAsync(start);
            check(await service.InspectAsync(schema, name) == before with { Start = 50 }, "START changes configured default without touching other parameters");
            check(Equals(state, await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}")), "START does not restart or change is_called");
            bool badStart = false; try { (start with { Value = 101 }).ToSql(); } catch (ArgumentException) { badStart = true; }
            check(badStart, "START validates inspected bounds");
            await Sql($"ALTER SEQUENCE {target} RESTART;");
            check((long)(await Sql($"SELECT nextval('{target}')"))! == 50, "future explicit RESTART uses the changed configured START");
            before = await service.InspectAsync(schema, name);
            var bounds = new SequenceParameterEdit(schema, name, SequenceOperation.SetBounds) { Expected = before, Value = -100, Maximum = 200 };
            state = await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}");
            await edits.ApplyAsync(bounds);
            check(await service.InspectAsync(schema, name) == before with { Minimum = -100, Maximum = 200 }, "bounds change preserves other parameters and OWNED BY");
            check(Equals(state, await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}")), "bounds change leaves current value intact");
            bool Bad(SequenceParameterEdit value) { try { value.ToSql(); return false; } catch (ArgumentException) { return true; } }
            check(Bad(bounds with { Value = 200 }) && Bad(bounds with { Maximum = 40 }) && Bad(bounds with { Maximum = long.MaxValue }), "bounds reject inverted range, excluded START and type overflow");
            before = await service.InspectAsync(schema, name);
            await Sql($"SELECT setval('{target}',150,true);");
            bool currentOutside = false;
            try { await edits.ApplyAsync(bounds with { Expected = before, Maximum = 100 }); } catch (PostgresException ex) when (ex.SqlState == "22023") { currentOutside = true; }
            check(currentOutside && await service.InspectAsync(schema, name) == before, "server rejects bounds excluding current value and transaction rolls back");
            bool staleBounds = false;
            try { await edits.ApplyAsync(bounds); } catch (InvalidOperationException) { staleBounds = true; }
            check(staleBounds, "bounds refuse stale configuration");
            string role = "pgms_param_role_" + Guid.NewGuid().ToString("N");
            try
            {
                await Sql($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN; GRANT SELECT ON SEQUENCE {target} TO {SqlIdentifier.Quote(role)};");
                var limited = new AdministrativeEditService(new NpgsqlConnectionStringBuilder(connection) { Username = role }.ConnectionString);
                bool denied = false;
                try { await limited.ApplyAsync(bounds with { Expected = before }); } catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
                check(denied && await service.InspectAsync(schema, name) == before, "parameter guard enforces ownership despite SELECT grant");
                await Sql($"ALTER SEQUENCE {target} OWNED BY NONE; ALTER SEQUENCE {target} OWNER TO {SqlIdentifier.Quote(role)};");
                bool changedOwner = false; try { await edits.ApplyAsync(bounds with { Expected = before }); } catch (InvalidOperationException) { changedOwner = true; }
                check(changedOwner, "parameter guard refuses owner changes after inspection");
                var owned = await service.InspectAsync(schema, name);
                await limited.ApplyAsync(bounds with { Expected = owned, Maximum = 300 });
                check((await service.InspectAsync(schema, name)).Maximum == 300, "ordinary owner can alter parameters without schema CREATE grant");
            }
            finally
            {
                await Sql($"ALTER SEQUENCE {target} OWNER TO {SqlIdentifier.Quote(before.Owner)}; REVOKE ALL ON SEQUENCE {target} FROM {SqlIdentifier.Quote(role)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote(role)};");
            }
            before = await service.InspectAsync(schema, name);
            await Sql($"DROP SEQUENCE {target}; CREATE SEQUENCE {target};");
            bool replaced = false; try { await edits.ApplyAsync(edit with { Expected = before }); } catch (InvalidOperationException) { replaced = true; }
            check(replaced && (await service.InspectAsync(schema, name)).Increment == 1, "parameter guard preserves same-name replacement");
        }
        finally { await Sql($"DROP TABLE IF EXISTS {ObjectSql.Relation(schema, name + "_owner")}; DROP SEQUENCE IF EXISTS {target};"); }
    }
}
