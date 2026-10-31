using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using PGMS.PostgreSql;

internal static class ClientCertificateBackupChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        var baseSettings=new NpgsqlConnectionStringBuilder(connection);
        if(baseSettings.Host!="127.0.0.1" || baseSettings.Port!=TestClusterEndpoint.Port || baseSettings.Username!="pgms_test")throw new InvalidOperationException("Isolated cluster required.");
        string suffix=Guid.NewGuid().ToString("N"),role="cert_"+suffix,source="cert_source_"+suffix,target="cert_target_"+suffix;
        string tools=Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")!;
        string folder=Path.GetFullPath(Path.Combine("artifacts/client-cert",suffix));Directory.CreateDirectory(folder);
        string unicode=Path.Combine(folder,"certificados_密碼");Directory.CreateDirectory(unicode);
        string caFile=Path.Combine(folder,"ca.crt"),certFile=Path.Combine(unicode,"cliente_密碼.crt"),keyFile=Path.Combine(unicode,"chave_密碼.key");
        using var caKey=RSA.Create(2048);var caRequest=new CertificateRequest(new X500DistinguishedName("CN=PGMS synthetic client CA",X500DistinguishedNameFlags.UseUTF8Encoding),caKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign,true));
        using var ca=caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(2));
        using var clientKey=RSA.Create(2048);var clientRequest=new CertificateRequest(new X500DistinguishedName("CN="+role,X500DistinguishedNameFlags.UseUTF8Encoding),clientKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        clientRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));clientRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid("1.3.6.1.5.5.7.3.2")},true));
        using var clientCert=clientRequest.Create(ca,DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddDays(1),RandomNumberGenerator.GetBytes(16));
        await File.WriteAllTextAsync(caFile,ca.ExportCertificatePem());await File.WriteAllTextAsync(certFile,clientCert.ExportCertificatePem());await File.WriteAllTextAsync(keyFile,clientKey.ExportPkcs8PrivateKeyPem());
        byte[] originalKey=await File.ReadAllBytesAsync(keyFile),originalCert=await File.ReadAllBytesAsync(certFile);
        string archive=Path.Combine(unicode,"backup_密碼.backup");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task<object?> Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);return await c.ExecuteScalarAsync();}
        string hba=(string)(await Sql("SHOW hba_file"))!;
        if(Path.GetFullPath(hba)!=TestClusterPaths.Data("pg_hba.conf"))throw new InvalidOperationException("Unexpected HBA.");
        if((string)(await Sql("SHOW ssl_ca_file"))! != "")throw new InvalidOperationException("Fixture requires default empty ssl_ca_file.");
        string originalHba=await File.ReadAllTextAsync(hba);bool createdRole=false,createdSource=false,createdTarget=false,configured=false;
        try
        {
            await Sql($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN");createdRole=true;
            await Sql($"CREATE DATABASE {SqlIdentifier.Quote(source)} OWNER {SqlIdentifier.Quote(role)}");createdSource=true;
            await Sql($"CREATE DATABASE {SqlIdentifier.Quote(target)} OWNER {SqlIdentifier.Quote(role)}");createdTarget=true;
            configured=true;await Sql("ALTER SYSTEM SET ssl_ca_file = '"+caFile.Replace('\\','/').Replace("'","''")+"'");
            await File.WriteAllTextAsync(hba,$"hostssl all {role} 127.0.0.1/32 cert{Environment.NewLine}"+originalHba);await Sql("SELECT pg_reload_conf()");
            var login=new NpgsqlConnectionStringBuilder(connection){Database=source,Username=role,Password="",Pooling=false,SslMode=SslMode.VerifyFull,RootCertificate=TestClusterPaths.Data("server.crt"),SslCertificate=certFile,SslKey=keyFile};
            bool authenticated=false;
            for(int i=0;i<40 && !authenticated;i++)
            {
                try{await using var c=new NpgsqlConnection(login.ConnectionString);await c.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT client_dn FROM pg_stat_ssl WHERE pid=pg_backend_pid()",c);authenticated=((string?)await cmd.ExecuteScalarAsync())?.Contains(role)==true;}
                catch(NpgsqlException)when(i<39){}
                if(!authenticated)await Task.Delay(50);
            }
            check(authenticated,"Client certificate fixture authenticates exact CN over verified TLS");
            await using(var c=new NpgsqlConnection(login.ConnectionString)){await c.OpenAsync();await using var cmd=new NpgsqlCommand("CREATE TABLE public.items(value text);INSERT INTO public.items VALUES('certificado 密碼')",c);await cmd.ExecuteNonQueryAsync();}
            await new BackupService(login.ConnectionString,tools).BackupAsync(archive);
            check(File.Exists(archive),"pg_dump authenticates using Unicode client certificate and key paths");
            login.Database=target;await new BackupService(login.ConnectionString,tools).RestoreAsync(archive);
            await using(var c=new NpgsqlConnection(login.ConnectionString)){await c.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT value FROM public.items",c);check((string)(await cmd.ExecuteScalarAsync())! =="certificado 密碼","Client-certificate archive roundtrip restores exact data");}
            check((await File.ReadAllBytesAsync(keyFile)).SequenceEqual(originalKey) && (await File.ReadAllBytesAsync(certFile)).SequenceEqual(originalCert),"Client certificate and key source bytes remain unchanged");
            string keyPassword="PGMS-synthetic-key-"+suffix;
            await File.WriteAllTextAsync(keyFile,clientKey.ExportEncryptedPkcs8PrivateKeyPem(keyPassword,new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc,HashAlgorithmName.SHA256,10000)));
            byte[] encryptedKey=await File.ReadAllBytesAsync(keyFile);login.SslPassword=keyPassword;login.Database=source;
            string encryptedArchive=Path.Combine(unicode,"encrypted-key.backup");
            string ownStaging="PGMS-restore-v1-*-"+Environment.ProcessId+"-*.partial";
            var before=Directory.GetFiles(Path.GetTempPath(),ownStaging).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool passwordRefused=false;
            try{await new BackupService(login.ConnectionString,tools).BackupAsync(encryptedArchive);}catch(ArgumentException ex){passwordRefused=ex.Message.Contains("not supported") && !ex.Message.Contains(keyPassword,StringComparison.Ordinal);}
            check(passwordRefused && !File.Exists(encryptedArchive) && (await File.ReadAllBytesAsync(keyFile)).SequenceEqual(encryptedKey),"Configured encrypted-key password is refused explicitly without secret echo or key rewrite");
            login.SslPassword="";bool encryptedRefused=false;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try{await new BackupService(login.ConnectionString,tools).BackupAsync(encryptedArchive,timeout.Token);}catch(IOException){encryptedRefused=true;}
            check(encryptedRefused && !timeout.IsCancellationRequested && !File.Exists(encryptedArchive),"Encrypted key without configured password fails without an invisible prompt or publication");
            check(!Directory.GetFiles(Path.GetTempPath(),ownStaging).Except(before,StringComparer.OrdinalIgnoreCase).Any(),"Certificate and key copies are removed after encrypted-key failure");
            login.Database=source;login.SslCertificate="";login.SslKey="";string refused=Path.Combine(folder,"missing-cert.backup");bool denied=false;
            try{await new BackupService(login.ConnectionString,tools).BackupAsync(refused);}catch(IOException){denied=true;}
            check(denied && !File.Exists(refused),"Server requires actual client certificate rather than password or trust fallback");
        }
        finally
        {
            if(configured){await File.WriteAllTextAsync(hba,originalHba);await Sql("ALTER SYSTEM RESET ssl_ca_file");await Sql("SELECT pg_reload_conf()");}
            if(createdTarget)await Sql($"DROP DATABASE {SqlIdentifier.Quote(target)}");if(createdSource)await Sql($"DROP DATABASE {SqlIdentifier.Quote(source)}");if(createdRole)await Sql($"DROP ROLE {SqlIdentifier.Quote(role)}");
        }
    }
}
