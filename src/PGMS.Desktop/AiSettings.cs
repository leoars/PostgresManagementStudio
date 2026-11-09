using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PGMS.Desktop;

// Append new values only: names are persisted in ai-settings.json.
public enum AiProvider { Anthropic, OpenAI, Ollama }

/// <summary>One provider's settings. The API key is held only as a DPAPI blob for the current Windows user,
/// with the provider name as entropy, so a blob copied to another provider entry or user does not open.</summary>
public sealed record AiProviderSettings(AiProvider Provider, string Model, string Endpoint, string? ProtectedKey = null)
{
    public const int MaximumKeyLength = 512;
    public static AiProviderSettings Default(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => new(provider, "claude-opus-5-5", "https://api.anthropic.com"),
        AiProvider.OpenAI => new(provider, "", "https://api.openai.com/v1"),
        _ => new(provider, "", "http://localhost:11434")
    };
    public bool NeedsKey => Provider != AiProvider.Ollama;
    public bool HasKey => ProtectedKey is not null;

    public AiProviderSettings Validate()
    {
        if (!Enum.IsDefined(Provider)) throw new InvalidDataException("Unsupported AI provider.");
        if ((Model ?? "").Length > 200) throw new ArgumentException("The model name is too long.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Enter the provider address as an http or https URL without credentials or query.");
        // Keys travel in headers: never send one over plain HTTP to another computer.
        if (NeedsKey && uri.Scheme == "http" && !uri.IsLoopback) throw new ArgumentException("A provider that needs an API key must use https (http is accepted only on this computer).");
        if (!NeedsKey && ProtectedKey is not null) throw new InvalidDataException("Ollama does not use an API key.");
        return this with { Model = (Model ?? "").Trim(), Endpoint = uri.ToString().TrimEnd('/') };
    }

    public AiProviderSettings WithKey(string key)
    {
        if (!NeedsKey) throw new InvalidOperationException("This provider does not use an API key.");
        key = key.Trim();
        if (key.Length == 0 || key.Length > MaximumKeyLength || key.Any(char.IsWhiteSpace)) throw new ArgumentException("Enter the API key exactly as issued, without spaces.");
        byte[] clear = Encoding.UTF8.GetBytes(key);
        try { return this with { ProtectedKey = Convert.ToBase64String(ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser)) }; }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public AiProviderSettings WithoutKey() => this with { ProtectedKey = null };

    /// <summary>The clear key for one request. Callers keep it in a local and never log, persist or display it.</summary>
    public string RevealKey()
    {
        if (ProtectedKey is null) throw new InvalidOperationException($"No API key is saved for {Provider}.");
        byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedKey), Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    private byte[] Entropy => Encoding.UTF8.GetBytes("PGMS.AI." + Provider);
}

public sealed record AiSettings(AiProvider ActiveProvider, IReadOnlyList<AiProviderSettings> Providers)
{
    public static AiSettings Default { get; } = new(AiProvider.Anthropic, Enum.GetValues<AiProvider>().Select(AiProviderSettings.Default).ToArray());
    public AiProviderSettings For(AiProvider provider) => Providers.FirstOrDefault(p => p.Provider == provider) ?? AiProviderSettings.Default(provider);
    public AiSettings With(AiProviderSettings settings) => this with { Providers = Providers.Where(p => p.Provider != settings.Provider).Append(settings.Validate()).OrderBy(p => p.Provider).ToArray() };
}

/// <summary>ai-settings.json under the PGMS state directory, written atomically under an exclusive lock.
/// Invalid content is reported and preserved, never repaired silently.</summary>
public sealed class AiSettingsStore(string path)
{
    private sealed record Document(int Version, AiProvider ActiveProvider, List<AiProviderSettings> Providers);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string DefaultPath => Path.Combine(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGMS"), "ai-settings.json");
    public string FilePath => path;

    public AiSettings Load()
    {
        if (!File.Exists(path)) return AiSettings.Default;
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("The AI settings file is empty or invalid.");
        if (document.Version != 1 || document.Providers is null || document.Providers.Any(p => p is null)) throw new InvalidDataException("Unsupported AI settings file version.");
        if (!Enum.IsDefined(document.ActiveProvider)) throw new InvalidDataException("Unsupported active AI provider.");
        if (document.Providers.Select(p => p.Provider).Distinct().Count() != document.Providers.Count) throw new InvalidDataException("Duplicate AI provider entries.");
        var settings = AiSettings.Default with { ActiveProvider = document.ActiveProvider };
        foreach (var provider in document.Providers)
        {
            try { settings = settings.With(provider); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid AI provider settings: " + ex.Message, ex); }
        }
        return settings;
    }

    public void Save(AiSettings settings)
    {
        var rows = settings.Providers.Select(p => p.Validate()).ToList();
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var gate = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, settings.ActiveProvider, rows), Json));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
