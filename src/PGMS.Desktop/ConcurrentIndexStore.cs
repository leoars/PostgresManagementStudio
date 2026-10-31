using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

internal sealed class ConcurrentIndexStore : IConcurrentIndexJournal
{
    // Encrypted data only, never embedded in HTML. Keep nested identifier JSON
    // compact without changing its value or the existing envelope format.
    private static readonly JsonSerializerOptions JournalJson = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record Envelope(int Version,ConcurrentIndexReceipt Pending);
    private readonly string root,path;
    private readonly byte[] entropy;
    private int leased;
    internal ConcurrentIndexStore(string connection, string? testRoot=null)
    {
        var b=new NpgsqlConnectionStringBuilder(connection);
        // App profiles contain these connection fields. No password, passfile,
        // certificate secret or full connection string is stored or hashed.
        string profileIdentity=JsonSerializer.Serialize(new { Operation="concurrent-index",b.Host,b.Port,b.Database,b.Username,Mode=b.SslMode.ToString() });
        entropy=SHA256.HashData(Encoding.UTF8.GetBytes(profileIdentity));
        root=Path.GetFullPath(testRoot ?? Path.Combine(Path.GetDirectoryName(ConnectionProfileStore.DefaultPath)!,"pending-index-operations"));
        DirectoryPath=Path.Combine(root,Convert.ToHexString(entropy));path=Path.Combine(DirectoryPath,"pending.index");
    }
    internal string DirectoryPath { get; }
    internal ConcurrentIndexReceipt? Recorded { get; private set; }
    internal IDisposable AcquireLease()
    {
        if(Interlocked.CompareExchange(ref leased,1,0)!=0)throw new IOException("Another index operation is active. Wait and check the transaction before retrying.");
        try
        {
            Directory.CreateDirectory(root);QueryRecoveryStore.RejectLink(Path.GetDirectoryName(root)!);QueryRecoveryStore.RejectLink(root);
            Directory.CreateDirectory(DirectoryPath);QueryRecoveryStore.RejectLink(DirectoryPath);
            string lease=Path.Combine(DirectoryPath,"operation.lock");if(File.Exists(lease))QueryRecoveryStore.RejectLink(lease);
            return new Lease(this,new FileStream(lease,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None));
        }
        catch {Volatile.Write(ref leased,0);throw;}
    }
    private sealed class Lease(ConcurrentIndexStore owner,FileStream stream):IDisposable
    {
        public void Dispose(){stream.Dispose();Volatile.Write(ref owner.leased,0);}
    }
    internal ConcurrentIndexReceipt? Read()
    {
        if(!File.Exists(path))return null;
        QueryRecoveryStore.RejectLink(Path.GetDirectoryName(root)!);QueryRecoveryStore.RejectLink(root);QueryRecoveryStore.RejectLink(DirectoryPath);QueryRecoveryStore.RejectLink(path);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        if(stream.Length>65536)throw new InvalidDataException("Pending index record exceeds its size limit.");
        byte[] encrypted=new byte[checked((int)stream.Length)];stream.ReadExactly(encrypted);
        byte[] clear=ProtectedData.Unprotect(encrypted,entropy,DataProtectionScope.CurrentUser);
        try
        {
            var envelope=JsonSerializer.Deserialize<Envelope>(clear);
            if(envelope is not{Version:1,Pending:{ } pending})
                throw new InvalidDataException("Pending index record has an invalid format.");
            Validate(pending);return pending;
        }
        finally{CryptographicOperations.ZeroMemory(clear);}
    }
    public Task RecordAsync(ConcurrentIndexReceipt receipt,CancellationToken token)=>Task.Run(()=>
    {
        token.ThrowIfCancellationRequested();RequireLease();
        if(Read() is not null)throw new IOException("A pending index operation must be reviewed before another Apply.");
        var pending=receipt;
        Validate(pending);
        byte[] clear=JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,pending),JournalJson);byte[] encrypted;
        try{encrypted=ProtectedData.Protect(clear,entropy,DataProtectionScope.CurrentUser);
            if(encrypted.Length>65536)throw new InvalidDataException("Pending index record exceeds its size limit.");}
        finally{CryptographicOperations.ZeroMemory(clear);}
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            token.ThrowIfCancellationRequested();
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {stream.Write(encrypted);stream.Flush(true);}
            token.ThrowIfCancellationRequested();
            File.Move(temporary,path,false);Recorded=pending;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    },token);
    internal void Remove(ConcurrentIndexReceipt receipt)
    {
        RequireLease();var pending=Read();if(pending is null)return;
        if(pending.OperationId!=receipt.OperationId || pending.DatabaseOid!=receipt.DatabaseOid)throw new InvalidDataException("Pending index identity changed; record was preserved.");
        File.Delete(path);Recorded=null;
    }
    internal void Archive(ConcurrentIndexReceipt receipt)
    {
        RequireLease();var pending=Read()??throw new InvalidOperationException("No pending index receipt.");
        if(pending.OperationId!=receipt.OperationId || pending.DatabaseOid!=receipt.DatabaseOid)
            throw new InvalidDataException("Pending identity changed; record preserved.");
        File.Move(path,Path.Combine(DirectoryPath,receipt.OperationId.ToString("N")+".reviewed"),false);
        Recorded=null;
    }
    private static void Validate(ConcurrentIndexReceipt pending)
    {
        if(pending.OperationId==Guid.Empty || pending.DatabaseOid==0 || pending.NamespaceOid==0 || pending.TableOid==0 || pending.OwnerOid==0 ||
            pending.ColumnSignature is null || pending.ColumnSignature.Length!=64 || !pending.ColumnSignature.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(pending.Sql) || pending.RecordedAt==default)
            throw new InvalidDataException("Pending index identity is incomplete.");
        _=SqlIdentifier.Quote(pending.Schema);_=SqlIdentifier.Quote(pending.Table);_=SqlIdentifier.Quote(pending.Index);
    }
    private void RequireLease(){if(Volatile.Read(ref leased)==0)throw new InvalidOperationException("An index journal lease is required.");}
}
