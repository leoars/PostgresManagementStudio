using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

internal sealed record PendingDefaultPrivilegeCommit(string TransactionId,uint DatabaseOid,DefaultPrivilegeRequest Request,DefaultPrivilegeIdentity Identity,DateTimeOffset RecordedAt);

internal sealed class DefaultPrivilegeCommitStore : IPrivilegeCommitJournal
{
    // Encrypted data only, never embedded in HTML. Keep nested identifier JSON
    // compact without changing its value or the existing envelope format.
    private static readonly JsonSerializerOptions JournalJson = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record Envelope(int Version,PendingDefaultPrivilegeCommit Pending);
    private readonly DefaultPrivilegeRequest? request;
    private readonly DefaultPrivilegeIdentity? identity;
    private readonly string root,path;
    private readonly byte[] entropy;
    private int leased;
    internal DefaultPrivilegeCommitStore(string connection,DefaultPrivilegeRequest? request=null,DefaultPrivilegeIdentity? identity=null)
    {
        this.request=request;this.identity=identity;
        var b=new NpgsqlConnectionStringBuilder(connection);
        // App profiles contain these connection fields. No password, passfile,
        // certificate secret or full connection string is stored or hashed.
        string profileIdentity=JsonSerializer.Serialize(new { Operation="default-privileges",b.Host,b.Port,b.Database,b.Username,Mode=b.SslMode.ToString() });
        entropy=SHA256.HashData(Encoding.UTF8.GetBytes(profileIdentity));
        root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ConnectionProfileStore.DefaultPath)!,"pending-default-permissions"));
        DirectoryPath=Path.Combine(root,Convert.ToHexString(entropy));path=Path.Combine(DirectoryPath,"pending.commit");
    }
    internal string DirectoryPath { get; }
    internal PendingDefaultPrivilegeCommit? Recorded { get; private set; }
    internal IDisposable AcquireLease()
    {
        if(Interlocked.CompareExchange(ref leased,1,0)!=0)throw new IOException("Another permission operation is active. Wait and check the transaction before retrying.");
        try
        {
            Directory.CreateDirectory(root);QueryRecoveryStore.RejectLink(Path.GetDirectoryName(root)!);QueryRecoveryStore.RejectLink(root);
            Directory.CreateDirectory(DirectoryPath);QueryRecoveryStore.RejectLink(DirectoryPath);
            string lease=Path.Combine(DirectoryPath,"operation.lock");if(File.Exists(lease))QueryRecoveryStore.RejectLink(lease);
            return new Lease(this,new FileStream(lease,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None));
        }
        catch {Volatile.Write(ref leased,0);throw;}
    }
    private sealed class Lease(DefaultPrivilegeCommitStore owner,FileStream stream):IDisposable
    {
        public void Dispose(){stream.Dispose();Volatile.Write(ref owner.leased,0);}
    }
    internal PendingDefaultPrivilegeCommit? Read()
    {
        if(!File.Exists(path))return null;
        QueryRecoveryStore.RejectLink(Path.GetDirectoryName(root)!);QueryRecoveryStore.RejectLink(root);QueryRecoveryStore.RejectLink(DirectoryPath);QueryRecoveryStore.RejectLink(path);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        if(stream.Length>65536)throw new InvalidDataException("Pending permission record exceeds its size limit.");
        byte[] encrypted=new byte[checked((int)stream.Length)];stream.ReadExactly(encrypted);
        byte[] clear=ProtectedData.Unprotect(encrypted,entropy,DataProtectionScope.CurrentUser);
        try
        {
            var envelope=JsonSerializer.Deserialize<Envelope>(clear);
            if(envelope is not{Version:1,Pending:{ } pending} ||
                !ulong.TryParse(pending.TransactionId,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var xid) || xid==0 || pending.DatabaseOid==0 || pending.Request is null)
                throw new InvalidDataException("Pending permission record has an invalid format or identity.");
            Validate(pending);return pending;
        }
        finally{CryptographicOperations.ZeroMemory(clear);}
    }
    public Task RecordAsync(PrivilegeCommitReceipt receipt,CancellationToken token)=>Task.Run(()=>
    {
        token.ThrowIfCancellationRequested();RequireLease();
        if(request is null || identity is null)throw new InvalidOperationException("Missing permission request for the pending journal.");
        _=request.ToSql();
        if(Read() is not null)throw new IOException("A pending permission transaction must be checked before another Apply.");
        var pending=new PendingDefaultPrivilegeCommit(receipt.TransactionId,receipt.DatabaseOid,request,identity,DateTimeOffset.UtcNow);
        Validate(pending);
        byte[] clear=JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,pending),JournalJson);byte[] encrypted;
        try{encrypted=ProtectedData.Protect(clear,entropy,DataProtectionScope.CurrentUser);
            if(encrypted.Length>65536)throw new InvalidDataException("Pending permission record exceeds its size limit.");}
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
    internal void Remove(PrivilegeCommitReceipt receipt)
    {
        RequireLease();var pending=Read();if(pending is null)return;
        if(pending.TransactionId!=receipt.TransactionId || pending.DatabaseOid!=receipt.DatabaseOid)throw new InvalidDataException("Pending permission identity changed; record was preserved.");
        File.Delete(path);Recorded=null;
    }
    private static void Validate(PendingDefaultPrivilegeCommit pending)
    {
        _=pending.Request.ToSql();
        var identity=pending.Identity;
        if(identity is null || identity.DatabaseOid!=pending.DatabaseOid || identity.CreatorOid==0 ||
            identity.Target!=pending.Request.Target || identity.Creator!=pending.Request.Creator || identity.Schema!=pending.Request.Schema ||
            (identity.Schema is null)!=(identity.NamespaceOid==0))
            throw new InvalidDataException("Pending default permission scope identity is invalid.");
    }
    private void RequireLease(){if(Volatile.Read(ref leased)==0)throw new InvalidOperationException("A permission journal lease is required.");}
}
