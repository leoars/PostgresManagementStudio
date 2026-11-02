using Npgsql;
using PGMS.PostgreSql;

/// <summary>pg_stat_statements read-only service against the isolated cluster, which the runner starts with the library preloaded.</summary>
internal static class QueryStatisticsChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12], marker = "pgms_stats_" + suffix, role = "stats_reader_" + suffix, missingDb = "pgms_stats_missing_" + suffix;
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        bool extensionCreated = false, roleCreated = false, dbCreated = false;
        var service = new QueryStatisticsService(connection);
        try
        {
            // Library loaded by the runner, extension absent in a fresh database: detection must explain without installing.
            await using (var create = new NpgsqlCommand($"CREATE DATABASE {SqlIdentifier.Quote(missingDb)}", admin)) { await create.ExecuteNonQueryAsync(); dbCreated = true; }
            var missingService = new QueryStatisticsService(new NpgsqlConnectionStringBuilder(connection) { Database = missingDb }.ConnectionString);
            var missing = await missingService.DetectAsync();
            check(missing.LibraryLoaded == true && !missing.ExtensionInstalled && !missing.IsReadable && missing.Database == missingDb, "Detection reports loaded library with extension absent from the connected database");
            check(missing.Describe().Contains("CREATE EXTENSION") && missing.Describe().Contains("PGMS does not install"), "Unavailable description names the manual step and disclaims installation");
            bool unavailable = false;
            try { await missingService.ReadSnapshotAsync(new QueryStatisticsFilter()); } catch (QueryStatisticsUnavailableException ex) when (!ex.Availability.ExtensionInstalled) { unavailable = true; }
            check(unavailable, "Reading without the extension raises an availability error instead of SQL failure");
            await using (var missingConnection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = missingDb }.ConnectionString))
            {
                await missingConnection.OpenAsync();
                await using var still = new NpgsqlCommand("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname='pg_stat_statements'", missingConnection);
                check((long)(await still.ExecuteScalarAsync())! == 0, "Detection and read attempts did not create the extension");
            }
            var notLoaded = missing with { LibraryLoaded = false };
            check(!notLoaded.IsReadable && notLoaded.Describe().Contains("shared_preload_libraries"), "Description for an unloaded library points to server configuration");

            await using (var exists = new NpgsqlCommand("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname='pg_stat_statements'", admin))
                if ((long)(await exists.ExecuteScalarAsync())! == 0) { await using var create = new NpgsqlCommand("CREATE EXTENSION pg_stat_statements", admin); await create.ExecuteNonQueryAsync(); extensionCreated = true; }
            var available = await service.DetectAsync();
            check(available.IsReadable && available.CanReadAllQueries && available.InstalledVersion is { Length: > 0 } && available.ViewSchema is { Length: > 0 }, "Detection reports installed version and schema for a superuser reader");

            // Distinct statements identified by a table name, which normalisation keeps (constants become $n).
            await using (var create = new NpgsqlCommand($"CREATE TABLE {SqlIdentifier.Quote(marker)}(id int)", admin)) await create.ExecuteNonQueryAsync();
            for (int i = 0; i < 3; i++) await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {SqlIdentifier.Quote(marker)}", admin)) await count.ExecuteScalarAsync();
            await using (var slow = new NpgsqlCommand($"SELECT count(*), pg_sleep(0.05) FROM {SqlIdentifier.Quote(marker)}", admin)) await slow.ExecuteScalarAsync();
            var byQuery = new QueryStatisticsFilter(Query: marker);
            var snapshot = await service.ReadSnapshotAsync(byQuery);
            var counted = snapshot.Rows.Single(r => r.Query.StartsWith("SELECT count(*) FROM", StringComparison.Ordinal) && !r.Query.Contains("pg_sleep"));
            check(counted.Calls == 3 && counted.Rows == 3 && counted.Database == "postgres" && counted.User == "pgms_test" && counted.TopLevel, "Repeated statement is aggregated with calls, rows, database and user");
            check(counted.TotalExecMs >= 0 && Math.Abs(counted.MeanExecMs - counted.TotalExecMs / 3) < 0.001 && counted.MinExecMs <= counted.MaxExecMs, "Mean equals total over calls and min does not exceed max");
            check(counted.StatsSince is { Kind: DateTimeKind.Utc } && snapshot.ObservedAt.Kind == DateTimeKind.Utc && snapshot.ObservedAt >= counted.StatsSince, "Stats since and observation are server UTC instants");
            check(counted.HitRatio is null or (>= 0 and <= 1) && !counted.QueryTextRestricted, "Hit ratio stays within [0,1] or is unknown; query text visible to superuser");
            check((counted with { SharedBlocksHit = 3, SharedBlocksRead = 1 }).HitRatio == 0.75 && (counted with { SharedBlocksHit = 0, SharedBlocksRead = 0 }).HitRatioLabel == "—", "Hit ratio is hits over hits plus reads and unknown without block access");
            var slept = snapshot.Rows.Single(r => r.Query.Contains("pg_sleep"));
            check(slept.Calls == 1 && slept.TotalExecMs >= 50, "Slow statement reports server execution time of at least the sleep");
            check(snapshot.Rows.Count == 3 && !snapshot.IsTruncated && snapshot.Availability.IsReadable, "Query text filter matches the three statements naming the table, including the utility command");

            check((await service.ReadSnapshotAsync(byQuery with { Database = "no_such_database" })).Rows.Count == 0, "Database filter can yield an empty result");
            check((await service.ReadSnapshotAsync(byQuery with { User = "PGMS_TE" })).Rows.Count == 3, "User filter is a case-insensitive literal fragment");
            check((await service.ReadSnapshotAsync(byQuery with { MinimumCalls = 3 })).Rows.Single().Calls == 3 && (await service.ReadSnapshotAsync(byQuery with { MinimumCalls = 4 })).Rows.Count == 0, "Minimum calls excludes statements below the threshold");
            check((await service.ReadSnapshotAsync(byQuery with { Order = QueryStatisticsOrder.TotalExecTime })).Rows[0].Query.Contains("pg_sleep"), "Total time ordering places the slow statement first");
            check((await service.ReadSnapshotAsync(byQuery with { Order = QueryStatisticsOrder.MaxExecTime })).Rows[0].Query.Contains("pg_sleep"), "Max time ordering places the slow statement first");
            check((await service.ReadSnapshotAsync(byQuery with { Order = QueryStatisticsOrder.Calls })).Rows[0].Calls == 3, "Calls ordering places the repeated statement first");
            await MetricChecks(admin, service, "pgms_io_" + suffix, check);
            var truncated = await service.ReadSnapshotAsync(byQuery, 2);
            check(truncated.IsTruncated && truncated.Rows.Count == 2 && truncated.MaximumRows == 2, "Sentinel row detects more statements beyond the limit");
            var exact = await service.ReadSnapshotAsync(byQuery, 3);
            check(!exact.IsTruncated && exact.Rows.Count == 3, "Exactly the limit is not reported as truncated");
            foreach (var (bad, label) in new (QueryStatisticsFilter, string)[] { (new(Query: new string('x', 257)), "overlong filter"), (new(MinimumCalls: -1), "negative minimum calls"), (new(Order: (QueryStatisticsOrder)99), "undefined ordering") })
            {
                bool refused = false; try { await service.ReadSnapshotAsync(bad); } catch (ArgumentException) { refused = true; }
                check(refused, "Service rejects " + label + " before connecting");
            }
            bool range = false; try { await service.ReadSnapshotAsync(byQuery, 0); } catch (ArgumentOutOfRangeException) { range = true; }
            check(range, "Service rejects a zero row limit");
            bool cancelled = false; try { await service.ReadSnapshotAsync(byQuery, 10, new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Pre-cancelled token stops the read before any query");

            await using (var create = new NpgsqlCommand($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN", admin)) { await create.ExecuteNonQueryAsync(); roleCreated = true; }
            string limited = new NpgsqlConnectionStringBuilder(connection) { Username = role, Pooling = false }.ConnectionString;
            var limitedService = new QueryStatisticsService(limited);
            var limitedAvailability = await limitedService.DetectAsync();
            check(limitedAvailability.IsReadable && !limitedAvailability.CanReadAllQueries && limitedAvailability.Describe().Contains("pg_read_all_stats"), "Ordinary role can read the view but is told other users' SQL is hidden");
            check(limitedAvailability.LibraryLoaded is null && (limitedAvailability with { ExtensionInstalled = false }).Describe().Contains("cannot read shared_preload_libraries"), "Ordinary role cannot see shared_preload_libraries and the description says so instead of guessing");
            var hidden = await limitedService.ReadSnapshotAsync(new QueryStatisticsFilter(User: "pgms_test", Order: QueryStatisticsOrder.Calls));
            check(hidden.Rows.Count > 0 && hidden.Rows.All(r => r.QueryTextRestricted && r.QueryId is null && r.Calls > 0), "PostgreSQL hides foreign query text and query id while exposing counters to the ordinary role");
            check((await limitedService.ReadSnapshotAsync(byQuery)).Rows.Count == 0, "Query text filter cannot match SQL hidden from the ordinary role");
            bool resetRefused = false; try { await limitedService.ResetAsync(); } catch (PostgresException ex) when (ex.SqlState == "42501") { resetRefused = true; }
            check(resetRefused, "Reset by the ordinary role is refused by PostgreSQL");
            check((await service.ReadSnapshotAsync(byQuery)).Rows.Count == 3, "Refused reset did not discard statistics");
            await using (var grant = new NpgsqlCommand($"GRANT pg_read_all_stats TO {SqlIdentifier.Quote(role)}", admin)) await grant.ExecuteNonQueryAsync();
            var granted = await limitedService.ReadSnapshotAsync(byQuery);
            check((await limitedService.DetectAsync()).CanReadAllQueries && granted.Rows.Count == 3 && granted.Rows.All(r => !r.QueryTextRestricted && r.QueryId is not null), "pg_read_all_stats membership reveals the SQL to the same role");
            resetRefused = false; try { await limitedService.ResetAsync(); } catch (PostgresException ex) when (ex.SqlState == "42501") { resetRefused = true; }
            check(resetRefused, "pg_read_all_stats does not authorise the reset");

            var selectiveAt = await service.ResetAsync(counted);
            var afterSelective = await service.ReadSnapshotAsync(byQuery);
            check(selectiveAt.Kind == DateTimeKind.Utc && afterSelective.Rows.Count == 2 && afterSelective.Rows.All(r => r.QueryId != counted.QueryId) && afterSelective.Rows.Any(r => r.Query.Contains("pg_sleep")), "Selective reset discards only the selected statement entry");
            await service.ResetAsync(counted);
            check((await service.ReadSnapshotAsync(byQuery)).Rows.Count == 2, "Selective reset of an entry already gone is accepted by the server without touching others");
            resetRefused = false; try { await limitedService.ResetAsync(slept); } catch (PostgresException ex) when (ex.SqlState == "42501") { resetRefused = true; }
            check(resetRefused && (await service.ReadSnapshotAsync(byQuery)).Rows.Count == 2, "Selective reset by a pg_read_all_stats member is refused and keeps the entry");
            bool hiddenRefused = false; try { await service.ResetAsync(counted with { QueryId = null }); } catch (ArgumentException) { hiddenRefused = true; }
            check(hiddenRefused, "Entry without a visible query id cannot be reset individually");
            var resetAt = await service.ResetAsync();
            check(resetAt.Kind == DateTimeKind.Utc && resetAt >= snapshot.ObservedAt, "Superuser reset returns the server reset instant");
            check((await service.ReadSnapshotAsync(byQuery)).Rows.Count == 0, "Reset discards the previously tracked statements");
            await WindowChecks(admin, service, limitedService, "pgms_window_" + suffix, check);
        }
        finally
        {
            await using (var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {SqlIdentifier.Quote(marker)}", admin)) await drop.ExecuteNonQueryAsync();
            if (roleCreated) { await using var drop = new NpgsqlCommand($"DROP ROLE {SqlIdentifier.Quote(role)}", admin); await drop.ExecuteNonQueryAsync(); }
            if (dbCreated) { await using var drop = new NpgsqlCommand($"DROP DATABASE {SqlIdentifier.Quote(missingDb)}", admin); await drop.ExecuteNonQueryAsync(); }
            if (extensionCreated) { await using var drop = new NpgsqlCommand("DROP EXTENSION pg_stat_statements", admin); await drop.ExecuteNonQueryAsync(); }
        }
    }

    // Time windows: a baseline of counters, then the difference of a later read. Statements name their own table; constants normalise to $n.
    private static async Task WindowChecks(NpgsqlConnection admin, QueryStatisticsService service, QueryStatisticsService limitedService, string table, Action<bool, string> check)
    {
        string quoted = SqlIdentifier.Quote(table);
        async Task Run(string sql, int times = 1) { for (int i = 0; i < times; i++) { await using var cmd = new NpgsqlCommand(sql, admin); await cmd.ExecuteNonQueryAsync(); } }
        string repeated = $"SELECT count(*) FROM {quoted}", untouched = $"SELECT max(id) FROM {quoted}";
        try
        {
            await Run($"CREATE TABLE {quoted}(id int)");
            await Run(repeated, 3); await Run(untouched, 2); await Run($"SELECT count(*), pg_sleep(0.0) FROM {quoted}");
            var filter = new QueryStatisticsFilter(Query: table);
            var baseline = await service.CaptureBaselineAsync();
            var before = (await service.ReadSnapshotAsync(filter)).Rows;
            var repeatedBefore = before.Single(r => r.Query.StartsWith("SELECT count(*) FROM", StringComparison.Ordinal) && !r.Query.Contains("pg_sleep"));
            check(baseline.TakenAt.Kind == DateTimeKind.Utc && baseline.Entries.Count >= 4 && baseline.Deallocations is >= 0
                && baseline.Entries.Single(e => e.QueryId == repeatedBefore.QueryId && e.DatabaseOid == repeatedBefore.DatabaseOid && e.UserOid == repeatedBefore.UserOid) is { Calls: 3 } counted
                && Math.Abs(counted.TotalExecMs - repeatedBefore.TotalExecMs) < 1e-9 && counted.StatsSince == repeatedBefore.StatsSince, "Baseline captures the counters, OIDs and stats_since of each visible entry without SQL text");

            var empty = await service.ReadSnapshotAsync(filter, baseline);
            check(empty.Rows.Count == 0 && empty.WindowStart == baseline.TakenAt && !empty.IsTruncated && empty.ObservedAt >= baseline.TakenAt && empty.EvictedSinceWindowStart == 0 && !empty.ResetSinceWindowStart,
                "Window right after the baseline is empty, keeps the server instant and reports no eviction or reset");

            await Run(repeated, 2);
            await Run($"SELECT count(*), pg_sleep(0.04) FROM {quoted}"); await Run($"SELECT count(*), pg_sleep(0.0) FROM {quoted}");
            await Run($"SELECT min(id) FROM {quoted}");
            var window = await service.ReadSnapshotAsync(filter, baseline);
            var delta = window.Rows.Single(r => r.QueryId == repeatedBefore.QueryId);
            check(delta.Calls == 2 && delta.Rows == 2 && delta.IsWindowed && Math.Abs(delta.MeanExecMs - delta.TotalExecMs / 2) < 1e-9 && delta.TotalExecMs >= 0,
                "Window counts only the two executions after the baseline, with mean over the window calls");
            check(double.IsNaN(delta.MinExecMs) && double.IsNaN(delta.MaxExecMs) && delta.MinExecLabel == "—" && delta.MaxExecLabel == "—", "Min and max are unavailable in a window and labelled —");
            check(!window.Rows.Any(r => r.Query.StartsWith("SELECT max(id)", StringComparison.Ordinal)), "Statement with no executions in the window is left out");
            check(window.Rows.Single(r => r.Query.StartsWith("SELECT min(id)", StringComparison.Ordinal)) is { Calls: 1 } created && created.StatsSince >= baseline.TakenAt, "Entry created after the baseline counts in full");
            var spread = window.Rows.Single(r => r.Query.Contains("pg_sleep"));
            check(spread.Calls == 2 && spread.TotalExecMs >= 40 && spread.StddevExecMs >= 15 && spread.StddevExecMs <= spread.TotalExecMs,
                "Window std dev is derived from the two new executions (about 0 and 40 ms): " + spread.StddevExecLabel);
            var cumulative = (await service.ReadSnapshotAsync(filter)).Rows.Single(r => r.QueryId == repeatedBefore.QueryId);
            check(cumulative.Calls == 5 && !cumulative.IsWindowed && cumulative.MaxExecLabel != "—", "Cumulative read of the same entry still reports all five calls and its max");
            check((await service.ReadSnapshotAsync(filter with { Order = QueryStatisticsOrder.Calls }, baseline)).Rows[0].Calls == 2
                && (await service.ReadSnapshotAsync(filter with { Order = QueryStatisticsOrder.TotalExecTime }, baseline)).Rows[0].Query.Contains("pg_sleep"), "Window ordering runs on the window counters");
            check((await service.ReadSnapshotAsync(filter with { MinimumCalls = 2 }, baseline)).Rows.Count == 2, "Minimum calls applies to calls within the window");
            var limited = await service.ReadSnapshotAsync(filter, baseline, 1);
            check(limited.IsTruncated && limited.Rows.Count == 1, "Window read keeps the sentinel truncation");
            bool refused = false; try { await service.ReadSnapshotAsync(filter with { Order = QueryStatisticsOrder.MaxExecTime }, baseline); } catch (ArgumentException) { refused = true; }
            check(refused, "Max execution time ordering is refused for a window before connecting");

            // Without the baseline counters of an entry that existed before, the window cannot measure it: counted, then left out.
            var partial = baseline with { Entries = baseline.Entries.Where(e => e.QueryId != repeatedBefore.QueryId).ToList() };
            var withoutCounters = await service.ReadSnapshotAsync(filter, partial);
            check(withoutCounters.UnusableEntries >= 1 && withoutCounters.Rows.All(r => r.QueryId != repeatedBefore.QueryId), "Entry missing from the baseline but older than it is counted as unmeasurable and left out");

            // A selective reset re-creates the entry: it then counts from its new creation instead of subtracting stale counters.
            await service.ResetAsync(delta); await Run(repeated);
            var recreated = (await service.ReadSnapshotAsync(filter, baseline)).Rows.Single(r => r.QueryId == repeatedBefore.QueryId);
            check(recreated.Calls == 1 && recreated.StatsSince > repeatedBefore.StatsSince, "Entry reset after the baseline counts only its executions since re-creation");

            var hidden = await limitedService.ReadSnapshotAsync(new QueryStatisticsFilter(Query: table), baseline);
            check(hidden.Rows.All(r => r.QueryId is not null), "Window rows always carry a query id, even for another role");

            foreach (var (bad, label) in new (QueryStatisticsBaseline, string)[]
            {
                (baseline with { TakenAt = DateTime.SpecifyKind(baseline.TakenAt, DateTimeKind.Local) }, "non-UTC instant"),
                (baseline with { Entries = [baseline.Entries[0], baseline.Entries[0]] }, "duplicate entry"),
                (baseline with { Entries = [baseline.Entries[0] with { Calls = -1 }] }, "negative calls"),
                (baseline with { Entries = [baseline.Entries[0] with { TotalExecMs = double.NaN }] }, "NaN time"),
                (baseline with { Entries = [baseline.Entries[0] with { StatsSince = baseline.TakenAt.AddMinutes(1) }] }, "entry newer than the baseline"),
                (baseline with { Deallocations = -1 }, "negative deallocations")
            })
            {
                bool invalid = false; try { await service.ReadSnapshotAsync(filter, bad); } catch (System.IO.InvalidDataException) { invalid = true; }
                check(invalid, "Window read rejects a baseline with " + label);
            }
            bool future = false; try { await service.ReadSnapshotAsync(filter, baseline with { TakenAt = DateTime.UtcNow.AddDays(1), Entries = [] }); } catch (QueryStatisticsBaselineException) { future = true; }
            check(future, "Baseline later than the server clock is refused instead of yielding negative windows");
            bool cancelled = false; try { await service.CaptureBaselineAsync(new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Pre-cancelled token stops the baseline capture before any query");

            var beforeReset = await service.CaptureBaselineAsync();
            await service.ResetAsync(); await Run(repeated, 2);
            var afterReset = await service.ReadSnapshotAsync(filter, beforeReset);
            check(afterReset.ResetSinceWindowStart && afterReset.Rows.Single(r => r.QueryId == repeatedBefore.QueryId).Calls == 2, "Full reset after the baseline is reported and entries count from their re-creation");
        }
        finally { await Run($"DROP TABLE IF EXISTS {quoted}"); }
    }

    // Statements naming their own table so each new ordering has a known leader: WAL from a bulk INSERT, temp blocks from a sort
    // forced to spill by a tiny work_mem, and execution-time spread from one normalised statement run with two sleep lengths.
    private static async Task MetricChecks(NpgsqlConnection admin, QueryStatisticsService service, string table, Action<bool, string> check)
    {
        string quoted = SqlIdentifier.Quote(table);
        async Task Run(string sql) { await using var cmd = new NpgsqlCommand(sql, admin); await cmd.ExecuteNonQueryAsync(); }
        try
        {
            check((await service.DetectAsync()).TrackIoTiming == false, "Detection reports track_io_timing off on the default test cluster");
            await Run($"CREATE TABLE {quoted}(id int, pad text)");
            await Run($"INSERT INTO {quoted} SELECT g, pg_catalog.repeat('x', 100) FROM pg_catalog.generate_series(1, 5000) g");
            await Run("SET work_mem = '64kB'");
            await Run($"SELECT id FROM {quoted} ORDER BY pad DESC, id OFFSET 4999");
            await Run("RESET work_mem");
            await Run($"SELECT count(*), pg_sleep(0.0) FROM {quoted}");
            await Run($"SELECT count(*), pg_sleep(0.04) FROM {quoted}");
            var filter = new QueryStatisticsFilter(Query: table);
            static bool Descending<T>(IReadOnlyList<QueryStatisticsEntry> rows, Func<QueryStatisticsEntry, T> key) where T : IComparable<T> =>
                rows.Zip(rows.Skip(1)).All(pair => key(pair.First).CompareTo(key(pair.Second)) >= 0);

            var wal = (await service.ReadSnapshotAsync(filter with { Order = QueryStatisticsOrder.WalBytes })).Rows;
            check(wal.Count == 4 && wal[0].Query.StartsWith("INSERT", StringComparison.Ordinal) && wal[0].WalBytes > 0 && Descending(wal, r => r.WalBytes), "WAL bytes ordering puts the bulk INSERT first and is descending: " + string.Join(" | ", wal.Select(r => r.WalBytes + " " + r.Query[..Math.Min(30, r.Query.Length)])));
            var temp = (await service.ReadSnapshotAsync(filter with { Order = QueryStatisticsOrder.TempBlocks })).Rows;
            check(temp[0].Query.Contains("ORDER BY", StringComparison.Ordinal) && temp[0].TempBlocks > 0 && temp[0].TempBlocks == temp[0].TempBlocksRead + temp[0].TempBlocksWritten && Descending(temp, r => r.TempBlocks), "Temp blocks ordering puts the spilled sort first and sums temp reads and writes");
            var shared = (await service.ReadSnapshotAsync(filter with { Order = QueryStatisticsOrder.SharedBlocks })).Rows;
            check(shared[0].SharedBlocks > 0 && shared.All(r => r.SharedBlocks == r.SharedBlocksHit + r.SharedBlocksRead) && Descending(shared, r => r.SharedBlocks), "Shared blocks ordering is hits plus reads, descending");
            var read = (await service.ReadSnapshotAsync(filter with { Order = QueryStatisticsOrder.SharedBlocksRead })).Rows;
            check(read.Count == 4 && Descending(read, r => r.SharedBlocksRead), "Blocks read ordering is descending by shared blocks read");
            var spread = (await service.ReadSnapshotAsync(filter with { Order = QueryStatisticsOrder.StddevExecTime })).Rows;
            check(spread[0].Query.Contains("pg_sleep", StringComparison.Ordinal) && spread[0].Calls == 2 && spread[0].StddevExecMs >= 10 && Descending(spread, r => r.StddevExecMs), "Std dev ordering puts the statement run with two sleep lengths first: " + spread[0].StddevExecLabel);
            var io = (await service.ReadSnapshotAsync(filter with { Order = QueryStatisticsOrder.IoTime })).Rows;
            check(io.Count == 4 && io.All(r => r.IoTimeMs == 0 && r.IoTimeLabel == "0.000"), "I/O time is read and stays 0 while track_io_timing is off");
            await Run("SET track_io_timing = on");
            await Run("SET work_mem = '64kB'");
            await Run($"SELECT id FROM {quoted} ORDER BY id DESC, pad OFFSET 4999");
            await Run("RESET work_mem"); await Run("RESET track_io_timing");
            io = (await service.ReadSnapshotAsync(filter with { Order = QueryStatisticsOrder.IoTime })).Rows;
            check(io.Count == 5 && io.All(r => r.IoTimeMs >= 0) && Descending(io, r => r.IoTimeMs), "I/O time ordering is descending once a statement ran with track_io_timing on (session-level SET by a superuser)");

            // Generic plan of the normalised text pg_stat_statements recorded: $n placeholders, planned without executing.
            await Run($"SELECT count(*) FROM {quoted} WHERE id = 42");
            await Run($"DELETE FROM {quoted} WHERE id = -1");
            var recorded = (await service.ReadSnapshotAsync(filter)).Rows;
            var parameterized = recorded.Single(r => r.Query.StartsWith("SELECT", StringComparison.Ordinal) && r.Query.Contains("WHERE id", StringComparison.Ordinal));
            var created = recorded.Single(r => r.Query.StartsWith("CREATE TABLE", StringComparison.Ordinal));
            var inserted = recorded.Single(r => r.Query.StartsWith("INSERT", StringComparison.Ordinal));
            var deleted = recorded.Single(r => r.Query.StartsWith("DELETE", StringComparison.Ordinal));
            check(parameterized.Query.Contains("$1", StringComparison.Ordinal) && parameterized.CanShowPlan && inserted.CanShowPlan && !created.CanShowPlan, "Recorded SELECT/INSERT text has $n and can show a plan; the utility command cannot");
            check(!(parameterized with { Query = "<insufficient privilege>" }).CanShowPlan, "Hidden statement text cannot show a plan");
            await using var session = new QuerySession(admin.ConnectionString); await session.OpenAsync();
            var generic = await session.ExplainGenericAsync(parameterized.Query);
            check(generic.Generic && !generic.Analyzed && generic.Json.Contains(table, StringComparison.Ordinal) && QueryPlanTree.Parse(generic, CancellationToken.None).Label.Length > 0, "Generic plan of the recorded parameterised SELECT is returned and parsed: " + generic.Json[..Math.Min(400, generic.Json.Length)].Replace("\n", " "));
            check(generic.Json.Contains("$1", StringComparison.Ordinal) && !generic.Json.Contains("NULL", StringComparison.Ordinal), "Plan keeps $1 unbound instead of folding a placeholder value");
            bool unbound = false; try { await session.ExplainAsync(parameterized.Query); } catch (UnboundParametersException ex) when (ex.Parameters.Number == 1) { unbound = true; }
            check(unbound && session.IsConnected, "Plain estimated plan of the same text is refused before sending $1 and the session stays open (PGMS-023)");
            await using var fresh = new QuerySession(admin.ConnectionString); await fresh.OpenAsync();
            var deletePlan = await fresh.ExplainGenericAsync(deleted.Query);
            check(deletePlan.Json.Contains("\"Delete\"", StringComparison.Ordinal) && deletePlan.Json.Contains("$1", StringComparison.Ordinal), "Generic plan of a recorded DELETE shows the write node with $1 unbound");
            bool ambiguous = false; try { await fresh.ExplainGenericAsync(inserted.Query); } catch (PostgresException ex) when (ex.SqlState == "42725") { ambiguous = true; }
            check(ambiguous && fresh.IsConnected, "Server refuses a generic plan whose $n types cannot be inferred (generate_series($3, $4)) and the session stays open");
            await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {quoted}", admin)) check((long)(await count.ExecuteScalarAsync())! == 5000, "Generic plans of recorded writes neither insert nor delete rows");
            bool utilityRefused = false; try { await fresh.ExplainGenericAsync(created.Query); } catch (PostgresException) { utilityRefused = true; }
            check(utilityRefused, "Server refuses a generic plan for a utility command");
            foreach (var (sql, expected) in new[] { ("  select 1", true), ("-- note\n/* c */ WITH x AS (SELECT 1) SELECT * FROM x", true), ("(SELECT 1) UNION (SELECT 2)", true), ("Values (1)", true), ("SET work_mem = $1", false), ("EXPLAIN SELECT 1", false), ("", false), ("/* never closed SELECT 1", false), ("SELECTED", false) })
                check(QueryStatisticsEntry.IsPlannable(sql) == expected, "Plannable keyword check: " + sql.Replace("\n", " "));
        }
        finally { await Run($"RESET ALL; DROP TABLE IF EXISTS {quoted}"); }
    }
}
