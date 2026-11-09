using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace PGMS.Desktop;

/// <summary>A saved connection. Group is the Registered Servers group path ("Production/Billing"); null or empty is the top level.</summary>
public sealed record ConnectionProfile(Guid Id, string Name, string Host, int Port, string Database, string Username, string SslMode, string? ProtectedPassword = null, string? RootCertificate = null, string? Group = null)
{
    public override string ToString() => Name;
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(Username) || Port is < 1 or > 65535)
            throw new ArgumentException("Enter a profile name, server, database, user and port from 1 to 65535.");
        if (SslMode is not ("VerifyFull" or "Require" or "Prefer" or "Disable")) throw new ArgumentException("Unsupported profile SSL mode.");
        if (!string.IsNullOrEmpty(Group)) ServerGroups.Validate(Group);
    }
    /// <summary>Same settings the Connect dialog uses for this profile, with the given password.</summary>
    public string ToConnectionString(string password)
    {
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = Host, Port = Port, Database = Database, Username = Username, Password = password, SslMode = Enum.Parse<SslMode>(SslMode),
            Timeout = 10, ApplicationName = "PGMS", Pooling = false, IncludeErrorDetail = false
        };
        if (!string.IsNullOrEmpty(RootCertificate)) settings.RootCertificate = RootCertificate;
        return settings.ConnectionString;
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

/// <summary>Registered Servers group paths: segments separated by '/', each trimmed, non-empty and at most 128 characters.</summary>
public static class ServerGroups
{
    public const char Separator = '/';
    public static string Validate(string path)
    {
        var segments = path.Split(Separator);
        if (path.Length > 1024 || segments.Any(s => s.Length is 0 or > 128 || s != s.Trim() || s.Any(char.IsControl)))
            throw new ArgumentException("Group names must be 1 to 128 characters, without '/' or leading and trailing spaces.");
        return path;
    }
    public static string Combine(string? parent, string name) => string.IsNullOrEmpty(parent) ? name : parent + Separator + name;
    public static string? Parent(string path) => path.LastIndexOf(Separator) is var i and >= 0 ? path[..i] : null;
    public static string Leaf(string path) => path[(path.LastIndexOf(Separator) + 1)..];
    public static bool IsWithin(string? path, string group) => path is not null && (path == group || path.StartsWith(group + Separator, StringComparison.Ordinal));
}

public sealed class ConnectionProfileStore(string path)
{
    // Groups lists groups created explicitly (they may be empty); groups used by profiles exist implicitly. Older PGMS versions ignore both new fields.
    private sealed record Document(int Version, List<ConnectionProfile> Profiles, List<string>? Groups = null);
    public static string DefaultPath => Path.Combine(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGMS"), "connections.json");
    private Document Read()
    {
        if (!File.Exists(path)) return new(1, [], []);
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) ?? throw new InvalidDataException("The profile file is empty or invalid.");
        if (document.Version != 1 || document.Profiles is null) throw new InvalidDataException("Unsupported connection profile file version.");
        if (document.Profiles.Any(p => p is null) || document.Groups?.Any(g => g is null) == true) throw new InvalidDataException("Invalid connection profile entry.");
        foreach (var profile in document.Profiles) profile.Validate();
        foreach (var group in document.Groups ?? []) ServerGroups.Validate(group);
        if (document.Profiles.Select(p => p.Id).Distinct().Count() != document.Profiles.Count) throw new InvalidDataException("Duplicate profile identities.");
        return document with { Groups = document.Groups ?? [] };
    }
    public IReadOnlyList<ConnectionProfile> Load() => Read().Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    /// <summary>Every group: explicit ones, the groups of profiles and all their ancestors, sorted.</summary>
    public IReadOnlyList<string> LoadGroups()
    {
        var document = Read();
        var groups = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string group in document.Groups!.Concat(document.Profiles.Where(p => !string.IsNullOrEmpty(p.Group)).Select(p => p.Group!)))
            for (string? g = group; g is not null; g = ServerGroups.Parent(g)) groups.Add(g);
        return groups.ToArray();
    }
    public void Save(ConnectionProfile profile)
    {
        profile.Validate();
        Mutate((rows, _) => { rows.RemoveAll(p => p.Id == profile.Id); rows.Add(profile); });
    }
    public void Delete(Guid id) => Mutate((rows, _) => rows.RemoveAll(p => p.Id == id));
    public void AddGroup(string group)
    {
        ServerGroups.Validate(group);
        Mutate((_, groups) =>
        {
            if (groups.Contains(group, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"The group \"{group}\" already exists.");
            groups.Add(group);
        });
    }
    /// <summary>Removes an empty group. A group that still holds servers or subgroups is refused, so no saved profile is lost by deleting a folder.</summary>
    public void DeleteGroup(string group)
    {
        Mutate((rows, groups) =>
        {
            if (rows.Any(p => ServerGroups.IsWithin(p.Group, group)) || groups.Any(g => g.StartsWith(group + ServerGroups.Separator, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"The group \"{group}\" is not empty. Move or delete its servers and subgroups first.");
            groups.RemoveAll(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
        });
    }
    public void Move(Guid id, string? group)
    {
        if (!string.IsNullOrEmpty(group)) ServerGroups.Validate(group);
        Mutate((rows, _) =>
        {
            int i = rows.FindIndex(p => p.Id == id);
            if (i < 0) throw new ArgumentException("The registered server no longer exists.");
            rows[i] = rows[i] with { Group = string.IsNullOrEmpty(group) ? null : group };
        });
    }
    // Points every saved profile of host:port at the server's new TLS configuration.
    public int UpdateTls(string host, int port, string sslMode, string? rootCertificate)
    {
        int updated = 0;
        Mutate((rows, _) =>
        {
            for (int i = 0; i < rows.Count; i++)
                if (string.Equals(rows[i].Host, host, StringComparison.OrdinalIgnoreCase) && rows[i].Port == port) { rows[i] = rows[i] with { SslMode = sslMode, RootCertificate = rootCertificate }; updated++; }
        });
        return updated;
    }
    private void Mutate(Action<List<ConnectionProfile>, List<string>> change)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var gate = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var document = Read();
        var rows = document.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(); var groups = document.Groups!.ToList();
        change(rows, groups);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, rows, groups), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
