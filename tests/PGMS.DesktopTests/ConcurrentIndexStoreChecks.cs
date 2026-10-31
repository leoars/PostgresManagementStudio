using System.IO;
using System.Text;
using System.Security.Cryptography;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ConcurrentIndexStoreChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="index_store_"+suffix,qs=SqlIdentifier.Quote(schema);
        string root=Path.GetFullPath(Path.Combine("artifacts","index-store-tests",suffix));
        await using var db=new NpgsqlConnection(connection);await db.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,db);await cmd.ExecuteNonQueryAsync();}
        await Sql($"CREATE SCHEMA {qs};CREATE TABLE {qs}.target(id integer,label text);INSERT INTO {qs}.target VALUES(1,'a'),(1,'b')");
        try
        {
            var target=await new ExpressionIndexCatalog(connection).InspectAsync(schema,"target");
            var store=new ConcurrentIndexStore(connection,root);
            var service=new ConcurrentIndexService(connection,store);
            var proposal=new ExpressionIndexEdit(target,"índice_復旧",[new(new("label"))]);
            bool noLease=false;try{await service.CreateAsync(proposal,true);}catch(InvalidOperationException){noLease=true;}
            check(noLease && (await service.InspectAsync(target,proposal.Name)).Kind==ConcurrentIndexObservationKind.Absent,"Durable index journal requires exclusive lease before DDL");
            ConcurrentIndexReceipt receipt;
            using(var lease=store.AcquireLease())
            {
                bool contested=false;try{using var other=new ConcurrentIndexStore(connection,root).AcquireLease();}catch(IOException){contested=true;}
                check(contested,"Index operation lease excludes another store instance");
                var result=await service.CreateAsync(proposal,true);receipt=result.Receipt;
                check(store.Read()==receipt && receipt.OwnerOid!=0 && receipt.ColumnSignature.Length==64,"Durable receipt preserves owner and complete column signature");
                byte[] encrypted=File.ReadAllBytes(Path.Combine(store.DirectoryPath,"pending.index"));
                check(!Encoding.UTF8.GetString(encrypted).Contains(schema) && !Encoding.UTF8.GetString(encrypted).Contains("CREATE INDEX"),"Index receipt is encrypted at rest by DPAPI");
                check(!Directory.EnumerateFiles(store.DirectoryPath,"*.partial").Any(),"Atomic receipt publication leaves no partial file");
                bool overwrite=false;try{await store.RecordAsync(receipt with{OperationId=Guid.NewGuid()},default);}catch(IOException){overwrite=true;}
                check(overwrite && store.Read()==receipt,"Pending index receipt cannot be overwritten");
                bool wrong=false;try{store.Remove(receipt with{OperationId=Guid.NewGuid()});}catch(InvalidDataException){wrong=true;}
                check(wrong && store.Read()==receipt,"Wrong receipt cannot remove pending operation");
            }
            var reopened=new ConcurrentIndexStore(connection,root);
            using(var lease=reopened.AcquireLease())
            {
                check(reopened.Read()==receipt,"New store instance restores exact Unicode receipt");
                var resumed=new ConcurrentIndexService(connection,reopened);
                check((await resumed.InspectReceiptAsync(reopened.Read()!)).Kind==ConcurrentIndexObservationKind.Valid,"Reopened receipt inspects valid index without original in-memory snapshot");
                await Sql($"ALTER TABLE {qs}.target ADD COLUMN extra integer");
                check((await resumed.InspectReceiptAsync(receipt)).Kind==ConcurrentIndexObservationKind.TargetChanged,"Reopened receipt detects changed column signature");
                await Sql($"ALTER TABLE {qs}.target DROP COLUMN extra");
                check((await resumed.InspectReceiptAsync(receipt)).Kind==ConcurrentIndexObservationKind.Valid,"Reinspection recognizes restored live column signature");
                bool malformed=false;try{await resumed.InspectReceiptAsync(receipt with{OwnerOid=0});}catch(ArgumentException){malformed=true;}
                check(malformed,"Incomplete persisted identity refused before resume");
                check((await resumed.InspectReceiptAsync(receipt with{OwnerOid=receipt.OwnerOid+1000000})).Kind==ConcurrentIndexObservationKind.TargetChanged,"Receipt owner mismatch cannot inspect a different authority as original");
                var altered=receipt with{Sql="DROP SCHEMA never_execute CASCADE;"};
                check((await resumed.InspectReceiptAsync(altered)).Kind==ConcurrentIndexObservationKind.Valid,"Receipt SQL is data and is never executed during reconciliation");
                reopened.Remove(receipt);check(reopened.Read() is null,"Matching receipt removal clears only pending metadata");
                check((await resumed.InspectReceiptAsync(receipt)).Kind==ConcurrentIndexObservationKind.Valid,"Clearing receipt does not drop the server index");
                var fresh=await new ExpressionIndexCatalog(connection).InspectAsync(schema,"target");
                bool uncertain=false;try{await resumed.CreateAsync(new(fresh,"invalid",[new(new("id"))],true),true);}catch(ConcurrentIndexUncertainException){uncertain=true;}
                check(uncertain && reopened.Read() is not null,"Concurrent failure preserves durable pending record");
            }
            var recovery=new ConcurrentIndexStore(connection,root);
            using(var lease=recovery.AcquireLease())
            {
                check((await new ConcurrentIndexService(connection,recovery).InspectReceiptAsync(recovery.Read()!)).Kind==ConcurrentIndexObservationKind.Invalid,"Reopened durable record identifies actual invalid residue");
                string path=Path.Combine(recovery.DirectoryPath,"pending.index");byte[] bytes=File.ReadAllBytes(path);bytes[^1]^=0x80;File.WriteAllBytes(path,bytes);
                bool corrupt=false;try{recovery.Read();}catch(CryptographicException){corrupt=true;}
                check(corrupt && File.Exists(path),"Corrupt encrypted record fails closed and is preserved");
            }
        }
        finally{await Sql($"DROP SCHEMA {qs} CASCADE");}
    }
}
