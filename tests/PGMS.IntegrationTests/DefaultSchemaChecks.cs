using Npgsql;
using PGMS.PostgreSql;

internal static class DefaultSchemaChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        string usage=f.Schema+"_u",both=f.Schema+"_b",create=f.Schema+"_c",none=f.Schema+"_n",foreign=f.Schema+"_f";
        string qu=SqlIdentifier.Quote(usage),qb=SqlIdentifier.Quote(both),qc=SqlIdentifier.Quote(create),qn=SqlIdentifier.Quote(none),qf=SqlIdentifier.Quote(foreign);
        check(await f.Apply(DefaultPrivilegeTarget.Schemas,"USAGE"),"Creator can configure schema defaults without database CREATE");
        bool denied=false;try{await f.Owner($"CREATE SCHEMA {qu}");}catch(PostgresException e)when(e.SqlState=="42501"){denied=true;}
        check(denied,"Schema defaults do not grant creator database CREATE");
        await f.Admin($"GRANT CREATE ON DATABASE {SqlIdentifier.Quote(new NpgsqlConnectionStringBuilder(connection).Database!)} TO {f.QO}");
        await f.Owner($"CREATE SCHEMA {qu};CREATE TABLE {qu}.data(value integer);INSERT INTO {qu}.data VALUES(1);GRANT SELECT ON {qu}.data TO {f.QR}");
        check(await f.Has("has_schema_privilege",usage,"USAGE") && !await f.Has("has_schema_privilege",usage,"CREATE"),"New schema materializes only configured USAGE");
        check(!await f.Has("has_schema_privilege",f.Schema,"USAGE"),"Global schema default does not modify earlier schema");
        check(await f.ReaderCount($"SELECT count(*) FROM {qu}.data")==1,"Schema default USAGE permits access to separately granted table");
        check(await f.Denied($"CREATE TABLE {qu}.denied(value integer)"),"Schema USAGE does not permit object creation");
        check(await f.Apply(DefaultPrivilegeTarget.Schemas,"CREATE"),"Global schema policy adds CREATE independently");
        await f.Owner($"CREATE SCHEMA {qb}");await f.Reader($"CREATE TABLE {qb}.reader_table(value integer);INSERT INTO {qb}.reader_table VALUES(1)");
        check(await f.ReaderCount($"SELECT count(*) FROM {qb}.reader_table")==1,"Recipient creates and uses objects in future schema with both rights");
        check(await f.Denied($"CREATE TABLE {qu}.still_denied(value integer)"),"Adding CREATE default does not upgrade existing schema");
        await f.Apply(DefaultPrivilegeTarget.Schemas,"USAGE",revoke:true);await f.Owner($"CREATE SCHEMA {qc}");
        check(await f.Has("has_schema_privilege",create,"CREATE") && !await f.Has("has_schema_privilege",create,"USAGE"),"Revoking USAGE template preserves independent CREATE for later schemas");
        await f.Apply(DefaultPrivilegeTarget.Schemas,"CREATE",revoke:true);await f.Owner($"CREATE SCHEMA {qn}");
        check(!await f.Has("has_schema_privilege",none,"CREATE") && !await f.Has("has_schema_privilege",none,"USAGE"),"Removing both defaults leaves later schema private");
        check(await f.Has("has_schema_privilege",both,"CREATE") && await f.Has("has_schema_privilege",both,"USAGE"),"Revoking templates preserves existing schema rights");
        var restored=await new DefaultPrivilegeCatalog(f.CreatorConnection).InspectAsync(DefaultPrivilegeTarget.Schemas,f.Creator);
        check(restored.GlobalPolicyOid==0 && restored.SchemaPolicyOid==0 && restored.Identity.NamespaceOid==0,"Returning to built-in schema defaults removes global policy row");
        await f.Admin($"CREATE SCHEMA {qf}");
        try
        {
            var request=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Tables,f.Creator,f.Recipient,"SELECT",foreign);
            var snapshot=await new DefaultPrivilegeCatalog(f.CreatorConnection).InspectAsync(request.Target,f.Creator,foreign);
            var recipient=(await new PrivilegeService(f.CreatorConnection).InspectRecipientAsync(f.Recipient,false))!;
            check(await new DefaultPrivilegeService(f.CreatorConnection).ApplyInspectedAsync(request,snapshot,recipient),"Creator can configure table template in schema without CREATE authority");
            denied=false;try{await f.Owner($"CREATE TABLE {qf}.denied(value integer)");}catch(PostgresException e)when(e.SqlState=="42501"){denied=true;}
            check(denied,"Configuring schema-scoped template does not authorize object creation there");
        }
        finally{await f.Admin($"DROP SCHEMA {qf} CASCADE");}
    }
}
