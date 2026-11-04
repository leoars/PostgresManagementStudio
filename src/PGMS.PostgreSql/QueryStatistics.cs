using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

/// <summary>What the connected database offers for query statistics. Read-only: PGMS never loads or installs the extension.</summary>
public sealed record QueryStatisticsAvailability(bool? LibraryLoaded, bool ExtensionInstalled, string? InstalledVersion, string? AvailableVersion,
    string? ViewSchema, bool CanReadAllQueries, string Database)
{
    /// <summary>track_io_timing as reported by pg_settings; null when hidden from the role. When off, I/O times stay 0.</summary>
    public bool? TrackIoTiming { get; init; }
    // The view columns follow the extension version created in the database, not the server: pg_upgrade keeps 1.10 on PostgreSQL 17 until ALTER EXTENSION … UPDATE.
    private Version? ParsedVersion => Version.TryParse(InstalledVersion, out var version) ? version : null;
    /// <summary>1.10 (PostgreSQL 15/16) is the oldest version read; it has temp block timing and toplevel. An unparsable version is read as current.</summary>
    public bool IsSupportedVersion => ParsedVersion is not { } v || v >= new Version(1, 10);
    /// <summary>1.11 (PostgreSQL 17) adds stats_since, which identifies an entry instance, and splits block timing into shared, local and temp.</summary>
    public bool HasEntryTimestamps => ParsedVersion is not { } v || v >= new Version(1, 11);
    /// <summary>LibraryLoaded is null when pg_settings hides shared_preload_libraries from the connected role; the read itself then proves it.</summary>
    public bool IsReadable => LibraryLoaded != false && ExtensionInstalled && ViewSchema is not null && IsSupportedVersion;
    public string Describe()
    {
        if (LibraryLoaded == false) return "pg_stat_statements is not loaded on this server: it requires shared_preload_libraries = 'pg_stat_statements' in postgresql.conf and a server restart. PGMS does not change server configuration.";
        if (ExtensionInstalled && !IsSupportedVersion) return $"pg_stat_statements {InstalledVersion} in database \"{Database}\" is older than 1.10, the oldest version PGMS reads. "
            + (AvailableVersion is null ? "" : $"This server offers {AvailableVersion}; a privileged user can run ALTER EXTENSION pg_stat_statements UPDATE. ") + "PGMS does not change extensions.";
        if (!ExtensionInstalled) return AvailableVersion is null
            ? $"The extension files for pg_stat_statements are not available to this server, so it cannot be created in database \"{Database}\"."
            : (LibraryLoaded is null
                ? $"The pg_stat_statements extension is not created in database \"{Database}\"; the connected role cannot read shared_preload_libraries to confirm the library is loaded. "
                : $"The pg_stat_statements library is loaded, but the extension is not created in database \"{Database}\". ")
              + "A privileged user can run CREATE EXTENSION pg_stat_statements in that database; PGMS does not install extensions.";
        return $"pg_stat_statements {InstalledVersion} is available in database \"{Database}\"."
            + (CanReadAllQueries ? "" : " Query text of other users is hidden as <insufficient privilege> because the connected role is not a member of pg_read_all_stats.")
            + (HasEntryTimestamps ? "" : " This version has no entry creation time (stats_since, 1.11): a time window cannot detect a statement removed and tracked again during the window, by eviction or by an individual reset, and may then count it short.");
    }
}

// Append new values only: names are persisted in query-statistics.json.
public enum QueryStatisticsOrder { TotalExecTime, MeanExecTime, Calls, Rows, MaxExecTime, StddevExecTime, SharedBlocks, SharedBlocksRead, TempBlocks, IoTime, WalBytes }

public sealed record QueryStatisticsFilter(string Database = "", string User = "", string Query = "", long MinimumCalls = 0, QueryStatisticsOrder Order = QueryStatisticsOrder.TotalExecTime)
{
    public QueryStatisticsFilter Validate()
    {
        if (new[] { Database, User, Query }.Any(value => value is null || value.Length > 256)) throw new ArgumentException("Query statistics filters must be non-null and 256 characters or fewer.");
        if (MinimumCalls < 0) throw new ArgumentException("Minimum calls cannot be negative.");
        if (!Enum.IsDefined(Order)) throw new ArgumentException("Unsupported query statistics ordering.");
        return this with { Database = Database.Trim(), User = User.Trim(), Query = Query.Trim() };
    }
}

/// <summary>One pg_stat_statements row as observed. Times are milliseconds as reported by the server.</summary>
/// <remarks>QueryId is null when PostgreSQL hides another user's statement from a role outside pg_read_all_stats; the text is then &lt;insufficient privilege&gt;.</remarks>
public sealed record QueryStatisticsEntry(long? QueryId, string Database, string User, bool TopLevel, long Calls, double TotalExecMs, double MeanExecMs,
    double MinExecMs, double MaxExecMs, double StddevExecMs, long Rows, long SharedBlocksHit, long SharedBlocksRead, long TempBlocksRead, long TempBlocksWritten,
    long WalBytes, string Query, DateTime? StatsSince)
{
    /// <summary>Share of shared-buffer block requests satisfied from cache; null when the statement touched no shared blocks.</summary>
    public double? HitRatio => SharedBlocksHit + SharedBlocksRead > 0 ? (double)SharedBlocksHit / (SharedBlocksHit + SharedBlocksRead) : null;
    public string HitRatioLabel => HitRatio is { } ratio ? ratio.ToString("P1", CultureInfo.InvariantCulture) : "—";
    public bool QueryTextRestricted => Query == "<insufficient privilege>";
    /// <summary>The visible SQL is a statement EXPLAIN accepts, so its generic plan can be requested.</summary>
    public bool CanShowPlan => !QueryTextRestricted && IsPlannable(Query);
    private static readonly string[] PlannableKeywords = ["SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "VALUES", "TABLE", "WITH"];
    /// <summary>Lexical check of the first keyword after whitespace and comments; the server still decides whether the plan succeeds.</summary>
    public static bool IsPlannable(string sql)
    {
        int i = 0;
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i]) || sql[i] == '(') { i++; continue; }
            if (sql.AsSpan(i).StartsWith("--")) { int end = sql.IndexOf('\n', i); if (end < 0) return false; i = end + 1; continue; }
            if (sql.AsSpan(i).StartsWith("/*")) { int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) return false; i = end + 2; continue; }
            break;
        }
        int start = i;
        while (i < sql.Length && char.IsAsciiLetter(sql[i])) i++;
        var word = sql.AsSpan(start, i - start);
        foreach (string keyword in PlannableKeywords) if (word.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    /// <summary>Shared-buffer block requests (hits plus reads), the PostgreSQL counterpart of logical reads.</summary>
    public long SharedBlocks => SharedBlocksHit + SharedBlocksRead;
    public long TempBlocks => TempBlocksRead + TempBlocksWritten;
    /// <summary>Shared, local and temp block read/write time in server ms; 0 unless track_io_timing was on while the statement ran.</summary>
    public double IoTimeMs { get; init; }
    /// <summary>Catalog OIDs of the owning role and database, used only to address a selective reset of this exact entry.</summary>
    public uint UserOid { get; init; }
    public uint DatabaseOid { get; init; }
    /// <summary>A selective reset needs the queryid PostgreSQL hides from roles outside pg_read_all_stats.</summary>
    public bool CanResetIndividually => QueryId is not null;
    public string TotalExecLabel => TotalExecMs.ToString("N3", CultureInfo.InvariantCulture);
    public string MeanExecLabel => MeanExecMs.ToString("N3", CultureInfo.InvariantCulture);
    /// <summary>True when the counters are the difference between two reads (a time window). Min and max cannot be derived then and read as NaN.</summary>
    public bool IsWindowed { get; init; }
    public string MinExecLabel => double.IsNaN(MinExecMs) ? "—" : MinExecMs.ToString("N3", CultureInfo.InvariantCulture);
    public string MaxExecLabel => double.IsNaN(MaxExecMs) ? "—" : MaxExecMs.ToString("N3", CultureInfo.InvariantCulture);
    public string StddevExecLabel => StddevExecMs.ToString("N3", CultureInfo.InvariantCulture);
    public string IoTimeLabel => IoTimeMs.ToString("N3", CultureInfo.InvariantCulture);
    public string StatsSinceLabel => StatsSince is { } since ? since.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "—";
}

public sealed record QueryStatisticsSnapshot(IReadOnlyList<QueryStatisticsEntry> Rows, bool IsTruncated, int MaximumRows, DateTime ObservedAt, QueryStatisticsAvailability Availability)
{
    /// <summary>Server instant of the baseline the counters are measured from; null for cumulative counters since the last reset.</summary>
    public DateTime? WindowStart { get; init; }
    /// <summary>Entries that existed before the window start but have no matching baseline counters (hidden or absent when the baseline was read); left out.</summary>
    public long UnusableEntries { get; init; }
    /// <summary>Entries pg_stat_statements evicted (dealloc) since the baseline; a positive value means some window counts can be low. Null when unknown.</summary>
    public long? EvictedSinceWindowStart { get; init; }
    /// <summary>pg_stat_statements_reset() ran after the baseline; entries created since are counted from their creation.</summary>
    public bool ResetSinceWindowStart { get; init; }
}

/// <summary>Cumulative counters of one visible pg_stat_statements entry, without its SQL. SumSquaresMs is the sum of squared execution times, so a
/// standard deviation can be derived for the difference of two reads. StatsSince identifies the entry instance: a reset or eviction recreates it with a new value.
/// It is null before pg_stat_statements 1.11.</summary>
public sealed record QueryStatisticsCounters(uint UserOid, uint DatabaseOid, long QueryId, bool TopLevel, DateTime? StatsSince, long Calls, double TotalExecMs, double SumSquaresMs,
    long Rows, long SharedBlocksHit, long SharedBlocksRead, long TempBlocksRead, long TempBlocksWritten, long WalBytes, double IoTimeMs);

/// <summary>Counters of every entry visible to the role at one server instant. Window reads subtract it from current counters.</summary>
public sealed record QueryStatisticsBaseline(DateTime TakenAt, DateTime? StatsReset, long? Deallocations, IReadOnlyList<QueryStatisticsCounters> Entries)
{
    public const int MaximumEntries = 20000;
    /// <summary>Rejects content a server could not have produced; used for baselines read back from disk.</summary>
    public QueryStatisticsBaseline Validate()
    {
        if (TakenAt.Kind != DateTimeKind.Utc || StatsReset is { Kind: not DateTimeKind.Utc }) throw new System.IO.InvalidDataException("Baseline instants must be UTC.");
        if (Deallocations < 0) throw new System.IO.InvalidDataException("Baseline deallocation count cannot be negative.");
        if (Entries is null || Entries.Count > MaximumEntries) throw new System.IO.InvalidDataException($"A baseline holds at most {MaximumEntries} entries.");
        var keys = new HashSet<(uint, uint, long, bool)>();
        foreach (var e in Entries)
        {
            if (e is null) throw new System.IO.InvalidDataException("Invalid baseline entry.");
            if (e.StatsSince is { } since && (since.Kind != DateTimeKind.Utc || since > TakenAt)) throw new System.IO.InvalidDataException("Baseline entry created after the baseline instant.");
            if (e.Calls < 0 || e.Rows < 0 || e.SharedBlocksHit < 0 || e.SharedBlocksRead < 0 || e.TempBlocksRead < 0 || e.TempBlocksWritten < 0 || e.WalBytes < 0) throw new System.IO.InvalidDataException("Baseline counters cannot be negative.");
            if (!double.IsFinite(e.TotalExecMs) || !double.IsFinite(e.SumSquaresMs) || !double.IsFinite(e.IoTimeMs) || e.TotalExecMs < 0 || e.SumSquaresMs < 0 || e.IoTimeMs < 0) throw new System.IO.InvalidDataException("Baseline times must be finite and non-negative.");
            if (!keys.Add((e.UserOid, e.DatabaseOid, e.QueryId, e.TopLevel))) throw new System.IO.InvalidDataException("Duplicate baseline entry.");
        }
        return this;
    }
}

/// <summary>Reads pg_stat_statements for the connected database. Detection, reading and reset are separate explicit calls; nothing is installed or configured.</summary>
public sealed class QueryStatisticsService(string connectionString)
{
    public const int DefaultMaximumRows = 1000;
    // pg_stat_statements 1.11 (PostgreSQL 17) splits block timing into shared, local and temp columns; in 1.10 blk_*_time covers shared and local blocks.
    private static string IoTimeSql(string p, QueryStatisticsAvailability a) => a.HasEntryTimestamps
        ? $"({p}.shared_blk_read_time + {p}.shared_blk_write_time + {p}.local_blk_read_time + {p}.local_blk_write_time + {p}.temp_blk_read_time + {p}.temp_blk_write_time)"
        : $"({p}.blk_read_time + {p}.blk_write_time + {p}.temp_blk_read_time + {p}.temp_blk_write_time)";
    private static string StatsSinceSql(string p, QueryStatisticsAvailability a) => a.HasEntryTimestamps ? $"{p}.stats_since" : "NULL::timestamptz";
    // Sum of squared execution times from the Welford variance pg_stat_statements keeps: population variance times calls plus total squared over calls.
    private static string SumSquaresSql(string p) => $"({p}.stddev_exec_time * {p}.stddev_exec_time * {p}.calls + CASE WHEN {p}.calls > 0 THEN {p}.total_exec_time * {p}.total_exec_time / {p}.calls ELSE 0 END)";

    public async Task<QueryStatisticsAvailability> DetectAsync(CancellationToken token = default)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        return await DetectAsync(c, token).ConfigureAwait(false);
    }

    private static async Task<QueryStatisticsAvailability> DetectAsync(NpgsqlConnection c, CancellationToken token)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT pg_catalog.current_database(),
                   (SELECT EXISTS (SELECT 1 FROM pg_catalog.regexp_split_to_table(g.setting, '\s*,\s*') AS lib
                                   WHERE pg_catalog.btrim(lib) IN ('pg_stat_statements', '$libdir/pg_stat_statements'))
                    FROM pg_catalog.pg_settings g WHERE g.name = 'shared_preload_libraries'),
                   e.extversion, n.nspname, a.default_version,
                   pg_catalog.pg_has_role(current_user, 'pg_read_all_stats', 'MEMBER')
                   OR (SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname = current_user),
                   (SELECT g.setting = 'on' FROM pg_catalog.pg_settings g WHERE g.name = 'track_io_timing')
            FROM (SELECT 1) AS one
            LEFT JOIN pg_catalog.pg_extension e ON e.extname = 'pg_stat_statements'
            LEFT JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace
            LEFT JOIN pg_catalog.pg_available_extensions a ON a.name = 'pg_stat_statements'
            """, c) { CommandTimeout = 15 };
        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Extension detection returned no row.");
        return new(reader.IsDBNull(1) ? null : reader.GetBoolean(1), !reader.IsDBNull(2), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(3) ? null : reader.GetString(3), !reader.IsDBNull(5) && reader.GetBoolean(5), reader.GetString(0)) { TrackIoTiming = reader.IsDBNull(6) ? null : reader.GetBoolean(6) };
    }

    public Task<QueryStatisticsSnapshot> ReadSnapshotAsync(QueryStatisticsFilter filter, int maximumRows = DefaultMaximumRows, CancellationToken token = default) =>
        ReadSnapshotAsync(filter, null, maximumRows, token);

    /// <summary>With a baseline, each row is the activity since the baseline: current counters minus the baseline counters of the same entry instance
    /// (same user, database, queryid, toplevel and stats_since), or the full counters of an entry created after the baseline. Rows without calls in the
    /// window are omitted, entries hidden from the role cannot be matched, and min/max are unavailable. Ordering and the row limit still run on the server.</summary>
    public async Task<QueryStatisticsSnapshot> ReadSnapshotAsync(QueryStatisticsFilter filter, QueryStatisticsBaseline? baseline, int maximumRows = DefaultMaximumRows, CancellationToken token = default)
    {
        if (maximumRows is < 1 or > DefaultMaximumRows) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        ArgumentNullException.ThrowIfNull(filter);
        filter = filter.Validate();
        if (baseline is not null)
        {
            baseline.Validate();
            if (filter.Order == QueryStatisticsOrder.MaxExecTime) throw new ArgumentException("Max execution time cannot be derived for a time window; choose another ordering.");
        }
        token.ThrowIfCancellationRequested();
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        var availability = await DetectAsync(c, token).ConfigureAwait(false);
        if (!availability.IsReadable) throw new QueryStatisticsUnavailableException(availability);
        // The view lives in the extension's schema; qualify it so search_path cannot redirect the read.
        string schema = SqlIdentifier.Quote(availability.ViewSchema!), view = schema + ".pg_stat_statements";
        (DateTime? StatsReset, long? Deallocations) info = default;
        if (baseline is not null) info = await InfoAsync(c, schema, availability, token).ConfigureAwait(false);
        bool resetSinceBaseline = baseline is not null && info.StatsReset is { } reset && reset > baseline.TakenAt;
        // Without stats_since a full reset cannot be told from the old entries by instance, but every entry now postdates the baseline: count them all.
        var subtracted = resetSinceBaseline && !availability.HasEntryTimestamps ? baseline! with { Entries = [] } : baseline;
        string order = filter.Order switch
        {
            QueryStatisticsOrder.MeanExecTime => "s.mean_exec_time DESC",
            QueryStatisticsOrder.Calls => "s.calls DESC",
            QueryStatisticsOrder.Rows => "s.rows DESC",
            QueryStatisticsOrder.MaxExecTime => "s.max_exec_time DESC",
            QueryStatisticsOrder.StddevExecTime => "s.stddev_exec_time DESC",
            QueryStatisticsOrder.SharedBlocks => "s.shared_blks_hit + s.shared_blks_read DESC",
            QueryStatisticsOrder.SharedBlocksRead => "s.shared_blks_read DESC",
            QueryStatisticsOrder.TempBlocks => "s.temp_blks_read + s.temp_blks_written DESC",
            QueryStatisticsOrder.IoTime => "s.io_time DESC",
            QueryStatisticsOrder.WalBytes => "s.wal_bytes DESC",
            _ => "s.total_exec_time DESC"
        };
        // Both modes produce the same columns in "s"; "j" also keeps entries a window cannot measure, so they can be counted.
        string source = baseline is null
            ? $"""
              j AS (SELECT p.queryid, p.userid, p.dbid, p.toplevel, p.query, {StatsSinceSql("p", availability)} AS stats_since, p.calls, p.total_exec_time, p.mean_exec_time, p.min_exec_time, p.max_exec_time,
                           p.stddev_exec_time, p.rows, p.shared_blks_hit, p.shared_blks_read, p.temp_blks_read, p.temp_blks_written, p.wal_bytes::bigint AS wal_bytes,
                           {IoTimeSql("p", availability)} AS io_time, true AS usable
                    FROM {view} p),
              s AS (SELECT * FROM j)
              """
            : $"""
              b AS (SELECT * FROM ROWS FROM (pg_catalog.unnest(@bu), pg_catalog.unnest(@bd), pg_catalog.unnest(@bq), pg_catalog.unnest(@bt), pg_catalog.unnest(@bs),
                                             pg_catalog.unnest(@bc), pg_catalog.unnest(@btot), pg_catalog.unnest(@bsq), pg_catalog.unnest(@br), pg_catalog.unnest(@bh),
                                             pg_catalog.unnest(@bre), pg_catalog.unnest(@btr), pg_catalog.unnest(@btw), pg_catalog.unnest(@bw), pg_catalog.unnest(@bio))
                    AS b(userid, dbid, queryid, toplevel, stats_since, calls, total, sumsq, rows, hit, rd, tr, tw, wal, io)),
              q AS (SELECT p.*, {StatsSinceSql("p", availability)} AS since, {IoTimeSql("p", availability)} AS io FROM {view} p WHERE p.queryid IS NOT NULL),
              j AS (SELECT p.queryid, p.userid, p.dbid, p.toplevel, p.query, p.since AS stats_since,
                           p.calls - coalesce(b.calls, 0) AS calls,
                           p.total_exec_time - coalesce(b.total, 0) AS total_exec_time,
                           {SumSquaresSql("p")} - coalesce(b.sumsq, 0) AS sumsq,
                           p.rows - coalesce(b.rows, 0) AS rows,
                           p.shared_blks_hit - coalesce(b.hit, 0) AS shared_blks_hit,
                           p.shared_blks_read - coalesce(b.rd, 0) AS shared_blks_read,
                           p.temp_blks_read - coalesce(b.tr, 0) AS temp_blks_read,
                           p.temp_blks_written - coalesce(b.tw, 0) AS temp_blks_written,
                           p.wal_bytes::bigint - coalesce(b.wal, 0) AS wal_bytes,
                           p.io - coalesce(b.io, 0) AS io_time,
                           -- Unmatched: created after the baseline (by stats_since, or by absence when it is unknown). Fewer calls than the baseline: a recreated entry.
                           CASE WHEN b.queryid IS NULL THEN p.since IS NULL OR p.since >= @windowStart ELSE p.calls >= b.calls END AS usable
                    FROM q p
                    LEFT JOIN b ON b.userid = p.userid AND b.dbid = p.dbid AND b.queryid = p.queryid AND b.toplevel = p.toplevel AND b.stats_since IS NOT DISTINCT FROM p.since),
              s AS (SELECT j.queryid, j.userid, j.dbid, j.toplevel, j.query, j.stats_since, j.calls, j.total_exec_time,
                           j.total_exec_time / j.calls AS mean_exec_time, 'NaN'::float8 AS min_exec_time, 'NaN'::float8 AS max_exec_time,
                           pg_catalog.sqrt(GREATEST(j.sumsq / j.calls - (j.total_exec_time / j.calls) * (j.total_exec_time / j.calls), 0)) AS stddev_exec_time,
                           j.rows, j.shared_blks_hit, j.shared_blks_read, j.temp_blks_read, j.temp_blks_written, j.wal_bytes, j.io_time, j.usable
                    FROM j WHERE j.usable AND j.calls > 0)
              """;
        // The outer single row always exists, so an empty result still carries the server instant and the unmeasurable count.
        await using var cmd = new NpgsqlCommand($"""
            WITH {source}
            SELECT x.queryid, x.datname, x.rolname, x.toplevel, x.calls, x.total_exec_time, x.mean_exec_time, x.min_exec_time, x.max_exec_time, x.stddev_exec_time, x.rows,
                   x.shared_blks_hit, x.shared_blks_read, x.temp_blks_read, x.temp_blks_written, x.wal_bytes, x.query, x.stats_since, pg_catalog.statement_timestamp(),
                   x.userid, x.dbid, x.io_time, k.unusable
            FROM (SELECT count(*) FILTER (WHERE NOT j.usable) AS unusable FROM j) k
            LEFT JOIN LATERAL (
                SELECT s.queryid, coalesce(d.datname, '') AS datname, coalesce(r.rolname, '') AS rolname, s.toplevel, s.calls,
                       s.total_exec_time, s.mean_exec_time, s.min_exec_time, s.max_exec_time, s.stddev_exec_time, s.rows,
                       s.shared_blks_hit, s.shared_blks_read, s.temp_blks_read, s.temp_blks_written, s.wal_bytes,
                       coalesce(s.query, '') AS query, s.stats_since, s.userid, s.dbid, s.io_time
                FROM s
                LEFT JOIN pg_catalog.pg_database d ON d.oid = s.dbid
                LEFT JOIN pg_catalog.pg_roles r ON r.oid = s.userid
                WHERE (@database = '' OR pg_catalog.strpos(pg_catalog.lower(coalesce(d.datname, '')), pg_catalog.lower(@database)) > 0)
                  AND (@user = '' OR pg_catalog.strpos(pg_catalog.lower(coalesce(r.rolname, '')), pg_catalog.lower(@user)) > 0)
                  AND (@query = '' OR pg_catalog.strpos(pg_catalog.lower(coalesce(s.query, '')), pg_catalog.lower(@query)) > 0)
                  AND s.calls >= @minimumCalls
                ORDER BY {order}, s.queryid, s.dbid, s.userid, s.toplevel
                LIMIT @limit) x ON true
            """, c) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("limit", maximumRows + 1);
        cmd.Parameters.AddWithValue("database", filter.Database);
        cmd.Parameters.AddWithValue("user", filter.User);
        cmd.Parameters.AddWithValue("query", filter.Query);
        cmd.Parameters.Add("minimumCalls", NpgsqlDbType.Bigint).Value = filter.MinimumCalls;
        if (subtracted is not null) AddBaseline(cmd, subtracted);
        var rows = new List<QueryStatisticsEntry>();
        DateTime observed = default; long unusable = 0;
        NpgsqlDataReader reader;
        try { reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false); }
        catch (PostgresException ex) when (ex.SqlState == "55000" && availability.LibraryLoaded is null) { throw new QueryStatisticsUnavailableException(availability with { LibraryLoaded = false }); }
        await using (reader)
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                observed = reader.GetDateTime(18); unusable = reader.GetInt64(22);
                if (reader.IsDBNull(4)) continue; // the single row of an empty result
                rows.Add(new(reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(4),
                    reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9), reader.GetInt64(10),
                    reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15),
                    reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetDateTime(17))
                { UserOid = reader.GetFieldValue<uint>(19), DatabaseOid = reader.GetFieldValue<uint>(20), IoTimeMs = reader.GetDouble(21), IsWindowed = baseline is not null });
            }
        if (baseline is not null && baseline.TakenAt > observed) throw new QueryStatisticsBaselineException("The baseline is later than the server clock: it was not taken on this server, or the clock moved back. Set a new baseline.");
        bool truncated = rows.Count > maximumRows;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return new(rows.AsReadOnly(), truncated, maximumRows, observed, availability)
        {
            WindowStart = baseline?.TakenAt, UnusableEntries = unusable,
            EvictedSinceWindowStart = baseline?.Deallocations is { } before && info.Deallocations is { } now ? Math.Max(0, now - before) : null,
            ResetSinceWindowStart = resetSinceBaseline
        };
    }

    private static void AddBaseline(NpgsqlCommand cmd, QueryStatisticsBaseline baseline)
    {
        var e = baseline.Entries;
        void Add<T>(string name, NpgsqlDbType type, Func<QueryStatisticsCounters, T> value) => cmd.Parameters.Add(name, NpgsqlDbType.Array | type).Value = e.Select(value).ToArray();
        Add("bu", NpgsqlDbType.Oid, x => x.UserOid); Add("bd", NpgsqlDbType.Oid, x => x.DatabaseOid); Add("bq", NpgsqlDbType.Bigint, x => x.QueryId);
        Add("bt", NpgsqlDbType.Boolean, x => x.TopLevel); Add("bs", NpgsqlDbType.TimestampTz, x => x.StatsSince); Add("bc", NpgsqlDbType.Bigint, x => x.Calls);
        Add("btot", NpgsqlDbType.Double, x => x.TotalExecMs); Add("bsq", NpgsqlDbType.Double, x => x.SumSquaresMs); Add("br", NpgsqlDbType.Bigint, x => x.Rows);
        Add("bh", NpgsqlDbType.Bigint, x => x.SharedBlocksHit); Add("bre", NpgsqlDbType.Bigint, x => x.SharedBlocksRead); Add("btr", NpgsqlDbType.Bigint, x => x.TempBlocksRead);
        Add("btw", NpgsqlDbType.Bigint, x => x.TempBlocksWritten); Add("bw", NpgsqlDbType.Bigint, x => x.WalBytes); Add("bio", NpgsqlDbType.Double, x => x.IoTimeMs);
        cmd.Parameters.Add("windowStart", NpgsqlDbType.TimestampTz).Value = baseline.TakenAt;
    }

    // pg_stat_statements_info (1.9+): last full reset and the number of entries evicted to make room for new ones.
    private static async Task<(DateTime? StatsReset, long? Deallocations)> InfoAsync(NpgsqlConnection c, string schema, QueryStatisticsAvailability availability, CancellationToken token)
    {
        await using var cmd = new NpgsqlCommand($"SELECT i.stats_reset, i.dealloc FROM {schema}.pg_stat_statements_info i", c) { CommandTimeout = 15 };
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return default;
            return (reader.IsDBNull(0) ? null : reader.GetDateTime(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
        }
        catch (PostgresException ex) when (ex.SqlState == "55000" && availability.LibraryLoaded is null) { throw new QueryStatisticsUnavailableException(availability with { LibraryLoaded = false }); }
    }

    /// <summary>Reads the counters (no SQL text) of every entry visible to the role at one server instant, for later window reads.
    /// Entries whose queryid PostgreSQL hides cannot be matched later and are skipped.</summary>
    public async Task<QueryStatisticsBaseline> CaptureBaselineAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        var availability = await DetectAsync(c, token).ConfigureAwait(false);
        if (!availability.IsReadable) throw new QueryStatisticsUnavailableException(availability);
        string schema = SqlIdentifier.Quote(availability.ViewSchema!);
        // The instant is read before the counters: an entry created in between is either among the counters (matched later) or newer than the instant.
        var info = await InfoAsync(c, schema, availability, token).ConfigureAwait(false);
        DateTime takenAt = await ServerTimeAsync(c, token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"""
            SELECT p.userid, p.dbid, p.queryid, p.toplevel, {StatsSinceSql("p", availability)}, p.calls, p.total_exec_time, {SumSquaresSql("p")}, p.rows,
                   p.shared_blks_hit, p.shared_blks_read, p.temp_blks_read, p.temp_blks_written, p.wal_bytes::bigint, {IoTimeSql("p", availability)}
            FROM {schema}.pg_stat_statements(false) p
            WHERE p.queryid IS NOT NULL
            ORDER BY p.queryid, p.dbid, p.userid, p.toplevel
            LIMIT @limit
            """, c) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("limit", QueryStatisticsBaseline.MaximumEntries + 1);
        var entries = new List<QueryStatisticsCounters>();
        await using (var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                entries.Add(new(reader.GetFieldValue<uint>(0), reader.GetFieldValue<uint>(1), reader.GetInt64(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.GetInt64(5),
                    reader.GetDouble(6), Math.Max(0, reader.GetDouble(7)), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12),
                    reader.GetInt64(13), reader.GetDouble(14)));
        if (entries.Count > QueryStatisticsBaseline.MaximumEntries) throw new QueryStatisticsBaselineException($"More than {QueryStatisticsBaseline.MaximumEntries} statements are tracked; a baseline cannot hold them all.");
        // Entries created after the instant count fully in a later window, so they must not be subtracted.
        entries.RemoveAll(e => e.StatsSince is { } since && since > takenAt);
        return new QueryStatisticsBaseline(takenAt, info.StatsReset, info.Deallocations, entries.AsReadOnly()).Validate();
    }

    private static async Task<DateTime> ServerTimeAsync(NpgsqlConnection c, CancellationToken token)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_catalog.statement_timestamp()", c) { CommandTimeout = 15 };
        return (DateTime)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    /// <summary>Discards every pg_stat_statements entry on the server. PostgreSQL authorises the call; returns the reset instant it reports.</summary>
    public Task<DateTime> ResetAsync(CancellationToken token = default) => ResetCoreAsync(null, token);

    /// <summary>Discards only the entry for the selected statement, database and user via pg_stat_statements_reset(userid, dbid, queryid).
    /// An entry already gone is not an error for the server; callers refresh to observe the outcome. Entries hidden from the role have no queryid and cannot be addressed.</summary>
    public Task<DateTime> ResetAsync(QueryStatisticsEntry selected, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (!selected.CanResetIndividually) throw new ArgumentException("The selected entry has no query id visible to this role and cannot be reset individually.");
        return ResetCoreAsync(selected, token);
    }

    private async Task<DateTime> ResetCoreAsync(QueryStatisticsEntry? selected, CancellationToken token)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        var availability = await DetectAsync(c, token).ConfigureAwait(false);
        if (!availability.IsReadable) throw new QueryStatisticsUnavailableException(availability);
        string function = SqlIdentifier.Quote(availability.ViewSchema!) + ".pg_stat_statements_reset";
        await using var cmd = new NpgsqlCommand(selected is null ? $"SELECT {function}()" : $"SELECT {function}(@userid, @dbid, @queryid)", c) { CommandTimeout = 15 };
        if (selected is not null)
        {
            cmd.Parameters.Add("userid", NpgsqlDbType.Oid).Value = selected.UserOid;
            cmd.Parameters.Add("dbid", NpgsqlDbType.Oid).Value = selected.DatabaseOid;
            cmd.Parameters.Add("queryid", NpgsqlDbType.Bigint).Value = selected.QueryId!.Value;
        }
        object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
        return result is DateTime at ? at : await ServerTimeAsync(c, token).ConfigureAwait(false);
    }
}

public sealed class QueryStatisticsUnavailableException(QueryStatisticsAvailability availability) : InvalidOperationException(availability.Describe())
{
    public QueryStatisticsAvailability Availability { get; } = availability;
}

/// <summary>A window baseline cannot be taken or used: too many entries, or it does not fit this server's timeline.</summary>
public sealed class QueryStatisticsBaselineException(string message) : InvalidOperationException(message);
