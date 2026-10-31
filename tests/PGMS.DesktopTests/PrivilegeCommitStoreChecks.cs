using System.IO;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class PrivilegeCommitStoreChecks
{
    private sealed class StopAfterRecord(PrivilegeCommitStore store):IPrivilegeCommitJournal
    {
        public async Task RecordAsync(PrivilegeCommitReceipt receipt,CancellationToken token)
        {await store.RecordAsync(receipt,token);throw new IOException("Synthetic failure after durable intent, before COMMIT.");}
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="journal_"+suffix,role="journal_role_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        async Task<bool> Has(){await using var cmd=new NpgsqlCommand("SELECT pg_catalog.has_table_privilege(@role,@table,'SELECT')",admin);cmd.Parameters.AddWithValue("role",role);cmd.Parameters.AddWithValue("table",table);return (bool)(await cmd.ExecuteScalarAsync())!;}
        var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",role,"SELECT",Schema:schema);
        var store=new PrivilegeCommitStore(connection,request);
        string file=Path.Combine(store.DirectoryPath,"pending.commit");
        try
        {
            await Sql($"CREATE ROLE {qr};CREATE SCHEMA {qs};CREATE TABLE {table}(id int)");
            check(store.Read() is null,"New permission journal has no fabricated pending intent");
            using(var lease=store.AcquireLease())
            {
                bool concurrent=false;try{using var blocked=new PrivilegeCommitStore(connection).AcquireLease();}catch(IOException){concurrent=true;}
                check(concurrent,"Connection-scoped lease excludes a second journal instance");
                bool stopped=false;try{await new PrivilegeService(connection,new StopAfterRecord(store)).ApplyCurrentAsync(request);}catch(IOException){stopped=true;}
                check(stopped && store.Recorded is not null,"Durable journal hook can stop COMMIT after recording intent");
                check(!await Has(),"Failure after durable record rolls back the actual ACL write");
            }
            var reopened=new PrivilegeCommitStore(connection);
            var pending=reopened.Read()!;
            check(pending.Request==request && pending.DatabaseOid>0 && ulong.TryParse(pending.TransactionId,out _),"Reopened journal restores exact request and full transaction identity");
            byte[] original=File.ReadAllBytes(file);
            check(!Encoding.UTF8.GetString(original).Contains(role,StringComparison.Ordinal) && !Encoding.UTF8.GetString(original).Contains("GRANT",StringComparison.Ordinal),"DPAPI journal file contains no plaintext request identifiers or SQL");
            var changedPassword=new NpgsqlConnectionStringBuilder(connection){Password="synthetic_journal_secret_"+suffix};
            check(new PrivilegeCommitStore(changedPassword.ConnectionString).DirectoryPath==store.DirectoryPath,"Journal profile identity excludes credentials and survives password rotation");
            var otherProfile=new PrivilegeCommitStore(new NpgsqlConnectionStringBuilder(connection){Username="journal_other_"+suffix}.ConnectionString);
            using(var otherLease=otherProfile.AcquireLease())
            {
                File.WriteAllBytes(Path.Combine(otherProfile.DirectoryPath,"pending.commit"),original);
                bool differentProfile=false;try{otherProfile.Read();}catch(CryptographicException){differentProfile=true;}
                check(differentProfile,"Copied encrypted intent cannot be imported under a different connection profile");
            }
            var service=new PrivilegeService(connection);
            var receipt=service.ResumeCommitCheck(pending.TransactionId,pending.DatabaseOid);
            check(await service.ReconcileCommitAsync(receipt)==PrivilegeCommitStatus.Aborted,"Restored durable intent reconciles as aborted after pre-COMMIT failure");
            bool missingLease=false;try{reopened.Remove(receipt);}catch(InvalidOperationException){missingLease=true;}
            check(missingLease && File.Exists(file),"Journal cleanup without a lease preserves the pending record");
            using(var lease=reopened.AcquireLease())
            {
                bool wrong=false;try{reopened.Remove(service.ResumeCommitCheck(pending.TransactionId,pending.DatabaseOid+1));}catch(InvalidDataException){wrong=true;}
                check(wrong && File.Exists(file),"Journal cleanup refuses a different database identity");
                byte[] corrupt=original.ToArray();corrupt[^1]^=0x40;File.WriteAllBytes(file,corrupt);
                bool tampered=false;try{reopened.Read();}catch(CryptographicException){tampered=true;}
                check(tampered && File.Exists(file),"Tampered DPAPI journal is refused and preserved");
                File.WriteAllBytes(file,original);
                using(var oversized=new FileStream(file,FileMode.Open,FileAccess.Write)){oversized.SetLength(65537);}
                bool bounded=false;try{reopened.Read();}catch(InvalidDataException){bounded=true;}
                check(bounded,"Oversized pending journal is refused before DPAPI or JSON allocation");
                File.WriteAllBytes(file,original);reopened.Remove(receipt);
                check(reopened.Read() is null,"Confirmed aborted intent can be removed under its lease");
            }
            using(var lease=store.AcquireLease())
            {
                check(await new PrivilegeService(connection,store).ApplyCurrentAsync(request),"Journal intent is durable before a successful real COMMIT");
            }
            pending=new PrivilegeCommitStore(connection).Read()!;service=new PrivilegeService(connection);receipt=service.ResumeCommitCheck(pending.TransactionId,pending.DatabaseOid);
            check(await service.ReconcileCommitAsync(receipt)==PrivilegeCommitStatus.Committed && await Has(),"New service restores and confirms a committed durable intent without replay");
            await new PrivilegeService(connection).ApplyAsync(request with{Revoke=true});
            check(await service.ReconcileCommitAsync(receipt)==PrivilegeCommitStatus.Committed && !await Has(),"Commit reconciliation remains historical when another operation changes current ACL");
            using(var lease=store.AcquireLease()){store.Remove(receipt);}
            check(store.Read() is null,"Confirmed committed intent cleanup leaves no pending entry");
            bool invalid=false;try{service.ResumeCommitCheck("not-an-xid",pending.DatabaseOid);}catch(ArgumentException){invalid=true;}
            check(invalid,"Restoring malformed transaction identity is refused before a connection");
        }
        finally{await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr}");}
    }
}
