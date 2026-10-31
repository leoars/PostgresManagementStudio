using Npgsql;
using PGMS.PostgreSql;

internal static class EffectivePrivilegeChecks
{
    public static async Task RunAsync(string connection, Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"), schema="perm_é'\"_"+suffix, role="pgms_perm_"+suffix;
        string group="pgms_perm_group_"+suffix;
        string qGroup=SqlIdentifier.Quote(group);
        string qSchema=SqlIdentifier.Quote(schema), table=ObjectSql.Relation(schema,"item'\""), qRole=SqlIdentifier.Quote(role);
        await using var admin=new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) { await using var command=new NpgsqlCommand(sql,admin); await command.ExecuteNonQueryAsync(); }
        try
        {
            await Sql($"CREATE ROLE {qRole} LOGIN; CREATE ROLE {qGroup}; CREATE SCHEMA {qSchema}; CREATE TABLE {table}(id integer, secret text); INSERT INTO {table} VALUES(1,'synthetic'); GRANT USAGE ON SCHEMA {qSchema} TO {qRole};");
            string limitedConnection=new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString;
            var limited=new EffectivePrivilegeService(limitedConnection);
            var db=await limited.InspectAsync(PrivilegeTarget.Database,"postgres");
            check(db.Role==role && db.Database=="postgres" && db.Privileges.Single(p=>p.Privilege=="CONNECT").Allowed,"Effective database inspection identifies connected limited role and CONNECT");
            check(db.Privileges.Count==3 && db.SchemaUsage is null && db.RowSecurityActive is null,"Database snapshot distinguishes non-applicable table context");
            var scope=await limited.InspectAsync(PrivilegeTarget.Schema,schema);
            check(scope.Name==schema && scope.Privileges.Single(p=>p.Privilege=="USAGE").Allowed && !scope.Privileges.Single(p=>p.Privilege=="CREATE").Allowed,"Schema inspection preserves exact special name and effective USAGE without CREATE");
            var denied=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(denied.Name=="item'\"" && denied.Schema==schema && denied.Privileges.All(p=>!p.Allowed),"Role can inspect table grants without gaining table access");
            check(denied.SchemaUsage==true && denied.RowSecurityEnabled==false && denied.RowSecurityActive==false,"Table snapshot keeps schema and RLS flags distinct");
            check(denied.Privileges.Any(p=>p.Privilege=="MAINTAIN"),"PG17 table inspection includes MAINTAIN");
            await Sql($"GRANT SELECT ON {table} TO {qRole} WITH GRANT OPTION;");
            var granted=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(granted.Oid==denied.Oid && granted.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT") is {Allowed:true,GrantOption:true},"Reinspection observes effective SELECT with grant option on same OID");
            check(granted.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="INSERT") is {Allowed:false,GrantOption:false},"SELECT grant does not invent INSERT authority");
            check(granted.SessionUser==role && granted.ObservedAt<=DateTimeOffset.UtcNow && granted.ObservedAt>DateTimeOffset.UtcNow.AddMinutes(-1),"Snapshot records fresh observation and session identity");
            check(granted.Privileges.Single(p=>p.Column=="id" && p.Privilege=="SELECT") is {Allowed:true,GrantOption:true},"Column privilege includes whole-table grant option");
            await Sql($"GRANT UPDATE(secret) ON {table} TO {qRole};");
            var column=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(!column.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="UPDATE").Allowed && column.Privileges.Single(p=>p.Column=="secret" && p.Privilege=="UPDATE").Allowed && !column.Privileges.Single(p=>p.Column=="id" && p.Privilege=="UPDATE").Allowed,"Column-only UPDATE is distinct from whole-table authority");
            await Sql($"REVOKE SELECT ON {table} FROM {qRole}; GRANT SELECT ON {table} TO PUBLIC;");
            var publicGrant=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(publicGrant.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT") is {Allowed:true,GrantOption:false},"Effective access includes PUBLIC after direct grant is removed");
            await Sql($"REVOKE SELECT ON {table} FROM PUBLIC; GRANT INSERT ON {table} TO {qGroup}; GRANT {qGroup} TO {qRole};");
            var inherited=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(inherited.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="INSERT").Allowed,"Effective access includes inherited role grant");
            await Sql($"GRANT {qGroup} TO {qRole} WITH INHERIT FALSE;");
            var noinherit=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(!noinherit.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="INSERT").Allowed,"Membership INHERIT FALSE does not report immediate authority");
            await Sql($"GRANT SELECT ON {table} TO {qRole}; REVOKE USAGE ON SCHEMA {qSchema} FROM {qRole};");
            var noUsage=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(noUsage.SchemaUsage==false && noUsage.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed,"SELECT and missing schema USAGE remain separate facts");
            await Sql($"GRANT USAGE ON SCHEMA {qSchema} TO {qRole}; ALTER TABLE {table} ENABLE ROW LEVEL SECURITY; ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
            var rls=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(rls.RowSecurityEnabled==true && rls.RowSecurityForced==true && rls.RowSecurityActive==true,"Limited role sees enabled forced and active RLS context");
            await using(var reader=new NpgsqlConnection(limitedConnection))
            {
                await reader.OpenAsync();
                await using var command=new NpgsqlCommand($"SELECT count(*) FROM {table}",reader);
                check((long)(await command.ExecuteScalarAsync())! == 0,"Effective SELECT does not bypass default-deny RLS");
            }
            await Sql($"ALTER TABLE {table} DROP COLUMN secret; ALTER TABLE {table} ADD COLUMN {SqlIdentifier.Quote("á'\"")} integer;");
            var dropped=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(!dropped.Privileges.Any(p=>p.Column=="secret") && dropped.Privileges.Any(p=>p.Column=="á'\"" && p.ColumnNumber==3),"Column inspection skips dropped attnum and preserves special names");
            await Sql($"DROP TABLE {table}; CREATE TABLE {table}({string.Join(",",Enumerable.Range(0,256).Select(i=>$"c{i} integer"))});");
            var boundary=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(boundary.Oid!=denied.Oid && boundary.Owner=="pgms_test","Fresh inspection reports replacement object identity and actual owner");
            check(!boundary.ColumnsTruncated && boundary.Privileges.Count(p=>p.Scope=="Column")==1024,"Exactly256 columns produce complete bounded details");
            await Sql($"ALTER TABLE {table} ADD COLUMN omitted integer;");
            var wide=await limited.InspectAsync(PrivilegeTarget.Table,"item'\"",schema);
            check(wide.ColumnsTruncated && wide.Privileges.Count(p=>p.Scope=="Column")==1024 && !wide.Privileges.Any(p=>p.Column=="omitted"),"Column257 marks explicit truncation without partial detail rows");
            using(var cancellation=new CancellationTokenSource())
            {
                cancellation.Cancel(); bool canceled=false;
                try { await new EffectivePrivilegeService("invalid connection").InspectAsync(PrivilegeTarget.Table,"x",token:cancellation.Token); } catch(OperationCanceledException){canceled=true;}
                check(canceled,"Pre-canceled inspection returns before parsing connection or loading metadata");
            }
            await Sql($"CREATE VIEW {ObjectSql.Relation(schema,"view")} AS SELECT 1 AS x;");
            bool unsupported=false;
            try { await limited.InspectAsync(PrivilegeTarget.Table,"view",schema); } catch(InvalidOperationException){unsupported=true;}
            check(unsupported,"Table inspection refuses a view instead of claiming table coverage");
            bool missing=false;try {await limited.InspectAsync(PrivilegeTarget.Table,"absent",schema);}catch(InvalidOperationException){missing=true;}
            check(missing,"Missing target is reported instead of an empty successful snapshot");
            bool invalid=false;try {await limited.InspectAsync((PrivilegeTarget)99,"x");}catch(ArgumentException){invalid=true;}
            check(invalid,"Unknown inspection target is refused before connection");
        }
        finally { await Sql($"DROP SCHEMA IF EXISTS {qSchema} CASCADE; DROP OWNED BY {qRole}; DROP OWNED BY {qGroup}; DROP ROLE IF EXISTS {qRole}; DROP ROLE IF EXISTS {qGroup};"); }
    }
}
