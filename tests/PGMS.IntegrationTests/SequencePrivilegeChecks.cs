using Npgsql;
using PGMS.PostgreSql;

internal static class SequencePrivilegeChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="seq_perm_é'\"_"+suffix,role="seq_role_"+suffix;
        string future="seq_future_"+suffix;
        string[] quotaRoles=Enumerable.Range(0,170).Select(i=>"sq_acl_"+suffix+"_"+i).ToArray();
        string name="seq'\"é",qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),qf=SqlIdentifier.Quote(future),sequence=ObjectSql.Relation(schema,name);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var command=new NpgsqlCommand(sql,admin);await command.ExecuteNonQueryAsync();}
        async Task<(long Value,bool Called)> State()
        {
            await using var command=new NpgsqlCommand($"SELECT last_value,is_called FROM {sequence}",admin);
            await using var row=await command.ExecuteReaderAsync();await row.ReadAsync();return(row.GetInt64(0),row.GetBoolean(1));
        }
        string limitedConnection=new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString;
        var inspect=new EffectivePrivilegeService(limitedConnection);
        try
        {
            await Sql($"CREATE ROLE {qr} LOGIN;CREATE SCHEMA {qs};CREATE SEQUENCE {sequence} START 100 CACHE 1;");
            var snapshot=await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema);
            check(snapshot.Target==PrivilegeTarget.Sequence && snapshot.Name==name && snapshot.Schema==schema && snapshot.Oid>0 && snapshot.Owner=="pgms_test","Sequence access snapshot preserves exact identity and owner");
            check(snapshot.Role==role && snapshot.SchemaUsage==false && snapshot.Privileges.Count==3 && snapshot.Privileges.All(p=>!p.Allowed && !p.GrantOption),"Limited role sees denied sequence privileges separately from missing schema USAGE");
            check(snapshot.AclEntries.Count==3 && snapshot.AclEntries.All(e=>e.Source==PrivilegeAclSource.BuiltInDefault && e.Scope=="Sequence" && e.Column is null),"NULL sequence ACL expands its three built-in entries without column rows");
            check(await State()==(100L,false) && snapshot.RowSecurityEnabled is null && snapshot.RowSecurityForced is null && snapshot.RowSecurityActive is null && !snapshot.ColumnsTruncated,"Sequence inspection neither consumes state nor invents RLS and column context");
            await Sql($"GRANT USAGE ON SCHEMA {qs} TO {qr};");
            await using var limited=new NpgsqlConnection(limitedConnection);await limited.OpenAsync();
            async Task<object?> Value(string sql)
            {
                await using var command=new NpgsqlCommand(sql,limited);command.Parameters.AddWithValue("sequence",sequence);return await command.ExecuteScalarAsync();
            }
            async Task<bool> Refused(string sql)
            {try{await Value(sql);return false;}catch(PostgresException ex) when(ex.SqlState=="42501"){return true;}}
            var service=new PrivilegeService(connection);
            var request=new PrivilegeRequest(PrivilegeTarget.Sequence,name,role,"USAGE",Schema:schema);
            await service.ApplyAsync(request);
            var usage=await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema);
            check(usage.Privileges.Single(p=>p.Privilege=="USAGE").Allowed && usage.Privileges.Where(p=>p.Privilege!="USAGE").All(p=>!p.Allowed) && await State()==(100L,false),"USAGE grant changes authority without consuming sequence state");
            check((long)(await Value("SELECT nextval(@sequence::regclass)"))! ==100,"USAGE permits nextval for a limited role");
            check((long)(await Value("SELECT currval(@sequence::regclass)"))! ==100,"USAGE permits currval in the same initialized session");
            check(await Refused("SELECT setval(@sequence::regclass,500)"),"USAGE alone does not permit setval");
            check(await Refused($"SELECT last_value FROM {sequence}"),"USAGE alone does not permit reading sequence last_value");
            await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema);
            check(await State()==(100L,true),"Repeated access inspection does not advance an initialized sequence");
            await service.ApplyAsync(request with {Revoke=true});
            await service.ApplyAsync(request with {Privilege="SELECT"});
            var selected=await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema);
            check(selected.Privileges.Single(p=>p.Privilege=="SELECT").Allowed && selected.Privileges.Where(p=>p.Privilege!="SELECT").All(p=>!p.Allowed),"SELECT sequence grant does not imply USAGE or UPDATE");
            check((long)(await Value($"SELECT last_value FROM {sequence}"))! ==100,"SELECT permits reading last_value");
            check((long)(await Value("SELECT currval(@sequence::regclass)"))! ==100,"SELECT permits session-local currval after prior initialization");
            check(await Refused("SELECT nextval(@sequence::regclass)"),"SELECT alone cannot advance sequence with nextval");
            check(await Refused("SELECT setval(@sequence::regclass,500)"),"SELECT alone cannot set sequence value");
            check(await State()==(100L,true),"Denied nextval and setval preserve sequence state");
            await service.ApplyAsync(request with {Privilege="SELECT",Revoke=true});
            await service.ApplyAsync(request with {Privilege="UPDATE"});
            var updated=await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema);
            check(updated.Privileges.Single(p=>p.Privilege=="UPDATE").Allowed && updated.Privileges.Where(p=>p.Privilege!="UPDATE").All(p=>!p.Allowed),"UPDATE sequence grant does not imply SELECT or USAGE");
            check((long)(await Value("SELECT nextval(@sequence::regclass)"))! ==101,"UPDATE permits advancing sequence without USAGE");
            check((long)(await Value("SELECT setval(@sequence::regclass,500,false)"))! ==500,"UPDATE permits setval with explicit is_called flag");
            check(await State()==(500L,false),"Only the deliberate setval changed current value and is_called");
            check(await Refused("SELECT currval(@sequence::regclass)"),"UPDATE alone does not permit currval");
            check(await Refused($"SELECT last_value FROM {sequence}"),"UPDATE alone does not permit reading last_value");
            await service.ApplyAsync(request with {Privilege="UPDATE",Revoke=true});
            var publicRequest=request with {RecipientIsPublic=true,Role=""};
            await service.ApplyAsync(publicRequest);
            check((await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema)).AclEntries.Any(e=>e.GranteeOid==0 && e.Privilege=="USAGE" && !e.GrantOption && e.Source==PrivilegeAclSource.Stored),"Sequence PUBLIC grant appears as stored OID0 without delegation");
            check((long)(await Value("SELECT nextval(@sequence::regclass)"))! ==500,"PUBLIC USAGE permits limited role nextval");
            await Sql($"CREATE ROLE {qf} LOGIN;GRANT USAGE ON SCHEMA {qs} TO {qf};");
            string futureConnection=new NpgsqlConnectionStringBuilder(connection){Username=future}.ConnectionString;
            await using(var reader=new NpgsqlConnection(futureConnection))
            {
                await reader.OpenAsync();await using var command=new NpgsqlCommand("SELECT nextval(@sequence::regclass)",reader);command.Parameters.AddWithValue("sequence",sequence);
                check((long)(await command.ExecuteScalarAsync())! ==501,"Sequence PUBLIC grant applies to a role created later");
            }
            await service.ApplyAsync(request with {Privilege="SELECT"});await service.ApplyAsync(publicRequest with {Revoke=true});
            var afterPublic=await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema);
            check(afterPublic.Privileges.Single(p=>p.Privilege=="SELECT").Allowed && !afterPublic.Privileges.Single(p=>p.Privilege=="USAGE").Allowed && await Refused("SELECT nextval(@sequence::regclass)"),"PUBLIC revoke preserves independent SELECT without preserving nextval authority");
            await service.ApplyAsync(request with {GrantOption=true});
            check((await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema)).Privileges.Single(p=>p.Privilege=="USAGE") is {Allowed:true,GrantOption:true},"Sequence USAGE grant option is effective for a non-owner");
            var delegated=request with {Role=future};var delegateService=new PrivilegeService(limitedConnection);
            await delegateService.ApplyAsync(delegated);
            check((await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema)).AclEntries.Any(e=>e.Grantee==future && e.Grantor==role && e.Privilege=="USAGE"),"Sequence delegation preserves actual non-owner grantor");
            bool dependency=false;try{await service.ApplyAsync(request with {GrantOption=true,Revoke=true});}catch(PostgresException ex) when(ex.SqlState=="2BP01"){dependency=true;}
            check(dependency && (await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema)).Privileges.Single(p=>p.Privilege=="USAGE").GrantOption,"Sequence grant-option RESTRICT refuses dependent grant and retains authority");
            await delegateService.ApplyAsync(delegated with {Revoke=true});await service.ApplyAsync(request with {GrantOption=true,Revoke=true});
            check((await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema)).Privileges.Single(p=>p.Privilege=="USAGE") is {Allowed:true,GrantOption:false},"Sequence delegation-only revoke preserves base USAGE after explicit dependent cleanup");
            await Sql($"REVOKE USAGE ON SCHEMA {qs} FROM {qr};");
            var noSchema=await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema);
            check(noSchema.SchemaUsage==false && noSchema.Privileges.Single(p=>p.Privilege=="USAGE").Allowed,"Sequence privilege and missing schema USAGE are independent observed facts");
            await Sql($"GRANT USAGE ON SCHEMA {qs} TO {qr};REVOKE ALL ON SEQUENCE {sequence} FROM pgms_test,{qr};");
            var empty=await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema);
            check(empty.AclEntries.Count==0 && !empty.AclTruncated,"Explicit empty sequence ACL is not replaced with built-in defaults");
            await Sql($"CREATE TABLE {qs}.table_target(id integer);CREATE VIEW {qs}.view_target AS SELECT 1 AS id;");
            foreach(string wrong in new[]{"table_target","view_target"})
            {
                bool refused=false;try{await inspect.InspectAsync(PrivilegeTarget.Sequence,wrong,schema);}catch(InvalidOperationException){refused=true;}
                check(refused,"Sequence inspection refuses wrong relation kind: "+wrong);
            }
            bool missing=false;try{await inspect.InspectAsync(PrivilegeTarget.Sequence,"absent",schema);}catch(InvalidOperationException){missing=true;}
            check(missing,"Missing sequence is reported instead of an empty successful observation");
            await Sql($"DROP SEQUENCE {sequence};CREATE SEQUENCE {sequence} START 900;");
            var replaced=await inspect.InspectAsync(PrivilegeTarget.Sequence,name,schema);
            check(replaced.Oid!=snapshot.Oid && replaced.Privileges.All(p=>!p.Allowed),"Fresh sequence inspection sees replacement OID without transferring old grants");
            using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel();bool refused=false;try{await new EffectivePrivilegeService("invalid connection").InspectAsync(PrivilegeTarget.Sequence,name,schema,canceled.Token);}catch(OperationCanceledException){refused=true;}
                check(refused,"Pre-canceled sequence inspection refuses before parsing connection");
            }
            string quota=ObjectSql.Relation(schema,"quota");
            await Sql($"CREATE SEQUENCE {quota};"+string.Join("",quotaRoles.Select((r,i)=>$"CREATE ROLE {SqlIdentifier.Quote(r)};GRANT {(i==169?"USAGE,SELECT":"USAGE,SELECT,UPDATE")} ON SEQUENCE {quota} TO {SqlIdentifier.Quote(r)};")));
            var exact=await inspect.InspectAsync(PrivilegeTarget.Sequence,"quota",schema);
            check(exact.AclEntries.Count==512 && !exact.AclTruncated && !exact.ColumnsTruncated,"Sequence ACL exactly512 entries is complete without table column limits");
            await Sql($"GRANT UPDATE ON SEQUENCE {quota} TO {SqlIdentifier.Quote(quotaRoles[^1])};");
            var over=await inspect.InspectAsync(PrivilegeTarget.Sequence,"quota",schema);
            check(over.AclEntries.Count==512 && over.AclTruncated && over.AclIncomplete,"Sequence ACL row513 marks explicit truncation without retaining extra entry");
        }
        finally {await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {qf};"+string.Join("",quotaRoles.Select(r=>$"DROP ROLE IF EXISTS {SqlIdentifier.Quote(r)};")));}
    }
    public static void RunSql(Action<bool,string> check)
    {
        check(PrivilegeRequest.Allowed(PrivilegeTarget.Sequence).SequenceEqual(new[]{"USAGE","SELECT","UPDATE"}),"Sequence writes offer exactly USAGE SELECT UPDATE");
        const string target="SEQUENCE \"s'\"\"é\".\"seq'\"\"é\"",recipient="\"r'\"\"é\"";
        foreach(string privilege in new[]{"USAGE","SELECT","UPDATE"})
        {
            var request=new PrivilegeRequest(PrivilegeTarget.Sequence,"seq'\"é","r'\"é",privilege,Schema:"s'\"é");
            check(request.ToSql()==$"GRANT {privilege} ON {target} TO {recipient};","Sequence grant quotes exact names: "+privilege);
            check((request with {GrantOption=true}).ToSql()==$"GRANT {privilege} ON {target} TO {recipient} WITH GRANT OPTION;","Sequence grant option SQL: "+privilege);
            check((request with {GrantOption=true,Revoke=true}).ToSql()==$"REVOKE GRANT OPTION FOR {privilege} ON {target} FROM {recipient} RESTRICT;","Sequence delegation revoke is restrictive: "+privilege);
        }
        var publicRequest=new PrivilegeRequest(PrivilegeTarget.Sequence,"seq","","USAGE",Schema:"s",RecipientIsPublic:true);
        check(publicRequest.ToSql()=="GRANT USAGE ON SEQUENCE \"s\".\"seq\" TO PUBLIC;","Sequence PUBLIC grant is explicit");
        check((publicRequest with {Revoke=true}).ToSql()=="REVOKE USAGE ON SEQUENCE \"s\".\"seq\" FROM PUBLIC RESTRICT;","Sequence PUBLIC revoke stays restrictive");
        foreach(var invalid in new[]{publicRequest with {GrantOption=true},publicRequest with {Privilege="INSERT"},publicRequest with {Schema=""}})
        {
            bool refused=false;try{invalid.ToSql();}catch(ArgumentException){refused=true;}
            check(refused,"Invalid sequence privilege request is refused: "+invalid);
        }
    }
}
