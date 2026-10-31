using Npgsql;
using PGMS.PostgreSql;

internal static class RestoreOwnershipChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),owner="restore_owner_"+suffix,reader="restore_reader_"+suffix,limited="restore_limited_"+suffix,source="restore_source_"+suffix;
        string qo=SqlIdentifier.Quote(owner),qr=SqlIdentifier.Quote(reader),ql=SqlIdentifier.Quote(limited);
        string tools=Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")??throw new Exception("Missing test tools.");
        string folder=Path.GetFullPath(Path.Combine("artifacts/restore-options",suffix));Directory.CreateDirectory(folder);string archive=Path.Combine(folder,"source.backup");
        var databases=new List<string>();
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        async Task Create(string name,string role){await Sql($"CREATE DATABASE {SqlIdentifier.Quote(name)} OWNER {SqlIdentifier.Quote(role)}");databases.Add(name);}
        async Task Drop(string name){await Sql($"DROP DATABASE {SqlIdentifier.Quote(name)}");databases.Remove(name);}
        string As(string database,string? role=null)=>new NpgsqlConnectionStringBuilder(connection){Database=database,Username=role??"pgms_test",Pooling=false}.ConnectionString;
        async Task<bool> Empty(string database){await using var db=new NpgsqlConnection(As(database));await db.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT to_regclass('public.items') IS NULL",db);return (bool)(await cmd.ExecuteScalarAsync())!;}
        check(new RestoreOptions().ToArguments().SequenceEqual(new[]{"--single-transaction","--exit-on-error","--no-owner","--no-privileges"}),"Default restore options preserve legacy transaction and omission flags");
        check(new RestoreOptions(true,true).ToArguments().SequenceEqual(new[]{"--single-transaction","--exit-on-error"}),"Ownership and ACL restoration never remove atomicity or exit-on-error");
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr} LOGIN;CREATE ROLE {ql} LOGIN");await Create(source,owner);
            await using(var db=new NpgsqlConnection(As(source,owner)))
            {
                await db.OpenAsync();await using var cmd=new NpgsqlCommand($"CREATE TABLE public.items(id serial PRIMARY KEY,value text);INSERT INTO public.items(value) VALUES('restored ação');GRANT SELECT ON public.items TO {qr}",db);await cmd.ExecuteNonQueryAsync();
            }
            await new BackupService(As(source,owner),tools).BackupAsync(archive);
            foreach(bool ownership in new[]{false,true})foreach(bool acl in new[]{false,true})
            {
                string target="restore_case_"+(ownership?"1":"0")+(acl?"1":"0")+"_"+suffix;await Create(target,"pgms_test");
                await new BackupService(As(target),tools).RestoreWithOptionsAsync(archive,new(ownership,acl));
                await using(var db=new NpgsqlConnection(As(target)))
                {
                    await db.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT pg_catalog.pg_get_userbyid(relowner) FROM pg_catalog.pg_class WHERE oid='public.items'::regclass",db);
                    check((string)(await cmd.ExecuteScalarAsync())! ==(ownership?owner:"pgms_test"),"Restored owner follows explicit choice: "+ownership+"/"+acl);
                    cmd.CommandText="SELECT value FROM public.items WHERE id=1";check((string)(await cmd.ExecuteScalarAsync())! =="restored ação","Ownership/ACL choices preserve table data: "+ownership+"/"+acl);
                    cmd.CommandText="INSERT INTO public.items(value) VALUES('next') RETURNING id";check((int)(await cmd.ExecuteScalarAsync())! ==2,"Ownership/ACL choices preserve serial state: "+ownership+"/"+acl);
                }
                await using(var client=new NpgsqlConnection(As(target,reader)))
                {
                    await client.OpenAsync();bool allowed=false;try{await using var cmd=new NpgsqlCommand("SELECT value FROM public.items WHERE id=1",client);allowed=(string)(await cmd.ExecuteScalarAsync())! =="restored ação";}catch(PostgresException ex)when(ex.SqlState=="42501"){}
                    check(allowed==acl,"Actual reader access follows ACL restore choice: "+ownership+"/"+acl);
                }
            }
            string denied="restore_denied_"+suffix;await Create(denied,limited);bool refused=false;
            try{await new BackupService(As(denied,limited),tools).RestoreWithOptionsAsync(archive,new(true,false));}catch(IOException){refused=true;}
            check(refused,"Ownership restoration respects a limited operator's role authority");
            check(await Empty(denied),"Ownership authority failure rolls back created table and data");
            foreach(string name in databases.ToArray())await Drop(name);
            await Sql($"DROP ROLE {qr}");
            string missing="restore_missing_"+suffix;await Create(missing,"pgms_test");bool absentRole=false;
            try{await new BackupService(As(missing),tools).RestoreWithOptionsAsync(archive,new(true,true));}catch(IOException){absentRole=true;}
            check(absentRole,"ACL restoration refuses a missing archive recipient without creating a role");
            check(await Empty(missing),"Missing ACL recipient rolls back objects restored earlier in the transaction");
        }
        finally
        {
            foreach(string name in databases.ToArray())await Drop(name);
            await Sql($"DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {qo};DROP ROLE IF EXISTS {ql}");
        }
    }
}
