using Npgsql;
using PGMS.PostgreSql;

internal static class AcceptanceJourneyChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string owner = "accept_owner_" + suffix, reader = "accept_reader_" + suffix;
        string source = "accept_source_" + suffix, target = "accept_target_" + suffix;
        string archive = Path.Combine(Path.GetTempPath(), "pgms_accept_" + suffix + ".backup");
        string tools = Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")
            ?? throw new InvalidOperationException("Run through Test-Integration.ps1.");
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Admin(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        string Connect(string database, string role) => new NpgsqlConnectionStringBuilder(connection) { Database = database, Username = role }.ConnectionString;
        string? serverVersion=null;
        try
        {
            await Admin($"CREATE ROLE {owner} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE; CREATE ROLE {reader} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE");
            await Admin($"CREATE DATABASE {source} OWNER {owner}");
            await Admin($"CREATE DATABASE {target} OWNER {owner}");
            string owned = Connect(source, owner);
            await using (var session = new QuerySession(owned))
            {
                await session.OpenAsync();
                var version = await session.ExecuteAsync("SHOW server_version");
                serverVersion=(string)version.Tables[0].Rows[0][0];
                var flags = await session.ExecuteAsync("SELECT rolsuper OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname=current_user");
                check((string)flags.Tables[0].Rows[0][0] == "f", "acceptance owner has no cluster administration flags");
                await new AdministrationService(owned).CreateAsync(new(CreateObjectKind.Schema, "journey"));
                await session.ExecuteAsync("CREATE TABLE journey.items(id integer PRIMARY KEY, value text NOT NULL); INSERT INTO journey.items VALUES(1, 'ação 😀')");
                var grants = new PrivilegeService(owned);
                await grants.ApplyAsync(new(PrivilegeTarget.Schema, "journey", reader, "USAGE"));
                await grants.ApplyAsync(new(PrivilegeTarget.Table, "items", reader, "SELECT", Schema: "journey"));
                await using var readSession = new QuerySession(Connect(source, reader));
                await readSession.OpenAsync();
                var data = await readSession.ExecuteAsync("SELECT value FROM journey.items");
                check((string)data.Tables[0].Rows[0][0] == "ação 😀", "acceptance granted reader reads Unicode row");
                bool denied = false;
                try { await readSession.ExecuteAsync("INSERT INTO journey.items VALUES(2, 'forbidden')"); }
                catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
                check(denied, "acceptance reader cannot write");
                await grants.ApplyAsync(new(PrivilegeTarget.Table, "items", reader, "SELECT", Revoke: true, Schema: "journey"));
                denied = false;
                try { await readSession.ExecuteAsync("SELECT * FROM journey.items"); }
                catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
                check(denied, "acceptance revoked reader loses access in existing session");
                await session.ExecuteAsync("BEGIN");
                bool conflict = false;
                try { await session.ExecuteAsync("INSERT INTO journey.items VALUES(1, 'duplicate')"); }
                catch (PostgresException ex) when (ex.SqlState == "23505") { conflict = true; }
                check(conflict, "acceptance constraint error is reported");
                await session.ExecuteAsync("ROLLBACK; INSERT INTO journey.items VALUES(2, 'recovered')");
                var recovered = await session.ExecuteAsync("SELECT count(*) FROM journey.items");
                check((string)recovered.Tables[0].Rows[0][0] == "2", "acceptance session recovers after rollback");
                await new BackupService(owned, tools).BackupAsync(archive);
                check(File.Exists(archive) && new FileInfo(archive).Length > 0, "acceptance limited owner creates custom backup");
            }
            string restored = Connect(target, owner);
            await new BackupService(restored, tools).RestoreAsync(archive);
            await using var restoredSession = new QuerySession(restored);
            await restoredSession.OpenAsync();
            var result = await restoredSession.ExecuteAsync("SELECT id, value FROM journey.items ORDER BY id");
            check(result.Tables[0].Rows.Count == 2 && (string)result.Tables[0].Rows[0][1] == "ação 😀" && (string)result.Tables[0].Rows[1][1] == "recovered", "acceptance limited owner restores exact committed rows");
            bool duplicate = false;
            try { await restoredSession.ExecuteAsync("INSERT INTO journey.items VALUES(1, 'duplicate')"); }
            catch (PostgresException ex) when (ex.SqlState == "23505") { duplicate = true; }
            check(duplicate, "acceptance restored primary key enforces uniqueness");
        }
        finally
        {
            await Admin($"DROP DATABASE IF EXISTS {source} WITH (FORCE)");
            await Admin($"DROP DATABASE IF EXISTS {target} WITH (FORCE)");
            await Admin($"DROP ROLE IF EXISTS {reader}; DROP ROLE IF EXISTS {owner}");
            if (File.Exists(archive)) File.Delete(archive);
        }
        string reportRoot=Path.GetFullPath(Path.Combine("artifacts","acceptance-journey",Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(reportRoot);
        var report=new {serverVersion,checks=9,ownerLimited=true,readerDeniedWrite=true,backupRestoreRoundtrip=true,fixturesRemoved=true,limitation="Services journey on isolated PostgreSQL; not a UI or clean Windows certification."};
        File.WriteAllText(Path.Combine(reportRoot,"journey.json"),System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Acceptance journey report: "+reportRoot);
    }
}
