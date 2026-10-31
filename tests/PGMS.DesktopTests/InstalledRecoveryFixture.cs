using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using PGMS.Desktop;

internal static class InstalledRecoveryFixture
{
    internal sealed record Item(string Text, SqlFileFormat Format);
    internal static readonly Item[] Items =
    [new("-- synthetic recovery ação 漢字\r\nSELECT 1;", SqlFileFormat.Utf8Bom),
     new("-- synthetic recovery 😀\nSELECT 2;", SqlFileFormat.Utf16LittleEndian),
     new("-- synthetic recovery Ω\r\nSELECT 3;", SqlFileFormat.Utf8)];

    internal static string Safe(string path)
    {
        string full = Path.GetFullPath(path), allowed = Path.GetFullPath("artifacts/acceptance") + Path.DirectorySeparatorChar;
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Synthetic acceptance paths only.");
        for (string? p = full; p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked fixture refused.");
        return full;
    }
    internal static void ValidateState(string state)
    {
        state = Safe(state);
        using var marker = JsonDocument.Parse(File.ReadAllBytes(Safe(Path.Combine(state, ".pgms-synthetic-state.json"))));
        var m = marker.RootElement;
        if (m.GetProperty("SchemaVersion").GetInt32() != 1 || m.GetProperty("Purpose").GetString() != "PGMS synthetic acceptance state" || m.GetProperty("RealServer").GetBoolean()) throw new IOException("Synthetic marker required.");
        foreach (var (file, key) in new[] { ("connections.json", "ProfileSha256"), ("synthetic-sentinel.txt", "SentinelSha256") })
            if (Hash(Safe(Path.Combine(state, file))) != m.GetProperty(key).GetString()) throw new IOException("Fixture profile/sentinel changed.");
    }
    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    internal static int Seed(string state)
    {
        try
        {
            state = Safe(state); ValidateState(state);
            string root = Safe(Path.Combine(state, "recovery"));
            if (Directory.Exists(root)) throw new IOException("Existing recovery evidence preserved.");
            using (var session = new QueryRecoverySession(root))
                for (int i = 0; i < Items.Length; i++) session.Store.Save(new(Guid.NewGuid(), $"synthetic-{i}.sql", Items[i].Text, Items[i].Format));
            Console.WriteLine("Seeded three synthetic encrypted drafts."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
