using System.Security.Cryptography;
using Npgsql;
using PGMS.PostgreSql;

internal static class UnicodeToolIdentifierChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        var settings=new NpgsqlConnectionStringBuilder(connection);
        if(settings.Host!="127.0.0.1" || settings.Port!=TestClusterEndpoint.Port || settings.Username!="pgms_test")throw new InvalidOperationException("Tool identifier checks require the isolated cluster.");
        string suffix=Guid.NewGuid().ToString("N")[..12],role="tools_é密碼_"+suffix,source="source_é密碼_?%='/"+suffix,target="target_é密碼_?%='/"+suffix;
        string tools=Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")??throw new Exception("Missing isolated tools.");
        string folder=Path.GetFullPath(Path.Combine("artifacts/tool-identifiers",suffix));Directory.CreateDirectory(folder);string archive=Path.Combine(folder,"source.backup");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        var created=new List<string>();bool roleCreated=false;
        string As(string database)=>new NpgsqlConnectionStringBuilder(connection){Database=database,Username=role,Pooling=false}.ConnectionString;
        try
        {
            await Sql($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN");roleCreated=true;
            foreach(string name in new[]{source,target}){await Sql($"CREATE DATABASE {SqlIdentifier.Quote(name)} OWNER {SqlIdentifier.Quote(role)}");created.Add(name);}
            await using(var client=new NpgsqlConnection(As(source)))
            {
                await client.OpenAsync();await using var c=new NpgsqlCommand("CREATE TABLE public.items(id integer GENERATED ALWAYS AS IDENTITY,value text,origin text);INSERT INTO public.items(value,origin) VALUES('ação 密碼 🔑',current_database())",client);await c.ExecuteNonQueryAsync();
                c.CommandText="SELECT current_user::text";check((string)(await c.ExecuteScalarAsync())! ==role,"Npgsql fixture connects using the exact Unicode role name");
            }
            await new BackupService(As(source),tools).BackupAsync(archive);
            check(File.Exists(archive) && new FileInfo(archive).Length>0,"pg_dump selects the exact Unicode database and role with reserved URI characters");
            await new BackupService(As(target),tools).RestoreAsync(archive);
            await using(var client=new NpgsqlConnection(As(target)))
            {
                await client.OpenAsync();await using var c=new NpgsqlCommand("SELECT value,origin,current_database(),current_user FROM public.items WHERE id=1",client);await using(var row=await c.ExecuteReaderAsync())
                {
                    check(await row.ReadAsync() && row.GetString(0)=="ação 密碼 🔑" && row.GetString(1)==source,"Unicode tool identifier roundtrip preserves exact data from the selected source");
                    check(row.GetString(2)==target && row.GetString(3)==role,"pg_restore writes only to the exact Unicode target as its selected ordinary owner");
                }
                c.CommandText="INSERT INTO public.items(value) VALUES('next') RETURNING id";check((int)(await c.ExecuteScalarAsync())! ==2,"Unicode target restore preserves identity generation state");
            }
            byte[] before=SHA256.HashData(await File.ReadAllBytesAsync(archive));bool refused=false;
            try{await new BackupService(As(source),tools).BackupAsync(archive);}catch(IOException){refused=true;}
            byte[] after=SHA256.HashData(await File.ReadAllBytesAsync(archive));
            check(refused && before.SequenceEqual(after),"Unicode identifier backup never overwrites an existing archive");
            string absent=Path.Combine(folder,"missing.backup");bool missing=false;
            try{await new BackupService(As("missing_密碼_"+suffix),tools).BackupAsync(absent);}catch(IOException){missing=true;}
            check(missing && !File.Exists(absent) && !Directory.EnumerateFiles(folder,"*.partial").Any(),"Missing Unicode database fails without fallback or published partial archive");
            string unicodeFolder=Path.Combine(folder,"pasta_é密碼🔑"),unicodeArchive=Path.Combine(unicodeFolder,"arquivo_é密碼🔑.backup");Directory.CreateDirectory(unicodeFolder);
            await new BackupService(As(source),tools).BackupAsync(unicodeArchive);
            check(File.Exists(unicodeArchive) && new FileInfo(unicodeArchive).Length>0,"Backup publishes the exact Unicode directory and archive filename");
            byte[] unicodeBefore=SHA256.HashData(await File.ReadAllBytesAsync(unicodeArchive));
            await using(var client=new NpgsqlConnection(As(target))){await client.OpenAsync();await using var c=new NpgsqlCommand("DROP TABLE public.items",client);await c.ExecuteNonQueryAsync();}
            await new BackupService(As(target),tools).RestoreAsync(unicodeArchive);
            await using(var client=new NpgsqlConnection(As(target)))
            {
                await client.OpenAsync();await using var c=new NpgsqlCommand("SELECT value,origin FROM public.items WHERE id=1",client);await using var row=await c.ExecuteReaderAsync();check(await row.ReadAsync() && row.GetString(0)=="ação 密碼 🔑" && row.GetString(1)==source,"Restore reads the exact Unicode archive path and preserves its data");
            }
            byte[] unicodeAfter=SHA256.HashData(await File.ReadAllBytesAsync(unicodeArchive));check(unicodeBefore.SequenceEqual(unicodeAfter),"Unicode path restore leaves the original archive bytes unchanged");
            check(!Directory.EnumerateFiles(unicodeFolder).Any(p=>p!=unicodeArchive),"Unicode path backup removes staging files after publication");
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();bool canceled=false;string canceledPath=Path.Combine(unicodeFolder,"cancelado_密碼.backup");
            try{await new BackupService(As(source),tools).BackupAsync(canceledPath,cancellation.Token);}catch(OperationCanceledException){canceled=true;}
            check(canceled && !File.Exists(canceledPath) && Directory.EnumerateFiles(unicodeFolder).Count()==1,"Pre-canceled Unicode path backup creates no destination or partial file");
            bool restoreCanceled=false;try{await new BackupService(As(target),tools).RestoreAsync(unicodeArchive,cancellation.Token);}catch(OperationCanceledException){restoreCanceled=true;}
            check(restoreCanceled && unicodeBefore.SequenceEqual(SHA256.HashData(File.ReadAllBytes(unicodeArchive))),"Pre-canceled Unicode restore preserves its source archive");
            string optionFile=Path.Combine(unicodeFolder,"--help");File.Copy(unicodeArchive,optionFile);bool conflict=false;
            try{await new BackupService(As(target),tools).RestoreAsync(optionFile);}catch(IOException){conflict=true;}
            check(conflict,"An option-like archive basename is treated as input, never a successful tool help command");
            string invalidFile=Path.Combine(unicodeFolder,"inválido_密碼.backup");await File.WriteAllTextAsync(invalidFile,"Not a PostgreSQL archive");bool invalid=false;
            try{await new BackupService(As(target),tools).RestoreAsync(invalidFile);}catch(IOException){invalid=true;}
            check(invalid && await File.ReadAllTextAsync(invalidFile)=="Not a PostgreSQL archive","Failed native restore preserves its original Unicode input file");
            bool unicodeOverwrite=false;try{await new BackupService(As(source),tools).BackupAsync(unicodeArchive);}catch(IOException){unicodeOverwrite=true;}
            check(unicodeOverwrite && unicodeBefore.SequenceEqual(SHA256.HashData(File.ReadAllBytes(unicodeArchive))),"Unicode archive publication refuses overwrite without altering existing bytes");
        }
        finally{foreach(string name in created.AsEnumerable().Reverse())await Sql($"DROP DATABASE {SqlIdentifier.Quote(name)}");if(roleCreated)await Sql($"DROP ROLE {SqlIdentifier.Quote(role)}");}
    }
}
