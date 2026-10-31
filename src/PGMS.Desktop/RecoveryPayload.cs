using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace PGMS.Desktop;

// This container is always inside DPAPI. Legacy JSON remains readable.
internal static class RecoveryPayload
{
    internal const int MaximumClearBytes=2*1024*1024;
    private static ReadOnlySpan<byte> Magic=>"PGMSRC2\0"u8;
    internal static byte[] Encode(ReadOnlySpan<byte> clear)
    {
        if(clear.Length>MaximumClearBytes)throw new InvalidDataException("Recovery JSON exceeds its size limit.");
        byte[] buffer=new byte[BrotliEncoder.GetMaxCompressedLength(clear.Length)];
        try
        {
            if(!BrotliEncoder.TryCompress(clear,buffer,out int written,1,22))throw new InvalidDataException("Recovery compression failed.");
            if(written+12>=clear.Length)return clear.ToArray();
            byte[] payload=new byte[written+12];Magic.CopyTo(payload);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8,4),clear.Length);
            buffer.AsSpan(0,written).CopyTo(payload.AsSpan(12));return payload;
        }
        finally{CryptographicOperations.ZeroMemory(buffer);}
    }
    internal static byte[] Decode(byte[] payload)
    {
        if(!payload.AsSpan().StartsWith(Magic))return payload;
        if(payload.Length<12)throw new InvalidDataException("Incomplete recovery container.");
        int length=BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8,4));
        if(length<=0 || length>MaximumClearBytes)throw new InvalidDataException("Invalid decompressed recovery size.");
        byte[] clear=new byte[length];using var decoder=new BrotliDecoder();
        try
        {
            var status=decoder.Decompress(payload.AsSpan(12),clear,out int consumed,out int written);
            if(status!=OperationStatus.Done || consumed!=payload.Length-12 || written!=length)
                throw new InvalidDataException("Incomplete or invalid compressed recovery record.");
            return clear;
        }
        catch{CryptographicOperations.ZeroMemory(clear);throw;}
    }
}
