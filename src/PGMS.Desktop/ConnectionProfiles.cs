using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace PGMS.Desktop;

public sealed record ConnectionProfile(Guid Id, string Name, string Host, int Port, string Database, string Username, string SslMode, string? ProtectedPassword = null)
{
    public override string ToString() => Name;
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(Username) || Port is < 1 or > 65535)
            throw new ArgumentException("Enter a profile name, server, database, user and port from 1 to 65535.");
        if (SslMode is not ("VerifyFull" or "Require" or "Prefer" or "Disable")) throw new ArgumentException("Unsupported profile SSL mode.");
    }
    public string RevealPassword()
    {
        if (ProtectedPassword is null) return "";
        byte[] clear = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), Id.ToByteArray(), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public ConnectionProfile WithPassword(string password, bool remember)
    {
        if (!remember) return this with { ProtectedPassword = null };
        byte[] clear = Encoding.UTF8.GetBytes(password);
        try { return this with { ProtectedPassword = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(clear, Id.ToByteArray(), DataProtectionScope.CurrentUser)) }; }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}

public sealed class ConnectionProfileStore(string path)
{
    private sealed record Document(int Version, List<ConnectionProfile> Profiles);
    public static string DefaultPath => Path.Combine(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGMS"), "connections.json");
    public IReadOnlyList<ConnectionProfile> Load()
    {
        if (!File.Exists(path)) return [];
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) ?? throw new InvalidDataException("The profile file is empty or invalid.");
        if (document.Version != 1 || document.Profiles is null) throw new InvalidDataException("Unsupported connection profile file version.");
        if (document.Profiles.Any(p => p is null)) throw new InvalidDataException("Invalid connection profile entry.");
        foreach (var profile in document.Profiles) profile.Validate();
        if (document.Profiles.Select(p => p.Id).Distinct().Count() != document.Profiles.Count) throw new InvalidDataException("Duplicate profile identities.");
        return document.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public void Save(ConnectionProfile profile)
    {
        profile.Validate();
        Mutate(rows => { rows.RemoveAll(p => p.Id == profile.Id); rows.Add(profile); });
    }
    public void Delete(Guid id) => Mutate(rows => rows.RemoveAll(p => p.Id == id));
    private void Mutate(Action<List<ConnectionProfile>> change)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var gate = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var rows = Load().ToList(); change(rows);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, rows), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
