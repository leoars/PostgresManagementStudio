using System.Globalization;
using Npgsql;

namespace PGMS.PostgreSql;

/// <summary>Cumulative pg_stat_database counters of one database row; Oid 0 is the row for shared catalogs.</summary>
public sealed record ActivityDatabaseCounters(uint Oid, string Database, long BlocksRead, long BlocksHit, long TempBytes, double ReadTimeMs, long Transactions, DateTime? StatsReset);

/// <summary>One server-wide reading for the Overview, Resource Waits and Data File I/O panels. Counters are cumulative; rates come from two readings.</summary>
public sealed record ActivityMetricsSample(DateTime ObservedAt, int ActiveSessions, int WaitingSessions, IReadOnlyDictionary<string, int> Waiters,
    long IoBytes, DateTime? IoStatsReset, bool? TrackIoTiming, int BlockSize, IReadOnlyList<ActivityDatabaseCounters> Databases)
{
    /// <summary>Where IoBytes comes from: pg_stat_io (PostgreSQL 16+) counts reads, writes and extends; older servers only expose database reads.</summary>
    public string IoSource { get; init; } = "";
}

/// <summary>One Overview point. Rates are null for the first reading, after a statistics reset, or when the server clock did not advance.</summary>
public sealed record ActivityMetricsPoint(DateTime ObservedAt, int ActiveSessions, int WaitingSessions, double? IoMegabytesPerSecond, double? TransactionsPerSecond);

/// <summary>Sampled wait estimate for one wait_event_type. PostgreSQL keeps no cumulative wait time, so each waiter seen in a reading counts as waiting
/// for the whole second (ms/sec) or interval (cumulative).</summary>
public sealed record ResourceWaitRow(string Category, int WaitingNow, double WaitMsPerSecond, double RecentWaitMsPerSecond, double AverageWaiters, double CumulativeWaitSeconds);

/// <summary>Per-database I/O over the last interval. Null means not measurable: first reading, reset, no reads, or track_io_timing off.</summary>
public sealed record DataFileIoRow(string Database, double? ReadMegabytesPerSecond, double? TempMegabytesPerSecond, double? AverageReadMs, double? HitRatio);

/// <summary>A pg_stat_statements entry measured over the interval between two refreshes.</summary>
public sealed record RecentExpensiveQuery(string Query, string Database, string User, double ExecutionsPerMinute, double ExecutionMsPerSecond,
    double PhysicalReadsPerSecond, double LogicalReadsPerSecond, double AverageDurationMs)
{
    public const int MaximumRows = 20;
    public string QueryLine => ActivityText.SingleLine(Query);
    /// <summary>Rates from a windowed Query Statistics snapshot; empty when the window has no measurable length.</summary>
    public static IReadOnlyList<RecentExpensiveQuery> FromWindow(QueryStatisticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.WindowStart is not { } start) return [];
        double seconds = (snapshot.ObservedAt - start).TotalSeconds;
        if (seconds <= 0) return [];
        return snapshot.Rows.Select(e => new RecentExpensiveQuery(e.Query, e.Database, e.User, e.Calls * 60 / seconds, e.TotalExecMs / seconds,
            e.SharedBlocksRead / seconds, e.SharedBlocks / seconds, e.MeanExecMs)).ToArray();
    }
}

/// <summary>A query running now, oldest first: the PostgreSQL view of SSMS "Active Expensive Queries".</summary>
public sealed record ActiveExpensiveQuery(int Pid, string Database, string User, TimeSpan? Elapsed, string Wait, string Query)
{
    public const int MaximumRows = 20;
    public string ElapsedLabel => Elapsed?.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture) ?? "—";
    public string QueryLine => ActivityText.SingleLine(Query);
}

public static class ActivityText
{
    /// <summary>Statement text on one grid line, like the SSMS query columns: runs of whitespace, line breaks included, become one space.</summary>
    public static string SingleLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Rolling history of readings and the rates derived from them. Pure computation: it never queries the server.</summary>
public sealed class ActivityMetricsHistory
{
    public const int Capacity = 60;
    /// <summary>Readings averaged by Recent Wait Time: one minute at the default 10-second refresh.</summary>
    public const int RecentReadings = 6;
    /// <summary>wait_event_type values that mean real waiting; Activity (idle main loops) is excluded when sampling.</summary>
    public static readonly IReadOnlyList<string> Categories = ["Lock", "LWLock", "BufferPin", "IO", "IPC", "Client", "Timeout", "Extension"];
    private const double Megabyte = 1024 * 1024;
    private readonly List<ActivityMetricsSample> samples = [];
    private readonly List<ActivityMetricsPoint> points = [];
    private readonly Dictionary<string, (double Waiters, double Cumulative)> totals = [];
    private int readings;
    public IReadOnlyList<ActivityMetricsPoint> Points => points;
    public IReadOnlyList<ResourceWaitRow> ResourceWaits { get; private set; } = [];
    public IReadOnlyList<DataFileIoRow> DataFileIo { get; private set; } = [];

    public ActivityMetricsPoint Add(ActivityMetricsSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var previous = samples.Count > 0 ? samples[^1] : null;
        if (previous is not null && sample.ObservedAt < previous.ObservedAt) throw new ArgumentException("Readings must not go back in time.", nameof(sample));
        double? seconds = previous is not null && sample.ObservedAt > previous.ObservedAt ? (sample.ObservedAt - previous.ObservedAt).TotalSeconds : null;
        double? io = seconds is { } s && previous!.IoStatsReset == sample.IoStatsReset && previous.IoSource == sample.IoSource && sample.IoBytes >= previous.IoBytes
            ? (sample.IoBytes - previous.IoBytes) / Megabyte / s : null;
        var before = previous?.Databases.ToDictionary(d => d.Oid) ?? [];
        double? transactions = null; var ioRows = new List<DataFileIoRow>();
        foreach (var d in sample.Databases.OrderBy(d => d.Oid == 0).ThenBy(d => d.Database, StringComparer.Ordinal))
        {
            bool comparable = seconds is not null && before.TryGetValue(d.Oid, out var p) && p.StatsReset == d.StatsReset
                && d.BlocksRead >= p.BlocksRead && d.BlocksHit >= p.BlocksHit && d.TempBytes >= p.TempBytes && d.Transactions >= p.Transactions && d.ReadTimeMs >= p.ReadTimeMs;
            if (!comparable) { ioRows.Add(new(Label(d), null, null, null, null)); continue; }
            var o = before[d.Oid]; double t = seconds!.Value;
            long reads = d.BlocksRead - o.BlocksRead, hits = d.BlocksHit - o.BlocksHit;
            transactions = (transactions ?? 0) + (d.Transactions - o.Transactions) / t;
            ioRows.Add(new(Label(d), reads * (double)sample.BlockSize / Megabyte / t, (d.TempBytes - o.TempBytes) / Megabyte / t,
                sample.TrackIoTiming == true && reads > 0 ? (d.ReadTimeMs - o.ReadTimeMs) / reads : null, reads + hits > 0 ? (double)hits / (reads + hits) : null));
        }
        DataFileIo = ioRows;
        readings++;
        foreach (string category in Categories.Concat(sample.Waiters.Keys).Concat(totals.Keys).Distinct().ToArray())
        {
            totals.TryGetValue(category, out var total);
            int now = sample.Waiters.GetValueOrDefault(category);
            int then = previous?.Waiters.GetValueOrDefault(category) ?? 0;
            // Trapezoid between readings: a waiter present in both readings counts for the whole interval.
            totals[category] = (total.Waiters + now, total.Cumulative + (seconds is { } span ? (now + then) / 2.0 * span : 0));
        }
        samples.Add(sample);
        if (samples.Count > Capacity) samples.RemoveAt(0);
        var recent = samples.TakeLast(RecentReadings).ToArray();
        ResourceWaits = Categories.Concat(totals.Keys.Except(Categories).Order(StringComparer.Ordinal)).Select(c =>
        {
            int now = sample.Waiters.GetValueOrDefault(c);
            return new ResourceWaitRow(c, now, now * 1000.0, recent.Average(r => r.Waiters.GetValueOrDefault(c)) * 1000, totals[c].Waiters / readings, totals[c].Cumulative);
        }).ToArray();
        var point = new ActivityMetricsPoint(sample.ObservedAt, sample.ActiveSessions, sample.WaitingSessions, io, transactions);
        points.Add(point);
        if (points.Count > Capacity) points.RemoveAt(0);
        return point;
    }

    private static string Label(ActivityDatabaseCounters d) => d.Oid == 0 ? "(shared catalogs)" : d.Database;
}

public sealed class ActivityMetricsService(string connectionString)
{
    /// <summary>Reads the server-wide counters in one statement, so every panel describes the same instant.</summary>
    public async Task<ActivityMetricsSample> ReadSampleAsync(CancellationToken token = default)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        // PostgreSQL 18 reports I/O in bytes and drops op_bytes; 16 and 17 report operations of op_bytes each; older servers have no pg_stat_io.
        (string io, string reset, string source) = c.PostgreSqlVersion.Major switch
        {
            >= 18 => ("(SELECT sum(coalesce(read_bytes,0)+coalesce(write_bytes,0)+coalesce(extend_bytes,0)) FROM pg_catalog.pg_stat_io)", "(SELECT max(stats_reset) FROM pg_catalog.pg_stat_io)", "pg_stat_io"),
            >= 16 => ("(SELECT sum((coalesce(reads,0)+coalesce(writes,0)+coalesce(extends,0))*op_bytes) FROM pg_catalog.pg_stat_io)", "(SELECT max(stats_reset) FROM pg_catalog.pg_stat_io)", "pg_stat_io"),
            _ => ("(SELECT sum(blks_read) FROM pg_catalog.pg_stat_database)*current_setting('block_size')::bigint", "(SELECT max(stats_reset) FROM pg_catalog.pg_stat_database)", "pg_stat_database reads")
        };
        // pg_sleep is a requested pause, like WAITFOR, which SSMS 22.11 leaves out of Waiting Tasks and Resource Waits (M1030).
        await using var cmd = new NpgsqlCommand($$"""
            WITH a AS MATERIALIZED (SELECT backend_type, state, CASE WHEN wait_event_type = 'Timeout' AND wait_event = 'PgSleep' THEN NULL ELSE wait_event_type END AS wait_event_type
                                    FROM pg_catalog.pg_stat_activity WHERE pid <> pg_catalog.pg_backend_pid()),
            w AS (SELECT wait_event_type AS category, count(*)::integer AS n FROM a
                  WHERE wait_event_type IS NOT NULL AND wait_event_type <> 'Activity' AND (backend_type <> 'client backend' OR state = 'active') GROUP BY wait_event_type),
            d AS (SELECT datid, coalesce(datname,'') AS datname, coalesce(blks_read,0) AS blks_read, coalesce(blks_hit,0) AS blks_hit, coalesce(temp_bytes,0) AS temp_bytes,
                         coalesce(blk_read_time,0) AS blk_read_time, coalesce(xact_commit,0)+coalesce(xact_rollback,0) AS xacts, stats_reset FROM pg_catalog.pg_stat_database)
            SELECT pg_catalog.statement_timestamp(),
                   (SELECT count(*)::integer FROM a WHERE backend_type = 'client backend' AND state = 'active'),
                   (SELECT count(*)::integer FROM a WHERE backend_type = 'client backend' AND state = 'active' AND wait_event_type IS NOT NULL),
                   coalesce((SELECT array_agg(category ORDER BY category) FROM w), '{}'), coalesce((SELECT array_agg(n ORDER BY category) FROM w), '{}'),
                   coalesce({{io}}, 0)::bigint, {{reset}}, (SELECT setting = 'on' FROM pg_catalog.pg_settings WHERE name = 'track_io_timing'), current_setting('block_size')::integer,
                   coalesce((SELECT array_agg(datid ORDER BY datid) FROM d), '{}'), coalesce((SELECT array_agg(datname ORDER BY datid) FROM d), '{}'),
                   coalesce((SELECT array_agg(blks_read ORDER BY datid) FROM d), '{}'), coalesce((SELECT array_agg(blks_hit ORDER BY datid) FROM d), '{}'),
                   coalesce((SELECT array_agg(temp_bytes ORDER BY datid) FROM d), '{}'), coalesce((SELECT array_agg(blk_read_time ORDER BY datid) FROM d), '{}'),
                   coalesce((SELECT array_agg(xacts ORDER BY datid) FROM d), '{}'), coalesce((SELECT array_agg(stats_reset ORDER BY datid) FROM d), '{}')
            """, c) { CommandTimeout = 15 };
        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Activity metrics returned no row.");
        string[] categories = reader.GetFieldValue<string[]>(3); int[] counts = reader.GetFieldValue<int[]>(4);
        uint[] oids = reader.GetFieldValue<uint[]>(9); string[] names = reader.GetFieldValue<string[]>(10);
        long[] read = reader.GetFieldValue<long[]>(11), hit = reader.GetFieldValue<long[]>(12), temp = reader.GetFieldValue<long[]>(13);
        double[] time = reader.GetFieldValue<double[]>(14); long[] xacts = reader.GetFieldValue<long[]>(15); DateTime?[] resets = reader.GetFieldValue<DateTime?[]>(16);
        var databases = oids.Select((oid, i) => new ActivityDatabaseCounters(oid, names[i], read[i], hit[i], temp[i], time[i], xacts[i], resets[i])).ToArray();
        return new(reader.GetDateTime(0), reader.GetInt32(1), reader.GetInt32(2), categories.Zip(counts).ToDictionary(p => p.First, p => p.Second),
            reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetDateTime(6), reader.IsDBNull(7) ? null : reader.GetBoolean(7), reader.GetInt32(8), databases) { IoSource = source };
    }

    /// <summary>Running queries of client backends other than this reading, oldest first.</summary>
    public async Task<IReadOnlyList<ActiveExpensiveQuery>> ReadActiveQueriesAsync(CancellationToken token = default)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT pid, coalesce(datname,''), coalesce(usename,''), statement_timestamp() - query_start, coalesce(wait_event_type || ': ' || wait_event,''), coalesce(query,'<restricted>')
            FROM pg_catalog.pg_stat_activity
            WHERE backend_type = 'client backend' AND state = 'active' AND pid <> pg_catalog.pg_backend_pid()
            ORDER BY query_start NULLS LAST, pid LIMIT @limit
            """, c) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("limit", ActiveExpensiveQuery.MaximumRows);
        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<ActiveExpensiveQuery>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            TimeSpan? elapsed = reader.IsDBNull(3) ? null : reader.GetTimeSpan(3);
            rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), elapsed >= TimeSpan.Zero ? elapsed : null, reader.GetString(4), reader.GetString(5)));
        }
        return rows.AsReadOnly();
    }
}
