using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;
using PGMS.PostgreSql;

internal static class GlobalBackupChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string tools=Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")??throw new Exception("Missing isolated tools");
        string suffix=Guid.NewGuid().ToString("N")[..12],role="global_ação_"+suffix,member="global_member_"+suffix,space="global_space_"+suffix;
        string root=Path.GetFullPath(Path.Combine("artifacts/global-backup",suffix));Directory.CreateDirectory(root);
        string data=Path.Combine(root,"target"),location=Path.Combine(root,"tablespace"),archive=Path.Combine(root,"globais_漢字.sql");Directory.CreateDirectory(location);
        const int port=55440;
        using(var probe=new TcpListener(IPAddress.Loopback,port)){probe.Start();probe.Stop();}
        bool started=false,roles=false,tablespace=false;
        async Task Tool(string tool,params string[] args)
        {
            bool capture=tool!="pg_ctl";
            var start=new ProcessStartInfo(Path.Combine(tools,tool+".exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=capture,RedirectStandardError=capture};
            foreach(string arg in args)start.ArgumentList.Add(arg);
            using var process=Process.Start(start)!;var output=capture?process.StandardOutput.ReadToEndAsync():Task.FromResult("");var error=capture?process.StandardError.ReadToEndAsync():Task.FromResult("");
            await process.WaitForExitAsync();await output;string detail=await error;if(process.ExitCode!=0)throw new Exception(tool+": "+detail);
        }
        async Task<object?> Sql(string cs,string sql)
        {await using var client=new NpgsqlConnection(cs);await client.OpenAsync();await using var command=new NpgsqlCommand(sql,client);return await command.ExecuteScalarAsync();}
        string target=new NpgsqlConnectionStringBuilder(connection){Port=port}.ConnectionString;
        string Q(string value)=>SqlIdentifier.Quote(value);
        try
        {
            foreach(string name in new[]{"manutenção_漢字_"+suffix,"dbname=postgres "+suffix,"postgresql://invalid/"+suffix})
            {
                await Sql(connection,$"CREATE DATABASE {Q(name)}");
                try
                {
                    string unicodeArchive=Path.Combine(root,"maintenance-"+Guid.NewGuid().ToString("N")+".sql");
                    var settings=new NpgsqlConnectionStringBuilder(connection){Database=name};
                    await new BackupService(settings.ConnectionString,tools).BackupGlobalsAsync(unicodeArchive);
                    check(File.Exists(unicodeArchive),"Global export connects to exact maintenance database: "+name);
                    await Sql(connection,$"ALTER DATABASE {Q(name)} ALLOW_CONNECTIONS false");
                    string deniedArchive=unicodeArchive+".denied";bool denied=false;
                    try{await new BackupService(settings.ConnectionString,tools).BackupGlobalsAsync(deniedArchive);}catch(IOException){denied=true;}
                    check(denied && !File.Exists(deniedArchive),"Global export cannot fall back when selected maintenance database disallows connections");
                }
                finally{await Sql(connection,$"DROP DATABASE {Q(name)}");}
            }
            await Tool("initdb","-D",data,"-U","pgms_test","--auth=trust","--encoding=UTF8","--locale=C");
            await Tool("pg_ctl","-D",data,"-l",Path.Combine(root,"target.log"),"-o","-h 127.0.0.1 -p "+port,"-w","start");started=true;
            await Sql(connection,$"CREATE ROLE {Q(role)} LOGIN CONNECTION LIMIT 7 PASSWORD 'synthetic-only';CREATE ROLE {Q(member)};GRANT {Q(member)} TO {Q(role)} WITH ADMIN TRUE");roles=true;
            await Sql(connection,$"CREATE TABLESPACE {Q(space)} OWNER {Q(role)} LOCATION '{location.Replace("'","''")}'");tablespace=true;
            var source=new BackupService(connection,tools);
            await source.BackupGlobalsAsync(archive);
            string script=await File.ReadAllTextAsync(archive);
            check(script.Contains(role) && script.Contains(space),"Global export contains Unicode role and tablespace");
            check(!script.Contains("SCRAM-SHA-256$") && !script.Contains("synthetic-only"),"Global export omits role password verifiers and plaintext");
            check(!script.Contains("CREATE DATABASE"),"Global export excludes databases");
            bool refused=false;try{await source.BackupGlobalsAsync(archive);}catch(IOException){refused=true;}
            check(refused && await File.ReadAllTextAsync(archive)==script,"Existing global backup is preserved");
            // Release only this fixture's empty tablespace directory for target restore.
            await Sql(connection,$"DROP TABLESPACE {Q(space)}");tablespace=false;
            // Explicit review of this fixture's known bootstrap role conflict.
            // The product must not rewrite arbitrary global scripts or ignore errors.
            check(script.Split("CREATE ROLE pgms_test;").Length==2,"Fixture identifies exactly one pre-existing bootstrap role creation");
            string reviewed=Path.Combine(root,"reviewed_漢字.sql");
            await File.WriteAllTextAsync(reviewed,script.Replace("CREATE ROLE pgms_test;","-- Existing bootstrap pgms_test retained by explicit fixture review."));
            var restore=new BackupService(target,tools);await restore.RestoreGlobalsAsync(reviewed);
            check(Convert.ToInt32(await Sql(target,$"SELECT rolconnlimit FROM pg_roles WHERE rolname='{role}'"))==7,"Global restore preserves Unicode role attributes");
            check(await Sql(target,$"SELECT rolpassword IS NULL FROM pg_authid WHERE rolname='{role}'") is true,"Restored role has no password until explicitly configured");
            check(await Sql(target,$"SELECT admin_option FROM pg_auth_members WHERE roleid=(SELECT oid FROM pg_roles WHERE rolname='{member}') AND member=(SELECT oid FROM pg_roles WHERE rolname='{role}')") is true,"Global restore preserves membership ADMIN option");
            check((string?)await Sql(target,$"SELECT pg_get_userbyid(spcowner) FROM pg_tablespace WHERE spcname='{space}'")==role,"Global restore preserves tablespace ownership");
            string restoredLocation=(string)(await Sql(target,$"SELECT pg_tablespace_location(oid) FROM pg_tablespace WHERE spcname='{space}'"))!;
            check(Path.GetFullPath(restoredLocation).Equals(Path.GetFullPath(location),StringComparison.OrdinalIgnoreCase),"Global restore preserves tablespace location");
            refused=false;try{await restore.RestoreGlobalsAsync(archive);}catch(IOException){refused=true;}
            check(refused,"Conflicting global restore stops on first error rather than claiming success");
            string partial=Path.Combine(root,"partial.sql"),partialRole="global_partial_"+suffix;
            await File.WriteAllTextAsync(partial,$"CREATE ROLE {Q(partialRole)}; SELECT pgms_intentionally_missing_function();");
            refused=false;try{await restore.RestoreGlobalsAsync(partial);}catch(IOException){refused=true;}
            check(refused && await Sql(target,$"SELECT EXISTS(SELECT FROM pg_roles WHERE rolname='{partialRole}')") is true,"Global restore reports failure while earlier statements can remain committed");
            using var cancel=new CancellationTokenSource();cancel.Cancel();string cancelled=Path.Combine(root,"cancelled.sql");
            refused=false;try{await source.BackupGlobalsAsync(cancelled,cancel.Token);}catch(OperationCanceledException){refused=true;}
            check(refused && !File.Exists(cancelled),"Pre-cancelled global export publishes no file");
            string failed=Path.Combine(root,"failed.sql");refused=false;
            try{await new BackupService(connection,root).BackupGlobalsAsync(failed);}catch(FileNotFoundException){refused=true;}
            check(refused && !File.Exists(failed) && Directory.GetFiles(root,"*.partial").Length==0,"Missing global tool leaves no published or partial backup");
        }
        finally
        {
            try
            {
                if(tablespace)await Sql(connection,$"DROP TABLESPACE {Q(space)}");
                if(roles)await Sql(connection,$"DROP ROLE {Q(role)};DROP ROLE {Q(member)}");
            }
            finally { if(started)await Tool("pg_ctl","-D",data,"-m","fast","-w","stop"); }
        }
    }
}
