using System.Diagnostics;
using System.Security;
using Npgsql;
using PGMS.PostgreSql;

internal static class UnicodePasswordChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host != "127.0.0.1" || settings.Port != 55439 || settings.Username != "pgms_test")
            throw new InvalidOperationException("Unicode password checks require the isolated cluster.");
        string role = "unicode_password_" + Guid.NewGuid().ToString("N");
        string psql = Path.Combine(Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN") ?? throw new Exception("Missing test tools"), "psql.exe");
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var cmd = new NpgsqlCommand(sql, admin); await cmd.ExecuteNonQueryAsync(); }
        async Task<string> Scalar(string sql) { await using var cmd = new NpgsqlCommand(sql, admin); return (string)(await cmd.ExecuteScalarAsync())!; }
        string hba = await Scalar("SHOW hba_file");
        string expected = TestClusterPaths.Data("pg_hba.conf");
        if (!string.Equals(Path.GetFullPath(hba), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing HBA outside the isolated test directory.");
        string original = await File.ReadAllTextAsync(hba);
        string tls = new NpgsqlConnectionStringBuilder(connection) { SslMode = SslMode.Require }.ConnectionString;
        async Task Set(string value, string? target = null, CancellationToken token = default)
        {
            using var secure = new SecureString(); foreach (char c in value) secure.AppendChar(c);
            secure.MakeReadOnly(); await RolePasswordService.SetAsync(target ?? tls, role, secure, token);
        }
        async Task<bool> Login(string value)
        {
            var login = new NpgsqlConnectionStringBuilder(tls) { Username = role, Password = value, Pooling = false };
            try { await using var client = new NpgsqlConnection(login.ConnectionString); await client.OpenAsync(); return true; }
            catch (PostgresException ex) when (ex.SqlState == "28P01") { return false; }
        }
        async Task<bool> ExternalLogin(string value)
        {
            var start = new ProcessStartInfo(psql) { UseShellExecute = false, RedirectStandardInput = true, StandardInputEncoding = new System.Text.UTF8Encoding(false), RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            foreach (string arg in new[] { "-X", "-W", "-A", "-t", "-h", "127.0.0.1", "-p", "55439", "-U", role, "-d", "postgres", "-c", "SELECT 1" }) start.ArgumentList.Add(arg);
            // PG17 simple_prompt's MSYS fallback reads redirected stdin. Feed explicit
            // UTF-8 bytes; Windows ANSI getenv(PGPASSWORD) corrupts Unicode otherwise.
            start.Environment["OSTYPE"] = "msys";
            start.Environment["PGSSLMODE"] = "require";
            start.Environment["PGSSLROOTCERT"] = TestClusterPaths.Data("server.crt");
            start.Environment["PGSSLCERT"] = Path.GetFullPath("artifacts/postgres-test/no-personal-client-certificate");
            start.Environment["PGSSLKEY"] = Path.GetFullPath("artifacts/postgres-test/no-personal-client-key");
            start.Environment["PGCONNECT_TIMEOUT"] = "3";
            start.Environment["PGPASSFILE"] = Path.GetFullPath("artifacts/postgres-test/no-personal-password-file");
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync(value); process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); throw; }
            string result = await output; await error; // Never print authentication details or test secrets.
            return process.ExitCode == 0 && result.Trim() == "1";
        }
        bool created = false, changedHba = false;
        try
        {
            await Sql($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN"); created = true;
            changedHba = true;
            await File.WriteAllTextAsync(hba, $"hostssl all {role} 127.0.0.1/32 scram-sha-256{Environment.NewLine}" + original);
            await Sql("SELECT pg_catalog.pg_reload_conf()");
            // Labels are public; passwords are synthetic and never written to output/files/SQL.
            var cases = new (string Label, string Password, string Equivalent)[]
            {
                ("accented Latin", "Café_senha_42", "Cafe\u0301_senha_42"),
                ("CJK", "密碼_日本語_42", "密碼_日本語_42"),
                ("Arabic", "كلمةالسر", "كلمةالسر"),
                ("Hebrew", "סיסמה", "סיסמה"),
                ("supplementary fallback", "🔑🙂_42", "🔑🙂_42"),
                ("compatibility composition", "\u00AA\u2168_42", "aIX_42"),
                ("nonbreaking space", "one\u00A0two_42", "one two_42"),
                ("private use fallback", "\uE000_42", "\uE000_42"),
                ("bidi fallback", "abc_אב_42", "abc_אב_42"),
                ("ASCII quote and slash", "quote'\\_42", "quote'\\_42"),
            };
            foreach (var item in cases)
            {
                await Set(item.Password);
                check(await Login(item.Password), "Npgsql TLS SCRAM authenticates " + item.Label);
                check(await Login(item.Equivalent), "Npgsql accepts PostgreSQL-equivalent input: " + item.Label);
                check(await ExternalLogin(item.Password), "psql TLS SCRAM authenticates " + item.Label);
                check(!await Login(item.Password + "wrong"), "Wrong password rejected: " + item.Label);
            }
            string baseline = await Scalar($"SELECT rolpassword FROM pg_catalog.pg_authid WHERE rolname='{role}'");
            var rejected = new[] { "I\u00ADX", "one\u200Btwo", "\u00AA🙂", "invalid\uD800", "NUL\0value", "" };
            foreach (string item in rejected)
            {
                bool refused = false;
                try { await Set(item); } catch (ArgumentException) { refused = true; }
                check(refused, "Incompatible or invalid Unicode refused before ALTER ROLE");
                check(baseline == await Scalar($"SELECT rolpassword FROM pg_catalog.pg_authid WHERE rolname='{role}'"), "Refusal leaves existing verifier unchanged");
            }
            check(await Login(cases[^1].Password) && await ExternalLogin(cases[^1].Password), "Original password remains usable after all refusals");
            bool nonTls = false; try { await Set(cases[0].Password, connection); } catch (InvalidOperationException) { nonTls = true; }
            check(nonTls, "Unicode password change still requires TLS");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool stopped = false; try { await Set(cases[0].Password, "invalid connection syntax", canceled.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped, "Pre-cancellation precedes connection parsing and password extraction");
        }
        finally
        {
            try
            {
                if (changedHba) { await File.WriteAllTextAsync(hba, original); await Sql("SELECT pg_catalog.pg_reload_conf()"); }
            }
            finally { if (created) await Sql($"DROP ROLE {SqlIdentifier.Quote(role)}"); }
        }
    }
}
