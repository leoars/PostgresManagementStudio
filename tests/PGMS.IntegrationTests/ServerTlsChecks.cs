using Npgsql;
using PGMS.PostgreSql;

internal static class ServerTlsChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host != "127.0.0.1" || settings.Port != 55439 || settings.Username != "pgms_test")
            throw new InvalidOperationException("Server TLS checks require the isolated cluster.");
        string tls = new NpgsqlConnectionStringBuilder(connection) { SslMode = SslMode.Require }.ConnectionString;
        var service = new ServerTlsService(tls);
        // Statistics must be collecting during Apply for the leak check below to mean anything.
        await using (var setup = new NpgsqlConnection(tls))
        {
            await setup.OpenAsync();
            await using var extension = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS pg_stat_statements", setup);
            await extension.ExecuteNonQueryAsync();
        }
        var before = await service.InspectAsync();
        check(before.SslOn && before.Superuser && before.Windows && before.Blocker is null && before.CertificateFile == "server.crt", "Server TLS inspection reports ssl, files, superuser and Windows");

        var generated = ServerCertificateBundle.Generate(["127.0.0.1", "localhost", "::1"], 1);
        var other = ServerCertificateBundle.Generate(["127.0.0.1"], 1);
        check(generated.SelfSigned && generated.Names.Contains("127.0.0.1") && generated.Names.Contains("localhost") && generated.Names.Contains("::1") && generated.NotAfter > DateTimeOffset.Now.AddDays(360), "Generated certificate is self-signed with the requested DNS and IP names");
        bool Refused(Func<object> action) { try { action(); return false; } catch (ArgumentException) { return true; } }
        check(Refused(() => ServerCertificateBundle.Import(generated.CertificatePem, other.KeyPem)), "Import refuses a key that does not match the certificate");
        check(Refused(() => ServerCertificateBundle.Import(generated.CertificatePem, "-----BEGIN ENCRYPTED PRIVATE KEY-----\nAAAA\n-----END ENCRYPTED PRIVATE KEY-----")), "Import refuses an encrypted private key");
        check(Refused(() => ServerCertificateBundle.Generate(["bad name!"], 1)) && Refused(() => ServerCertificateBundle.Generate([" "], 1)) && Refused(() => ServerCertificateBundle.Generate(["localhost"], 0)), "Generation refuses invalid names and validity");
        check(ServerCertificateBundle.Import(generated.CertificatePem, generated.KeyPem).Thumbprint == generated.Thumbprint, "Import accepts a matching unencrypted PEM pair");

        try
        {
            var result = await service.ApplyAsync(generated);
            var after = await service.InspectAsync();
            check(after.SslOn && after.CertificateFile == result.CertificateFile && after.KeyFile == result.KeyFile && result.CertificateFile.StartsWith("pgms-tls-", StringComparison.Ordinal) && result.VerifyFull, "Apply writes new files, points the settings at them and verifies VerifyFull with the certificate as root");
            string root = Path.Combine(Path.GetTempPath(), "pgms-test-root-" + Guid.NewGuid().ToString("N") + ".crt");
            try
            {
                await File.WriteAllTextAsync(root, generated.CertificatePem);
                await using var verified = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { SslMode = SslMode.VerifyFull, RootCertificate = root }.ConnectionString);
                await verified.OpenAsync();
                await using var ssl = new NpgsqlCommand("SELECT ssl FROM pg_catalog.pg_stat_ssl WHERE pid = pg_catalog.pg_backend_pid()", verified);
                check(await ssl.ExecuteScalarAsync() is true, "An independent VerifyFull session trusts the generated certificate");
                await using var statements = new NpgsqlCommand("SELECT count(*) FROM pg_stat_statements WHERE query LIKE '%PRIVATE KEY%' OR query LIKE '%BEGIN CERTIFICATE%'", verified);
                check(await statements.ExecuteScalarAsync() is 0L, "Certificate and key contents never reach pg_stat_statements");
            }
            finally { File.Delete(root); }

            bool exists;
            await using (var admin = new NpgsqlConnection(tls))
            {
                await admin.OpenAsync();
                await using var stat = new NpgsqlCommand("SELECT (pg_catalog.pg_stat_file(@path, true)).size IS NOT NULL", admin);
                stat.Parameters.AddWithValue("path", Path.Combine(after.DataDirectory, "server.crt"));
                exists = (bool)(await stat.ExecuteScalarAsync())!;
            }
            check(exists, "The original server.crt is left in place");
            string? collision = null;
            try { await new ServerTlsService(tls) { FileNameOverride = "server" }.ApplyAsync(other); } catch (InvalidOperationException ex) { collision = ex.Message; }
            var unchanged = await service.InspectAsync();
            check(collision is not null && collision.Contains("already exists", StringComparison.Ordinal) && unchanged.CertificateFile == result.CertificateFile, "Apply never overwrites an existing file in the data directory");

            // A certificate with the wrong key: the server cannot load it, so Apply must restore the settings.
            var broken = new ServerCertificateBundle(other.CertificatePem, generated.KeyPem, checkPair: false);
            string? failure = null;
            try { await service.ApplyAsync(broken); } catch (InvalidOperationException ex) { failure = ex.Message; }
            var restored = await service.InspectAsync();
            check(failure is not null && failure.Contains("restored", StringComparison.Ordinal) && restored.CertificateFile == result.CertificateFile && restored.KeyFile == result.KeyFile, "Failed verification restores the previous certificate settings");
        }
        finally
        {
            await service.RestoreAsync(before);
            await Task.Delay(1000);
        }
        var final = await service.InspectAsync();
        bool tlsWorks;
        await using (var again = new NpgsqlConnection(tls))
        {
            await again.OpenAsync();
            await using var ssl = new NpgsqlCommand("SELECT ssl FROM pg_catalog.pg_stat_ssl WHERE pid = pg_catalog.pg_backend_pid()", again);
            tlsWorks = await ssl.ExecuteScalarAsync() is true;
        }
        check(final.CertificateFile == "server.crt" && final.KeyFile == "server.key" && tlsWorks, "Restore returns the cluster to its original certificate");
    }
}
