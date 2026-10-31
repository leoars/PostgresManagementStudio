using System.Security.Cryptography;
using System.Text;

namespace PGMS.PostgreSql;

internal static class ScramPasswordPreparation
{
    // PostgreSQL 17.11 saslprep.c: space mapping precedes B.1, validation and
    // bidi checks use mapped input; prohibited/unassigned input falls back to raw.
    // Npgsql 10.0.3 normalizes only FormKC. Refuse incompatible inputs before SQL.
    internal static byte[] Prepare(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty || password.Contains('\0'))
            throw new ArgumentException("Enter a non-empty password without NUL characters.");
        byte[]? raw = null, prepared = null, client = null;
        var mapped = new List<int>();
        var mappedText = new StringBuilder();
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            try
            {
                raw = new byte[utf8.GetByteCount(password)];
                utf8.GetBytes(password, raw);
            }
            catch (EncoderFallbackException) { throw new ArgumentException("The password contains invalid Unicode."); }
            string input = new(password);
            foreach (var rune in input.EnumerateRunes())
            {
                int code = rune.Value;
                if (Contains(SaslPrepTables.Spaces, code)) code = 0x20;
                else if (Contains(SaslPrepTables.Nothing, code)) continue;
                mapped.Add(code);
                mappedText.Append(new Rune(code));
            }
            bool invalid = mapped.Count == 0 || mapped.Any(c =>
                Contains(SaslPrepTables.Prohibited, c) || Contains(SaslPrepTables.Unassigned, c));
            bool rtl = mapped.Any(c => Contains(SaslPrepTables.RightToLeft, c));
            invalid |= rtl && (mapped.Any(c => Contains(SaslPrepTables.LeftToRight, c)) ||
                !Contains(SaslPrepTables.RightToLeft, mapped[0]) ||
                !Contains(SaslPrepTables.RightToLeft, mapped[^1]));
            // Pure ASCII uses PostgreSQL's fast path, including control characters.
            prepared = input.All(c => c < 128) || invalid
                ? raw.ToArray() : utf8.GetBytes(mappedText.ToString().Normalize(NormalizationForm.FormKC));
            client = utf8.GetBytes(input.Normalize(NormalizationForm.FormKC));
            if (!CryptographicOperations.FixedTimeEquals(prepared, client))
                throw new ArgumentException("This password has Unicode mappings that differ between PostgreSQL SCRAM and the connection driver. Choose different characters. No password change was sent.");
            var result = prepared;
            prepared = null;
            return result;
        }
        finally
        {
            if (raw is not null) CryptographicOperations.ZeroMemory(raw);
            if (prepared is not null) CryptographicOperations.ZeroMemory(prepared);
            if (client is not null) CryptographicOperations.ZeroMemory(client);
            for (int i = 0; i < mapped.Count; i++) mapped[i] = 0;
            for (int i = 0; i < mappedText.Length; i++) mappedText[i] = '\0';
            mappedText.Clear();
            // .NET normalization creates immutable strings; do not promise RAM erasure.
        }
    }

    private static bool Contains(int[] ranges, int code)
    {
        int low = 0, high = ranges.Length / 2 - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (code < ranges[middle * 2]) high = middle - 1;
            else if (code > ranges[middle * 2 + 1]) low = middle + 1;
            else return true;
        }
        return false;
    }
}
