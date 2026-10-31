using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public sealed record ActivitySession(int Pid, DateTime BackendStart, string Database, string User, string Application,
    string State, string Wait, string Query, int[] BlockingPids)
{
    public DateTime ObservedAt { get; init; }
    public DateTime? QueryStart { get; init; }
    public DateTime? TransactionStart { get; init; }
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

public sealed record ActivitySnapshot(IReadOnlyList<ActivitySession> Rows, bool IsTruncated, int MaximumRows);

public sealed class ActivityService(string connectionString)
{
    public async Task<IReadOnlyList<ActivitySession>> ReadAsync(CancellationToken token = default)
        => await ReadAsync(new ActivityFilter(), token).ConfigureAwait(false);

    public async Task<IReadOnlyList<ActivitySession>> ReadAsync(ActivityFilter filter, CancellationToken token = default)
        => (await ReadSnapshotAsync(filter,1000,token).ConfigureAwait(false)).Rows;

    public async Task<ActivitySnapshot> ReadSnapshotAsync(ActivityFilter filter, int maximumRows=1000, CancellationToken token=default)
    {
        if(maximumRows is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(maximumRows));
        ArgumentNullException.ThrowIfNull(filter);
        filter = filter.Validate();
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT pid, backend_start, coalesce(datname,''), coalesce(usename,''), coalesce(application_name,''),
                   coalesce(state,'<restricted>'), coalesce(wait_event_type || ': ' || wait_event,''),
                   coalesce(query,'<restricted>'), pg_blocking_pids(pid), statement_timestamp(), query_start, xact_start
            FROM pg_catalog.pg_stat_activity
            WHERE backend_type='client backend' AND pid<>pg_backend_pid() AND backend_start IS NOT NULL
              AND (@database = '' OR strpos(lower(coalesce(datname,'')), lower(@database)) > 0)
              AND (@user = '' OR strpos(lower(coalesce(usename,'')), lower(@user)) > 0)
              AND (@application = '' OR strpos(lower(coalesce(application_name,'')), lower(@application)) > 0)
              AND (@state = '' OR state = @state)
              AND (@pid IS NULL OR pid = @pid)
              AND (NOT @blockedOnly OR cardinality(pg_blocking_pids(pid))>0)
            ORDER BY pid LIMIT @limit
            """, c) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("limit",maximumRows+1);
        cmd.Parameters.AddWithValue("database", filter.Database);
        cmd.Parameters.AddWithValue("user", filter.User);
        cmd.Parameters.AddWithValue("application", filter.Application);
        cmd.Parameters.AddWithValue("state", filter.State);
        cmd.Parameters.AddWithValue("blockedOnly", filter.BlockedOnly);
        var pidParameter = cmd.Parameters.Add("pid", NpgsqlDbType.Integer); pidParameter.Value = filter.Pid is { } pid ? pid : DBNull.Value;
        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<ActivitySession>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            rows.Add(new(reader.GetInt32(0), reader.GetDateTime(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetFieldValue<int[]>(8)){ObservedAt=reader.GetDateTime(9),QueryStart=reader.IsDBNull(10)?null:reader.GetDateTime(10),TransactionStart=reader.IsDBNull(11)?null:reader.GetDateTime(11)});
        bool truncated=rows.Count>maximumRows;
        if(truncated)rows.RemoveAt(rows.Count-1);
        return new(rows.AsReadOnly(),truncated,maximumRows);
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
