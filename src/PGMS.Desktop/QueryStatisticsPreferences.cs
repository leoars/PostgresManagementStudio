using System.IO;
using System.Text.Json;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Last-used Query Statistics filters for one connection target (host:port/database/user). Never holds a password or SQL results.</summary>
/// <remarks>Auto-refresh enablement is deliberately not persisted: a reopened window must not start polling the server on its own.</remarks>
public sealed record QueryStatisticsPreference(string Target, string Database, string User, string Query, long MinimumCalls, QueryStatisticsOrder Order, int AutoRefreshSeconds, DateTimeOffset LastUsed)
{
    public static readonly int[] AllowedIntervals = [5, 10, 30, 60];
    public static readonly int[] AllowedChartCounts = [10, 25, 50];
    /// <summary>Bars in the top-N chart. Absent in files written before M1021, which then read as 25.</summary>
    public int ChartCount { get; init; } = 25;
    /// <summary>Whether the top-N chart is shown. Absent in files written before M1021, which then read as shown.</summary>
    public bool ShowChart { get; init; } = true;
    /// <summary>Counters shown: cumulative or since the saved baseline. Sliding windows are never restored, since they need fresh sampling. Absent before M1022: cumulative.</summary>
    public QueryStatisticsWindowMode Window { get; init; } = QueryStatisticsWindowMode.Cumulative;
    public const int MaximumTargetLength = 1024;
    public static string TargetOf(string host, int port, string database, string user) => $"{host}:{port}/{database}/{user}";
    public QueryStatisticsPreference Validate()
    {
        if (string.IsNullOrWhiteSpace(Target) || Target.Length > MaximumTargetLength) throw new InvalidDataException("Query statistics preference target is missing or too long.");
        var filter = new QueryStatisticsFilter(Database, User, Query, MinimumCalls, Order).Validate();
        if (!AllowedIntervals.Contains(AutoRefreshSeconds)) throw new InvalidDataException("Unsupported auto-refresh interval.");
        if (!AllowedChartCounts.Contains(ChartCount)) throw new InvalidDataException("Unsupported top-N chart size.");
        if (Window is not (QueryStatisticsWindowMode.Cumulative or QueryStatisticsWindowMode.SinceBaseline)) throw new InvalidDataException("Unsupported saved statistics window.");
        return this with { Database = filter.Database, User = filter.User, Query = filter.Query };
    }
    public QueryStatisticsFilter ToFilter() => new(Database, User, Query, MinimumCalls, Order);
}

/// <summary>JSON store under the PGMS state directory, written atomically under an exclusive lock. Invalid content is reported, never repaired silently.</summary>
public sealed class QueryStatisticsPreferenceStore(string path)
{
    private sealed record Document(int Version, List<QueryStatisticsPreference> Preferences);
    public const int MaximumTargets = 64;
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGMS"), "query-statistics.json");
    public string FilePath => path;
    public IReadOnlyList<QueryStatisticsPreference> Load()
    {
        if (!File.Exists(path)) return [];
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) ?? throw new InvalidDataException("The query statistics preference file is empty or invalid.");
        if (document.Version != 1 || document.Preferences is null) throw new InvalidDataException("Unsupported query statistics preference file version.");
        if (document.Preferences.Any(p => p is null)) throw new InvalidDataException("Invalid query statistics preference entry.");
        var rows = document.Preferences.Select(p => p.Validate()).ToArray();
        if (rows.Select(p => p.Target).Distinct(StringComparer.Ordinal).Count() != rows.Length) throw new InvalidDataException("Duplicate query statistics preference targets.");
        return rows;
    }
    /// <summary>Returns the stored preference for the target, or null when none exists. Throws InvalidDataException for an unreadable file.</summary>
    public QueryStatisticsPreference? Find(string target) => Load().FirstOrDefault(p => string.Equals(p.Target, target, StringComparison.Ordinal));
    /// <summary>Replaces the entry for the preference target and keeps at most <see cref="MaximumTargets"/> most recently used targets.</summary>
    public void Save(QueryStatisticsPreference preference)
    {
        preference = preference.Validate();
        string full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        using var gate = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var rows = Load().Where(p => !string.Equals(p.Target, preference.Target, StringComparison.Ordinal)).ToList();
        rows.Add(preference);
        rows = rows.OrderByDescending(p => p.LastUsed).Take(MaximumTargets).ToList();
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, rows), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

// Append new values only: names are persisted in query-statistics.json.
public enum QueryStatisticsWindowMode { Cumulative, SinceBaseline, Last5Minutes, Last15Minutes, Last60Minutes }

/// <summary>Baseline counters of one connection target, one file per target under the PGMS state directory. Holds OIDs, query ids and counters,
/// never SQL text or a password. Written atomically; unreadable content is reported and preserved, never repaired silently.</summary>
public sealed class QueryStatisticsBaselineStore(string directory)
{
    private sealed record Document(int Version, string Target, QueryStatisticsBaseline Baseline);
    public const int MaximumTargets = 32;
    public static string DefaultDirectory => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(QueryStatisticsPreferenceStore.DefaultPath)!, "query-statistics-baselines");
    public string Directory => directory;
    /// <summary>File of a target: a hash keeps host names and user names out of file names and bounds their length.</summary>
    public string PathOf(string target) =>
        System.IO.Path.Combine(directory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(target)))[..32].ToLowerInvariant() + ".json");
    /// <summary>Saved baseline of the target, or null when none exists. Throws InvalidDataException for unreadable or foreign content.</summary>
    public QueryStatisticsBaseline? Find(string target)
    {
        string file = PathOf(target);
        if (!File.Exists(file)) return null;
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(file)) ?? throw new InvalidDataException("The saved statistics baseline is empty or invalid.");
        if (document.Version != 1) throw new InvalidDataException("Unsupported statistics baseline file version.");
        if (!string.Equals(document.Target, target, StringComparison.Ordinal)) throw new InvalidDataException("The saved statistics baseline belongs to another target.");
        return (document.Baseline ?? throw new InvalidDataException("The saved statistics baseline has no counters.")).Validate();
    }
    /// <summary>Replaces the target's baseline and keeps at most <see cref="MaximumTargets"/> most recently written baseline files.</summary>
    public void Save(string target, QueryStatisticsBaseline baseline)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > QueryStatisticsPreference.MaximumTargetLength) throw new InvalidDataException("Statistics baseline target is missing or too long.");
        baseline.Validate();
        System.IO.Directory.CreateDirectory(directory);
        string file = PathOf(target), temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var gate = new FileStream(System.IO.Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, target, baseline)));
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        foreach (var stale in new DirectoryInfo(directory).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaximumTargets)) stale.Delete();
    }
}
