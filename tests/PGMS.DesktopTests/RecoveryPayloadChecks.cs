using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PGMS.Desktop;

internal static class RecoveryPayloadChecks
{
    internal static void Run(Action<bool,string> check)
    {
        byte[] clear=Encoding.UTF8.GetBytes(new string('á',QueryRecoveryStore.MaximumCharacters));
        byte[] compressed=RecoveryPayload.Encode(clear);
        check(compressed.Length<8192 && RecoveryPayload.Decode(compressed).SequenceEqual(clear),"Bounded compressed container roundtrips maximum Unicode payload");
        byte[] random=RandomNumberGenerator.GetBytes(256),encoded=RecoveryPayload.Encode(random);
        check(encoded.SequenceEqual(random) && RecoveryPayload.Decode(encoded).SequenceEqual(random),"Incompressible payload uses unchanged legacy bytes without expansion");
        bool oversized=false;try{RecoveryPayload.Encode(new byte[RecoveryPayload.MaximumClearBytes+1]);}catch(InvalidDataException){oversized=true;}
        check(oversized,"Compression refuses clear payload above allocation budget");
        foreach(int declared in new[]{-1,0,RecoveryPayload.MaximumClearBytes+1,clear.Length-1,clear.Length+1})
        {
            byte[] malformed=compressed.ToArray();BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(8,4),declared);
            bool refused=false;try{RecoveryPayload.Decode(malformed);}catch(InvalidDataException){refused=true;}
            check(refused,"Compressed recovery refuses invalid or inexact declared size: "+declared);
        }
        foreach(byte[] malformed in new[]{compressed[..8],compressed[..^1],compressed.Concat(new byte[]{0}).ToArray()})
        {bool refused=false;try{RecoveryPayload.Decode(malformed);}catch(InvalidDataException){refused=true;}check(refused,"Compressed recovery refuses incomplete header/stream or trailing data");}
        string root=Path.GetFullPath(Path.Combine("artifacts","recovery-container",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        var store=new QueryRecoveryStore(root);var draft=new RecoveryDraft(Guid.NewGuid(),"controls",new string('\0',QueryRecoveryStore.MaximumCharacters),SqlFileFormat.Utf8);
        store.Save(draft);check(store.Read(draft.Id)==draft,"Maximum escaped JSON expansion remains within bounded compressed reader");
        string path=Path.Combine(root,draft.Id.ToString("N")+".draft");byte[] bad=ProtectedData.Protect(compressed[..^1],draft.Id.ToByteArray(),DataProtectionScope.CurrentUser);File.WriteAllBytes(path,bad);
        bool rejected=false;try{store.Read(draft.Id);}catch(InvalidDataException){rejected=true;}
        check(rejected && File.ReadAllBytes(path).SequenceEqual(bad),"Authenticated but malformed compressed record is retained for recovery diagnosis");
    }
}
