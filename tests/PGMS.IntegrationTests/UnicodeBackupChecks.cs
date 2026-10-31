using System.Security;
using Npgsql;
using PGMS.PostgreSql;

internal static class UnicodeBackupChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        var baseSettings=new NpgsqlConnectionStringBuilder(connection);
        if(baseSettings.Host!="127.0.0.1" || baseSettings.Port!=55439 || baseSettings.Username!="pgms_test")throw new InvalidOperationException("Unicode backup checks require the isolated cluster.");
        string suffix=Guid.NewGuid().ToString("N"),role="backup_unicode_"+suffix,source="backup_unicode_source_"+suffix,target="backup_unicode_target_"+suffix;
        string qr=SqlIdentifier.Quote(role),qs=SqlIdentifier.Quote(source),qt=SqlIdentifier.Quote(target);
        string password="Café_密碼_🔑_"+suffix;
        string tools=Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")??throw new Exception("Missing isolated test tools.");
        string artifact=Path.GetFullPath(Path.Combine("artifacts/backup-unicode","pasta_ação密碼_"+suffix));Directory.CreateDirectory(artifact);
        string archive=Path.Combine(artifact,"arquivo_密碼🔑.backup");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        string hba;await using(var cmd=new NpgsqlCommand("SHOW hba_file",admin))hba=(string)(await cmd.ExecuteScalarAsync())!;
        if(!string.Equals(Path.GetFullPath(hba),TestClusterPaths.Data("pg_hba.conf"),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("HBA outside isolated cluster.");
        string original=await File.ReadAllTextAsync(hba);bool createdRole=false,createdSource=false,createdTarget=false,changed=false;
        try
        {
            await Sql($"CREATE ROLE {qr} LOGIN");createdRole=true;
            await Sql($"CREATE DATABASE {qs} OWNER {qr}");createdSource=true;
            await Sql($"CREATE DATABASE {qt} OWNER {qr}");createdTarget=true;
            using(var secret=new SecureString())
            {
                foreach(char c in password)secret.AppendChar(c);secret.MakeReadOnly();
                await RolePasswordService.SetAsync(new NpgsqlConnectionStringBuilder(connection){SslMode=SslMode.Require}.ConnectionString,role,secret);
            }
            changed=true;await File.WriteAllTextAsync(hba,$"hostssl all {role} 127.0.0.1/32 scram-sha-256{Environment.NewLine}"+original);await Sql("SELECT pg_catalog.pg_reload_conf()");
            string unicodeRoot=Path.Combine(artifact,"raiz_密碼.crt");File.Copy(TestClusterPaths.Data("server.crt"),unicodeRoot);
            var login=new NpgsqlConnectionStringBuilder(connection){Database=source,Username=role,Password=password,Pooling=false,SslMode=SslMode.VerifyFull,RootCertificate=unicodeRoot};
            bool scram=false;
            for(int i=0;i<40 && !scram;i++)
            {
                var wrong=new NpgsqlConnectionStringBuilder(login.ConnectionString){Password=password+"wrong"};
                try{await using var rejected=new NpgsqlConnection(wrong.ConnectionString);await rejected.OpenAsync();}
                catch(PostgresException ex)when(ex.SqlState=="28P01"){scram=true;}
                if(!scram)await Task.Delay(25);
            }
            check(scram,"Unicode backup fixture requires real TLS SCRAM rather than trust authentication");
            await using(var client=new NpgsqlConnection(login.ConnectionString))
            {
                await client.OpenAsync();await using var cmd=new NpgsqlCommand("CREATE TABLE public.items(id serial PRIMARY KEY,value text NOT NULL);INSERT INTO public.items(value) VALUES('ação 密碼 🔑');",client);await cmd.ExecuteNonQueryAsync();
            }
            await new BackupService(login.ConnectionString,tools).BackupAsync(archive);
            check(File.ReadAllBytes(unicodeRoot).AsSpan().SequenceEqual(File.ReadAllBytes(TestClusterPaths.Data("server.crt"))),"Unicode root certificate remains byte-exact after verified TLS backup");
            check(File.Exists(archive) && new FileInfo(archive).Length>0,"pg_dump authenticates a Unicode password and publishes its complete archive");
            login.Database=target;
            await new BackupService(login.ConnectionString,tools).RestoreAsync(archive);
            await using(var restored=new NpgsqlConnection(login.ConnectionString))
            {
                await restored.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT value FROM public.items WHERE id=1",restored);
                check((string)(await cmd.ExecuteScalarAsync())! =="ação 密碼 🔑","pg_restore authenticates Unicode password and preserves exact restored data");
                cmd.CommandText="INSERT INTO public.items(value) VALUES('next') RETURNING id";check((int)(await cmd.ExecuteScalarAsync())! ==2,"Unicode credential roundtrip preserves serial sequence state");
            }
            check(!Directory.EnumerateFiles(artifact,"*.partial").Any(),"Successful Unicode credential backup leaves no partial archive");
            check(File.ReadAllBytes(archive).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(password))<0,"Archive contains no plaintext connection password");
            string sqlArchive=Path.Combine(artifact,"script_密碼🔑.sql");login.Database=source;
            await new BackupService(login.ConnectionString,tools).BackupWithOptionsAsync(sqlArchive,new(BackupFormat.PlainSql));
            check(File.Exists(sqlArchive) && !File.ReadAllText(sqlArchive).Contains(password,StringComparison.Ordinal),"Plain SQL backup supports Unicode password/path without writing the connection secret");
            string globalArchive=Path.Combine(artifact,"globais_密碼🔑.sql");
            await new BackupService(login.ConnectionString,tools).BackupGlobalsAsync(globalArchive);
            string globalScript=await File.ReadAllTextAsync(globalArchive);
            check(globalScript.Contains(role) && !globalScript.Contains(password,StringComparison.Ordinal) && !globalScript.Contains("SCRAM-SHA-256$"),"Global export authenticates Unicode TLS/SCRAM password as ordinary role and omits secrets");
            login.Database=target;
            await using(var empty=new NpgsqlConnection(login.ConnectionString)){await empty.OpenAsync();await using var c=new NpgsqlCommand("DROP TABLE public.items",empty);await c.ExecuteNonQueryAsync();}
            await new BackupService(login.ConnectionString,tools).RestoreSqlAsync(sqlArchive);
            await using(var restoredSql=new NpgsqlConnection(login.ConnectionString))
            {await restoredSql.OpenAsync();await using var c=new NpgsqlCommand("SELECT value FROM public.items WHERE id=1",restoredSql);check((string)(await c.ExecuteScalarAsync())! =="ação 密碼 🔑","psql restores exact plain SQL data with a real Unicode TLS/SCRAM password and Unicode path");c.CommandText="INSERT INTO public.items(value) VALUES('next SQL') RETURNING id";check((int)(await c.ExecuteScalarAsync())! ==2,"Unicode-authenticated psql roundtrip preserves serial sequence state");}
            login.Database=source;
            string invalidRoot=Path.Combine(artifact,"inválido_密碼.crt");await File.WriteAllTextAsync(invalidRoot,"not a certificate");login.RootCertificate=invalidRoot;
            string invalidRootArchive=Path.Combine(artifact,"invalid-root.backup");bool rootRefused=false;
            try{await new BackupService(login.ConnectionString,tools).BackupAsync(invalidRootArchive);}catch(IOException){rootRefused=true;}
            check(rootRefused && !File.Exists(invalidRootArchive),"VerifyFull refuses invalid Unicode root certificate without publishing a backup");
            login.RootCertificate=unicodeRoot;
            login.Database=source;login.Password=password+"wrong";
            string failedArchive=Path.Combine(artifact,"wrong-password.backup");bool denied=false;
            try{await new BackupService(login.ConnectionString,tools).BackupAsync(failedArchive);}
            catch(IOException ex){denied=!ex.Message.Contains(login.Password,StringComparison.Ordinal) && !ex.Message.Contains(password,StringComparison.Ordinal);}
            check(denied && !File.Exists(failedArchive),"Wrong Unicode tool password is refused without publishing an archive or echoing the secret");
            check(!Directory.EnumerateFiles(artifact,"*.partial").Any(),"Failed Unicode authentication removes its partial archive");
            string failedGlobal=Path.Combine(artifact,"wrong-global.sql");denied=false;
            try{await new BackupService(login.ConnectionString,tools).BackupGlobalsAsync(failedGlobal);}
            catch(IOException ex){denied=!ex.Message.Contains(login.Password,StringComparison.Ordinal) && !ex.Message.Contains(password,StringComparison.Ordinal);}
            check(denied && !File.Exists(failedGlobal) && !Directory.EnumerateFiles(artifact,"*.partial").Any(),"Global export refuses wrong Unicode password without secret echo or partial publication");
            login.Password=password+"\nline";bool lineBreak=false;
            try{await new BackupService(login.ConnectionString,tools).BackupAsync(Path.Combine(artifact,"line-break.backup"));}
            catch(ArgumentException ex){lineBreak=ex.Message.Contains("line breaks",StringComparison.Ordinal) && !ex.Message.Contains(login.Password,StringComparison.Ordinal);}
            check(lineBreak && !File.Exists(Path.Combine(artifact,"line-break.backup")),"Unsupported Unicode line-break prompt input is refused before launching a tool");
            login.Password=password;
            foreach(string field in new[]{"Root Certificate","SSL Certificate","SSL Key"})
            {
                var absent=new NpgsqlConnectionStringBuilder(login.ConnectionString);absent[field]=Path.GetFullPath("artifacts/missing-tls-"+suffix+".pem");
                string missingOutput=Path.Combine(artifact,"missing-"+field.Replace(' ','-')+".backup");bool missingRefused=false;
                try{await new BackupService(absent.ConnectionString,tools).BackupAsync(missingOutput);}catch(FileNotFoundException ex){missingRefused=ex.Message.Contains("configured TLS");}
                check(missingRefused && !File.Exists(missingOutput),"Explicit absent TLS file is refused before tools can ignore it: "+field);
            }
            login.Password=password;using var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;
            try{await new BackupService(login.ConnectionString,tools).BackupAsync(Path.Combine(artifact,"canceled.backup"),canceled.Token);}catch(OperationCanceledException){stopped=true;}
            check(stopped && !File.Exists(Path.Combine(artifact,"canceled.backup")),"Pre-canceled Unicode backup publishes no archive and starts no authentication");
        }
        finally
        {
            if(changed){await File.WriteAllTextAsync(hba,original);await Sql("SELECT pg_catalog.pg_reload_conf()");}
            if(createdTarget)await Sql($"DROP DATABASE {qt}");if(createdSource)await Sql($"DROP DATABASE {qs}");if(createdRole)await Sql($"DROP ROLE {qr}");
        }
        await ClientCertificateBackupChecks.RunAsync(connection,check);
    }
}
