using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PGMS.Desktop;

public sealed record RecoveryDraft(Guid Id, string Title, string Text, SqlFileFormat Format)
{
    public int CommandTimeoutSeconds{get;init;}
    public bool WordWrap{get;init;}
    public bool ShowWhiteSpace{get;init;}
}

/// <summary>One encrypted file per draft; callers own the session lease.</summary>
public sealed class QueryRecoveryStore(string directory)
{
    public const int MaximumCharacters = 262144;
    public const int MaximumDocuments = 32;
    private const int MaximumEncryptedBytes = 3 * 1024 * 1024;
    private sealed record Envelope(int Version, RecoveryDraft Draft);
    // This JSON is only consumed after DPAPI decryption, never embedded in HTML.
    private static readonly JsonSerializerOptions WriteOptions = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public IReadOnlyList<Guid> ListIds() => !Directory.Exists(DirectoryPath) ? [] :
        Directory.EnumerateFiles(DirectoryPath, "*.draft").Select(Path.GetFileNameWithoutExtension)
            .Where(name => Guid.TryParseExact(name, "N", out _)).Select(name => Guid.ParseExact(name!, "N"))
            .Take(MaximumDocuments).ToArray();

    internal sealed record SaveMeasurement(double ValidateMilliseconds,double SerializeMilliseconds,double CompressMilliseconds,double ProtectMilliseconds,double WriteFlushMilliseconds,double RenameMilliseconds,int ClearBytes,int ProtectionBytes,int EncryptedBytes);
    public void Save(RecoveryDraft draft) => SaveCore(draft,false);
    internal SaveMeasurement MeasureSave(RecoveryDraft draft) => SaveCore(draft,true)!;
    private SaveMeasurement? SaveCore(RecoveryDraft draft,bool measure)
    {
        long stamp=measure?System.Diagnostics.Stopwatch.GetTimestamp():0;
        double Phase()
        {
            if(!measure)return 0;
            long now=System.Diagnostics.Stopwatch.GetTimestamp();double milliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(stamp,now).TotalMilliseconds;stamp=now;return milliseconds;
        }
        Validate(draft);
        Directory.CreateDirectory(DirectoryPath);
        RejectLink(DirectoryPath);
        string path = DraftPath(draft.Id);
        if (!File.Exists(path) && Directory.EnumerateFiles(DirectoryPath, "*.draft").Take(MaximumDocuments).Count() >= MaximumDocuments)
            throw new IOException("Recovery supports up to 32 unsaved queries per window. Save some queries to files.");
        if (File.Exists(path)) RejectLink(path);
        double validateMs=Phase();
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, draft), WriteOptions);
        double serializeMs=Phase();
        byte[] payload;
        try{payload=RecoveryPayload.Encode(clear);}
        finally{CryptographicOperations.ZeroMemory(clear);}
        double compressMs=Phase();
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(payload, draft.Id.ToByteArray(), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(payload); }
        double protectMs=Phase();
        double writeMs=0,renameMs=0;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(encrypted); stream.Flush(true); }
            writeMs=Phase();
            File.Move(temporary, path, true);
            renameMs=Phase();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return measure?new(validateMs,serializeMs,compressMs,protectMs,writeMs,renameMs,clear.Length,payload.Length,encrypted.Length):null;
    }

    public RecoveryDraft Read(Guid id)
    {
        RejectLink(DirectoryPath);
        string path = DraftPath(id); RejectLink(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumEncryptedBytes) throw new InvalidDataException("Recovery record exceeds its size limit.");
        byte[] encrypted = new byte[checked((int)stream.Length)]; stream.ReadExactly(encrypted);
        byte[] payload = ProtectedData.Unprotect(encrypted, id.ToByteArray(), DataProtectionScope.CurrentUser);
        byte[]? clear=null;
        try
        {
            clear=RecoveryPayload.Decode(payload);
            var envelope = JsonSerializer.Deserialize<Envelope>(clear);
            if (envelope is null || envelope.Version != 1 || envelope.Draft is null || envelope.Draft.Id != id)
                throw new InvalidDataException("Recovery record has an unsupported format or identity.");
            Validate(envelope.Draft);
            return envelope.Draft;
        }
        finally { if(clear is not null)CryptographicOperations.ZeroMemory(clear);CryptographicOperations.ZeroMemory(payload); }
    }

    public void Remove(Guid id)
    {
        if (!Directory.Exists(DirectoryPath)) return;
        RejectLink(DirectoryPath);
        File.Delete(DraftPath(id));
    }

    private string DraftPath(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Recovery identity is missing.");
        return Path.Combine(DirectoryPath, id.ToString("N") + ".draft");
    }

    private static void Validate(RecoveryDraft draft)
    {
        if (draft.Id == Guid.Empty || draft.Title is null || draft.Title.Length > 128 || draft.Text is null ||
            draft.Text.Length > MaximumCharacters || !Enum.IsDefined(draft.Format) || draft.CommandTimeoutSeconds is < 0 or > 86400)
            throw new InvalidDataException("Recovery supports valid formats and up to 262144 text characters per query. Save larger queries to files.");
        try { var encoding=new UTF8Encoding(false,true); _=encoding.GetByteCount(draft.Text); _=encoding.GetByteCount(draft.Title); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Recovery cannot encode invalid Unicode. Correct the text or save a copy.", ex); }
    }

    internal static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recovery paths cannot be links.");
    }
}
