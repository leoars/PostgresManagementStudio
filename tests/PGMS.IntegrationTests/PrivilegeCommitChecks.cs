using Npgsql;
using PGMS.PostgreSql;

internal static class PrivilegeCommitChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="commit_"+suffix,role="commit_role_"+suffix,owner="commit_owner_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        async Task<bool> Has(){await using var cmd=new NpgsqlCommand("SELECT pg_catalog.has_table_privilege(@role,@table,'SELECT')",admin);cmd.Parameters.AddWithValue("role",role);cmd.Parameters.AddWithValue("table",table);return (bool)(await cmd.ExecuteScalarAsync())!;}
        var direct=new PrivilegeService(connection);
        var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",role,"SELECT",Schema:schema);
        try
        {
            await Sql($"CREATE ROLE {qr};CREATE ROLE {SqlIdentifier.Quote(owner)} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {SqlIdentifier.Quote(owner)};CREATE TABLE {table}(id int);INSERT INTO {table} VALUES(123);ALTER TABLE {table} OWNER TO {SqlIdentifier.Quote(owner)}");
            await using(var flags=new NpgsqlCommand("SELECT NOT rolsuper AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolreplication AND NOT rolbypassrls FROM pg_catalog.pg_roles WHERE rolname=@owner",admin))
            {flags.Parameters.AddWithValue("owner",owner);check((bool)(await flags.ExecuteScalarAsync())!,"Commit and status checks execute as an ordinary owner without cluster administration flags");}
            foreach(bool revoke in new[]{false,true})foreach(bool before in new[]{true,false})
            {
                await direct.ApplyAsync(request with{Revoke=!revoke});
                var identity=await direct.InspectTargetAsync(request.Target,request.Name,schema);
                var recipient=(await direct.InspectRecipientAsync(role))!;
                await using var proxy=new CommitAckProxy(before);
                var settings=new NpgsqlConnectionStringBuilder(connection){Username=owner,Port=proxy.Port,Pooling=false,SslMode=SslMode.Disable,Timeout=3};
                var service=new PrivilegeService(settings.ConnectionString);
                PrivilegeCommitReceipt? receipt=null;
                try{await service.ApplyInspectedAsync(request with{Revoke=revoke},identity,recipient);}
                catch(PrivilegeCommitUncertainException ex){receipt=ex.Receipt;}
                string label=(revoke?"REVOKE":"GRANT")+"/"+(before?"before COMMIT":"after server COMMIT");
                check(proxy.FaultInjected && receipt is not null,"Wire disconnect yields an uncertain receipt: "+label);
                check(ulong.TryParse(receipt!.TransactionId,out var xid) && xid>0 && receipt.DatabaseOid>0,"Receipt carries full xid8 and database identity: "+label);
                PrivilegeCommitStatus status=PrivilegeCommitStatus.InProgress;
                for(int i=0;i<50 && status==PrivilegeCommitStatus.InProgress;i++)
                {
                    status=await service.ReconcileCommitAsync(receipt);
                    if(status==PrivilegeCommitStatus.InProgress)await Task.Delay(20);
                }
                check(status==(before?PrivilegeCommitStatus.Aborted:PrivilegeCommitStatus.Committed),"Fresh read reconciles exact transaction status: "+label);
                check(await Has()==(before?revoke:!revoke),"Reconciled outcome matches actual recipient ACL: "+label);
                bool changedContext=false;try{await direct.ReconcileCommitAsync(receipt);}catch(ArgumentException){changedContext=true;}
                check(changedContext,"Receipt cannot be checked using a different service/profile context: "+label);
            }
        }
        finally{await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {SqlIdentifier.Quote(owner)}");}
    }
}
