using Npgsql;
using PGMS.PostgreSql;

internal static class DefaultPrivilegeChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),owner="default_owner_"+suffix,role="default_role_"+suffix,schema="default_"+suffix;
        string qo=SqlIdentifier.Quote(owner),qr=SqlIdentifier.Quote(role),qs=SqlIdentifier.Quote(schema);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Admin(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        string owned=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        await using var client=new NpgsqlConnection(owned);
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,client);await c.ExecuteNonQueryAsync();}
        async Task<bool> Entry(string kind,bool grant)
        {
            await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_default_acl d CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a WHERE d.defaclrole=@owner::regrole AND d.defaclobjtype::text=@kind AND a.grantee=@role::regrole AND a.is_grantable=@grant)",admin);
            c.Parameters.AddWithValue("owner",owner);c.Parameters.AddWithValue("role",role);c.Parameters.AddWithValue("kind",kind);c.Parameters.AddWithValue("grant",grant);return (bool)(await c.ExecuteScalarAsync())!;
        }
        try
        {
            await Admin($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr};CREATE SCHEMA {qs} AUTHORIZATION {qo}");await client.OpenAsync();
            var catalog=new DefaultPrivilegeCatalog(owned);
            var baseline=await catalog.InspectAsync(DefaultPrivilegeTarget.Routines,owner,schema);
            check(baseline.Identity.CreatorOid>0 && baseline.Identity.NamespaceOid>0 && baseline.GlobalPolicyOid==0 && baseline.SchemaPolicyOid==0,"Default inspection captures creator/schema identity even when no policy row exists");
            check(baseline.Entries.Any(e=>e.Origin==DefaultPrivilegeOrigin.BuiltIn && e.RecipientOid==0 && e.Privilege=="EXECUTE"),"Absent routine policy exposes built-in PUBLIC EXECUTE explicitly");
            var localRevoke=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Routines,owner,"","EXECUTE",schema,Revoke:true,RecipientIsPublic:true);
            await Sql(localRevoke.ToSql());
            var unchanged=await catalog.InspectAsync(DefaultPrivilegeTarget.Routines,owner,schema);
            check(unchanged.SchemaPolicyOid==0 && unchanged.Entries.SequenceEqual(baseline.Entries),"Per-schema revoke does not subtract built-in global PUBLIC authority");
            foreach(var item in new[]{(DefaultPrivilegeTarget.Tables,"SELECT","r"),(DefaultPrivilegeTarget.Sequences,"USAGE","S"),(DefaultPrivilegeTarget.Routines,"EXECUTE","f"),(DefaultPrivilegeTarget.Types,"USAGE","T"),(DefaultPrivilegeTarget.Schemas,"USAGE","n")})
            {
                var request=new DefaultPrivilegeRequest(item.Item1,owner,role,item.Item2,item.Item1==DefaultPrivilegeTarget.Schemas?null:schema);
                var inspected=await catalog.InspectAsync(item.Item1,owner,request.Schema);
                check(inspected.Entries.Any(e=>e.Recipient==owner && e.Origin==DefaultPrivilegeOrigin.BuiltIn),"Built-in owner policy inspection supports class: "+item.Item1);
                await Sql(request.ToSql());check(await Entry(item.Item3,false),"Ordinary creator grants explicit future-object default: "+item.Item1);
                await Sql((request with{GrantOption=true}).ToSql());check(await Entry(item.Item3,true),"Default grant option changes catalog policy: "+item.Item1);
                await Sql((request with{GrantOption=true,Revoke=true}).ToSql());check(await Entry(item.Item3,false) && !await Entry(item.Item3,true),"Default REVOKE GRANT OPTION preserves base policy: "+item.Item1);
                await Sql((request with{Revoke=true}).ToSql());check(!await Entry(item.Item3,false) && !await Entry(item.Item3,true),"Default REVOKE RESTRICT removes recipient policy: "+item.Item1);
            }
            await Sql($"CREATE TABLE {qs}.old(id int)");
            var table=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Tables,owner,role,"SELECT",schema);
            await Sql(table.ToSql());
            var tablePolicy=await catalog.InspectAsync(DefaultPrivilegeTarget.Tables,owner,schema);
            check(tablePolicy.SchemaPolicyOid>0 && tablePolicy.GlobalPolicyOid==0 && tablePolicy.Entries.Any(e=>e.Origin==DefaultPrivilegeOrigin.Schema && e.Recipient==role && e.Privilege=="SELECT"),"Schema policy inspection separates additive entries from built-in owner defaults");
            var otherScope=await catalog.InspectAsync(DefaultPrivilegeTarget.Tables,owner);
            check(otherScope.Identity.NamespaceOid==0 && otherScope.SchemaPolicyOid==0 && !otherScope.Entries.Any(e=>e.Recipient==role),"Global inspection never imports grants from a particular schema");
            await Sql($"CREATE TABLE {qs}.future(id int)");
            await using(var c=new NpgsqlCommand("SELECT NOT pg_catalog.has_table_privilege(@role,@old,'SELECT') AND pg_catalog.has_table_privilege(@role,@future,'SELECT')",admin))
            {c.Parameters.AddWithValue("role",role);c.Parameters.AddWithValue("old",qs+".old");c.Parameters.AddWithValue("future",qs+".future");check((bool)(await c.ExecuteScalarAsync())!,"Default policy affects future tables and preserves existing table ACL");}
            var global=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Routines,owner,"","EXECUTE",RecipientIsPublic:true,Revoke:true);
            await Sql(global.ToSql());
            var globalPolicy=await catalog.InspectAsync(DefaultPrivilegeTarget.Routines,owner,schema);
            check(globalPolicy.GlobalPolicyOid>0 && globalPolicy.Entries.All(e=>e.Origin==DefaultPrivilegeOrigin.Global) && !globalPolicy.Entries.Any(e=>e.RecipientOid==0),"Explicit global policy replaces built-in PUBLIC defaults in inspection");
            await Sql($"CREATE FUNCTION {qs}.future_fn() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
            await using(var c=new NpgsqlCommand("SELECT NOT pg_catalog.has_function_privilege(@role,@signature,'EXECUTE')",admin))
            {c.Parameters.AddWithValue("role",role);c.Parameters.AddWithValue("signature",qs+".future_fn()");check((bool)(await c.ExecuteScalarAsync())!,"Database-wide routine default can remove built-in PUBLIC execution for future routines");}
            await Sql((global with{Role=owner,RecipientIsPublic=false}).ToSql());
            var emptyPolicy=await catalog.InspectAsync(DefaultPrivilegeTarget.Routines,owner,schema);
            check(emptyPolicy.GlobalPolicyOid>0 && emptyPolicy.Entries.Count==0,"Explicit empty global ACL is preserved instead of replaced with built-in grants");
            await Sql((global with{Role=owner,RecipientIsPublic=false,Revoke=false}).ToSql());
            await Sql((global with{Role=role,RecipientIsPublic=false,Revoke=false,Schema=schema}).ToSql());
            var combined=await catalog.InspectAsync(DefaultPrivilegeTarget.Routines,owner,schema);
            check(combined.Entries.Any(e=>e.Origin==DefaultPrivilegeOrigin.Global && e.Recipient==owner) && combined.Entries.Any(e=>e.Origin==DefaultPrivilegeOrigin.Schema && e.Recipient==role),"Inspection retains global and additive schema origins together");
            await Admin($"CREATE SCHEMA {SqlIdentifier.Quote(schema+"x")} AUTHORIZATION {qo}");
            var previousSchema=await catalog.InspectAsync(DefaultPrivilegeTarget.Tables,owner,schema+"x");
            await Admin($"DROP SCHEMA {SqlIdentifier.Quote(schema+"x")};CREATE SCHEMA {SqlIdentifier.Quote(schema+"x")} AUTHORIZATION {qo}");
            var replacedSchema=await catalog.InspectAsync(DefaultPrivilegeTarget.Tables,owner,schema+"x");
            check(previousSchema.Identity.NamespaceOid!=replacedSchema.Identity.NamespaceOid,"Schema replacement cannot reuse captured default-scope identity");
            await Sql($"DROP SCHEMA {SqlIdentifier.Quote(schema+"x")}");
            foreach(var invalid in new[]{table with{Privilege="SELECT; DROP ROLE anything"},table with{Schema=""},table with{RecipientIsPublic=true,GrantOption=true},table with{Target=DefaultPrivilegeTarget.Schemas,Privilege="USAGE"}})
            {bool refused=false;try{invalid.ToSql();}catch(ArgumentException){refused=true;}check(refused,"Invalid default policy is refused before SQL: "+invalid.Target+"/"+invalid.Privilege);}
            bool missing=false;try{await catalog.InspectAsync(DefaultPrivilegeTarget.Tables,owner,"missing_"+suffix);}catch(InvalidOperationException){missing=true;}
            check(missing,"Missing schema is refused instead of silently inspecting global defaults");
            using(var cancel=new CancellationTokenSource())
            {cancel.Cancel();bool canceled=false;try{await new DefaultPrivilegeCatalog("Host=invalid").InspectAsync(DefaultPrivilegeTarget.Tables,owner,token:cancel.Token);}catch(OperationCanceledException){canceled=true;}check(canceled,"Pre-canceled default inspection does not connect");}
            await Admin($"ALTER ROLE {qo} RENAME TO {SqlIdentifier.Quote(owner+"x")};CREATE ROLE {qo} LOGIN");
            var replacement=await catalog.InspectAsync(DefaultPrivilegeTarget.Tables,owner,schema);
            check(replacement.Identity.CreatorOid!=tablePolicy.Identity.CreatorOid && replacement.SchemaPolicyOid==0,"Same creator name after replacement has a new identity and no inherited policy");
            await Admin($"DROP ROLE {qo};ALTER ROLE {SqlIdentifier.Quote(owner+"x")} RENAME TO {qo}");
            var quoted=table with{Creator="creator\"; --",Role="recipient\"; --",Schema="schema\"; --"};
            check(quoted.ToSql().Contains("FOR ROLE \"creator\"\"; --\" IN SCHEMA \"schema\"\"; --\"",StringComparison.Ordinal),"Default SQL treats creator/schema/recipient as quoted identifiers");
        }
        finally{await client.CloseAsync();await Admin($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP OWNED BY {qo};DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {qo}");}
    }
}
