using Npgsql;
using PGMS.PostgreSql;

internal static class PrivilegeIdentityChecks
{
    internal static async Task RunAsync(string connection, Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="identity_é\"_"+suffix,role="identity_role_"+suffix,grantor="identity_grantor_"+suffix,database="identity_db_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),qg=SqlIdentifier.Quote(grantor),qd=SqlIdentifier.Quote(database);
        string table=ObjectSql.Relation(schema,"target"),sequence=ObjectSql.Relation(schema,"counter");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        async Task<bool> Has(PrivilegeRequest r)
        {
            string sql=r.Target switch
            {
                PrivilegeTarget.Database=>"SELECT pg_catalog.has_database_privilege(@role,@name,@privilege)",
                PrivilegeTarget.Schema=>"SELECT pg_catalog.has_schema_privilege(@role,@name,@privilege)",
                PrivilegeTarget.Sequence=>"SELECT pg_catalog.has_sequence_privilege(@role,@name,@privilege)",
                _=>r.Column is null?"SELECT pg_catalog.has_table_privilege(@role,@name,@privilege)":"SELECT pg_catalog.has_column_privilege(@role,@name,@column,@privilege)"
            };
            await using var c=new NpgsqlCommand(sql,admin);c.Parameters.AddWithValue("role",role);c.Parameters.AddWithValue("name",r.Target is PrivilegeTarget.Table or PrivilegeTarget.Sequence?ObjectSql.Relation(r.Schema,r.Name):r.Name);c.Parameters.AddWithValue("privilege",r.Privilege);c.Parameters.AddWithValue("column",r.Column??"");return (bool)(await c.ExecuteScalarAsync())!;
        }
        var service=new PrivilegeService(connection);
        var requests=new[]{new PrivilegeRequest(PrivilegeTarget.Database,database,role,"CONNECT"),new(PrivilegeTarget.Schema,schema,role,"USAGE"),new(PrivilegeTarget.Table,"target",role,"SELECT",Schema:schema),new(PrivilegeTarget.Sequence,"counter",role,"USAGE",Schema:schema),new(PrivilegeTarget.Table,"target",role,"UPDATE",Schema:schema,Column:"value")};
        async Task<PrivilegeTargetIdentity> Inspect(PrivilegeRequest r)=>await service.InspectTargetAsync(r.Target,r.Name,r.Schema,r.Column);
        async Task Refuse(PrivilegeRequest r,PrivilegeTargetIdentity expected,string message)
        {
            bool refused=false;try{await service.ApplyInspectedAsync(r,expected);}catch(InvalidOperationException){refused=true;}
            check(refused,message);check(!await Has(r),"Rejected identity leaves replacement ACL unchanged: "+message);
        }
        try
        {
            await Sql($"CREATE ROLE {qr}; CREATE ROLE {qg} LOGIN");
            await Sql($"CREATE DATABASE {qd}");
            await Sql($"REVOKE CONNECT ON DATABASE {qd} FROM PUBLIC; CREATE SCHEMA {qs}; CREATE TABLE {table}(id int,value text); INSERT INTO {table} VALUES(17,'preserved'); CREATE SEQUENCE {sequence};");
            foreach(var r in requests)
            {
                var identity=await Inspect(r);
                check(identity.DatabaseOid>0 && identity.ObjectOid>0 && (r.Column is null || identity.ColumnNumber==2),"Inspection captures exact identity: "+r.Target+"/"+r.Column);
                check(await service.ApplyInspectedAsync(r,identity) && await Has(r),"Bound GRANT commits expected object: "+r.Target+"/"+r.Column);
                check(await service.ApplyInspectedAsync(r with{Revoke=true},identity) && !await Has(r),"Bound REVOKE commits expected object: "+r.Target+"/"+r.Column);
            }
            var tableRequest=requests[2];var stale=await Inspect(tableRequest);
            await Sql($"ALTER TABLE {table} RENAME TO original; CREATE TABLE {table}(id int,value text)");
            await Refuse(tableRequest,stale,"Recreated table rejected after command");
            await Sql($"DROP TABLE {table}; ALTER TABLE {ObjectSql.Relation(schema,"original")} RENAME TO target");
            var seqRequest=requests[3];stale=await Inspect(seqRequest);
            await Sql($"ALTER SEQUENCE {sequence} RENAME TO old_counter; CREATE SEQUENCE {sequence}");
            await Refuse(seqRequest,stale,"Recreated sequence rejected after command");
            await Sql($"DROP SEQUENCE {sequence}; ALTER SEQUENCE {ObjectSql.Relation(schema,"old_counter")} RENAME TO counter");
            var colRequest=requests[4];stale=await Inspect(colRequest);
            await Sql($"ALTER TABLE {table} DROP COLUMN value; ALTER TABLE {table} ADD COLUMN value text");
            await Refuse(colRequest,stale,"Recreated column with new attnum rejected");
            var schemaRequest=requests[1];var schemaIdentity=await Inspect(schemaRequest);
            await Sql($"ALTER SCHEMA {qs} RENAME TO {SqlIdentifier.Quote(schema+"_old")}; CREATE SCHEMA {qs}");
            await Refuse(schemaRequest,schemaIdentity,"Recreated schema rejected after command");
            await Sql($"DROP SCHEMA {qs}; ALTER SCHEMA {SqlIdentifier.Quote(schema+"_old")} RENAME TO {qs}");

            string ns="identity_ns_"+suffix,side="identity_side_"+suffix;
            await Sql($"CREATE SCHEMA {SqlIdentifier.Quote(ns)}; CREATE SCHEMA {SqlIdentifier.Quote(side)}; CREATE TABLE {ObjectSql.Relation(ns,"stable")}(id int)");
            try
            {
                var nsRequest=tableRequest with{Schema=ns,Name="stable"};var before=await Inspect(nsRequest);
                await Sql($"ALTER TABLE {ObjectSql.Relation(ns,"stable")} SET SCHEMA {SqlIdentifier.Quote(side)}; DROP SCHEMA {SqlIdentifier.Quote(ns)}; CREATE SCHEMA {SqlIdentifier.Quote(ns)}; ALTER TABLE {ObjectSql.Relation(side,"stable")} SET SCHEMA {SqlIdentifier.Quote(ns)}");
                var after=await Inspect(nsRequest);
                check(after.ObjectOid==before.ObjectOid && after.NamespaceOid!=before.NamespaceOid,"Fixture retains table OID while replacing its namespace identity");
                await Refuse(nsRequest,before,"Same table OID in recreated namespace rejected");
            }
            finally{await Sql($"DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(ns)} CASCADE; DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(side)} CASCADE");}

            using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel();var identity=await Inspect(tableRequest);
                foreach(var target in Enum.GetValues<PrivilegeTarget>())
                {
                    bool stopped=false;try{await new PrivilegeService("invalid connection").ApplyInspectedAsync(tableRequest with{Target=target},identity,canceled.Token);}catch(OperationCanceledException ex){stopped=ex.CancellationToken==canceled.Token;}
                    check(stopped,"Pre-canceled inspected Apply stops before connection or identity validation: "+target);
                }
            }
            await Sql($"CREATE TABLE {ObjectSql.Relation(schema,"wide")}({string.Join(",",Enumerable.Range(1,260).Select(i=>$"c{i} int"))})");
            var wide=await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,"wide",schema,column:"c260");
            check(wide.ColumnsTruncated && wide.TargetIdentity?.ColumnNumber==260,"Explicit column identity is captured beyond the displayed256-column ACL limit");
            var wideRequest=colRequest with{Name="wide",Column="c260"};
            check(await service.ApplyInspectedAsync(wideRequest,wide.TargetIdentity!) && await Has(wideRequest),"Bound column Apply beyond displayed ACL limit commits only inspected column");
            check(await service.ApplyInspectedAsync(wideRequest with{Revoke=true},wide.TargetIdentity!) && !await Has(wideRequest),"Bound column revoke beyond displayed ACL limit preserves exact identity");

            // Attempt an ABA while GRANT waits on replacement B. PG17 may prevent
            // the swap with its own tuple-lock deadlock detection before our guard.
            stale=await Inspect(tableRequest);
            await Sql($"ALTER TABLE {table} RENAME TO aba_original; CREATE TABLE {table}(id int,value text)");
            uint replacementOid=(await Inspect(tableRequest)).ObjectOid;
            bool swapCommitted=false;
            await using(var locker=new NpgsqlConnection(connection))
            {
                await locker.OpenAsync();await using var held=await locker.BeginTransactionAsync();
                await using(var c=new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_class WHERE oid=@table::regclass FOR UPDATE",locker,held)){c.Parameters.AddWithValue("table",table);await c.ExecuteScalarAsync();}
                string app="PGMS.Identity.ABA."+suffix;
                var raced=new PrivilegeService(new NpgsqlConnectionStringBuilder(connection){ApplicationName=app}.ConnectionString);
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var pending=raced.ApplyInspectedAsync(tableRequest,stale,deadline.Token);
                try
                {
                    bool waiting=false;var until=DateTime.UtcNow.AddSeconds(8);
                    while(!pending.IsCompleted && DateTime.UtcNow<until)
                    {
                        await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND @pid=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                        c.Parameters.AddWithValue("app",app);c.Parameters.AddWithValue("pid",locker.ProcessID);
                        if((bool)(await c.ExecuteScalarAsync())!){waiting=true;break;}await Task.Delay(40);
                    }
                    check(waiting,"ABA GRANT observed waiting on exact replacement catalog row");
                    try
                    {
                        await using(var c=new NpgsqlCommand($"ALTER TABLE {table} RENAME TO aba_replacement; ALTER TABLE {ObjectSql.Relation(schema,"aba_original")} RENAME TO target",locker,held))await c.ExecuteNonQueryAsync();
                        await held.CommitAsync();swapCommitted=true;
                    }
                    catch(PostgresException ex)when(ex.SqlState=="40P01"){await held.RollbackAsync();}
                    bool refused=false;string outcome="";
                    try{await pending;}catch(InvalidOperationException){refused=true;outcome="identity guard";}catch(PostgresException ex)when(ex.SqlState=="40P01"){refused=true;outcome="native PG deadlock refusal before guard";}
                    check(refused,"ABA attempt refuses Apply without commit: "+outcome);
                    await using var acl=new NpgsqlCommand("SELECT NOT pg_catalog.has_table_privilege(@role,@a,'SELECT') AND NOT pg_catalog.has_table_privilege(@role,@b,'SELECT')",admin);
                    acl.Parameters.AddWithValue("role",role);acl.Parameters.AddWithValue("a",NpgsqlTypes.NpgsqlDbType.Oid,stale.ObjectOid);acl.Parameters.AddWithValue("b",NpgsqlTypes.NpgsqlDbType.Oid,replacementOid);
                    check((bool)(await acl.ExecuteScalarAsync())!,"ABA rollback preserves ACLs by OID on both original and replacement");
                }
                finally
                {
                    deadline.Cancel();if(!pending.IsCompleted){try{await held.RollbackAsync();}catch(InvalidOperationException){}try{await pending;}catch(Exception ex)when(ex is NpgsqlException or OperationCanceledException or InvalidOperationException){}}
                }
            }
            if(swapCommitted)await Sql($"DROP TABLE {ObjectSql.Relation(schema,"aba_replacement")}");
            else await Sql($"DROP TABLE {table}; ALTER TABLE {ObjectSql.Relation(schema,"aba_original")} RENAME TO target");
            var dbRequest=requests[0];stale=await Inspect(dbRequest);
            await Sql($"ALTER DATABASE {qd} RENAME TO {SqlIdentifier.Quote(database+"_old")}");
            await Sql($"CREATE DATABASE {qd}");await Sql($"REVOKE CONNECT ON DATABASE {qd} FROM PUBLIC");
            await Refuse(dbRequest,stale,"Recreated database rejected after command");
            await Sql($"DROP DATABASE {qd}");await Sql($"ALTER DATABASE {SqlIdentifier.Quote(database+"_old")} RENAME TO {qd}");

            await Sql($"GRANT USAGE ON SCHEMA {qs} TO {qg}; GRANT UPDATE(value) ON TABLE {table} TO {qg} WITH GRANT OPTION");
            var limited=new PrivilegeService(new NpgsqlConnectionStringBuilder(connection){Username=grantor}.ConnectionString);
            var limitedIdentity=await limited.InspectTargetAsync(PrivilegeTarget.Table,"target",schema,"value");
            check(await limited.ApplyInspectedAsync(colRequest,limitedIdentity) && await Has(colRequest),"Column-only grantor delegates without table SELECT or catalog UPDATE");
            check(await limited.ApplyInspectedAsync(colRequest with{Revoke=true},limitedIdentity) && !await Has(colRequest),"Column-only grantor revokes under same identity guard");
            var emptyIdentity=await Inspect(colRequest);await Sql($"REVOKE UPDATE(value) ON TABLE {table} FROM {qg}");
            check(!await service.ApplyInspectedAsync(colRequest with{Revoke=true},emptyIdentity),"Empty column ACL is rolled back rather than falsely committed");
            await using(var c=new NpgsqlCommand($"SELECT count(*)=1 AND min(id)=17 FROM {table}",admin))check((bool)(await c.ExecuteScalarAsync())!,"Identity operations preserve table data");
            await using(var c=new NpgsqlCommand($"SELECT last_value=1 AND NOT is_called FROM {sequence}",admin))check((bool)(await c.ExecuteScalarAsync())!,"Identity operations do not consume sequence values");
        }
        finally
        {
            await Sql($"DROP DATABASE IF EXISTS {qd}");await Sql($"DROP DATABASE IF EXISTS {SqlIdentifier.Quote(database+"_old")}");
            await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE; DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(schema+"_old")} CASCADE; DROP OWNED BY {qg}; DROP ROLE {qg}; DROP ROLE {qr}");
        }
    }
}
