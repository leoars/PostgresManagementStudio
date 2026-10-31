using Npgsql;
using PGMS.PostgreSql;

internal static class BackupFormatChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N")[..12],owner="format_owner_"+suffix,reader="format_reader_"+suffix,source="format_source_"+suffix;
        string tools=Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")??throw new Exception("Missing isolated tools.");
        string folder=Path.GetFullPath(Path.Combine("artifacts/backup-formats","formatos_密碼_"+suffix));Directory.CreateDirectory(folder);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();var databases=new List<string>();bool rolesCreated=false;
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        async Task Create(string name,string role){await Sql($"CREATE DATABASE {SqlIdentifier.Quote(name)} OWNER {SqlIdentifier.Quote(role)}");databases.Add(name);}
        string As(string db,string role="pgms_test")=>new NpgsqlConnectionStringBuilder(connection){Database=db,Username=role,Pooling=false}.ConnectionString;
        async Task<object?> Scalar(string db,string sql){await using var client=new NpgsqlConnection(As(db));await client.OpenAsync();await using var c=new NpgsqlCommand(sql,client);return await c.ExecuteScalarAsync();}
        try
        {
            await Sql($"CREATE ROLE {SqlIdentifier.Quote(owner)} LOGIN;CREATE ROLE {SqlIdentifier.Quote(reader)} LOGIN");rolesCreated=true;await Create(source,owner);
            await using(var client=new NpgsqlConnection(As(source,owner)))
            {await client.OpenAsync();await using var c=new NpgsqlCommand($"CREATE TABLE public.items(id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,value text);INSERT INTO public.items(value) VALUES('ação 密碼 🔑');GRANT SELECT ON public.items TO {SqlIdentifier.Quote(reader)}",client);await c.ExecuteNonQueryAsync();}
            foreach(var format in Enum.GetValues<BackupFormat>())foreach(var scope in Enum.GetValues<BackupDataScope>())
            {
                string label=format+"/"+scope,target="format_"+format.ToString().ToLowerInvariant()+"_"+(int)scope+"_"+suffix,archive=Path.Combine(folder,"cópia_密碼_"+(int)format+"_"+(int)scope+(format==BackupFormat.PlainSql?".sql":".backup"));
                await new BackupService(As(source,owner),tools).BackupWithOptionsAsync(archive,new(format,scope){Jobs=format==BackupFormat.Directory?2:1});check(format==BackupFormat.Directory?File.Exists(Path.Combine(archive,"toc.dat")):File.Exists(archive) && new FileInfo(archive).Length>0,"Selected backup format/scope publishes an actual file: "+label);
                await Create(target,"pgms_test");if(scope==BackupDataScope.DataOnly)await Scalar(target,"CREATE TABLE public.items(id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,value text)");
                var service=new BackupService(As(target),tools);if(format==BackupFormat.PlainSql)await service.RestoreSqlAsync(archive);else if(format==BackupFormat.Directory)await service.RestoreDirectoryAsync(archive);else await service.RestoreAsync(archive);
                check(Convert.ToInt64(await Scalar(target,"SELECT count(*) FROM public.items"))==(scope==BackupDataScope.SchemaOnly?0:1),"Roundtrip restores the requested schema/data scope: "+label);
                if(scope!=BackupDataScope.SchemaOnly)check((string)(await Scalar(target,"SELECT value FROM public.items WHERE id=1"))! =="ação 密碼 🔑","Selected format preserves exact Unicode row data: "+label);
                check(Convert.ToInt32(await Scalar(target,"INSERT INTO public.items(value) VALUES('next') RETURNING id"))==(scope==BackupDataScope.SchemaOnly?1:2),"Selected format/scope preserves the expected identity state: "+label);
            }
            string existing=Path.Combine(folder,"existing-directory");Directory.CreateDirectory(existing);
            string sentinel=Path.Combine(existing,"keep.txt");await File.WriteAllTextAsync(sentinel,"preserve");
            bool existingRefused=false;
            try{await new BackupService(As(source,owner),tools).BackupWithOptionsAsync(existing,new(BackupFormat.Directory){Jobs=2});}catch(IOException){existingRefused=true;}
            check(existingRefused && await File.ReadAllTextAsync(sentinel)=="preserve","Directory backup refuses an existing directory and preserves its contents");
            string broken=Path.Combine(folder,"failed-directory");bool brokenRefused=false;
            try{await new BackupService(As("missing_"+suffix,owner),tools).BackupWithOptionsAsync(broken,new(BackupFormat.Directory){Jobs=2});}catch(IOException){brokenRefused=true;}
            check(brokenRefused && !Directory.Exists(broken) && !Directory.EnumerateFileSystemEntries(folder,"PGMS-*.partial").Any(),"Failed directory backup publishes nothing and cleans its own flat partial directory");
            bool missingToc=false;try{await new BackupService(As(source,owner),tools).RestoreDirectoryAsync(existing);}catch(DirectoryNotFoundException){missingToc=true;}
            check(missingToc,"Directory restore refuses a directory without a table of contents");
            await using(var client=new NpgsqlConnection(As(source,owner))){await client.OpenAsync();await using var c=new NpgsqlCommand("CREATE TABLE public.compressible AS SELECT n,repeat('compressão 密碼 ',1000) AS value FROM generate_series(1,100) n",client);await c.ExecuteNonQueryAsync();}
            foreach(var compressionFormat in new[]{BackupFormat.Custom,BackupFormat.Directory})
            {
                long uncompressedLength=0;
                foreach(var compression in new[]{BackupCompression.None,BackupCompression.Gzip})
                {
                    string compressedPath=Path.Combine(folder,"compression_"+compressionFormat+"_"+compression),compressedTarget="compress_"+(int)compressionFormat+"_"+(int)compression+"_"+suffix;
                    await new BackupService(As(source,owner),tools).BackupWithOptionsAsync(compressedPath,new(compressionFormat){Compression=compression,CompressionLevel=compression==BackupCompression.Gzip?9:null});
                    long length=compressionFormat==BackupFormat.Directory?Directory.EnumerateFiles(compressedPath).Sum(f=>new FileInfo(f).Length):new FileInfo(compressedPath).Length;
                    if(compression==BackupCompression.None)uncompressedLength=length;else check(length<uncompressedLength/2,"Gzip actually reduces repetitive archive data size: "+compressionFormat);
                    await Create(compressedTarget,"pgms_test");var compressedService=new BackupService(As(compressedTarget),tools);
                    if(compressionFormat==BackupFormat.Directory)await compressedService.RestoreDirectoryAsync(compressedPath);else await compressedService.RestoreAsync(compressedPath);
                    check(Convert.ToInt64(await Scalar(compressedTarget,"SELECT count(*) FROM public.compressible WHERE value=repeat('compressão 密碼 ',1000)"))==100,"Compression choice roundtrips all exact Unicode payloads: "+compressionFormat+"/"+compression);
                    if(compressionFormat==BackupFormat.Directory)check(Directory.EnumerateFiles(compressedPath,"*.gz").Any()==(compression==BackupCompression.Gzip),"Directory data suffix reflects selected compression: "+compression);
                }
            }
            foreach(var invalidCompression in new[]{new BackupOptions(BackupFormat.Tar){Compression=BackupCompression.Gzip},new(BackupFormat.PlainSql){Compression=BackupCompression.Gzip},new(){Compression=(BackupCompression)999},new(){CompressionLevel=1},new(){Compression=BackupCompression.None,CompressionLevel=1},new(){Compression=BackupCompression.Gzip,CompressionLevel=0},new(){Compression=BackupCompression.Gzip,CompressionLevel=10}})
            {bool refused=false;try{invalidCompression.ToArguments();}catch(ArgumentException){refused=true;}check(refused,"Invalid or unsupported compression is refused before exporting");}
            string owned=Path.Combine(folder,"ownership_密碼.sql"),ownedTarget="format_owned_"+suffix;
            await new BackupService(As(source,owner),tools).BackupWithOptionsAsync(owned,new(BackupFormat.PlainSql,SqlOwnership:true,SqlPrivileges:true));await Create(ownedTarget,"pgms_test");await new BackupService(As(ownedTarget),tools).RestoreSqlAsync(owned);
            check((string)(await Scalar(ownedTarget,"SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid='public.items'::regclass"))! ==owner,"Plain SQL can explicitly retain archive ownership");
            await using(var client=new NpgsqlConnection(As(ownedTarget,reader))){await client.OpenAsync();await using var c=new NpgsqlCommand("SELECT value FROM public.items WHERE id=1",client);check((string)(await c.ExecuteScalarAsync())! =="ação 密碼 🔑","Plain SQL can explicitly retain object privileges for a real reader");}
            string failure=Path.Combine(folder,"falha_密碼.sql"),failureTarget="format_failure_"+suffix;await Create(failureTarget,"pgms_test");
            await File.WriteAllTextAsync(failure,"CREATE TABLE public.rollback_probe(id int); INSERT INTO public.rollback_probe VALUES(1); SELECT 1/0;");bool failed=false;
            try{await new BackupService(As(failureTarget),tools).RestoreSqlAsync(failure);}catch(IOException){failed=true;}
            check(failed && (bool)(await Scalar(failureTarget,"SELECT to_regclass('public.rollback_probe') IS NULL"))!,"psql ON_ERROR_STOP and single transaction roll back earlier ordinary script statements");
            string explicitCommit=Path.Combine(folder,"commit_密碼.sql");await File.WriteAllTextAsync(explicitCommit,"CREATE TABLE public.explicit_commit(id int); COMMIT; SELECT 1/0;");bool partial=false;
            try{await new BackupService(As(failureTarget),tools).RestoreSqlAsync(explicitCommit);}catch(IOException){partial=true;}
            check(partial && (bool)(await Scalar(failureTarget,"SELECT to_regclass('public.explicit_commit') IS NOT NULL"))!,"Explicit COMMIT in an arbitrary SQL script controls its transaction boundary; no blanket atomicity claim");
            foreach(var invalid in new[]{new BackupOptions((BackupFormat)999),new(BackupFormat.Custom,(BackupDataScope)999),new(BackupFormat.Tar,SqlOwnership:true),new(BackupFormat.Custom){Jobs=2},new(BackupFormat.Directory){Jobs=0},new(BackupFormat.Directory){Jobs=33}})
            {bool refused=false;try{invalid.ToArguments();}catch(ArgumentException){refused=true;}check(refused,"Unsupported or ignored backup options are refused before starting a tool");}
        }
        finally{foreach(string db in databases.AsEnumerable().Reverse())await Sql($"DROP DATABASE {SqlIdentifier.Quote(db)}");if(rolesCreated)await Sql($"DROP ROLE {SqlIdentifier.Quote(reader)};DROP ROLE {SqlIdentifier.Quote(owner)}");}
        await BackupSelectionChecks.RunAsync(connection,check);
    }
}
