using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace PGMS.Desktop;

public enum SqlFileFormat { Utf8, Utf8Bom, Utf16LittleEndian, Utf16BigEndian, Utf32LittleEndian, Utf32BigEndian }
public sealed record SqlFileVersion(string Path, string Sha256);
public sealed record SqlFileSnapshot(string Path, string Text, SqlFileFormat Format, SqlFileVersion Version);

public static class SqlFileStore
{
    public const int MaximumBytes = 16 * 1024 * 1024;

    public static SqlFileSnapshot Write(string path, string text, SqlFileFormat format, SqlFileVersion? expected = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var encoding = GetEncoding(format);
        byte[] bytes;
        try
        {
            if (text.Contains('\0')) throw new InvalidDataException("SQL text contains NUL characters; the file was not saved.");
            if ((long)encoding.GetByteCount(text) + encoding.GetPreamble().Length > MaximumBytes)
                throw new IOException("SQL files are limited to 16 MiB. Split this script before saving it.");
            bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("SQL text contains invalid Unicode; the file was not saved.", ex); }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (expected is not null)
            {
                if (!string.Equals(path, expected.Path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The file version belongs to a different path.");
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        // Deny in-place writes; delete sharing permits replacement but not atomic CAS.
                        using var current = OpenRegularFile(path, FileShare.Read | FileShare.Delete);
                        if (Fingerprint(current) != expected.Sha256)
                            throw new IOException("The SQL file changed outside this document. Your edits are still open. Use Save As with a new filename to preserve both versions.");
                        File.Replace(temporary, path, null);
                        break;
                    }
                    // Win32 ERROR_UNABLE_TO_REMOVE_REPLACED leaves both original names intact.
                    // Retry only that error, releasing the handle and rechecking the version.
                    catch (IOException ex) when ((ex.HResult & 0xffff) == 1175 && attempt < 2)
                    { Thread.Sleep(20); }
                }
            }
            else File.Move(temporary, path);
            return new(path, text, format, new(path, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static Task<SqlFileSnapshot> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            await using var stream = OpenRegularFile(path, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ReadStreamAsync(stream, path, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    internal static async Task<SqlFileSnapshot> ReadStreamAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long length = stream.Length;
        if (length < 0 || length > MaximumBytes) throw new IOException("SQL files are limited to 16 MiB. Split this script before opening it.");
        byte[] bytes = new byte[checked((int)length)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(bytes.AsMemory(offset, Math.Min(65536, bytes.Length - offset)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The SQL file ended before its declared length.");
            offset += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Decode(path, bytes);
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }

    public static SqlFileSnapshot Read(string path)
    {
        path = Path.GetFullPath(path);
        using var stream = OpenRegularFile(path, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new IOException("SQL files are limited to 16 MiB. Split this script before opening it.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return Decode(path, bytes);
    }

    private static SqlFileSnapshot Decode(string path, byte[] bytes)
    {
        SqlFileFormat format = SqlFileFormat.Utf8;
        int skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { format = SqlFileFormat.Utf32LittleEndian; skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { format = SqlFileFormat.Utf32BigEndian; skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { format = SqlFileFormat.Utf8Bom; skip = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { format = SqlFileFormat.Utf16LittleEndian; skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { format = SqlFileFormat.Utf16BigEndian; skip = 2; }
        try
        {
            string text = GetEncoding(format).GetString(bytes, skip, bytes.Length - skip);
            if (text.Contains('\0')) throw new InvalidDataException("The SQL file contains NUL characters. Convert binary or BOM-less UTF-16/32 files to UTF-8 before opening.");
            return new(path, text, format, new(path, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid SQL file encoding. Use UTF-8, or UTF-16/32 with a BOM; no characters were replaced.", ex); }
    }

    public static SqlFileVersion CaptureVersion(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        using var stream = OpenRegularFile(path, FileShare.Read);
        return new(path, Fingerprint(stream));
    }

    private static FileStream OpenRegularFile(string path, FileShare share, FileOptions options = FileOptions.None)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("SQL file links are not supported. Open the original file directly.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, share, 4096, options);
    }

    private static string Fingerprint(FileStream stream)
    {
        if (stream.Length > MaximumBytes) throw new IOException("SQL files are limited to 16 MiB.");
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static Encoding GetEncoding(SqlFileFormat format) => format switch
    {
        SqlFileFormat.Utf8 => new UTF8Encoding(false, true),
        SqlFileFormat.Utf8Bom => new UTF8Encoding(true, true),
        SqlFileFormat.Utf16LittleEndian => new UnicodeEncoding(false, true, true),
        SqlFileFormat.Utf16BigEndian => new UnicodeEncoding(true, true, true),
        SqlFileFormat.Utf32LittleEndian => new UTF32Encoding(false, true, true),
        SqlFileFormat.Utf32BigEndian => new UTF32Encoding(true, true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}
