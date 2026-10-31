using Npgsql;
using PGMS.PostgreSql;

internal static class PrivilegeCancellationChecks
{
    public static async Task RunGuardsAsync(Action<bool,string> check)
    {
        using var canceled=new CancellationTokenSource(); canceled.Cancel();
        foreach(var target in Enum.GetValues<PrivilegeTarget>())
        {
            bool stopped=false;
            try { await new PrivilegeService("not a connection string").ApplyAsync(new(target,"","","invalid"),canceled.Token); }
            catch(OperationCanceledException ex) { stopped=ex.CancellationToken==canceled.Token; }
            check(stopped,"Pre-canceled "+target+" Apply stops before SQL validation and connection parsing");
        }
    }

    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await RunGuardsAsync(check);
        string suffix=Guid.NewGuid().ToString("N"),schema="cancel_é\"_"+suffix,role="cancel_role_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        string database="cancel_db_"+suffix,qdb=SqlIdentifier.Quote(database);
        string app="PGMS.Cancel."+suffix;
        var applyConnection=new NpgsqlConnectionStringBuilder(connection){ApplicationName=app}.ConnectionString;
        var service=new PrivilegeService(applyConnection);
        await using var admin=new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) { await using var c=new NpgsqlCommand(sql,admin); await c.ExecuteNonQueryAsync(); }
        async Task<bool> Has(bool option=false)
        {
            await using var c=new NpgsqlCommand("SELECT pg_catalog.has_table_privilege(@role,@table,@privilege)",admin);
            c.Parameters.AddWithValue("role",role); c.Parameters.AddWithValue("table",table);
            c.Parameters.AddWithValue("privilege",option?"SELECT WITH GRANT OPTION":"SELECT");
            return (bool)(await c.ExecuteScalarAsync())!;
        }
        async Task Blocked(PrivilegeRequest request,bool expected,bool expectedOption)
        {
            await using var locker=new NpgsqlConnection(connection); await locker.OpenAsync();
            await using var tx=await locker.BeginTransactionAsync();
            await using(var c=new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_class WHERE oid=@table::regclass FOR UPDATE",locker,tx)) { c.Parameters.AddWithValue("table",table); await c.ExecuteScalarAsync(); }
            using var cancel=new CancellationTokenSource();
            var pending=service.ApplyAsync(request,cancel.Token);
            try
            {
                var until=DateTime.UtcNow.AddSeconds(8); bool waiting=false;
                while(!pending.IsCompleted && DateTime.UtcNow<until)
                {
                    await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND @pid=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                    c.Parameters.AddWithValue("app",app); c.Parameters.AddWithValue("pid",locker.ProcessID);
                    if((bool)(await c.ExecuteScalarAsync())!) {waiting=true;break;} await Task.Delay(40);
                }
                check(waiting,"Permission command observed waiting on exact fixture lock: "+request.ToSql());
                cancel.Cancel(); bool canceled=false;
                try { await pending.WaitAsync(TimeSpan.FromSeconds(8)); } catch(OperationCanceledException){canceled=true;}
                check(canceled,"Blocked permission command acknowledges cancellation");
                check(await Has()==expected && await Has(true)==expectedOption,"Canceled permission command preserves base and grant-option ACL");
                await using var count=new NpgsqlCommand("SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE application_name=@app",admin); count.Parameters.AddWithValue("app",app);
                check((long)(await count.ExecuteScalarAsync())! ==0,"Canceled permission transaction and backend are disposed before return");
            }
            finally { cancel.Cancel(); await tx.RollbackAsync(); try { await pending; } catch(OperationCanceledException){} }
        }
        try
        {
            await Sql($"CREATE SCHEMA {qs};CREATE ROLE {qr};CREATE TABLE {table}(id integer);INSERT INTO {table} VALUES(42);");
            await Sql($"CREATE DATABASE {qdb};CREATE SEQUENCE {qs}.seq START 81;");
            var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",role,"SELECT",Schema:schema);
            await Blocked(request,false,false);
            await service.ApplyAsync(request);
            check(await Has() && !await Has(true),"Retry after canceled GRANT commits base privilege without delegation");
            await Blocked(request with {Revoke=true},true,false);
            await service.ApplyAsync(request with {Revoke=true});
            check(!await Has(),"Retry after canceled REVOKE removes the direct grant");
            await Blocked(request with {GrantOption=true},false,false);
            await service.ApplyAsync(request with {GrantOption=true});
            check(await Has() && await Has(true),"Retry of delegated GRANT commits base and grant option");
            await Blocked(request with {GrantOption=true,Revoke=true},true,true);
            await service.ApplyAsync(request with {GrantOption=true,Revoke=true});
            check(await Has() && !await Has(true),"Retry of option-only REVOKE preserves base privilege");
            var inspector=new EffectivePrivilegeService(connection);
            foreach(var other in new[]{
                new PrivilegeRequest(PrivilegeTarget.Database,database,role,"CREATE"),
                new PrivilegeRequest(PrivilegeTarget.Schema,schema,role,"USAGE"),
                new PrivilegeRequest(PrivilegeTarget.Sequence,"seq",role,"USAGE",Schema:schema)})
            {
                var before=await inspector.InspectAsync(other.Target,other.Name,other.Schema);
                string catalog=other.Target switch {PrivilegeTarget.Database=>"pg_database",PrivilegeTarget.Schema=>"pg_namespace",_=>"pg_class"};
                await using var locker=new NpgsqlConnection(connection);await locker.OpenAsync();
                await using var tx=await locker.BeginTransactionAsync();
                await using(var c=new NpgsqlCommand($"SELECT oid FROM pg_catalog.{catalog} WHERE oid=@oid FOR UPDATE",locker,tx))
                {c.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,before.Oid);await c.ExecuteScalarAsync();}
                using var cancel=new CancellationTokenSource();var pending=service.ApplyAsync(other,cancel.Token);
                try
                {
                    bool waiting=false;var until=DateTime.UtcNow.AddSeconds(8);
                    while(!pending.IsCompleted && DateTime.UtcNow<until)
                    {
                        await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND @pid=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                        c.Parameters.AddWithValue("app",app);c.Parameters.AddWithValue("pid",locker.ProcessID);
                        if((bool)(await c.ExecuteScalarAsync())!){waiting=true;break;}await Task.Delay(40);
                    }
                    check(waiting,"Exact catalog row blocks "+other.Target+" grant");
                    cancel.Cancel();bool canceled=false;try{await pending.WaitAsync(TimeSpan.FromSeconds(8));}catch(OperationCanceledException){canceled=true;}
                    check(canceled,"Blocked "+other.Target+" grant is canceled");
                    var after=await inspector.InspectAsync(other.Target,other.Name,other.Schema);
                    check(before.Oid==after.Oid && before.AclEntries.SequenceEqual(after.AclEntries),"Canceled "+other.Target+" grant preserves inspected identity and complete fixture ACL");
                }
                finally {cancel.Cancel();await tx.RollbackAsync();try{await pending;}catch(OperationCanceledException){}}
                await service.ApplyAsync(other);
                check((await inspector.InspectAsync(other.Target,other.Name,other.Schema)).AclEntries.Any(e=>e.Grantee==role && e.Privilege==other.Privilege),"Explicit retry commits "+other.Target+" privilege after canceled wait");
            }
            await using(var state=new NpgsqlCommand($"SELECT last_value=81 AND NOT is_called FROM {qs}.seq",admin))
                check((bool)(await state.ExecuteScalarAsync())!,"Canceled and retried sequence grants preserve sequence state");
            await using(var data=new NpgsqlCommand($"SELECT id FROM {table}",admin))
                check((int)(await data.ExecuteScalarAsync())! ==42,"All permission cancellations preserve table data");
        }
        finally { await Sql($"DROP DATABASE IF EXISTS {qdb};DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr};"); }
    }
}
