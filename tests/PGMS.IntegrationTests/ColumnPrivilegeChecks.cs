using Npgsql;
using PGMS.PostgreSql;

internal static class ColumnPrivilegeChecks
{
    public static void RunSql(Action<bool,string> check)
    {
        var request=new PrivilegeRequest(PrivilegeTarget.Table,"item","worker","SELECT",Schema:"s",Column:"value");
        foreach(string privilege in PrivilegeRequest.AllowedForColumn)
        {
            var r=request with {Privilege=privilege};string spec=privilege+" (\"value\") ON TABLE \"s\".\"item\"";
            check(r.ToSql()==$"GRANT {spec} TO \"worker\";","Single-column grant SQL: "+privilege);
            check((r with {Revoke=true}).ToSql()==$"REVOKE {spec} FROM \"worker\" RESTRICT;","Single-column restrictive revoke SQL: "+privilege);
            check((r with {GrantOption=true}).ToSql()==$"GRANT {spec} TO \"worker\" WITH GRANT OPTION;","Column delegation SQL: "+privilege);
            check((r with {GrantOption=true,Revoke=true}).ToSql()==$"REVOKE GRANT OPTION FOR {spec} FROM \"worker\" RESTRICT;","Column delegation-only revoke SQL: "+privilege);
        }
        check((request with {RecipientIsPublic=true,Role=""}).ToSql()=="GRANT SELECT (\"value\") ON TABLE \"s\".\"item\" TO PUBLIC;","Column PUBLIC is explicit and needs no role name");
        check((request with {RecipientIsPublic=true,Role="",Revoke=true}).ToSql()=="REVOKE SELECT (\"value\") ON TABLE \"s\".\"item\" FROM PUBLIC RESTRICT;","Column PUBLIC revoke remains restrictive");
        check((request with {Role="PUBLIC"}).ToSql().EndsWith("TO \"PUBLIC\";"),"Typed PUBLIC remains a quoted role in column scope");
        var quoted=request with {Schema="s'\"é",Name="t'\"é",Column="c'\"é; DROP ROLE x",Role="r'\"é"};
        check(quoted.ToSql()=="GRANT SELECT (\"c'\"\"é; DROP ROLE x\") ON TABLE \"s'\"\"é\".\"t'\"\"é\" TO \"r'\"\"é\";","All column identifiers including SQL-like text are quoted as single exact names");
        foreach(var invalid in new[]{request with {Column=""},request with {Column="  "},request with {Column="a\0b"},request with {Privilege="DELETE"},request with {Privilege="TRIGGER"},request with {Privilege="TRUNCATE"},request with {Privilege="MAINTAIN"},request with {Target=PrivilegeTarget.Database},request with {Target=PrivilegeTarget.Schema},request with {Target=PrivilegeTarget.Sequence},request with {RecipientIsPublic=true,GrantOption=true}})
        {
            bool refused=false;try{invalid.ToSql();}catch(ArgumentException){refused=true;}
            check(refused,"Invalid column request refused before connection: "+invalid.Privilege+"/"+invalid.Target);
        }
        check((request with {Column="a,b"}).ToSql().Contains("(\"a,b\")"),"Comma in a column name never becomes a column list");
        check((request with {Column=null}).ToSql()=="GRANT SELECT ON TABLE \"s\".\"item\" TO \"worker\";","Null column preserves whole-table SQL");
    }
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="col_é\"_"+suffix,role="col_role_"+suffix;
        string future="col_future_"+suffix,qf=SqlIdentifier.Quote(future);
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        const string column="value'\"é";string qc=SqlIdentifier.Quote(column);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        async Task<object?> AdminValue(string sql){await using var c=new NpgsqlCommand(sql,admin);return await c.ExecuteScalarAsync();}
        string limitedConnection=new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString;
        try
        {
            await Sql($"CREATE SCHEMA {qs};CREATE ROLE {qr} LOGIN;GRANT USAGE ON SCHEMA {qs} TO {qr};CREATE TABLE {table}({qc} integer UNIQUE,secret text DEFAULT 'hidden');INSERT INTO {table} VALUES(10,'private');");
            await using var limited=new NpgsqlConnection(limitedConnection);await limited.OpenAsync();
            async Task<object?> Value(string sql){await using var c=new NpgsqlCommand(sql,limited);return await c.ExecuteScalarAsync();}
            async Task<bool> Denied(string sql){try{await Value(sql);return false;}catch(PostgresException ex)when(ex.SqlState=="42501"){return true;}}
            var service=new PrivilegeService(connection);
            var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",role,"SELECT",Schema:schema,Column:column);
            var inspector=new EffectivePrivilegeService(limitedConnection);
            check(await Denied($"SELECT {qc} FROM {table}"),"Limited role initially cannot read target column");
            await service.ApplyAsync(request);
            check((int)(await Value($"SELECT {qc} FROM {table}"))! ==10,"Column SELECT permits reading only granted column");
            check(await Denied($"SELECT secret FROM {table}") && await Denied($"SELECT * FROM {table}"),"Column SELECT does not grant sibling or SELECT star");
            var observed=await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema);
            check(!observed.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed && observed.Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT").Allowed,"Effective snapshot distinguishes column SELECT from denied table SELECT");
            check(observed.AclEntries.Any(e=>e.Column==column && e.Grantee==role && e.Privilege=="SELECT" && e.ColumnNumber==1 && e.Source==PrivilegeAclSource.Stored),"Stored column ACL preserves exact name attnum and recipient");
            await service.ApplyAsync(request with {Revoke=true});
            check(await Denied($"SELECT {qc} FROM {table}"),"Column REVOKE removes direct column SELECT");
            await service.ApplyAsync(request with {Privilege="UPDATE"});await Value($"UPDATE {table} SET {qc}=11");
            check((int)(await AdminValue($"SELECT {qc} FROM {table}"))! ==11,"Column UPDATE permits writing its column without SELECT when no read is needed");
            check(await Denied($"UPDATE {table} SET secret='changed'"),"Column UPDATE cannot write sibling column");
            check(await Denied($"UPDATE {table} SET {qc}=12 WHERE {qc}=11"),"UPDATE predicate still requires SELECT on the referenced column");
            await service.ApplyAsync(request with {Privilege="UPDATE",Revoke=true});
            check(await Denied($"UPDATE {table} SET {qc}=12"),"Revoked column UPDATE prevents further writes");
            await service.ApplyAsync(request with {Privilege="INSERT"});await Value($"INSERT INTO {table}({qc}) VALUES(20)");
            check((long)(await AdminValue($"SELECT count(*) FROM {table} WHERE {qc}=20 AND secret='hidden'"))! ==1,"Column INSERT uses sibling default without granting sibling write access");
            check(await Denied($"INSERT INTO {table}({qc},secret) VALUES(30,'leak')"),"Column INSERT cannot supply sibling value");
            await service.ApplyAsync(request with {Privilege="INSERT",Revoke=true});
            check(await Denied($"INSERT INTO {table}({qc}) VALUES(30)"),"Revoked column INSERT prevents new rows");
            await Sql($"GRANT CREATE ON SCHEMA {qs} TO {qr};");
            check(await Denied($"CREATE TABLE {qs}.child_denied(id integer REFERENCES {table}({qc}))"),"Without REFERENCES role cannot create foreign key to parent column");
            await service.ApplyAsync(request with {Privilege="REFERENCES"});
            await Value($"CREATE TABLE {qs}.child(id integer REFERENCES {table}({qc}));INSERT INTO {qs}.child VALUES(11)");
            check((long)(await AdminValue($"SELECT count(*) FROM {qs}.child"))! ==1,"Column REFERENCES permits real FK and valid referencing row");
            var refs=await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema);
            check(refs.Privileges.Single(p=>p.Column==column && p.Privilege=="REFERENCES").Allowed && !refs.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="REFERENCES").Allowed,"Column REFERENCES stays separate from whole-table authority");
            await service.ApplyAsync(request with {Privilege="REFERENCES",Revoke=true});
            check(await Denied($"CREATE TABLE {qs}.child_after(id integer REFERENCES {table}({qc}))"),"REFERENCES revoke prevents creation of new foreign keys");
            await Value($"INSERT INTO {qs}.child VALUES(20)");
            check((long)(await AdminValue($"SELECT count(*) FROM {qs}.child"))! ==2,"Revoking REFERENCES does not remove an existing foreign key");
            var publicRequest=request with {RecipientIsPublic=true,Role=""};await service.ApplyAsync(publicRequest);
            check((await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema)).AclEntries.Any(e=>e.Column==column && e.GranteeOid==0 && !e.GrantOption),"Column PUBLIC appears as OID0 without grant option");
            check((long)(await Value($"SELECT count({qc}) FROM {table}"))! ==2,"PUBLIC column SELECT permits granted column for current role");
            await Sql($"CREATE ROLE {qf} LOGIN;GRANT USAGE ON SCHEMA {qs} TO {qf};");
            string futureConnection=new NpgsqlConnectionStringBuilder(connection){Username=future}.ConnectionString;
            await using var futureDb=new NpgsqlConnection(futureConnection);await futureDb.OpenAsync();
            async Task<bool> FutureCanRead()
            {try{await using var c=new NpgsqlCommand($"SELECT {qc} FROM {table}",futureDb);await c.ExecuteScalarAsync();return true;}catch(PostgresException ex)when(ex.SqlState=="42501"){return false;}}
            check(await FutureCanRead(),"PUBLIC column SELECT applies to role created after the grant");
            await service.ApplyAsync(request);await service.ApplyAsync(publicRequest with {Revoke=true});
            check(!await FutureCanRead(),"Revoking PUBLIC column grant removes future role access");
            check((long)(await Value($"SELECT count({qc}) FROM {table}"))! ==2,"PUBLIC revoke preserves independent direct column SELECT");
            await service.ApplyAsync(request with {GrantOption=true});
            var delegatedSnapshot=await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema);
            check(delegatedSnapshot.Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT").GrantOption && !delegatedSnapshot.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").GrantOption,"Column grant option does not grant table-level delegation");
            var delegateService=new PrivilegeService(limitedConnection);var delegated=request with {Role=future};await delegateService.ApplyAsync(delegated);
            check((await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema)).AclEntries.Any(e=>e.Column==column && e.Grantee==future && e.Grantor==role),"Delegated column ACL retains actual non-owner grantor");
            bool dependent=false;try{await service.ApplyAsync(request with {GrantOption=true,Revoke=true});}catch(PostgresException ex)when(ex.SqlState=="2BP01"){dependent=true;}
            check(dependent && (await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema)).Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT").GrantOption,"Column option-only RESTRICT refuses dependent grant and retains delegation");
            await delegateService.ApplyAsync(delegated with {Revoke=true});await service.ApplyAsync(request with {GrantOption=true,Revoke=true});
            var stripped=(await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema)).Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT");
            check(stripped.Allowed && !stripped.GrantOption,"Removing only column grant option preserves base SELECT");
            check(!await FutureCanRead(),"Explicit delegated revoke removes downstream column access");

            string application="PGMS.ColumnCancel."+suffix;
            var cancelService=new PrivilegeService(new NpgsqlConnectionStringBuilder(connection){ApplicationName=application}.ConnectionString);
            foreach(bool revoke in new[]{false,true})
            {
                if(revoke)await service.ApplyAsync(request with {Privilege="UPDATE"});
                var before=(await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema)).AclEntries;
                await using var locker=new NpgsqlConnection(connection);await locker.OpenAsync();await using var tx=await locker.BeginTransactionAsync();
                await using(var c=new NpgsqlCommand("SELECT attnum FROM pg_catalog.pg_attribute WHERE attrelid=@table::regclass AND attname=@column FOR UPDATE",locker,tx))
                {c.Parameters.AddWithValue("table",table);c.Parameters.AddWithValue("column",column);await c.ExecuteScalarAsync();}
                using var cancel=new CancellationTokenSource();var pending=cancelService.ApplyAsync(request with {Privilege="UPDATE",Revoke=revoke},cancel.Token);
                try
                {
                    bool waiting=false;var until=DateTime.UtcNow.AddSeconds(8);
                    while(!pending.IsCompleted && DateTime.UtcNow<until)
                    {
                        await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND @pid=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                        c.Parameters.AddWithValue("app",application);c.Parameters.AddWithValue("pid",locker.ProcessID);
                        if((bool)(await c.ExecuteScalarAsync())!){waiting=true;break;}await Task.Delay(40);
                    }
                    check(waiting,"Column command waits on exact pg_attribute fixture row; revoke="+revoke);
                    cancel.Cancel();bool canceled=false;try{await pending.WaitAsync(TimeSpan.FromSeconds(8));}catch(OperationCanceledException){canceled=true;}
                    check(canceled,"Blocked column permission command acknowledges cancel; revoke="+revoke);
                    check(before.SequenceEqual((await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema)).AclEntries),"Column cancel preserves stored ACL; revoke="+revoke);
                }
                finally {cancel.Cancel();await tx.RollbackAsync();try{await pending;}catch(OperationCanceledException){}}
                await service.ApplyAsync(request with {Privilege="UPDATE",Revoke=revoke});
                check((await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema)).Privileges.Single(p=>p.Column==column && p.Privilege=="UPDATE").Allowed==!revoke,"Explicit retry after canceled column command commits desired state");
            }
            var priorAcl=(await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema)).AclEntries;
            bool missing=false;try{await service.ApplyAsync(request with {Column="missing"});}catch(PostgresException ex)when(ex.SqlState=="42703"){missing=true;}
            check(missing && priorAcl.SequenceEqual((await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema)).AclEntries),"Missing column fails atomically with42703 and preserves existing ACL");
            using(var canceled=new CancellationTokenSource())
            {canceled.Cancel();bool stopped=false;try{await new PrivilegeService("invalid connection").ApplyAsync(request,canceled.Token);}catch(OperationCanceledException){stopped=true;}check(stopped,"Pre-canceled column Apply refuses before opening connection");}
            await Sql($"ALTER TABLE {table} ADD COLUMN ephemeral text;");
            await service.ApplyAsync(request with {Column="ephemeral"});
            var oldColumn=(await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema)).AclEntries.Single(e=>e.Column=="ephemeral" && e.Grantee==role);
            check(oldColumn.ColumnNumber==3 && oldColumn.Privilege=="SELECT","New column grant records its actual attnum");
            await Sql($"ALTER TABLE {table} DROP COLUMN ephemeral;ALTER TABLE {table} ADD COLUMN ephemeral text;");
            var recreated=await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema);
            check(recreated.Privileges.Any(p=>p.Column=="ephemeral" && p.ColumnNumber==4 && !p.Allowed) && !recreated.AclEntries.Any(e=>e.Column=="ephemeral" && e.Grantee==role),"Recreated same-name column has new attnum and no inherited old grants");
            await service.ApplyAsync(request with {Column=null});await service.ApplyAsync(request with {Revoke=true});
            var inherited=await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema);
            check(inherited.Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT").Allowed && !inherited.AclEntries.Any(e=>e.Column==column && e.Grantee==role && e.Privilege=="SELECT"),"Revoking direct column SELECT does not override whole-table SELECT");
            check((long)(await Value($"SELECT count(secret) FROM {table}"))! ==2,"Whole-table grant continues authorizing sibling column reads");
            await service.ApplyAsync(request with {Column=null,Revoke=true});
            check(await Denied($"SELECT {qc} FROM {table}") && await Denied($"SELECT secret FROM {table}"),"Removing table grant removes remaining SELECT without inventing DENY");
            await service.ApplyAsync(request with {Role=future});await Sql($"GRANT {qf} TO {qr};");
            var member=await inspector.InspectAsync(PrivilegeTarget.Table,"target",schema);
            check(member.Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT").Allowed && member.AclEntries.Any(e=>e.Column==column && e.Grantee==future && e.RoleAvailable),"Inherited column authority is effective while ACL keeps original group recipient");
            await Sql($"REVOKE {qf} FROM {qr};");
            check(await Denied($"SELECT {qc} FROM {table}"),"Removing membership removes inherited column SELECT contribution");
        }
        finally {await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr},{qf};");}
    }

}
