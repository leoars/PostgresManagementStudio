using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PGMS.Desktop;

/// <summary>Writes executions to Query History on its own queue (PGMS-043). Recording never blocks or fails a query: the tab hands over what ran and returns;
/// a failure only raises Warning. Settings and the profile exclusions are read on the queue, at the moment of each write.</summary>
public sealed class QueryHistoryRecorder
{
    private readonly Func<QueryHistorySettings> settings;
    private readonly Func<string, int, bool> excluded;
    private readonly Func<DateTime> clock;
    public QueryHistoryStore Store { get; }
    /// <summary>Serializes every disk operation of this history; the window reads through it too.</summary>
    public RecoveryIoQueue Io { get; } = new();
    /// <summary>Raised on a worker thread with a message for the status bar.</summary>
    public event Action<string>? Warning;
    /// <summary>Raised on a worker thread after an execution was written.</summary>
    public event Action<QueryHistoryEntry>? Recorded;

    /// <param name="excluded">True when recording is turned off for that server and port; an exception counts as excluded, so a profile file that cannot be read never lets SQL through.</param>
    public QueryHistoryRecorder(QueryHistoryStore store, Func<QueryHistorySettings> settings, Func<string, int, bool> excluded, Func<DateTime>? clock = null)
    {
        Store = store; this.settings = settings; this.excluded = excluded; this.clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Queues the execution. The task says whether it was written; callers never wait for it.</summary>
    public Task<bool> RecordAsync(QueryHistoryCapture capture) => Io.RunAsync(() =>
    {
        try
        {
            var current = settings().Normalized();
            if (!current.Enabled) return false;
            bool skip;
            try { skip = excluded(capture.Server, capture.Port); }
            catch (Exception ex) when (IsDiskError(ex))
            {
                Warn("Query History did not record this execution: saved connection profiles could not be read to check whether this server is excluded. " + ex.Message);
                return false;
            }
            if (skip) return false;
            var entry = capture.ToEntry(current);
            Store.Append(entry, current, clock());
            Recorded?.Invoke(entry);
            return true;
        }
        catch (Exception ex) when (IsDiskError(ex))
        {
            Warn("Query History could not save this execution; the query itself was not affected. " + ex.Message);
            return false;
        }
    });

    /// <summary>Startup cleanup by age and count.</summary>
    public Task<int> PruneAsync() => Io.RunAsync(() =>
    {
        try { return Store.Prune(settings().Normalized(), clock()); }
        catch (Exception ex) when (IsDiskError(ex)) { Warn("Query History cleanup did not complete. " + ex.Message); return 0; }
    });

    public Task<QueryHistorySnapshot> ReadAsync() => Io.RunAsync(Store.ReadAll);
    public Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids) => Io.RunAsync(() => Store.Delete(ids));
    public Task SetPinnedAsync(QueryHistoryKey key, bool pinned) => Io.RunAsync(() => Store.SetPinned(key, pinned));
    public Task<int> ClearAsync() => Io.RunAsync(Store.Clear);
    public QueryHistorySettings CurrentSettings => settings().Normalized();

    private void Warn(string message) { try { Warning?.Invoke(message); } catch (InvalidOperationException) { } }

    internal static bool IsDiskError(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException or ArgumentException or NotSupportedException;

    /// <summary>True when a saved profile for this server and port has "Do not record query history" on. Host names compare without case.</summary>
    public static bool ProfileExcludes(string profilePath, string server, int port) =>
        File.Exists(profilePath) && new ConnectionProfileStore(profilePath).Load().Any(p => p.DisableQueryHistory && p.Port == port && string.Equals(p.Host, server, StringComparison.OrdinalIgnoreCase));
}
