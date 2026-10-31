using System.IO;
using System.Text.Json;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class RoutineBoundaryChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string schema="b"+Guid.NewGuid().ToString("N")+new string('"',30),type=new string('"',63),role="bound_"+Guid.NewGuid().ToString("N");
        string qs=SqlIdentifier.Quote(schema),qt=SqlIdentifier.Quote(type),qr=SqlIdentifier.Quote(role);
        string signature=$"{qs}.target("+string.Join(",",Enumerable.Repeat(qs+"."+qt,100))+")";
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        try
        {
            await Sql($"CREATE ROLE {qr};CREATE SCHEMA {qs};CREATE DOMAIN {qs}.{qt} AS integer;CREATE FUNCTION {signature} RETURNS integer LANGUAGE sql AS 'SELECT 1';REVOKE EXECUTE ON FUNCTION {signature} FROM PUBLIC");
            await using var lookup=new NpgsqlCommand("SELECT @signature::regprocedure::oid",admin);lookup.Parameters.AddWithValue("signature",signature);
            uint oid=(uint)(await lookup.ExecuteScalarAsync())!;
            var selected=await new RoutinePrivilegeCatalog(connection).InspectAsync(oid);
            check(selected.InputTypeOids.Split(' ').Length==100,"Catalog captures PostgreSQL's 100 input arguments with escaped 63-byte type identifiers");
            check(selected.ArgumentTypesJson.Length<=32768,"Canonical argument JSON remains within its own restore limit at the supported catalog boundary");
            var restored=JsonSerializer.Deserialize<RoutinePrivilegeSelection>(JsonSerializer.Serialize(selected));
            check(restored==selected && restored.QualifiedSignature==selected.QualifiedSignature,"Maximum escaped argument identity roundtrips without truncation or SQL changes");
            var request=new PrivilegeRequest(PrivilegeTarget.Routine,"target",role,"EXECUTE",Schema:schema){Routine=selected};
            var store=new PrivilegeCommitStore(connection,request);
            using(var lease=store.AcquireLease())
            {
                check(await new PrivilegeService(connection,store).ApplyCurrentAsync(request),"Durable Apply accepts a real 100-argument routine with maximally escaped type name");
                var pending=store.Read()!;
                check(pending.Request==request && pending.Request.ToSql()==request.ToSql(),"Encrypted journal restores the full boundary signature by immutable identity");
                check(new FileInfo(Path.Combine(store.DirectoryPath,"pending.commit")).Length<=65536,"Boundary journal stays readable under the 64 KiB encrypted limit");
                var service=new PrivilegeService(connection);var receipt=service.ResumeCommitCheck(pending.TransactionId,pending.DatabaseOid);
                check(await service.ReconcileCommitAsync(receipt)==PrivilegeCommitStatus.Committed,"Boundary journal reconciles the actual committed transaction");
                store.Remove(receipt);
            }
            await using var has=new NpgsqlCommand("SELECT pg_catalog.has_function_privilege(@role,@oid,'EXECUTE')",admin);has.Parameters.AddWithValue("role",role);has.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,oid);
            check((bool)(await has.ExecuteScalarAsync())!,"Boundary grant changes the selected routine ACL in PostgreSQL");
            using var document=JsonDocument.Parse(selected.ArgumentTypesJson);
            string tooMany="["+string.Join(",",Enumerable.Repeat(document.RootElement[0].GetRawText(),101))+"]";
            bool rejected=false;try{_=new RoutinePrivilegeSelection(selected.DatabaseOid,oid,selected.NamespaceOid,schema,"target","f",tooMany);}catch(ArgumentException){rejected=true;}
            check(rejected,"Persisted selection above the supported 100-argument limit is refused");
            await new PrivilegeService(connection).ApplyCurrentAsync(request with{Revoke=true});
            check(!(bool)(await has.ExecuteScalarAsync())!,"Boundary signature also revokes the exact routine privilege");
        }
        finally{await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr}");}
    }
}
