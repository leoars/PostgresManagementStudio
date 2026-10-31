using Npgsql;
using PGMS.PostgreSql;

internal static class ConstraintValidationChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_validate_" + Guid.NewGuid().ToString("N");
        string target = "public." + SqlIdentifier.Quote(table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        var edits = new AdministrativeEditService(connection);
        try
        {

            await session.ExecuteAsync($"CREATE TABLE {target}(id int PRIMARY KEY, other int); INSERT INTO {target} VALUES (1,-1);");
            var create63 = new CheckConstraintEdit(CheckConstraintOperation.Add, "public", table, "ck", "other > 0");
            bool rejected63 = false;
            try { await edits.ApplyAsync(create63); } catch (PostgresException ex) when (ex.SqlState == "23514") { rejected63 = true; }
            check(rejected63, "CHECK default creation rejects invalid existing rows");
            await edits.ApplyAsync(create63 with { NotValid = true });
            check(!(await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "ck").Validated, "CHECK NOT VALID creates over invalid existing rows");
            rejected63 = false;
            try { await session.ExecuteAsync($"INSERT INTO {target} VALUES (2,-2)"); } catch (PostgresException ex) when (ex.SqlState == "23514") { rejected63 = true; }
            check(rejected63, "CHECK NOT VALID still rejects invalid new rows");
            var snapshot61 = (await new CheckConstraintService(connection).InspectAsync("public", table)).Single();
            var request61 = new CheckConstraintEdit(CheckConstraintOperation.Validate, "public", table, "ck", "") { ExpectedIdentity = snapshot61.Identity };
            check(request61.ToSql() == $"ALTER TABLE {ObjectSql.Relation("public", table)} VALIDATE CONSTRAINT {SqlIdentifier.Quote("ck")};", "CHECK validation scripts exact qualified identifiers");
            bool rejected61 = false;
            try { await edits.ApplyAsync(request61); } catch (PostgresException ex) when (ex.SqlState == "23514") { rejected61 = true; }
            check(rejected61 && !(await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "ck").Validated, "CHECK invalid existing data prevents validation without changing state");
            await session.ExecuteAsync($"UPDATE {target} SET other=1;");
            await edits.ApplyAsync(request61);
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "ck").Validated, "CHECK validation succeeds after data repair");
            await edits.ApplyAsync(request61);
            check(true, "CHECK already-valid constraint permits idempotent validation");
            await session.ExecuteAsync($"ALTER TABLE {target} DROP CONSTRAINT ck; ALTER TABLE {target} ADD CONSTRAINT ck CHECK(other > 0) NOT VALID;");
            rejected61 = false;
            try { await edits.ApplyAsync(request61); } catch (InvalidOperationException ex) when (ex.Message.Contains("identity")) { rejected61 = true; }
            check(rejected61 && !(await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "ck").Validated, "CHECK validation rejects replacement identity");
            rejected61 = false;
            try { await edits.ApplyAsync(request61 with { Name = table + "_pkey", ExpectedIdentity = null }); } catch (InvalidOperationException) { rejected61 = true; }
            check(rejected61, "CHECK validation rejects constraints from a different family");
            var current61 = (await new CheckConstraintService(connection).InspectAsync("public", table)).Single();
            await edits.ApplyAsync(request61 with { ExpectedIdentity = current61.Identity });
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "ck").Validated, "CHECK reinspection permits validating replacement");
            await using (var lockSession = new NpgsqlConnection(connection))
            {
                await lockSession.OpenAsync(); await using var transaction = await lockSession.BeginTransactionAsync();
                await (request61 with { ExpectedIdentity = current61.Identity }).ValidateAsync(lockSession, transaction);
                await using var writer = new NpgsqlConnection(connection); await writer.OpenAsync();
                await using var insert = new NpgsqlCommand($"INSERT INTO {target} VALUES (10,10)", writer) { CommandTimeout = 3 };
                await insert.ExecuteNonQueryAsync();
                check(true, "CHECK identity preflight lock permits concurrent ordinary writes");
                await transaction.RollbackAsync();
            }
            await session.ExecuteAsync($"DROP TABLE {target};");

            await session.ExecuteAsync($"CREATE TABLE {target}(id int PRIMARY KEY, other int); INSERT INTO {target} VALUES (1,2);");
            var create64 = new ForeignKeyEdit(ForeignKeyOperation.Add, "public", table, "fk", ["other"], "public", table, ["id"], ReferentialAction.NoAction, ReferentialAction.NoAction);
            bool rejected64 = false;
            try { await edits.ApplyAsync(create64); } catch (PostgresException ex) when (ex.SqlState == "23503") { rejected64 = true; }
            check(rejected64, "FK default creation rejects invalid existing rows");
            await edits.ApplyAsync(create64 with { NotValid = true });
            check(!(await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "fk").Validated, "FK NOT VALID creates over invalid existing rows");
            rejected64 = false;
            try { await session.ExecuteAsync($"INSERT INTO {target} VALUES (3,99)"); } catch (PostgresException ex) when (ex.SqlState == "23503") { rejected64 = true; }
            check(rejected64, "FK NOT VALID still rejects invalid new rows");
            var snapshot62 = (await new ForeignKeyService(connection).InspectAsync("public", table)).Single();
            var request62 = new ForeignKeyEdit(ForeignKeyOperation.Validate, "public", table, "fk", [], "public", table, [], ReferentialAction.NoAction, ReferentialAction.NoAction) { ExpectedIdentity = snapshot62.Identity };
            check(request62.ToSql() == $"ALTER TABLE {ObjectSql.Relation("public", table)} VALIDATE CONSTRAINT {SqlIdentifier.Quote("fk")};", "FOREIGN KEY validation scripts exact qualified identifiers");
            bool rejected62 = false;
            try { await edits.ApplyAsync(request62); } catch (PostgresException ex) when (ex.SqlState == "23503") { rejected62 = true; }
            check(rejected62 && !(await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "fk").Validated, "FOREIGN KEY invalid existing data prevents validation without changing state");
            await session.ExecuteAsync($"UPDATE {target} SET other=1;");
            await edits.ApplyAsync(request62);
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "fk").Validated, "FOREIGN KEY validation succeeds after data repair");
            await edits.ApplyAsync(request62);
            check(true, "FOREIGN KEY already-valid constraint permits idempotent validation");
            await session.ExecuteAsync($"ALTER TABLE {target} DROP CONSTRAINT fk; ALTER TABLE {target} ADD CONSTRAINT fk FOREIGN KEY(other) REFERENCES {target}(id) NOT VALID;");
            rejected62 = false;
            try { await edits.ApplyAsync(request62); } catch (InvalidOperationException ex) when (ex.Message.Contains("identity")) { rejected62 = true; }
            check(rejected62 && !(await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "fk").Validated, "FOREIGN KEY validation rejects replacement identity");
            rejected62 = false;
            try { await edits.ApplyAsync(request62 with { Name = table + "_pkey", ExpectedIdentity = null }); } catch (InvalidOperationException) { rejected62 = true; }
            check(rejected62, "FOREIGN KEY validation rejects constraints from a different family");
            var current62 = (await new ForeignKeyService(connection).InspectAsync("public", table)).Single();
            await edits.ApplyAsync(request62 with { ExpectedIdentity = current62.Identity });
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "fk").Validated, "FOREIGN KEY reinspection permits validating replacement");
            await using (var lockSession = new NpgsqlConnection(connection))
            {
                await lockSession.OpenAsync(); await using var transaction = await lockSession.BeginTransactionAsync();
                await (request62 with { ExpectedIdentity = current62.Identity }).ValidateAsync(lockSession, transaction);
                await using var writer = new NpgsqlConnection(connection); await writer.OpenAsync();
                await using var insert = new NpgsqlCommand($"INSERT INTO {target} VALUES (10,10)", writer) { CommandTimeout = 3 };
                await insert.ExecuteNonQueryAsync();
                check(true, "FOREIGN KEY identity preflight lock permits concurrent ordinary writes");
                await transaction.RollbackAsync();
            }
            await session.ExecuteAsync($"DROP TABLE {target};");
            // Validation cases.
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};"); }
    }
}
