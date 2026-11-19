using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PGMS.Desktop;

/// <summary>A release version as the project tags it ("v0.1.0-alpha.7"): numbers and an optional pre-release label, compared as in
/// Semantic Versioning 2.0 (a version with a label comes before the same version without it; labels compare part by part, numbers as numbers).</summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string Label) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        int plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];
        int dash = value.IndexOf('-');
        string numbers = dash >= 0 ? value[..dash] : value, label = dash >= 0 ? value[(dash + 1)..] : "";
        string[] parts = numbers.Split('.');
        if (parts.Length is < 2 or > 3 || dash >= 0 && label.Length == 0) return null;
        var parsed = new int[3];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i])) return null;
        return new(parsed[0], parsed[1], parsed[2], label);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Label.Length == 0 && other.Label.Length == 0) return 0;
        if (Label.Length == 0) return 1; // 1.0.0 comes after 1.0.0-beta.1
        if (other.Label.Length == 0) return -1;
        string[] mine = Label.Split('.'), theirs = other.Label.Split('.');
        for (int i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            bool a = int.TryParse(mine[i], NumberStyles.None, CultureInfo.InvariantCulture, out int x), b = int.TryParse(theirs[i], NumberStyles.None, CultureInfo.InvariantCulture, out int y);
            int part = a && b ? x.CompareTo(y) : a ? -1 : b ? 1 : string.CompareOrdinal(mine[i], theirs[i]);
            if (part != 0) return Math.Sign(part);
        }
        return mine.Length.CompareTo(theirs.Length);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Label.Length > 0 ? "-" + Label : "");
}

/// <summary>One published release of the public repository.</summary>
public sealed record AvailableRelease(string Tag, ReleaseVersion Version, string Address, bool Prerelease);

/// <summary>New version notice (PGMS-083, M1118): reads the release list of the public repository through the GitHub API. Nothing is downloaded
/// or installed; the request sends only what any HTTPS request sends (the IP address and a generic user agent). Every project release so far is a
/// pre-release, so pre-releases count; drafts are not visible without credentials and are skipped anyway.</summary>
public static class UpdateCheck
{
    public const string ReleasesAddress = "https://api.github.com/repos/leoars/PostgresManagementStudio/releases?per_page=10";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PGMS-update-check", "1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>The real request; tests replace it so they never use the network.</summary>
    public static async Task<string> FetchAsync(Uri address, CancellationToken cancellation)
    {
        using var response = await Http.GetAsync(address, cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellation);
    }

    /// <summary>The version of this build, from the informational version the release process sets; a development build (no version, or the
    /// SDK default 1.0.0, the same rule as Help ▸ About) has none and is never compared.</summary>
    public static ReleaseVersion? CurrentVersion(string? informational)
    {
        string version = informational?.Split('+')[0] ?? "";
        return version.Length == 0 || version == "1.0.0" ? null : ReleaseVersion.Parse(version);
    }

    /// <summary>The releases in the API answer, without drafts and tags that are not versions.</summary>
    public static IReadOnlyList<AvailableRelease> ParseReleases(string json)
    {
        var result = new List<AvailableRelease>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            string? tag = item.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            string? address = item.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (tag is null || ReleaseVersion.Parse(tag) is not { } version) continue;
            // Only pages of the project itself are opened from the notice.
            if (address is null || !address.StartsWith("https://github.com/leoars/PostgresManagementStudio/", StringComparison.OrdinalIgnoreCase)) address = MainWindow.ReleaseNotesAddress;
            bool prerelease = item.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            result.Add(new(tag, version, address, prerelease));
        }
        return result;
    }

    /// <summary>The newest release above the current version, or null when there is none (or it is the version the user chose to skip).</summary>
    public static AvailableRelease? Newer(IEnumerable<AvailableRelease> releases, ReleaseVersion current, string? skipped = null)
    {
        var newest = releases.Where(r => r.Version.CompareTo(current) > 0).OrderByDescending(r => r.Version).FirstOrDefault();
        return newest is null || skipped is not null && ReleaseVersion.Parse(skipped) is { } s && s.CompareTo(newest.Version) == 0 ? null : newest;
    }

    /// <summary>Fetches and compares. Any failure (network, timeout, HTTP error, unexpected answer) throws; the caller decides whether to say so.</summary>
    public static async Task<AvailableRelease?> FindNewerAsync(Func<Uri, CancellationToken, Task<string>> fetch, ReleaseVersion current, string? skipped, CancellationToken cancellation)
    {
        string json = await fetch(new Uri(ReleasesAddress), cancellation);
        return Newer(ParseReleases(json), current, skipped);
    }

    /// <summary>Exceptions a check can end with that are not bugs.</summary>
    public static bool IsExpectedFailure(Exception ex) => ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or IOException or InvalidOperationException or UriFormatException;
}
