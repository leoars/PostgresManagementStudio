using System.IO;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class DefaultPrivilegeJournalChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),owner="dj_owner_"+suffix,role="dj_role_"+suffix,schema="dj_"+suffix;
        string qo=SqlIdentifier.Quote(owner),qr=SqlIdentifier.Quote(role),qs=SqlIdentifier.Quote(schema);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr};CREATE SCHEMA {qs} AUTHORIZATION {qo}");
            var request=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Tables,owner,role,"SELECT",schema);
            foreach(bool before in new[]{true,false})
            {
                await Sql((request with{Revoke=true}).ToSql());
                await using var proxy=new CommitAckProxy(before){Armed=false};
                string settings=new NpgsqlConnectionStringBuilder(connection){Username=owner,Port=proxy.Port,Pooling=false,SslMode=SslMode.Disable}.ConnectionString;
                var snapshot=await new DefaultPrivilegeCatalog(settings).InspectAsync(request.Target,owner,schema);
                var recipient=(await new PrivilegeService(settings).InspectRecipientAsync(role))!;
                var store=new DefaultPrivilegeCommitStore(settings,request,snapshot.Identity);
                var service=new DefaultPrivilegeService(settings,store);
                using(var lease=store.AcquireLease())
                {
                    bool busy=false;try{using var other=new DefaultPrivilegeCommitStore(settings).AcquireLease();}catch(IOException){busy=true;}
                    check(busy,"Default journal lease excludes another window while applying: "+before);
                    proxy.Armed=true;bool uncertain=false;
                    try{await service.ApplyInspectedAsync(request,snapshot,recipient);}catch(PrivilegeCommitUncertainException){uncertain=true;}
                    check(uncertain && proxy.FaultInjected,"Default COMMIT acknowledgement loss yields uncertain receipt: "+before);
                }
                var reopened=new DefaultPrivilegeCommitStore(settings);var pending=reopened.Read()!;
                check(pending.Request==request && pending.Identity==snapshot.Identity,"Default journal restores exact policy and captured creator/schema identity: "+before);
                string file=Path.Combine(reopened.DirectoryPath,"pending.commit");byte[] original=File.ReadAllBytes(file);
                check(!Encoding.UTF8.GetString(original).Contains(owner,StringComparison.Ordinal),"Default journal encrypts identifying fields with DPAPI: "+before);
                var checking=new DefaultPrivilegeService(settings);var receipt=checking.ResumeCommitCheck(pending.TransactionId,pending.DatabaseOid);
                proxy.RefuseNewConnections=true;
                bool unavailable=false;try{unavailable=await checking.ReconcileCommitAsync(receipt)==PrivilegeCommitStatus.Unknown;}catch(NpgsqlException){unavailable=true;}
                check(unavailable && File.Exists(file),"Unavailable cluster preserves pending default receipt: "+before);
                proxy.RefuseNewConnections=false;
                PrivilegeCommitStatus status=PrivilegeCommitStatus.Unknown;
                for(int i=0;i<30 && status==PrivilegeCommitStatus.Unknown;i++){status=await checking.ReconcileCommitAsync(receipt);if(status==PrivilegeCommitStatus.Unknown)await Task.Delay(20);}
                check(status==(before?PrivilegeCommitStatus.Aborted:PrivilegeCommitStatus.Committed),"Restored default receipt confirms exact original outcome without replay: "+before);
                var actual=await new DefaultPrivilegeCatalog(settings).InspectAsync(request.Target,owner,schema);
                check(actual.Entries.Any(e=>e.Recipient==role && e.Privilege=="SELECT")==!before,"Default reconciliation preserves actual committed policy only: "+before);
                using(var lease=reopened.AcquireLease())
                {
                    byte[] corrupt=original.ToArray();corrupt[^1]^=0x20;File.WriteAllBytes(file,corrupt);
                    bool refused=false;try{reopened.Read();}catch(CryptographicException){refused=true;}
                    check(refused && File.Exists(file),"Corrupt default journal is refused and preserved: "+before);
                    File.WriteAllBytes(file,original);
                    refused=false;try{reopened.Remove(checking.ResumeCommitCheck(pending.TransactionId,pending.DatabaseOid+1));}catch(InvalidDataException){refused=true;}
                    check(refused && File.Exists(file),"Default cleanup refuses a different database identity: "+before);
                    reopened.Remove(receipt);check(reopened.Read() is null,"Confirmed default receipt is removed under lease: "+before);
                }
            }
        }
        finally{await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP OWNED BY {qo};DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {qo}");}
    }
}
