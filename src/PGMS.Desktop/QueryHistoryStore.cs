using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PGMS.Desktop;

/// <summary>What Query History holds now. UnreadableFiles counts files that failed to decrypt or validate; they are kept on disk untouched.</summary>
public sealed record QueryHistorySnapshot(IReadOnlyList<QueryHistoryEntry> Entries, IReadOnlySet<QueryHistoryKey> Pins, int UnreadableFiles);

/// <summary>Query History on disk (PGMS-043): executions in segment files of about 1 MiB of JSON each, compressed and encrypted with DPAPI for the current Windows user,
/// like the recovery drafts. The entropy includes the file name, so a renamed or copied segment does not decrypt. Nothing is ever sent to a server.
/// Writes take an exclusive lock file, so two PGMS processes of the same user do not lose each other's executions.</summary>
public sealed class QueryHistoryStore
{
    public const int SegmentTargetBytes = 1024 * 1024;
    private const int MaximumEncryptedBytes = 3 * 1024 * 1024;
    internal const string SegmentPattern = "*.qhs", PinsFileName = "pins.qhp", LockFileName = "history.lock";
    private static readonly byte[] Purpose = "PGMS query history 1\0"u8.ToArray();
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record SegmentEnvelope(int Version, string Name, List<QueryHistoryEntry> Entries);
    private sealed record PinsEnvelope(int Version, List<QueryHistoryKey> Pins);
    private sealed record SegmentInfo(DateTime WriteTimeUtc, long Length, int Count, DateTime OldestUtc);
    private readonly Dictionary<string, SegmentInfo> infos = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();
    private DateTime lastAgePrune = DateTime.MinValue;

    public QueryHistoryStore(string directory) => DirectoryPath = Path.GetFullPath(directory);
    public string DirectoryPath { get; }
    /// <summary>%LOCALAPPDATA%\PGMS\history, or history under PGMS_STATE_DIRECTORY; PGMS_HISTORY_DIRECTORY overrides both (tests).</summary>
    public static string DefaultDirectory => Environment.GetEnvironmentVariable("PGMS_HISTORY_DIRECTORY") is { Length: > 0 } explicitPath ? explicitPath :
        Path.Combine(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGMS"), "history");
    /// <summary>How long a gate wait lasts before a write gives up; tests shorten it.</summary>
    internal TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>The age limit is applied at startup and then at most this often while writing.</summary>
    internal static readonly TimeSpan AgePruneInterval = TimeSpan.FromHours(1);

    /// <summary>Adds one execution and then applies the limits.</summary>
    public void Append(QueryHistoryEntry entry, QueryHistorySettings settings, DateTime nowUtc)
    {
        entry.Validate();
        lock (sync)
        {
            using var gate = Gate();
            var names = Segments();
            string? name = null; List<QueryHistoryEntry> entries = [];
            if (names.Count > 0 && TryReadSegment(names[^1], out var last))
            {
                last.Add(entry);
                byte[] probe = Serialize(names[^1], last);
                bool fits = probe.Length <= SegmentTargetBytes || last.Count == 1;
                CryptographicOperations.ZeroMemory(probe);
                if (fits) { name = names[^1]; entries = last; }
            }
            if (name is null) { name = NewSegmentName(nowUtc, names); entries = [entry]; }
            WriteSegment(name, entries);
            PruneCore(settings.Normalized(), nowUtc, force: false);
        }
    }

    /// <summary>Applies the limits now (startup); returns how many executions were removed.</summary>
    public int Prune(QueryHistorySettings settings, DateTime nowUtc)
    {
        lock (sync)
        {
            if (!Directory.Exists(DirectoryPath)) return 0;
            using var gate = Gate();
            return PruneCore(settings.Normalized(), nowUtc, force: true);
        }
    }

    public QueryHistorySnapshot ReadAll()
    {
        lock (sync)
        {
            if (!Directory.Exists(DirectoryPath)) return new([], new HashSet<QueryHistoryKey>(), 0);
            QueryRecoveryStore.RejectLink(DirectoryPath);
            var entries = new List<QueryHistoryEntry>(); int unreadable = 0;
            foreach (string name in Segments())
                if (TryReadSegment(name, out var read)) entries.AddRange(read); else unreadable++;
            if (!TryReadPins(out var pins)) unreadable++;
            return new(entries, pins, unreadable);
        }
    }

    /// <summary>Removes the given executions; returns how many were found.</summary>
    public int Delete(IReadOnlyCollection<Guid> ids)
    {
        lock (sync)
        {
            if (!Directory.Exists(DirectoryPath) || ids.Count == 0) return 0;
            using var gate = Gate();
            var remove = ids.ToHashSet(); int removed = 0;
            foreach (string name in Segments())
            {
                if (!TryReadSegment(name, out var entries)) continue;
                int count = entries.RemoveAll(e => remove.Contains(e.Id));
                if (count > 0) { WriteSegment(name, entries); removed += count; }
            }
            return removed;
        }
    }

    /// <summary>A pinned query is listed first and its newest execution is never removed by the limits.</summary>
    public void SetPinned(QueryHistoryKey key, bool pinned)
    {
        lock (sync)
        {
            Directory.CreateDirectory(DirectoryPath); QueryRecoveryStore.RejectLink(DirectoryPath);
            using var gate = Gate();
            if (!TryReadPins(out var pins)) throw new InvalidDataException("The pinned queries file could not be read; it was kept unchanged. Clear History removes it.");
            if (pinned ? !pins.Add(key) : !pins.Remove(key)) return;
            WriteProtected(PinsFileName, JsonSerializer.SerializeToUtf8Bytes(new PinsEnvelope(1, pins.ToList()), WriteOptions));
        }
    }

    /// <summary>Deletes every history file, readable or not, and the pins. Returns the number of executions that were readable.</summary>
    public int Clear()
    {
        lock (sync)
        {
            if (!Directory.Exists(DirectoryPath)) return 0;
            using var gate = Gate();
            int count = 0;
            foreach (string name in Segments()) { if (TryReadSegment(name, out var entries)) count += entries.Count; File.Delete(PathOf(name)); }
            foreach (string partial in Directory.EnumerateFiles(DirectoryPath, "*.partial")) File.Delete(partial);
            if (File.Exists(PathOf(PinsFileName))) File.Delete(PathOf(PinsFileName));
            infos.Clear();
            return count;
        }
    }

    // ---- limits ----
    private int PruneCore(QueryHistorySettings settings, DateTime nowUtc, bool force)
    {
        var names = Segments();
        int total = 0; DateTime oldest = DateTime.MaxValue;
        foreach (string name in names)
            if (Info(name) is { } info) { total += info.Count; if (info.OldestUtc < oldest) oldest = info.OldestUtc; }
        bool ageDue = force || nowUtc - lastAgePrune >= AgePruneInterval;
        if (ageDue) lastAgePrune = nowUtc;
        DateTime cutoff = nowUtc.AddDays(-settings.RetentionDays);
        bool overAge = ageDue && oldest < cutoff, overCount = total > settings.MaxExecutions;
        if (!overAge && !overCount) return 0;
        // Rare: read everything, so the newest execution of each pinned query is known.
        var segments = new List<(string Name, List<QueryHistoryEntry> Entries)>();
        foreach (string name in names) if (TryReadSegment(name, out var entries)) segments.Add((name, entries));
        var pins = TryReadPins(out var readPins) ? readPins : [];
        var all = segments.SelectMany(s => s.Entries).ToList();
        var kept = all.Where(e => pins.Contains(e.Key)).GroupBy(e => e.Key).Select(g => g.MaxBy(e => e.StartedUtc)!.Id).ToHashSet();
        var remove = new HashSet<Guid>();
        if (overAge) foreach (var e in all) if (e.StartedUtc < cutoff && !kept.Contains(e.Id)) remove.Add(e.Id);
        int remaining = all.Count - remove.Count;
        // Past the count limit, the oldest go until 95% of it is left, so a full history is not rewritten on every execution.
        if (remaining > settings.MaxExecutions)
        {
            int target = settings.MaxExecutions - Math.Max(1, settings.MaxExecutions / 20);
            foreach (var e in all.Where(e => !remove.Contains(e.Id) && !kept.Contains(e.Id)).OrderBy(e => e.StartedUtc).ThenBy(e => e.Id))
            {
                if (remaining <= target) break;
                remove.Add(e.Id); remaining--;
            }
        }
        if (remove.Count == 0) return 0;
        foreach (var (name, entries) in segments)
            if (entries.RemoveAll(e => remove.Contains(e.Id)) > 0) WriteSegment(name, entries);
        return remove.Count;
    }

    // ---- files ----
    private string PathOf(string name) => Path.Combine(DirectoryPath, name);

    private List<string> Segments()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        return Directory.EnumerateFiles(DirectoryPath, SegmentPattern).Select(Path.GetFileName).Where(n => n is not null && n.EndsWith(".qhs", StringComparison.OrdinalIgnoreCase))
            .Select(n => n!).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private static string NewSegmentName(DateTime nowUtc, List<string> existing)
    {
        // Names sort in time order; a clock that went back still sorts after the last segment.
        string stamp = nowUtc.ToString("yyyyMMdd'T'HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        if (existing.Count > 0 && existing[^1].Length >= stamp.Length && string.CompareOrdinal(stamp, existing[^1][..stamp.Length]) < 0) stamp = existing[^1][..stamp.Length];
        return stamp + "-" + Guid.NewGuid().ToString("N") + ".qhs";
    }

    private FileStream Gate()
    {
        Directory.CreateDirectory(DirectoryPath); QueryRecoveryStore.RejectLink(DirectoryPath);
        string path = PathOf(LockFileName);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (File.Exists(path)) QueryRecoveryStore.RejectLink(path);
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (clock.Elapsed < LockTimeout) { Thread.Sleep(25); }
        }
    }

    private SegmentInfo? Info(string name)
    {
        var file = new FileInfo(PathOf(name));
        if (!file.Exists) return null;
        if (infos.TryGetValue(name, out var cached) && cached.WriteTimeUtc == file.LastWriteTimeUtc && cached.Length == file.Length) return cached;
        return TryReadSegment(name, out _) && infos.TryGetValue(name, out var read) ? read : null;
    }

    private bool TryReadSegment(string name, out List<QueryHistoryEntry> entries)
    {
        try { entries = ReadSegment(name); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException or NotSupportedException)
        { entries = []; infos.Remove(name); return false; }
    }

    internal List<QueryHistoryEntry> ReadSegment(string name)
    {
        byte[] clear = ReadProtected(name);
        try
        {
            var envelope = JsonSerializer.Deserialize<SegmentEnvelope>(clear);
            if (envelope is null || envelope.Version != 1 || envelope.Entries is null || !string.Equals(envelope.Name, name, StringComparison.OrdinalIgnoreCase) || envelope.Entries.Any(e => e is null))
                throw new InvalidDataException("Query History file has an unsupported format or identity.");
            foreach (var entry in envelope.Entries) entry.Validate();
            var file = new FileInfo(PathOf(name));
            infos[name] = new(file.LastWriteTimeUtc, file.Length, envelope.Entries.Count, envelope.Entries.Count == 0 ? DateTime.MaxValue : envelope.Entries.Min(e => e.StartedUtc));
            return envelope.Entries;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static byte[] Serialize(string name, List<QueryHistoryEntry> entries) => JsonSerializer.SerializeToUtf8Bytes(new SegmentEnvelope(1, name, entries), WriteOptions);

    private void WriteSegment(string name, List<QueryHistoryEntry> entries)
    {
        if (entries.Count == 0) { File.Delete(PathOf(name)); infos.Remove(name); return; }
        byte[] clear = Serialize(name, entries);
        WriteProtected(name, clear);
        var file = new FileInfo(PathOf(name));
        infos[name] = new(file.LastWriteTimeUtc, file.Length, entries.Count, entries.Min(e => e.StartedUtc));
    }

    private bool TryReadPins(out HashSet<QueryHistoryKey> pins)
    {
        pins = [];
        if (!File.Exists(PathOf(PinsFileName))) return true;
        try
        {
            byte[] clear = ReadProtected(PinsFileName);
            try
            {
                var envelope = JsonSerializer.Deserialize<PinsEnvelope>(clear);
                if (envelope is null || envelope.Version != 1 || envelope.Pins is null || envelope.Pins.Any(p => p is null || p.Server is null || p.Database is null || p.Hash is not { Length: 64 }))
                    throw new InvalidDataException("Unsupported pinned queries file.");
                pins = envelope.Pins.ToHashSet();
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException or NotSupportedException) { return false; }
    }

    private static byte[] Entropy(string name) => [.. Purpose, .. Encoding.UTF8.GetBytes(name.ToLowerInvariant())];

    private byte[] ReadProtected(string name)
    {
        string path = PathOf(name);
        QueryRecoveryStore.RejectLink(DirectoryPath); QueryRecoveryStore.RejectLink(path);
        byte[] encrypted;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > MaximumEncryptedBytes) throw new InvalidDataException("Query History file exceeds its size limit.");
            encrypted = new byte[checked((int)stream.Length)]; stream.ReadExactly(encrypted);
        }
        byte[] payload = ProtectedData.Unprotect(encrypted, Entropy(name), DataProtectionScope.CurrentUser);
        try
        {
            // Uncompressed payloads come back as the same array, which is zeroed below.
            byte[] clear = RecoveryPayload.Decode(payload);
            return ReferenceEquals(clear, payload) ? (byte[])payload.Clone() : clear;
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private void WriteProtected(string name, byte[] clear)
    {
        byte[] payload;
        try { payload = RecoveryPayload.Encode(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(payload, Entropy(name), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(payload); }
        string path = PathOf(name), temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        if (File.Exists(path)) QueryRecoveryStore.RejectLink(path);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(encrypted); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
