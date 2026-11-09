using System.Globalization;
using Npgsql;

namespace PGMS.PostgreSql;

// Append new values only: menu item names are derived from them.
public enum ServerReportKind
{
    ServerDashboard, Configuration, AllSessions, AllBlockingTransactions, TopTransactionsByAge, TopQueriesByTotalTime, TopQueriesByAverageTime, TopQueriesByTotalIo,
    DiskUsage, DiskUsageByTopTables, IndexUsageStatistics, DatabaseBlockingTransactions
}

public sealed record ServerReportDefinition(ServerReportKind Kind, string Title, bool DatabaseScope, string Description);

/// <summary>One table of a report. Rows keep the server's types (numbers, booleans, text) so a grid sorts them correctly.</summary>
public sealed record ServerReportTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows)
{
    public bool IsTruncated { get; init; }
}

public sealed record ServerReport(ServerReportDefinition Definition, string Target, string ServerTime, IReadOnlyList<ServerReportTable> Tables, IReadOnlyList<string> Notes);

/// <summary>PostgreSQL counterparts of the SSMS 22.11 Standard Reports (docs/MARCO-M1035.md). Each report is read in a read-only transaction;
/// reports with no PostgreSQL data source (history of configuration or schema changes, scheduler, memory clerks, cursors) are not offered.</summary>
public sealed class ServerReportService(string connectionString)
{
    public const int MaximumRows = 1000;
    public const int TopQueries = 20;

    public static IReadOnlyList<ServerReportDefinition> ServerReports { get; } =
    [
        new(ServerReportKind.ServerDashboard, "Server Dashboard", false, "Version, uptime, databases with size and activity counters, and client sessions by state."),
        new(ServerReportKind.Configuration, "Configuration (Non-Default Settings)", false, "Server settings whose value does not come from the built-in default, with where each value is defined. PostgreSQL keeps no history of configuration changes."),
        new(ServerReportKind.AllSessions, "Activity - All Sessions", false, "Every client session on the server with state, wait and last statement."),
        new(ServerReportKind.AllBlockingTransactions, "Activity - All Blocking Transactions", false, "Sessions waiting for a lock and the sessions holding it (pg_blocking_pids)."),
        new(ServerReportKind.TopTransactionsByAge, "Activity - Top Transactions by Age", false, "Open transactions, oldest first. Long transactions hold back VACUUM and keep locks."),
        new(ServerReportKind.TopQueriesByTotalTime, "Performance - Top Queries by Total Time", false, $"The {TopQueries} statements with the most total execution time (pg_stat_statements)."),
        new(ServerReportKind.TopQueriesByAverageTime, "Performance - Top Queries by Average Time", false, $"The {TopQueries} statements with the highest average execution time (pg_stat_statements)."),
        new(ServerReportKind.TopQueriesByTotalIo, "Performance - Top Queries by Total I/O", false, $"The {TopQueries} statements with the most shared-buffer block requests, the PostgreSQL counterpart of logical reads (pg_stat_statements)."),
    ];

    public static IReadOnlyList<ServerReportDefinition> DatabaseReports { get; } =
    [
        new(ServerReportKind.DiskUsage, "Disk Usage", true, "Database size split into table data, indexes, TOAST and maps, system catalogs and the rest, and space by schema."),
        new(ServerReportKind.DiskUsageByTopTables, "Disk Usage by Top Tables", true, $"Tables and materialized views by total size (up to {MaximumRows})."),
        new(ServerReportKind.IndexUsageStatistics, "Index Usage Statistics", true, "Scans, entries read and size of every user index (pg_stat_user_indexes)."),
        new(ServerReportKind.DatabaseBlockingTransactions, "All Blocking Transactions", true, "Sessions in this database waiting for a lock and the sessions holding it."),
    ];

    public static ServerReportDefinition Definition(ServerReportKind kind) => ServerReports.Concat(DatabaseReports).Single(d => d.Kind == kind);

    public async Task<ServerReport> RunAsync(ServerReportKind kind, CancellationToken token = default)
    {
        var definition = Definition(kind);
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        string target = definition.DatabaseScope ? $"{builder.Database} on {builder.Host}:{builder.Port}" : $"{builder.Host}:{builder.Port}";
        var tables = new List<ServerReportTable>();
        var notes = new List<string>();
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        string serverTime = (string)(await Scalar("SELECT to_char(pg_catalog.now(), 'YYYY-MM-DD HH24:MI:SS TZ')").ConfigureAwait(false))!;

        async Task<object?> Scalar(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
            var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return value is DBNull ? null : value;
        }
        async Task Table(string title, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<object?[]>();
            bool truncated = false;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (rows.Count == MaximumRows) { truncated = true; break; }
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            tables.Add(new(title, columns, rows) { IsTruncated = truncated });
        }
        // A single row shown as Property/Value pairs, in column order.
        async Task Properties(string title, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            await reader.ReadAsync(token).ConfigureAwait(false);
            var rows = Enumerable.Range(0, reader.FieldCount).Select(i => new object?[] { reader.GetName(i), reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) }).ToList();
            tables.Add(new(title, ["Property", "Value"], rows));
        }
        const string Timestamp = "'YYYY-MM-DD HH24:MI:SS'";
        static string OneLine(string column) => $"pg_catalog.regexp_replace(pg_catalog.left({column}, 500), '\\s+', ' ', 'g')";
        static string Seconds(string from) => $"pg_catalog.round(EXTRACT(epoch FROM pg_catalog.now() - {from})::numeric, 1)";
        string Blocking(bool currentDatabase) => $"""
            SELECT w.pid AS "Blocked PID", w.usename AS "Blocked user", w.datname AS "Database", {Seconds("w.query_start")} AS "Statement age (s)",
                   w.wait_event_type || ': ' || w.wait_event AS "Wait", {OneLine("w.query")} AS "Blocked statement",
                   k.pid AS "Blocking PID", b.usename AS "Blocking user", b.state AS "Blocking state", {Seconds("b.xact_start")} AS "Blocking transaction age (s)",
                   {OneLine("b.query")} AS "Blocking last statement"
            FROM (SELECT DISTINCT pid FROM pg_catalog.pg_locks WHERE NOT granted AND pid IS NOT NULL) l
            JOIN pg_catalog.pg_stat_activity w ON w.pid = l.pid
            CROSS JOIN LATERAL pg_catalog.unnest(pg_catalog.pg_blocking_pids(l.pid)) AS k(pid)
            LEFT JOIN pg_catalog.pg_stat_activity b ON b.pid = k.pid
            {(currentDatabase ? "WHERE w.datname = pg_catalog.current_database()" : "")}
            ORDER BY w.query_start, w.pid, k.pid
            """;
        const string UserRelations = "n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\\_toast%' AND n.nspname NOT LIKE 'pg\\_temp\\_%'";

        switch (kind)
        {
            case ServerReportKind.ServerDashboard:
                await Properties("Server", $"""
                    SELECT current_setting('server_version') AS "Version", to_char(pg_catalog.pg_postmaster_start_time(), {Timestamp}) AS "Started",
                           date_trunc('second', pg_catalog.now() - pg_catalog.pg_postmaster_start_time())::text AS "Uptime",
                           pg_catalog.pg_is_in_recovery() AS "In recovery (standby)",
                           (SELECT count(*) FROM pg_catalog.pg_database WHERE NOT datistemplate) AS "Databases",
                           (SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE backend_type = 'client backend') AS "Client sessions",
                           current_setting('max_connections') AS "max_connections"
                    """).ConfigureAwait(false);
                await Table("Databases", """
                    SELECT d.datname AS "Database",
                           CASE WHEN pg_catalog.has_database_privilege(d.oid, 'CONNECT') THEN pg_catalog.round(pg_catalog.pg_database_size(d.oid) / 1048576.0, 2) END AS "Size (MB)",
                           s.numbackends AS "Sessions", s.xact_commit AS "Commits", s.xact_rollback AS "Rollbacks",
                           pg_catalog.round(100.0 * s.blks_hit / NULLIF(s.blks_hit + s.blks_read, 0), 2) AS "Cache hit %",
                           s.deadlocks AS "Deadlocks", s.temp_files AS "Temp files", pg_catalog.round(s.temp_bytes / 1048576.0, 2) AS "Temp (MB)",
                           to_char(s.stats_reset, 'YYYY-MM-DD HH24:MI:SS') AS "Statistics reset"
                    FROM pg_catalog.pg_database d LEFT JOIN pg_catalog.pg_stat_database s ON s.datid = d.oid
                    WHERE NOT d.datistemplate ORDER BY d.datname
                    """).ConfigureAwait(false);
                await Table("Client sessions by state", """
                    SELECT coalesce(state, '(not visible to this role)') AS "State", count(*) AS "Sessions"
                    FROM pg_catalog.pg_stat_activity WHERE backend_type = 'client backend' GROUP BY 1 ORDER BY 2 DESC, 1
                    """).ConfigureAwait(false);
                notes.Add("Database sizes are shown only for databases this role can connect to. Counters are cumulative since each database's statistics reset.");
                break;
            case ServerReportKind.Configuration:
                await Table("Settings not at their default", """
                    SELECT name AS "Setting", setting AS "Value", unit AS "Unit", boot_val AS "Default", source AS "Source",
                           CASE WHEN sourcefile IS NOT NULL THEN sourcefile || ':' || sourceline END AS "Defined in", pending_restart AS "Restart pending", category AS "Category"
                    FROM pg_catalog.pg_settings WHERE source NOT IN ('default', 'override', 'client', 'session')
                    ORDER BY category, name
                    """).ConfigureAwait(false);
                notes.Add("PostgreSQL keeps no history of configuration changes, so this lists the current values that differ from the built-in defaults. Values set only for this report's session are left out. The file and line are visible only to superusers and members of pg_read_all_settings.");
                break;
            case ServerReportKind.AllSessions:
                await Table("Client sessions", $"""
                    SELECT pid AS "PID", usename AS "User", datname AS "Database", application_name AS "Application",
                           coalesce(pg_catalog.host(client_addr), CASE WHEN client_port = -1 THEN 'local socket' END) AS "Client",
                           to_char(backend_start, {Timestamp}) AS "Connected at", state AS "State", wait_event_type AS "Wait type", wait_event AS "Wait event",
                           {Seconds("xact_start")} AS "Transaction age (s)", {OneLine("query")} AS "Last statement"
                    FROM pg_catalog.pg_stat_activity WHERE backend_type = 'client backend' AND pid <> pg_catalog.pg_backend_pid() ORDER BY pid
                    """).ConfigureAwait(false);
                notes.Add("The session reading this report is left out. Without superuser or pg_read_all_stats, state and statements of other users' sessions are hidden.");
                break;
            case ServerReportKind.AllBlockingTransactions:
            case ServerReportKind.DatabaseBlockingTransactions:
                await Table("Blocked sessions", Blocking(kind == ServerReportKind.DatabaseBlockingTransactions)).ConfigureAwait(false);
                notes.Add("Each row is one session waiting for a lock and one session it waits for; a session blocked by several appears once per blocker.");
                break;
            case ServerReportKind.TopTransactionsByAge:
                await Table("Open transactions", $"""
                    SELECT pid AS "PID", usename AS "User", datname AS "Database", to_char(xact_start, {Timestamp}) AS "Transaction start",
                           {Seconds("xact_start")} AS "Age (s)", state AS "State", backend_xid::text AS "Transaction ID", backend_xmin::text AS "Snapshot xmin",
                           wait_event_type AS "Wait type", {OneLine("query")} AS "Last statement"
                    FROM pg_catalog.pg_stat_activity WHERE xact_start IS NOT NULL AND pid <> pg_catalog.pg_backend_pid()
                    ORDER BY xact_start, pid
                    """).ConfigureAwait(false);
                notes.Add("The session reading this report is left out. A transaction ID appears only after the transaction writes.");
                break;
            case ServerReportKind.TopQueriesByTotalTime or ServerReportKind.TopQueriesByAverageTime or ServerReportKind.TopQueriesByTotalIo:
                await transaction.CommitAsync(token).ConfigureAwait(false);
                await TopQueriesAsync(kind, tables, notes, token).ConfigureAwait(false);
                return new(definition, target, serverTime, tables, notes);
            case ServerReportKind.DiskUsage:
                await Table("Space used", $"""
                    WITH rel AS (
                        SELECT NOT ({UserRelations}) AS system, pg_catalog.pg_relation_size(c.oid) AS main,
                               pg_catalog.pg_table_size(c.oid) - pg_catalog.pg_relation_size(c.oid) AS extra, pg_catalog.pg_indexes_size(c.oid) AS idx
                        -- Shared catalogs (pg_database, pg_authid, …) live in the global tablespace, outside pg_database_size.
                        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'm') AND NOT c.relisshared),
                    t AS (SELECT pg_catalog.pg_database_size(pg_catalog.current_database()) AS total)
                    SELECT x.category AS "Category", pg_catalog.round(x.bytes / 1048576.0, 2) AS "Size (MB)", pg_catalog.round(100.0 * x.bytes / NULLIF(t.total, 0), 1) AS "% of database"
                    FROM t CROSS JOIN LATERAL (VALUES
                        (1, 'Table data', (SELECT coalesce(sum(main), 0) FROM rel WHERE NOT system)),
                        (2, 'Indexes', (SELECT coalesce(sum(idx), 0) FROM rel WHERE NOT system)),
                        (3, 'TOAST and free space/visibility maps', (SELECT coalesce(sum(extra), 0) FROM rel WHERE NOT system)),
                        (4, 'System catalogs', (SELECT coalesce(sum(main + extra + idx), 0) FROM rel WHERE system)),
                        (5, 'Other (sequences, files outside tables)', t.total - (SELECT coalesce(sum(main + extra + idx), 0) FROM rel)),
                        (6, 'Total', t.total)) AS x(ord, category, bytes)
                    ORDER BY x.ord
                    """).ConfigureAwait(false);
                await Table("Space by schema", $"""
                    SELECT n.nspname AS "Schema", count(*) AS "Tables",
                           pg_catalog.round(sum(pg_catalog.pg_table_size(c.oid)) / 1048576.0, 2) AS "Data (MB)",
                           pg_catalog.round(sum(pg_catalog.pg_indexes_size(c.oid)) / 1048576.0, 2) AS "Indexes (MB)",
                           pg_catalog.round(sum(pg_catalog.pg_total_relation_size(c.oid)) / 1048576.0, 2) AS "Total (MB)"
                    FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    WHERE c.relkind IN ('r', 'm') AND {UserRelations}
                    GROUP BY n.nspname ORDER BY sum(pg_catalog.pg_total_relation_size(c.oid)) DESC, n.nspname
                    """).ConfigureAwait(false);
                notes.Add("Sizes are files on disk, including dead rows not yet reclaimed by VACUUM. Data in \"Space by schema\" includes TOAST and maps.");
                break;
            case ServerReportKind.DiskUsageByTopTables:
                await Table("Tables by total size", $"""
                    SELECT n.nspname AS "Schema", c.relname AS "Table", CASE c.relkind WHEN 'm' THEN 'materialized view' ELSE 'table' END AS "Type",
                           CASE WHEN c.reltuples >= 0 THEN c.reltuples::bigint END AS "Rows (estimate)",
                           pg_catalog.pg_total_relation_size(c.oid) / 1024 AS "Reserved (KB)", pg_catalog.pg_relation_size(c.oid) / 1024 AS "Data (KB)",
                           pg_catalog.pg_indexes_size(c.oid) / 1024 AS "Indexes (KB)",
                           (pg_catalog.pg_table_size(c.oid) - pg_catalog.pg_relation_size(c.oid)) / 1024 AS "TOAST and maps (KB)"
                    FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    WHERE c.relkind IN ('r', 'm') AND {UserRelations}
                    ORDER BY pg_catalog.pg_total_relation_size(c.oid) DESC, n.nspname, c.relname
                    """).ConfigureAwait(false);
                notes.Add("Row counts are the planner estimate from the last VACUUM or ANALYZE; empty when the table was never analyzed. Partitioned tables are listed by partition.");
                break;
            case ServerReportKind.IndexUsageStatistics:
                await Table("User indexes", $"""
                    SELECT s.schemaname AS "Schema", s.relname AS "Table", s.indexrelname AS "Index",
                           CASE WHEN i.indisprimary THEN 'primary key' WHEN i.indisunique THEN 'unique' ELSE '' END AS "Kind",
                           s.idx_scan AS "Scans", to_char(s.last_idx_scan, {Timestamp}) AS "Last scan", s.idx_tup_read AS "Entries read", s.idx_tup_fetch AS "Rows fetched",
                           pg_catalog.pg_relation_size(s.indexrelid) / 1024 AS "Size (KB)", i.indisvalid AS "Valid"
                    FROM pg_catalog.pg_stat_user_indexes s JOIN pg_catalog.pg_index i ON i.indexrelid = s.indexrelid
                    ORDER BY s.schemaname, s.relname, s.indexrelname
                    """).ConfigureAwait(false);
                var reset = await Scalar($"SELECT to_char(stats_reset, {Timestamp}) FROM pg_catalog.pg_stat_database WHERE datname = pg_catalog.current_database()").ConfigureAwait(false);
                notes.Add($"Counters are cumulative since {(reset is null ? "statistics were last reset or the server was initialized" : "the statistics reset at " + reset)}. An index with no scans may be unused, unless it enforces a key. Statistics reach this view with a short delay.");
                break;
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new(definition, target, serverTime, tables, notes);
    }

    private async Task TopQueriesAsync(ServerReportKind kind, List<ServerReportTable> tables, List<string> notes, CancellationToken token)
    {
        var statements = new QueryStatisticsService(connectionString);
        var availability = await statements.DetectAsync(token).ConfigureAwait(false);
        if (!availability.IsReadable) { notes.Add(availability.Describe()); return; }
        var order = kind switch { ServerReportKind.TopQueriesByAverageTime => QueryStatisticsOrder.MeanExecTime, ServerReportKind.TopQueriesByTotalIo => QueryStatisticsOrder.SharedBlocks, _ => QueryStatisticsOrder.TotalExecTime };
        var snapshot = await statements.ReadSnapshotAsync(new QueryStatisticsFilter(Order: order), TopQueries, token).ConfigureAwait(false);
        static string Line(string sql) => System.Text.RegularExpressions.Regex.Replace(sql.Length > 500 ? sql[..500] : sql, @"\s+", " ");
        var rows = snapshot.Rows.Select(r => new object?[]
        {
            Line(r.Query), r.Database, r.User, r.Calls, Math.Round(r.TotalExecMs, 3), Math.Round(r.MeanExecMs, 3), r.Rows, r.SharedBlocks, r.SharedBlocksRead,
            r.HitRatio is { } hit ? Math.Round(hit * 100, 2) : null, Math.Round(r.IoTimeMs, 3)
        }).ToList();
        tables.Add(new("Statements", ["Query", "Database", "User", "Calls", "Total time (ms)", "Average time (ms)", "Rows", "Shared blocks", "Shared blocks read", "Cache hit %", "I/O time (ms)"], rows));
        notes.Add("Counters are cumulative since the last pg_stat_statements reset, for all databases on the server. Query Statistics (View menu) offers filters, time windows and plans.");
        if (!availability.CanReadAllQueries) notes.Add("Statements of other users are shown as <insufficient privilege> because the connected role is not a member of pg_read_all_stats.");
        if (kind == ServerReportKind.TopQueriesByTotalIo && availability.TrackIoTiming == false) notes.Add("track_io_timing is off, so I/O time stays 0.");
    }
}
