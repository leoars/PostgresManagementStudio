using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record ServerTlsSetting(string Name, string Value, bool FromAutoConf);
public sealed record ServerTlsState(bool SslOn, string CertificateFile, string KeyFile, string DataDirectory, bool Superuser, bool Windows, IReadOnlyList<ServerTlsSetting> Settings)
{
    public string? Blocker => !Superuser ? "Configuring server TLS requires a superuser connection (it writes files in the data directory and runs ALTER SYSTEM)."
        : !Windows ? "This version configures TLS automatically only on servers running on Windows. On Linux and macOS PostgreSQL requires the key file to be readable only by its owner, which cannot be set through SQL; install the files manually and set ssl = on."
        : null;
}

// A PEM certificate and its unencrypted private key, checked to belong together.
public sealed class ServerCertificateBundle
{
    public string CertificatePem { get; }
    internal string KeyPem { get; }
    public string Thumbprint { get; }
    public string Subject { get; }
    public DateTimeOffset NotAfter { get; }
    public bool SelfSigned { get; }
    public IReadOnlyList<string> Names { get; }

    internal ServerCertificateBundle(string certificatePem, string keyPem, bool checkPair = true)
    {
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        if (checkPair)
        {
            using var pair = X509Certificate2.CreateFromPem(certificatePem, keyPem); // Throws when the key does not match.
            if (!pair.HasPrivateKey) throw new ArgumentException("The private key does not match the certificate.");
        }
        CertificatePem = certificate.ExportCertificatePem() + "\n"; KeyPem = keyPem.Trim() + "\n";
        Thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        Subject = certificate.Subject; NotAfter = certificate.NotAfter; SelfSigned = certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
        var names = new List<string>();
        foreach (var extension in certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(extension.EnumerateDnsNames());
            names.AddRange(extension.EnumerateIPAddresses().Select(a => a.ToString()));
        }
        Names = names;
    }

    public static ServerCertificateBundle Generate(IEnumerable<string> names, int years)
    {
        var list = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) throw new ArgumentException("Enter at least one server name or IP address.");
        if (years is < 1 or > 20) throw new ArgumentException("Validity must be from 1 to 20 years.");
        var san = new SubjectAlternativeNameBuilder();
        foreach (string name in list)
        {
            if (IPAddress.TryParse(name.Trim('[', ']'), out var address)) san.AddIpAddress(address);
            else if (Uri.CheckHostName(name) == UriHostNameType.Dns) san.AddDnsName(name);
            else throw new ArgumentException($"'{name}' is not a valid server name or IP address.");
        }
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + list[0].Replace(",", "\\,", StringComparison.Ordinal)), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(san.Build());
        // Self-signed: the same certificate is the client's root, so it is marked as a CA.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(years));
        return new(certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    public static ServerCertificateBundle Import(string certificatePem, string keyPem)
    {
        if (keyPem.Contains("ENCRYPTED", StringComparison.Ordinal)) throw new ArgumentException("The private key is encrypted. PostgreSQL on Windows cannot prompt for its passphrase; export an unencrypted key.");
        if (!keyPem.Contains("PRIVATE KEY-----", StringComparison.Ordinal)) throw new ArgumentException("The key file does not contain a PEM private key.");
        if (!certificatePem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)) throw new ArgumentException("The certificate file does not contain a PEM certificate.");
        try
        {
            var bundle = new ServerCertificateBundle(certificatePem, keyPem);
            if (bundle.NotAfter < DateTimeOffset.Now) throw new ArgumentException($"The certificate expired on {bundle.NotAfter:yyyy-MM-dd}.");
            return bundle;
        }
        catch (CryptographicException ex) { throw new ArgumentException("The certificate and key could not be read as a matching PEM pair: " + ex.Message, ex); }
    }
}

// VerifyFull: a VerifyFull session succeeded, with the certificate itself as root when self-signed
// or with the Windows trust store otherwise.
public sealed record ServerTlsResult(string CertificateFile, string KeyFile, bool VerifyFull);

public sealed class ServerTlsService(string connectionString)
{
    private static readonly string[] SettingNames = ["ssl", "ssl_cert_file", "ssl_key_file"];
    internal string? FileNameOverride { get; init; } // Tests force a collision with existing files.

    private NpgsqlConnection Open(string? overrideConnection = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(overrideConnection ?? connectionString) { Pooling = false };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    public async Task<ServerTlsState> InspectAsync(CancellationToken token = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(token).ConfigureAwait(false);
        return await InspectAsync(connection, token).ConfigureAwait(false);
    }

    private static async Task<ServerTlsState> InspectAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT r.rolsuper, pg_catalog.version(), pg_catalog.current_setting('data_directory', true) FROM pg_catalog.pg_roles r WHERE r.rolname = current_user", connection) { CommandTimeout = 15 };
        bool superuser; string version, dataDirectory;
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("The connected role was not found in pg_roles.");
            superuser = reader.GetBoolean(0); version = reader.GetString(1); dataDirectory = reader.IsDBNull(2) ? "" : reader.GetString(2);
        }
        var settings = new List<ServerTlsSetting>();
        await using (var settingsCommand = new NpgsqlCommand("SELECT name, setting, coalesce(sourcefile, '') FROM pg_catalog.pg_settings WHERE name = ANY(@names) ORDER BY name", connection) { CommandTimeout = 15 })
        {
            settingsCommand.Parameters.AddWithValue("names", SettingNames);
            await using var reader = await settingsCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                settings.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2).EndsWith("postgresql.auto.conf", StringComparison.OrdinalIgnoreCase)));
        }
        string Value(string name) => settings.FirstOrDefault(s => s.Name == name)?.Value ?? "";
        bool windows = version.Contains("Windows", StringComparison.OrdinalIgnoreCase) || version.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) || version.Contains("mingw", StringComparison.OrdinalIgnoreCase);
        return new(Value("ssl") == "on", Value("ssl_cert_file"), Value("ssl_key_file"), dataDirectory, superuser, windows, settings);
    }

    // Writes new files (never overwrites), points ssl_cert_file/ssl_key_file at them, enables ssl and
    // verifies a TLS session that presents exactly this certificate. Any failure restores the settings.
    public async Task<ServerTlsResult> ApplyAsync(ServerCertificateBundle bundle, CancellationToken token = default)
    {
        ServerTlsState before;
        string name = FileNameOverride ?? "pgms-tls-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        string certificateFile = name + ".crt", keyFile = name + ".key";
        await using (var connection = Open())
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            before = await InspectAsync(connection, token).ConfigureAwait(false);
            if (before.Blocker is { } blocker) throw new InvalidOperationException(blocker);
            if (before.DataDirectory.Length == 0) throw new InvalidOperationException("The server did not report its data directory.");
            char separator = before.DataDirectory.Contains('\\') ? '\\' : '/';
            string directory = before.DataDirectory.TrimEnd('\\', '/') + separator;
            await using (var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                // Keep the key out of the server log and statistics; the file contents travel only as parameters.
                await Execute(connection, "SET LOCAL log_statement = 'none'; SET LOCAL log_min_duration_statement = -1; SET LOCAL log_min_error_statement = 'panic'; SET LOCAL log_parameter_max_length = 0; SET LOCAL log_parameter_max_length_on_error = 0", token).ConfigureAwait(false);
                if (await Scalar(connection, "SELECT count(*) FROM pg_catalog.pg_settings WHERE name = 'pgaudit.log'", token).ConfigureAwait(false) is long audit && audit > 0)
                    await Execute(connection, "SET LOCAL pgaudit.log = 'none'", token).ConfigureAwait(false);
                await Execute(connection, "CREATE TEMP TABLE pgms_tls_file (n integer, line text) ON COMMIT DROP", token).ConfigureAwait(false);
                foreach (var (file, pem) in new[] { (certificateFile, bundle.CertificatePem), (keyFile, bundle.KeyPem) })
                {
                    string path = directory + file;
                    await using (var exists = new NpgsqlCommand("SELECT (pg_catalog.pg_stat_file(@path, true)).size IS NOT NULL", connection, transaction))
                    {
                        exists.Parameters.AddWithValue("path", path);
                        if ((bool)(await exists.ExecuteScalarAsync(token).ConfigureAwait(false))!) throw new InvalidOperationException($"{file} already exists in the data directory; nothing was changed.");
                    }
                    await Execute(connection, "TRUNCATE pgms_tls_file", token).ConfigureAwait(false);
                    await using (var insert = new NpgsqlCommand("INSERT INTO pgms_tls_file SELECT ordinality, line FROM unnest(@lines) WITH ORDINALITY AS t(line, ordinality)", connection, transaction))
                    {
                        insert.Parameters.AddWithValue("lines", pem.Replace("\r", "", StringComparison.Ordinal).TrimEnd('\n').Split('\n'));
                        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    await Execute(connection, $"COPY (SELECT line FROM pgms_tls_file ORDER BY n) TO {Literal(path)}", token).ConfigureAwait(false);
                }
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }
        }
        try
        {
            await AlterSystemAsync([("ssl_cert_file", certificateFile), ("ssl_key_file", keyFile), ("ssl", "on")], token).ConfigureAwait(false);
            await WaitForCertificateAsync(bundle, token).ConfigureAwait(false);
            return new(certificateFile, keyFile, await VerifiesFullAsync(bundle.SelfSigned ? bundle : null, token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            try { await RestoreAsync(before, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception restore) when (restore is NpgsqlException or IOException or TimeoutException)
            { throw new InvalidOperationException($"TLS verification failed ({ex.Message}) and the previous settings could not be restored ({restore.Message}). Check ssl, ssl_cert_file and ssl_key_file in postgresql.auto.conf.", ex); }
            throw new InvalidOperationException($"The server did not present the new certificate over TLS ({ex.Message}). The previous ssl, ssl_cert_file and ssl_key_file settings were restored; the files {certificateFile} and {keyFile} remain in the data directory.", ex);
        }
    }

    // Puts ssl, ssl_cert_file and ssl_key_file back as they were: values that came from
    // postgresql.auto.conf are set again, the rest are reset so postgresql.conf applies.
    public async Task RestoreAsync(ServerTlsState previous, CancellationToken token = default)
    {
        await using var connection = await OpenForRecoveryAsync(token).ConfigureAwait(false);
        foreach (string setting in SettingNames)
        {
            var old = previous.Settings.FirstOrDefault(s => s.Name == setting);
            await Execute(connection, old is { FromAutoConf: true } ? $"ALTER SYSTEM SET {setting} = {Literal(old.Value)}" : $"ALTER SYSTEM RESET {setting}", token).ConfigureAwait(false);
        }
        await Execute(connection, "SELECT pg_catalog.pg_reload_conf()", token).ConfigureAwait(false);
    }

    // A certificate the server cannot load turns TLS off for new sessions, so a profile that
    // requires TLS could not undo it. Recovery then retries with Prefer; SCRAM still protects the password.
    private async Task<NpgsqlConnection> OpenForRecoveryAsync(CancellationToken token)
    {
        var connection = Open();
        try { await connection.OpenAsync(token).ConfigureAwait(false); return connection; }
        catch (NpgsqlException) when (new NpgsqlConnectionStringBuilder(connectionString).SslMode is SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            var fallback = Open(new NpgsqlConnectionStringBuilder(connectionString) { SslMode = SslMode.Prefer }.ConnectionString);
            try { await fallback.OpenAsync(token).ConfigureAwait(false); return fallback; }
            catch { await fallback.DisposeAsync().ConfigureAwait(false); throw; }
        }
    }

    private async Task AlterSystemAsync(IEnumerable<(string Name, string Value)> values, CancellationToken token)
    {
        await using var connection = Open();
        await connection.OpenAsync(token).ConfigureAwait(false);
        foreach (var (setting, value) in values) await Execute(connection, $"ALTER SYSTEM SET {setting} = {Literal(value)}", token).ConfigureAwait(false);
        await Execute(connection, "SELECT pg_catalog.pg_reload_conf()", token).ConfigureAwait(false);
    }

    // The reload is asynchronous: retry until a new TLS session presents this exact certificate.
    private async Task WaitForCertificateAsync(ServerCertificateBundle bundle, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15); Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try { if (await PresentsAsync(bundle, token).ConfigureAwait(false)) return; last = null; }
            catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or System.Security.Authentication.AuthenticationException) { last = ex; }
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        throw new TimeoutException(last?.Message ?? "The server kept presenting a different certificate.");
    }

    private async Task<bool> PresentsAsync(ServerCertificateBundle bundle, CancellationToken token)
    {
        string? presented = null;
        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString) { SslMode = SslMode.Require, Pooling = false }.ConnectionString);
        builder.UseSslClientAuthenticationOptionsCallback(options => options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            presented = certificate?.GetCertHashString(HashAlgorithmName.SHA256);
            return true; // Identity is checked below by thumbprint, not by chain.
        });
        await using var source = builder.Build();
        await using var connection = await source.OpenConnectionAsync(token).ConfigureAwait(false);
        bool tls = await Scalar(connection, "SELECT ssl FROM pg_catalog.pg_stat_ssl WHERE pid = pg_catalog.pg_backend_pid()", token).ConfigureAwait(false) is true;
        return tls && string.Equals(presented, bundle.Thumbprint, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> VerifiesFullAsync(ServerCertificateBundle? root, CancellationToken token)
    {
        string? rootFile = root is null ? null : Path.Combine(Path.GetTempPath(), "pgms-root-" + Guid.NewGuid().ToString("N") + ".crt");
        try
        {
            if (rootFile is not null) await File.WriteAllTextAsync(rootFile, root!.CertificatePem, token).ConfigureAwait(false);
            await using var connection = Open(new NpgsqlConnectionStringBuilder(connectionString) { SslMode = SslMode.VerifyFull, RootCertificate = rootFile }.ConnectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or System.Security.Authentication.AuthenticationException) { return false; }
        finally { if (rootFile is not null) File.Delete(rootFile); }
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
