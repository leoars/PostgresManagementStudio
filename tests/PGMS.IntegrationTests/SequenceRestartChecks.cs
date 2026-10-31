using Npgsql;
using PGMS.PostgreSql;

internal static class SequenceRestartChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string name = "pgms_restart_" + Guid.NewGuid().ToString("N"), target = ObjectSql.Relation("public", name);
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        async Task<object?> Sql(string sql) { await using var c = new NpgsqlCommand(sql, db); return await c.ExecuteScalarAsync(); }
        var service = new SequenceService(connection); var edits = new AdministrativeEditService(connection);
        try
        {
            await Sql($"CREATE SEQUENCE {target} MINVALUE 1 MAXVALUE 100 START 10;");
            var before = await service.InspectAsync("public", name);
            var edit = new SequenceRestartEdit("public", name, 50) { Expected = before };
            bool missing = false; try { (edit with { Expected = null }).ToSql(); } catch (ArgumentException) { missing = true; }
            check(missing, "Restart requires inspection before SQL review");
            bool outside = false; try { (edit with { Value = 101 }).ToSql(); } catch (ArgumentException) { outside = true; }
            check(outside, "Restart refuses value outside inspected limits");
            await edits.ApplyAsync(edit);
            check(await service.InspectAsync("public", name) == before, "Restart preserves configured START and all metadata");
            check(Equals(await Sql($"SELECT last_value::text||':'||is_called::text FROM {target}"), "50:false"), "Restart verifies current value without consuming it");
            check(Equals(await Sql($"SELECT nextval('{target}')"), 50L), "First nextval returns explicit restart value");
            await using (var tx = await db.BeginTransactionAsync())
            {
                var rollbackEdit = edit with { Value = 75 };
                await rollbackEdit.ValidateAsync(db, tx);
                await using var command = new NpgsqlCommand(rollbackEdit.ToSql(), db, tx); await command.ExecuteNonQueryAsync();
                await rollbackEdit.VerifyAppliedAsync(db, tx); await tx.RollbackAsync();
            }
            check(Equals(await Sql($"SELECT nextval('{target}')"), 51L), "Restart rollback restores prior sequence state");
            await Sql($"ALTER SEQUENCE {target} CACHE 2;");
            bool stale = false; try { await edits.ApplyAsync(edit); } catch (InvalidOperationException) { stale = true; }
            check(stale, "Restart rejects changed configuration");
            before = await service.InspectAsync("public", name);
            string role = "pgms_restart_reader_" + Guid.NewGuid().ToString("N");
            try
            {
                await Sql($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN; GRANT SELECT ON SEQUENCE {target} TO {SqlIdentifier.Quote(role)};");
                bool denied = false;
                var limited = new AdministrativeEditService(new NpgsqlConnectionStringBuilder(connection) { Username = role }.ConnectionString);
                try { await limited.ApplyAsync(edit with { Expected = before }); } catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
                check(denied, "Restart requires ownership even with SELECT granted");
            }
            finally { await Sql($"REVOKE ALL ON SEQUENCE {target} FROM {SqlIdentifier.Quote(role)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote(role)};"); }
            await using (var tx = await db.BeginTransactionAsync())
            {
                var locked = edit with { Expected = before };
                await locked.ValidateAsync(db, tx);
                await using var waiter = new NpgsqlConnection(connection); await waiter.OpenAsync();
                await using var command = new NpgsqlCommand($"SELECT nextval('{target}')", waiter);
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                bool canceled = false;
                try { await command.ExecuteScalarAsync(cancel.Token); } catch (OperationCanceledException) { canceled = true; }
                check(canceled, "Restart guard blocks concurrent nextval until transaction ends");
                await tx.RollbackAsync();
            }
            await using (var tx = await db.BeginTransactionAsync())
            {
                await using var lockCommand = new NpgsqlCommand($"ALTER SEQUENCE {target} CACHE 2", db, tx); await lockCommand.ExecuteNonQueryAsync();
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                bool canceled = false;
                try { await edits.ApplyAsync(edit with { Expected = before }, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
                check(canceled, "Restart awaiting lock honors cancellation");
                await tx.RollbackAsync();
            }
            check(await service.InspectAsync("public", name) == before, "Canceled restart leaves configuration intact");
            await Sql($"DROP SEQUENCE {target}; CREATE SEQUENCE {target} MINVALUE 1 MAXVALUE 100 START 10 CACHE 2;");
            bool replaced = false; try { await edits.ApplyAsync(edit with { Expected = before }); } catch (InvalidOperationException) { replaced = true; }
            check(replaced, "Restart rejects replacement identity");
            await Sql($"ALTER SEQUENCE {target} INCREMENT -1 MINVALUE -100 MAXVALUE -1 START -10 RESTART -10 CACHE 1;");
            before = await service.InspectAsync("public", name);
            await edits.ApplyAsync(edit with { Expected = before, Value = -50 });
            check(Equals(await Sql($"SELECT nextval('{target}')"), -50L), "Descending sequence restart supports signed values");
        }
        finally { await Sql($"DROP SEQUENCE IF EXISTS {target};"); }
    }
}
