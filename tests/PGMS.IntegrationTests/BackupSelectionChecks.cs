using Npgsql;
using PGMS.PostgreSql;

internal static class BackupSelectionChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N")[..12],owner="filter_owner_"+suffix,source="filter_source_"+suffix;
        string schema="selected_密碼.*\"\\",other="selected_密碼.ab\"\\";
        string tools=Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")!;
        string folder=Path.GetFullPath(Path.Combine("artifacts/backup-selection",suffix));Directory.CreateDirectory(folder);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();var databases=new List<string>();bool roleCreated=false;
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        async Task Create(string db){await Sql($"CREATE DATABASE {SqlIdentifier.Quote(db)} OWNER {SqlIdentifier.Quote(owner)}");databases.Add(db);}
        string As(string db)=>new NpgsqlConnectionStringBuilder(connection){Database=db,Username=owner,Pooling=false}.ConnectionString;
        async Task<object?> Scalar(string db,string sql){await using var c=new NpgsqlConnection(As(db));await c.OpenAsync();await using var cmd=new NpgsqlCommand(sql,c);return await cmd.ExecuteScalarAsync();}
        try
        {
            await Sql($"CREATE ROLE {SqlIdentifier.Quote(owner)} LOGIN");roleCreated=true;await Create(source);
            await Scalar(source,$"CREATE SCHEMA {SqlIdentifier.Quote(schema)};CREATE SCHEMA {SqlIdentifier.Quote(other)};CREATE TABLE {SqlIdentifier.Quote(schema)}.items(value text);INSERT INTO {SqlIdentifier.Quote(schema)}.items VALUES('seleção 密碼');CREATE TABLE {SqlIdentifier.Quote(other)}.items(value text);CREATE TABLE public.excluded(id int)");
            uint largeObject=Convert.ToUInt32(await Scalar(source,"SELECT lo_from_bytea(0,decode('00017fff80','hex'))"));
            string[] names=[schema];var selection=new BackupSelection(names);names[0]="public";
            check(selection.Schemas.Single()==schema,"Backup selection snapshots literal schema names instead of retaining a mutable array");
            foreach(var format in Enum.GetValues<BackupFormat>())
            {
                string destination=Path.Combine(folder,"selected-"+format),target="filter_"+(int)format+"_"+suffix;
                await new BackupService(As(source),tools).BackupWithOptionsAsync(destination,new(format){Selection=selection});await Create(target);
                var restore=new BackupService(As(target),tools);
                if(format==BackupFormat.PlainSql)await restore.RestoreSqlAsync(destination);else if(format==BackupFormat.Directory)await restore.RestoreDirectoryAsync(destination);else await restore.RestoreAsync(destination);
                check((string)(await Scalar(target,$"SELECT value FROM {SqlIdentifier.Quote(schema)}.items"))! =="seleção 密碼","Literal Unicode schema filter roundtrips selected data: "+format);
                check((bool)(await Scalar(target,"SELECT NOT EXISTS(SELECT FROM pg_namespace WHERE nspname='"+other.Replace("'","''")+"') AND to_regclass('public.excluded') IS NULL"))!,"Schema filter does not expand literal wildcard or include unrelated public data: "+format);
                check(Convert.ToInt64(await Scalar(target,"SELECT count(*) FROM pg_largeobject_metadata"))==0,"Schema filter omits large objects by default: "+format);
                string included=Path.Combine(folder,"with-large-objects-"+format),includedTarget="filter_lo_"+(int)format+"_"+suffix;
                await new BackupService(As(source),tools).BackupWithOptionsAsync(included,new(format){Selection=selection,LargeObjects=BackupLargeObjects.Include});await Create(includedTarget);
                var loRestore=new BackupService(As(includedTarget),tools);if(format==BackupFormat.PlainSql)await loRestore.RestoreSqlAsync(included);else if(format==BackupFormat.Directory)await loRestore.RestoreDirectoryAsync(included);else await loRestore.RestoreAsync(included);
                check((string)(await Scalar(includedTarget,$"SELECT encode(lo_get({largeObject}::oid),'hex')"))! =="00017fff80","Explicit large-object inclusion restores exact binary payload: "+format);
            }
            string excluded=Path.Combine(folder,"without-large-objects.backup"),excludedTarget="filter_no_lo_"+suffix;
            await new BackupService(As(source),tools).BackupWithOptionsAsync(excluded,new(){LargeObjects=BackupLargeObjects.Exclude});await Create(excludedTarget);await new BackupService(As(excludedTarget),tools).RestoreAsync(excluded);
            check(Convert.ToInt64(await Scalar(excludedTarget,"SELECT count(*) FROM pg_largeobject_metadata"))==0 && (string)(await Scalar(excludedTarget,$"SELECT value FROM {SqlIdentifier.Quote(schema)}.items"))! =="seleção 密碼","Explicit exclusion omits large objects while preserving ordinary data in a full backup");
            foreach(var invalidLo in new[]{new BackupOptions(Scope:BackupDataScope.SchemaOnly){LargeObjects=BackupLargeObjects.Include},new(){LargeObjects=(BackupLargeObjects)999}})
            {bool refusedLo=false;try{invalidLo.ToArguments();}catch(ArgumentException){refusedLo=true;}check(refusedLo,"Invalid large-object choices are refused before execution");}
            string selectedTable="items.*密碼\"\\",siblingTable="items.ab密碼\"\\";
            await Scalar(source,$"CREATE TABLE {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(selectedTable)}(value text);INSERT INTO {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(selectedTable)} VALUES('tabela 密碼');CREATE TABLE {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(siblingTable)}(value text)");
            var tableSelection=BackupSelection.ForTables([new(schema,selectedTable)]);
            foreach(var format in Enum.GetValues<BackupFormat>())
            {
                string tableArchive=Path.Combine(folder,"table-"+format),tableTarget="filter_table_"+(int)format+"_"+suffix;
                await new BackupService(As(source),tools).BackupWithOptionsAsync(tableArchive,new(format){Selection=tableSelection});await Create(tableTarget);
                await Scalar(tableTarget,$"CREATE SCHEMA {SqlIdentifier.Quote(schema)}");
                var tableRestore=new BackupService(As(tableTarget),tools);if(format==BackupFormat.PlainSql)await tableRestore.RestoreSqlAsync(tableArchive);else if(format==BackupFormat.Directory)await tableRestore.RestoreDirectoryAsync(tableArchive);else await tableRestore.RestoreAsync(tableArchive);
                check((string)(await Scalar(tableTarget,$"SELECT value FROM {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(selectedTable)}"))! =="tabela 密碼","Exact qualified table filter restores Unicode/metacharacter name: "+format);
                check(Convert.ToInt64(await Scalar(tableTarget,"SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='"+schema.Replace("'","''")+"' AND c.relkind='r'"))==1,"Table filter excludes wildcard-like sibling and other tables: "+format);
            }
            await Scalar(source,"CREATE TABLE public.scope_items(id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,value text);INSERT INTO public.scope_items(value) VALUES('scope 密碼')");
            foreach(var format in new[]{BackupFormat.Custom,BackupFormat.Tar,BackupFormat.Directory})
            {
                string scopeArchive=Path.Combine(folder,"scope-"+format),scopeTarget="scope_"+(int)format+"_"+suffix;
                await new BackupService(As(source),tools).BackupWithOptionsAsync(scopeArchive,new(format){Selection=BackupSelection.ForTables([new("public","scope_items")])});await Create(scopeTarget);
                var scopeRestore=new BackupService(As(scopeTarget),tools);
                async Task RestoreScope(BackupDataScope scope){var options=new RestoreOptions{Scope=scope};if(format==BackupFormat.Directory)await scopeRestore.RestoreDirectoryAsync(scopeArchive,options);else await scopeRestore.RestoreWithOptionsAsync(scopeArchive,options);}
                await RestoreScope(BackupDataScope.SchemaOnly);
                check(Convert.ToInt64(await Scalar(scopeTarget,"SELECT count(*) FROM public.scope_items"))==0,"Restore schema-only creates definitions without rows: "+format);
                await RestoreScope(BackupDataScope.DataOnly);
                check((string)(await Scalar(scopeTarget,"SELECT value FROM public.scope_items WHERE id=1"))! =="scope 密碼","Restore data-only loads exact rows into existing definitions: "+format);
                check(Convert.ToInt32(await Scalar(scopeTarget,"INSERT INTO public.scope_items(value) VALUES('after') RETURNING id"))==2,"Data-only restore restores identity sequence state: "+format);
                bool duplicateRefused=false;try{await RestoreScope(BackupDataScope.DataOnly);}catch(IOException){duplicateRefused=true;}
                check(duplicateRefused && Convert.ToInt64(await Scalar(scopeTarget,"SELECT count(*) FROM public.scope_items"))==2,"Conflicting data-only retry preserves existing rows: "+format);
            }
            bool invalidScope=false;try{new RestoreOptions{Scope=(BackupDataScope)999}.ToArguments();}catch(ArgumentException){invalidScope=true;}check(invalidScope,"Unknown restore scope is refused before execution");
            string missingTableArchive=Path.Combine(folder,"missing-table.backup");bool missingTable=false;
            try{await new BackupService(As(source),tools).BackupWithOptionsAsync(missingTableArchive,new(){Selection=BackupSelection.ForTables([new(schema,"missing_"+suffix)])});}catch(IOException){missingTable=true;}
            check(missingTable && !File.Exists(missingTableArchive),"Strict table filter refuses missing table without full-backup fallback");
            string absent=Path.Combine(folder,"absent.backup");bool refused=false;
            try{await new BackupService(As(source),tools).BackupWithOptionsAsync(absent,new(){Selection=new BackupSelection(["missing_"+suffix])});}catch(IOException){refused=true;}
            check(refused && !File.Exists(absent),"Strict schema selection refuses a missing schema without falling back to full database");
            foreach(var invalid in new[]{Array.Empty<string>(),new[]{"x\nexclude schema public"},new[]{"x\rfoo"},new[]{"x\0foo"},Enumerable.Repeat("public",257).ToArray()})
            {bool invalidRefused=false;try{_=new BackupSelection(invalid);}catch(ArgumentException){invalidRefused=true;}check(invalidRefused,"Invalid schema filter is refused before file or process creation");}
            check(!Directory.EnumerateFileSystemEntries(folder,"PGMS-*.partial").Any(),"Filtered backup operations leave no output partial files");
        }
        finally{foreach(string db in databases.AsEnumerable().Reverse())await Sql($"DROP DATABASE {SqlIdentifier.Quote(db)}");if(roleCreated)await Sql($"DROP ROLE {SqlIdentifier.Quote(owner)}");}
    }
}
