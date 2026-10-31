using Npgsql;
using PGMS.PostgreSql;

internal static class SequenceChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await SequenceParameterChecks.RunAsync(connection, check);
        string name = "PGMS_seq_" + Guid.NewGuid().ToString("N");
        string target = ObjectSql.Relation("public", name), table = ObjectSql.Relation("public", name + "_table");
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, db); return await command.ExecuteScalarAsync(); }
        var service = new SequenceService(connection);
        try
        {
            await session.ExecuteAsync($"CREATE SEQUENCE {target} AS integer INCREMENT BY -2 MINVALUE -20 MAXVALUE -1 START -2 CACHE 5 CYCLE; CREATE TABLE {table}(id integer);");
            var before = await Scalar($"SELECT last_value::text||':'||is_called::text FROM {target}");
            var snapshot = await service.InspectAsync("public", name);
            check(snapshot.Oid > 0 && snapshot.Name == name && snapshot.DataType == "integer" && snapshot.Start == -2 && snapshot.Increment == -2 && snapshot.Minimum == -20 && snapshot.Maximum == -1 && snapshot.Cache == 5 && snapshot.Cycle, "sequence inspection reads exact configuration");
            check(Equals(before, await Scalar($"SELECT last_value::text||':'||is_called::text FROM {target}")), "sequence inspection does not consume values or change is_called");
            bool missing = false, wrongKind = false;
            try { await service.InspectAsync("public", name + "_missing"); } catch (InvalidOperationException) { missing = true; }
            try { await service.InspectAsync("public", name + "_table"); } catch (InvalidOperationException) { wrongKind = true; }
            check(missing && wrongKind, "sequence inspector rejects missing targets and tables");
            await session.ExecuteAsync($"ALTER SEQUENCE {target} OWNED BY {table}.id; COMMENT ON SEQUENCE {target} IS 'fixture comment';");
            snapshot = await service.InspectAsync("public", name);
            check(snapshot.OwnedBy?.Contains(name + "_table") == true && snapshot.Comment == "fixture comment", "sequence inspection exposes ownership dependency and comment");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool interrupted = false;
            try { await service.InspectAsync("public", name, canceled.Token); } catch (OperationCanceledException) { interrupted = true; }
            check(interrupted, "sequence inspection honors cancellation");
            check(new CatalogEntry(CatalogKind.Relation, name) { RelationType = "S" }.CanInspectSequence && !new CatalogEntry(CatalogKind.Relation, name) { RelationType = "r" }.CanInspectSequence, "Explorer sequence actions distinguish relation kinds");
            var edits = new AdministrativeEditService(connection);
            var create = new SequenceCreateEdit("public", name + "_created");
            string created = ObjectSql.Relation(create.Schema, create.Name);
            bool Rejects(SequenceCreateEdit edit) { try { edit.ToSql(); return false; } catch (ArgumentException) { return true; } }
            check(Rejects(create with { Increment = 0 }) && Rejects(create with { Cache = 0 }) && Rejects(create with { Minimum = 5, Maximum = 5 }) && Rejects(create with { Start = 0 }) && Rejects(create with { DataType = SequenceDataType.SmallInt, Maximum = 40000 }), "sequence creation rejects invalid parameters before execution");
            foreach (var type in Enum.GetValues<SequenceDataType>())
            {
                await edits.ApplyAsync(create with { DataType = type, Increment = -1 });
                var actual = await service.InspectAsync("public", create.Name);
                check(actual.Start == -1 && actual.Maximum == -1 && actual.Increment == -1 && actual.Cache == 1 && !actual.Cycle && actual.DataType == (type == SequenceDataType.SmallInt ? "smallint" : type == SequenceDataType.Integer ? "integer" : "bigint"), $"sequence creates {type} with descending defaults");
                await session.ExecuteAsync($"DROP SEQUENCE {created};");
            }
            await edits.ApplyAsync(create with { Start = 2, Increment = 2, Minimum = 1, Maximum = 4, Cycle = true });
            async Task<long> Next() { await using var command = new NpgsqlCommand("SELECT nextval(@name::regclass)", db); command.Parameters.AddWithValue("name", created); return (long)(await command.ExecuteScalarAsync())!; }
            check(await Next() == 2 && await Next() == 4 && await Next() == 1, "sequence creates functional bounds, increment and cycle");
            var originalOid = (await service.InspectAsync("public", create.Name)).Oid;
            bool collision = false;
            try { await edits.ApplyAsync(create); } catch (PostgresException ex) when (ex.SqlState == "42P07") { collision = true; }
            check(collision && (await service.InspectAsync("public", create.Name)).Oid == originalOid, "duplicate sequence creation preserves existing object");
            await session.ExecuteAsync($"DROP SEQUENCE {created};");
            var inspected = await service.InspectAsync("public", name);
            var rename = new SequenceChangeEdit("public", name, SequenceOperation.Rename) { ExpectedOid = inspected.Oid, NewName = name + "_renamed" };
            var stateBeforeRename = await Scalar($"SELECT last_value::text||':'||is_called::text FROM {target}");
            await edits.ApplyAsync(rename);
            var renamed = await service.InspectAsync("public", rename.NewName);
            check(renamed.Oid == inspected.Oid && renamed.OwnedBy == inspected.OwnedBy, "sequence rename preserves OID and ownership dependency");
            check(Equals(stateBeforeRename, await Scalar($"SELECT last_value::text||':'||is_called::text FROM {ObjectSql.Relation("public", rename.NewName)}")), "sequence rename guard does not consume values");
            await edits.ApplyAsync(rename with { Name = rename.NewName, NewName = name });
            await session.ExecuteAsync($"DROP SEQUENCE {target}; CREATE SEQUENCE {target};");
            bool stale = false;
            try { await edits.ApplyAsync(rename); } catch (InvalidOperationException) { stale = true; }
            var replacement = await service.InspectAsync("public", name);
            check(stale && replacement.Oid != inspected.Oid, "stale sequence rename refuses same-name replacement");
            bool withoutInspection = false;
            try { await edits.ApplyAsync(rename with { ExpectedOid = null }); } catch (InvalidOperationException) { withoutInspection = true; }
            check(withoutInspection, "sequence mutations require inspected identity");
            bool racingRenameBlocked = false;
            await edits.ApplyAsync(new AfterSequenceGuard(rename with { ExpectedOid = replacement.Oid }, async () =>
            {
                await using (var timeout = new NpgsqlCommand("SET lock_timeout='200ms'", db)) await timeout.ExecuteNonQueryAsync();
                try { await using var concurrent = new NpgsqlCommand($"ALTER SEQUENCE {target} RENAME TO {SqlIdentifier.Quote(name + "_race")}", db); await concurrent.ExecuteNonQueryAsync(); }
                catch (PostgresException ex) when (ex.SqlState == "55P03") { racingRenameBlocked = true; }
                finally { await using var timeout = new NpgsqlCommand("SET lock_timeout=0", db); await timeout.ExecuteNonQueryAsync(); }
            }));
            check(racingRenameBlocked && (await service.InspectAsync("public", rename.NewName)).Oid == replacement.Oid, "sequence preflight lock blocks concurrent name swap until commit");
            await edits.ApplyAsync(rename with { Name = rename.NewName, NewName = name, ExpectedOid = replacement.Oid });
            string commentText = "Quote ' and slash " + (char)92 + " line" + Environment.NewLine + (char)231 + "; DROP SEQUENCE ignored";
            var comment = new SequenceChangeEdit("public", name, SequenceOperation.Comment) { ExpectedOid = replacement.Oid, Comment = commentText };
            await edits.ApplyAsync(comment);
            check((await service.InspectAsync("public", name)).Comment == commentText, "sequence comment roundtrips quotes, backslashes, newline and Unicode as data");
            bool emptyComment = false;
            try { (comment with { Comment = "" }).ToSql(); } catch (ArgumentException) { emptyComment = true; }
            check(emptyComment, "empty sequence comment cannot silently remove metadata");
            await edits.ApplyAsync(comment with { RemoveComment = true });
            check((await service.InspectAsync("public", name)).Comment is null, "explicit sequence comment removal writes SQL NULL");
            await session.ExecuteAsync($"DROP SEQUENCE {target}; CREATE SEQUENCE {target};");
            bool staleComment = false;
            try { await edits.ApplyAsync(comment); } catch (InvalidOperationException) { staleComment = true; }
            check(staleComment && (await service.InspectAsync("public", name)).Comment is null, "stale comment cannot alter same-name replacement");
            string role = "pgms_seq_role_" + Guid.NewGuid().ToString("N");
            try
            {
                await session.ExecuteAsync($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN; GRANT SELECT ON SEQUENCE {target} TO {SqlIdentifier.Quote(role)};");
                var limited = new AdministrativeEditService(new NpgsqlConnectionStringBuilder(connection) { Username = role }.ConnectionString);
                var current = comment with { ExpectedOid = (await service.InspectAsync("public", name)).Oid };
                bool denied = false;
                try { await limited.ApplyAsync(current); } catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
                check(denied && (await service.InspectAsync("public", name)).Comment is null, "sequence comment respects ownership despite SELECT grant");
                await session.ExecuteAsync($"REVOKE SELECT ON SEQUENCE {target} FROM {SqlIdentifier.Quote(role)};");
                denied = false;
                try { await limited.ApplyAsync(current); } catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
                check(denied, "sequence identity guard reports missing SELECT permission");
            }
            finally { await session.ExecuteAsync($"REVOKE ALL ON SEQUENCE {target} FROM {SqlIdentifier.Quote(role)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote(role)};"); }
            var drop = new SequenceChangeEdit("public", name, SequenceOperation.Drop) { ExpectedOid = (await service.InspectAsync("public", name)).Oid };
            await session.ExecuteAsync($"ALTER TABLE {table} ALTER COLUMN id SET DEFAULT nextval('{target.Replace("'", "''")}'::regclass);");
            bool dependency = false;
            try { await edits.ApplyAsync(drop); } catch (PostgresException ex) when (ex.SqlState == "2BP01") { dependency = true; }
            check(dependency && (await service.InspectAsync("public", name)).Oid == drop.ExpectedOid, "DROP SEQUENCE RESTRICT preserves sequence with dependent default");
            await session.ExecuteAsync($"ALTER TABLE {table} ALTER COLUMN id DROP DEFAULT; DROP SEQUENCE {target}; CREATE SEQUENCE {target};");
            bool staleDrop = false;
            try { await edits.ApplyAsync(drop); } catch (InvalidOperationException) { staleDrop = true; }
            var freshDrop = await service.InspectAsync("public", name);
            check(staleDrop && freshDrop.Oid != drop.ExpectedOid, "stale sequence Drop preserves replacement");
            await edits.ApplyAsync(drop with { ExpectedOid = freshDrop.Oid });
            bool removed = false;
            try { await service.InspectAsync("public", name); } catch (InvalidOperationException) { removed = true; }
            check(removed, "reinspected sequence Drop removes intended object");
            await session.ExecuteAsync($"CREATE TABLE {target}(id int);");
            bool tableRefused = false;
            try { await edits.ApplyAsync(drop with { ExpectedOid = (uint)(await Scalar($"SELECT '{target.Replace("'", "''")}'::regclass::oid"))! }); } catch (InvalidOperationException) { tableRefused = true; }
            check(tableRefused && (long)(await Scalar($"SELECT count(*) FROM {target}"))! == 0, "sequence Drop refuses a table even with matching OID");
            await session.ExecuteAsync($"DROP TABLE {target};");
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {table}; DROP SEQUENCE IF EXISTS {target}; DROP SEQUENCE IF EXISTS {ObjectSql.Relation("public", name + "_created")}; DROP SEQUENCE IF EXISTS {ObjectSql.Relation("public", name + "_renamed")}; DROP SEQUENCE IF EXISTS {ObjectSql.Relation("public", name + "_race")};"); }
    }
}

internal sealed record AfterSequenceGuard(IAdministrativeEdit Inner, Func<Task> After) : IAdministrativeEdit
{
    public string ToSql() => Inner.ToSql();
    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    { await Inner.ValidateAsync(connection, transaction, token); await After(); }
    public Task VerifyAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default) => Inner.VerifyAppliedAsync(connection, transaction, token);
}
