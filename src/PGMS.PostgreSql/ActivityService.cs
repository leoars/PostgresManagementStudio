using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public sealed record ActivitySession(int Pid, DateTime BackendStart, string Database, string User, string Application,
    string State, string Wait, string Query, int[] BlockingPids)
{
    public DateTime ObservedAt { get; init; }
    public DateTime? QueryStart { get; init; }
    public DateTime? TransactionStart { get; init; }
    /// <summary>client_hostname when reverse lookup is on, else client_addr; "local" for a Unix socket; empty when not visible.</summary>
    public string Host { get; init; } = "";
    /// <summary>Blocks at least one lock waiter anywhere on the server and is not waiting on a lock itself.</summary>
    public bool IsHeadBlocker { get; init; }
    public string HeadBlocker => IsHeadBlocker ? "Yes" : "";
    public TimeSpan? QueryDuration=>State=="active"?Elapsed(QueryStart):null;
    public TimeSpan? TransactionDuration=>Elapsed(TransactionStart);
    private TimeSpan? Elapsed(DateTime? start)=>ObservedAt!=default && start is { } value && ObservedAt>=value?ObservedAt-value:null;
    public string QueryAge=>QueryDuration?.ToString(@"d\.hh\:mm\:ss",System.Globalization.CultureInfo.InvariantCulture)??"—";
    public string TransactionAge=>TransactionDuration?.ToString(@"d\.hh\:mm\:ss",System.Globalization.CultureInfo.InvariantCulture)??"—";
    public string Blockers => string.Join(", ", BlockingPids);
}

public sealed record ActivityFilter(string Database = "", string User = "", string Application = "", string State = "", int? Pid = null)
{
    public bool BlockedOnly { get; init; }
    public ActivityFilter Validate()
    {
        if (new[] { Database, User, Application, State }.Any(value => value is null || value.Length > 256)) throw new ArgumentException("Activity filters must be non-null and 256 characters or fewer.");
        if(Pid is <=0)throw new ArgumentException("PID must be a positive integer.");
        if (State is not ("" or "active" or "idle" or "idle in transaction" or "idle in transaction (aborted)" or "fastpath function call" or "disabled"))
            throw new ArgumentException("Unsupported activity state filter.");
        return this with { Database = Database.Trim(), User = User.Trim(), Application = Application.Trim() };
    }
}

/// <summary>Keyset page over live PID order: sessions after <see cref="AfterPid"/> or the nearest ones before <see cref="BeforePid"/>.</summary>
public sealed record ActivityPage(int? AfterPid = null, int? BeforePid = null)
{
    public static ActivityPage First { get; } = new();
    public ActivityPage Validate()
    {
        if (AfterPid is < 0 || BeforePid is <= 0) throw new ArgumentException("Page PIDs must be positive.");
        if (AfterPid is not null && BeforePid is not null) throw new ArgumentException("Choose either the page after or before a PID.");
        return this;
    }
}

/// <summary>IsTruncated means more matching sessions follow this page. Counts come from the same server snapshot as the rows.</summary>
public sealed record ActivitySnapshot(IReadOnlyList<ActivitySession> Rows, bool IsTruncated, int MaximumRows)
{
    public int TotalMatching { get; init; }
    public int Preceding { get; init; }
    public bool HasPrevious => Preceding > 0;
}

public sealed class ActivityService(string connectionString)
{
    public async Task<IReadOnlyList<ActivitySession>> ReadAsync(CancellationToken token = default)
        => await ReadAsync(new ActivityFilter(), token).ConfigureAwait(false);

    public async Task<IReadOnlyList<ActivitySession>> ReadAsync(ActivityFilter filter, CancellationToken token = default)
        => (await ReadSnapshotAsync(filter,1000,token).ConfigureAwait(false)).Rows;

    public Task<ActivitySnapshot> ReadSnapshotAsync(ActivityFilter filter, int maximumRows=1000, CancellationToken token=default)
        => ReadSnapshotAsync(filter, ActivityPage.First, maximumRows, token);

    public async Task<ActivitySnapshot> ReadSnapshotAsync(ActivityFilter filter, ActivityPage page, int maximumRows=1000, CancellationToken token=default)
    {
        if(maximumRows is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(maximumRows));
        ArgumentNullException.ThrowIfNull(filter);ArgumentNullException.ThrowIfNull(page);
        filter = filter.Validate();page = page.Validate();
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        // One statement reads one activity snapshot, so the page and its counts agree with each other.
        await using var cmd = new NpgsqlCommand("""
            WITH matching AS MATERIALIZED (
            SELECT pid, backend_start, datname, usename, application_name, state, wait_event_type, wait_event, query, query_start, xact_start,
                   CASE WHEN client_hostname IS NOT NULL THEN client_hostname WHEN client_addr IS NOT NULL THEN host(client_addr) WHEN client_port = -1 THEN 'local' ELSE '' END AS host
            FROM pg_catalog.pg_stat_activity
            WHERE backend_type='client backend' AND pid<>pg_backend_pid() AND backend_start IS NOT NULL
              AND (@database = '' OR strpos(lower(coalesce(datname,'')), lower(@database)) > 0)
              AND (@user = '' OR strpos(lower(coalesce(usename,'')), lower(@user)) > 0)
              AND (@application = '' OR strpos(lower(coalesce(application_name,'')), lower(@application)) > 0)
              AND (@state = '' OR state = @state)
              AND (@pid IS NULL OR pid = @pid)
              AND (NOT @blockedOnly OR cardinality(pg_blocking_pids(pid))>0)),
            page AS (
            SELECT * FROM matching WHERE (@after IS NULL OR pid>@after) AND (@before IS NULL OR pid<@before)
            ORDER BY CASE WHEN @before IS NULL THEN pid ELSE -pid END LIMIT @limit),
            -- Blockers of every lock waiter on the server, not only the filtered page: pg_locks is visible
            -- to all roles, while wait columns of other roles' sessions may be hidden.
            blockers AS MATERIALIZED (
            SELECT DISTINCT unnest(pg_blocking_pids(w.pid)) AS pid FROM (SELECT DISTINCT pid FROM pg_catalog.pg_locks WHERE NOT granted AND pid IS NOT NULL) w)
            SELECT pid, backend_start, coalesce(datname,''), coalesce(usename,''), coalesce(application_name,''),
                   coalesce(state,'<restricted>'), coalesce(wait_event_type || ': ' || wait_event,''),
                   coalesce(query,'<restricted>'), b.pids, statement_timestamp(), query_start, xact_start,
                   (SELECT count(*) FROM matching)::integer, (SELECT count(*) FROM matching m WHERE m.pid<(SELECT min(p.pid) FROM page p))::integer,
                   host, cardinality(b.pids)=0 AND pid IN (SELECT pid FROM blockers)
            FROM page CROSS JOIN LATERAL (SELECT pg_blocking_pids(page.pid) AS pids) b ORDER BY pid
            """, c) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("limit",maximumRows);
        cmd.Parameters.Add("after",NpgsqlDbType.Integer).Value=page.AfterPid is { } after?after:DBNull.Value;
        cmd.Parameters.Add("before",NpgsqlDbType.Integer).Value=page.BeforePid is { } before?before:DBNull.Value;
        cmd.Parameters.AddWithValue("database", filter.Database);
        cmd.Parameters.AddWithValue("user", filter.User);
        cmd.Parameters.AddWithValue("application", filter.Application);
        cmd.Parameters.AddWithValue("state", filter.State);
        cmd.Parameters.AddWithValue("blockedOnly", filter.BlockedOnly);
        var pidParameter = cmd.Parameters.Add("pid", NpgsqlDbType.Integer); pidParameter.Value = filter.Pid is { } pid ? pid : DBNull.Value;
        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<ActivitySession>();int total=0,preceding=0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            total=reader.GetInt32(12);preceding=reader.GetInt32(13);
            rows.Add(new(reader.GetInt32(0), reader.GetDateTime(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetFieldValue<int[]>(8)){ObservedAt=reader.GetDateTime(9),QueryStart=reader.IsDBNull(10)?null:reader.GetDateTime(10),TransactionStart=reader.IsDBNull(11)?null:reader.GetDateTime(11),Host=reader.GetString(14),IsHeadBlocker=reader.GetBoolean(15)});
        }
        // An empty page carries no counts; callers reload the first page when an anchored page empties.
        return new(rows.AsReadOnly(),preceding+rows.Count<total,maximumRows){TotalMatching=total,Preceding=preceding};
    }

    /// <summary>Rereads one session; null when that PID ended or now belongs to a different backend.</summary>
    public async Task<ActivitySession?> ReadSessionAsync(ActivitySession session, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = await ReadSnapshotAsync(new ActivityFilter(Pid: session.Pid), ActivityPage.First, 1, token).ConfigureAwait(false);
        return snapshot.Rows.SingleOrDefault(s => s.BackendStart == session.BackendStart);
    }

    public async Task<bool> SignalAsync(ActivitySession selected, bool terminate, CancellationToken token = default)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        // Recheck PID plus backend_start to reject stale selections after process reuse.
        // PostgreSQL still owns permission checks; no privileges are elevated here.
        string signal = terminate ? "pg_terminate_backend(pid, 2000)" : "pg_cancel_backend(pid)";
        await using var cmd = new NpgsqlCommand($"SELECT {signal} FROM pg_catalog.pg_stat_activity WHERE pid=@pid AND backend_start=@started AND backend_type='client backend' AND pid<>pg_backend_pid()", c) { CommandTimeout = 10 };
        cmd.Parameters.AddWithValue("pid", selected.Pid);
        cmd.Parameters.AddWithValue("started", selected.BackendStart);
        object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
        return result is true;
    }
}
