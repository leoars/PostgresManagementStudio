using Npgsql;
using PGMS.PostgreSql;

internal static class PrivilegeAclChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"), schema="acl_s_"+suffix, database="acl_db_"+suffix, role="acl_r_é'\"_"+suffix;
        string group="acl_g_"+suffix,other="acl_o_"+suffix;
        string qg=SqlIdentifier.Quote(group),qo=SqlIdentifier.Quote(other);
        string qs=SqlIdentifier.Quote(schema),qd=SqlIdentifier.Quote(database),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"item'\"");
        await using var admin=new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) {await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        try
        {
            await Sql($"CREATE ROLE {qr} LOGIN; CREATE ROLE {qg}; CREATE ROLE {qo}; CREATE SCHEMA {qs}; CREATE TABLE {table}(id integer, secret text);");
            await Sql($"CREATE DATABASE {qd};");
            var ownerService=new EffectivePrivilegeService(connection);
            var limited=new EffectivePrivilegeService(new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString);
            var db=await limited.InspectAsync(PrivilegeTarget.Database,database);
            check(db.AclEntries.Any(e=>e.GranteeOid==0 && e.Grantee=="PUBLIC" && e.Privilege=="CONNECT" && e.Source==PrivilegeAclSource.BuiltInDefault && e.RoleAvailable),"Database NULL ACL exposes built-in PUBLIC CONNECT with OID0");
            await Sql($"GRANT CONNECT ON DATABASE {qd} TO {qr} WITH GRANT OPTION;");
            db=await limited.InspectAsync(PrivilegeTarget.Database,database);
            check(db.AclEntries.Single(e=>e.Grantee==role && e.Privilege=="CONNECT") is {GrantOption:true,Source:PrivilegeAclSource.Stored,Grantor:"pgms_test"},"Stored database ACL retains exact recipient grantor and grant option");
            var schemaDefault=await ownerService.InspectAsync(PrivilegeTarget.Schema,schema);
            check(schemaDefault.AclEntries.Count==2 && schemaDefault.AclEntries.All(e=>e.Source==PrivilegeAclSource.BuiltInDefault && e.Grantee=="pgms_test"),"Fresh schema NULL ACL expands only built-in owner rights");
            await Sql($"GRANT USAGE ON SCHEMA {qs} TO {qr};");
            var schemaStored=await limited.InspectAsync(PrivilegeTarget.Schema,schema);
            check(schemaStored.AclEntries.Single(e=>e.Grantee==role && e.Privilege=="USAGE") is {Source:PrivilegeAclSource.Stored,GrantOption:false,RoleAvailable:true},"Schema stored ACL preserves exact special-name connected role");
            var tableDefault=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(tableDefault.AclEntries.Count==8 && tableDefault.AclEntries.All(e=>e.Source==PrivilegeAclSource.BuiltInDefault && e.Grantee=="pgms_test"),"Table NULL ACL exposes PG17 built-in owner entries");
            check(!tableDefault.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed && tableDefault.AclEntries.Count>0,"Visible ACL metadata does not grant data authority to limited reader");
            await Sql($"GRANT SELECT ON {table} TO {qr} WITH GRANT OPTION;");
            var direct=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            var grant=direct.AclEntries.Single(e=>e.Grantee==role && e.Privilege=="SELECT");
            check(grant.GrantOption && grant.Grantor=="pgms_test" && grant.GranteeOid!=0 && grant.GrantorOid!=grant.GranteeOid && grant.Source==PrivilegeAclSource.Stored,"Table stored grant retains distinct role OIDs and grantor");
            check(direct.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").GrantOption,"ACL inspection preserves independent effective grant-option calculation");
            await using(var delegateConnection=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString))
            {
                await delegateConnection.OpenAsync();
                await using var command=new NpgsqlCommand($"GRANT SELECT ON {table} TO {qo};",delegateConnection);
                await command.ExecuteNonQueryAsync();
            }
            var delegated=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(delegated.AclEntries.Single(e=>e.Grantee==other && e.Privilege=="SELECT") is {GrantOption:false,RoleAvailable:false} && delegated.AclEntries.Any(e=>e.Grantee==other && e.Grantor==role),"Delegated grant retains non-owner Unicode grantor and unrelated recipient");
            await Sql($"GRANT SELECT ON {table} TO PUBLIC; GRANT INSERT ON {table} TO {qg}; GRANT {qg} TO {qr};");
            var inherited=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(inherited.AclEntries.Single(e=>e.GranteeOid==0 && e.Privilege=="SELECT") is {RoleAvailable:true,GrantOption:false,Source:PrivilegeAclSource.Stored},"Stored PUBLIC row remains distinct from a direct grant with grant option");
            check(inherited.AclEntries.Single(e=>e.Grantee==group && e.Privilege=="INSERT").RoleAvailable,"ACL recipient role can be immediately available through membership");
            await Sql($"GRANT {qg} TO {qr} WITH INHERIT FALSE;");
            var unavailable=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(!unavailable.AclEntries.Single(e=>e.Grantee==group && e.Privilege=="INSERT").RoleAvailable && !unavailable.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="INSERT").Allowed,"INHERIT FALSE preserves the ACL entry while removing immediate role authority");
            await Sql($"GRANT UPDATE(secret) ON {table} TO {qr} WITH GRANT OPTION;");
            var column=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(column.AclEntries.Single(e=>e.Scope=="Column" && e.Column=="secret" && e.Grantee==role) is {ColumnNumber:2,Privilege:"UPDATE",GrantOption:true,Source:PrivilegeAclSource.Stored},"Column ACL retains scope attnum grantor and grant option");
            check(!column.AclEntries.Any(e=>e.Scope=="Column" && e.Privilege=="SELECT") && column.Privileges.Single(p=>p.Column=="secret" && p.Privilege=="SELECT").Allowed,"Inherited table SELECT is not invented as a stored column ACL");
            await Sql($"ALTER TABLE {table} DROP COLUMN secret; ALTER TABLE {table} ADD COLUMN secret text;");
            var recreatedColumn=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(!recreatedColumn.AclEntries.Any(e=>e.Scope=="Column") && recreatedColumn.Privileges.Any(p=>p.Column=="secret" && p.ColumnNumber==3),"Dropped column ACL does not transfer to a same-name replacement");
            string emptyTable=ObjectSql.Relation(schema,"empty");
            await Sql($"CREATE TABLE {emptyTable}(id integer); GRANT CREATE ON SCHEMA {qs} TO {qr}; ALTER TABLE {emptyTable} OWNER TO {qr}; REVOKE ALL ON {emptyTable} FROM {qr};");
            var empty=await limited.InspectAsync(PrivilegeTarget.Table,"empty",schema);
            check(empty.AclEntries.Count==0 && !empty.AclIncomplete,"Explicit empty ACL stays empty instead of using built-in defaults");
            check(!empty.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed && empty.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").GrantOption,"Owner grant authority can exist without a stored ACL row or SELECT data grant");
            string cap=ObjectSql.Relation(schema,"cap");
            await Sql($"CREATE TABLE {cap}({string.Join(",",Enumerable.Range(0,128).Select(i=>$"c{i} integer"))});");
            string columns=string.Join(",",Enumerable.Range(0,126).Select(i=>$"c{i}"));
            await Sql($"GRANT SELECT({columns}),INSERT({columns}),UPDATE({columns}),REFERENCES({columns}) ON {cap} TO {qr};");
            var boundary=await limited.InspectAsync(PrivilegeTarget.Table,"cap",schema);
            check(boundary.AclEntries.Count==512 && !boundary.AclIncomplete,"Exactly512 ACL rows remain complete at the real quota");
            await Sql($"GRANT SELECT(c126) ON {cap} TO {qr};");
            var over=await limited.InspectAsync(PrivilegeTarget.Table,"cap",schema);
            check(over.AclEntries.Count==512 && over.AclTruncated && over.AclIncomplete && !over.ColumnsTruncated,"ACL row513 is detected without exceeding512 retained rows");
            var repeated=await limited.InspectAsync(PrivilegeTarget.Table,"cap",schema);
            check(repeated.AclEntries.SequenceEqual(over.AclEntries),"Limited ACL rows have deterministic attnum and role-OID ordering");
            string scopeOverflow=ObjectSql.Relation(schema,"scope_overflow");
            await Sql($"CREATE TABLE {scopeOverflow}({string.Join(",",Enumerable.Range(0,257).Select(i=>$"c{i} integer"))}); GRANT SELECT(c256) ON {scopeOverflow} TO {qr};");
            var scope=await limited.InspectAsync(PrivilegeTarget.Table,"scope_overflow",schema);
            check(scope.ColumnsTruncated && scope.AclIncomplete && !scope.AclTruncated && !scope.AclEntries.Any(e=>e.Column=="c256"),"Column scope limit marks ACL incomplete even below the entry quota");
            await Sql($"CREATE TABLE {qs}.legacy(id integer); ALTER DEFAULT PRIVILEGES IN SCHEMA {qs} GRANT SELECT ON TABLES TO {qr}; CREATE TABLE {qs}.future(id integer);");
            var legacy=await limited.InspectAsync(PrivilegeTarget.Table,"legacy",schema);
            var future=await limited.InspectAsync(PrivilegeTarget.Table,"future",schema);
            check(legacy.AclEntries.All(e=>e.Source==PrivilegeAclSource.BuiltInDefault) && future.AclEntries.Any(e=>e.Grantee==role && e.Privilege=="SELECT" && e.Source==PrivilegeAclSource.Stored),"Creation-time default privileges become stored ACL and do not change old NULL ACL");
            await Sql($"ALTER DEFAULT PRIVILEGES IN SCHEMA {qs} REVOKE SELECT ON TABLES FROM {qr};");
            var unchanged=await limited.InspectAsync(PrivilegeTarget.Table,"future",schema);
            check(unchanged.AclEntries.SequenceEqual(future.AclEntries),"Changing default-privilege template does not rewrite an existing stored ACL");


        }
        finally
        {
            await Sql($"DROP DATABASE IF EXISTS {qd}; DROP SCHEMA IF EXISTS {qs} CASCADE; DROP OWNED BY {qr}; DROP OWNED BY {qg}; DROP OWNED BY {qo}; DROP ROLE IF EXISTS {qr}; DROP ROLE IF EXISTS {qg}; DROP ROLE IF EXISTS {qo};");
        }
    }
}
