using Npgsql;
using PGMS.PostgreSql;

internal static class ServerReportChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string schema = "rpt_" + Guid.NewGuid().ToString("N")[..8], role = schema + "_reader";
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(NpgsqlConnection c, string sql) { await using var command = new NpgsqlCommand(sql, c); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        var service = new ServerReportService(connection);
        static int Column(ServerReportTable t, string name) => t.Columns.ToList().IndexOf(name);
        bool extensionCreated = false;
        try
        {
            await Sql(admin, $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.items (id integer PRIMARY KEY, payload text);
                INSERT INTO {schema}.items SELECT g, repeat('x', 200) FROM generate_series(1, 3000) g;
                CREATE INDEX items_payload ON {schema}.items (payload);
                ANALYZE {schema}.items;
                CREATE ROLE {role} LOGIN;
                """);

            check(ServerReportService.ServerReports.All(d => !d.DatabaseScope) && ServerReportService.DatabaseReports.All(d => d.DatabaseScope)
                && Enum.GetValues<ServerReportKind>().All(k => ServerReportService.Definition(k).Kind == k), "Every report kind has one definition, split into server and database reports");

            // Disk Usage by Top Tables: sizes match the server's own functions.
            var top = (await service.RunAsync(ServerReportKind.DiskUsageByTopTables)).Tables.Single();
            var row = top.Rows.Single(r => (string)r[0]! == schema && (string)r[1]! == "items");
            long total = (long)(await Scalar($"SELECT pg_total_relation_size('{schema}.items') / 1024"))!, indexes = (long)(await Scalar($"SELECT pg_indexes_size('{schema}.items') / 1024"))!;
            check((long)row[Column(top, "Reserved (KB)")]! == total && (long)row[Column(top, "Indexes (KB)")]! == indexes && (long)row[Column(top, "Rows (estimate)")]! == 3000,
                $"Disk Usage by Top Tables reports total and index size and the row estimate ({row[Column(top, "Reserved (KB)")]} KB)");
            check(top.Rows.All(r => r[0] is string s && s != "pg_catalog" && s != "information_schema"), "Disk Usage by Top Tables leaves out system catalogs");

            // Disk Usage: the categories add up to the database size.
            var usage = await service.RunAsync(ServerReportKind.DiskUsage);
            var space = usage.Tables[0];
            decimal Size(string category) => (decimal)space.Rows.Single(r => (string)r[0]! == category)[1]!;
            decimal parts = space.Rows.Where(r => (string)r[0]! != "Total").Sum(r => (decimal)r[1]!);
            decimal database = Math.Round((long)(await Scalar("SELECT pg_database_size(current_database())"))! / 1048576m, 2);
            check(space.Rows.All(r => (decimal)r[1]! >= 0) && Size("System catalogs") < Size("Total"), $"Disk Usage categories are never negative and shared catalogs are not counted in the database (other: {Size("Other (sequences, files outside tables)")} MB)");
            check(Math.Abs(parts - Size("Total")) <= 0.06m && Math.Abs(Size("Total") - database) <= 0.5m && Size("Indexes") > 0 && Size("System catalogs") > 0,
                $"Disk Usage categories add up to the database size ({parts} of {Size("Total")} MB, server {database} MB)");
            check(usage.Tables[1].Rows.Any(r => (string)r[0]! == schema && (long)r[1]! == 1), "Disk Usage lists space by schema");

            // Index Usage Statistics: a scan through the primary key reaches the report.
            await Sql(admin, $"SET enable_seqscan = off; SELECT payload FROM {schema}.items WHERE id = 42; RESET enable_seqscan");
            long scans = 0;
            bool keyMarked = false;
            for (int i = 0; i < 40 && scans == 0; i++)
            {
                var indexTable = (await service.RunAsync(ServerReportKind.IndexUsageStatistics)).Tables.Single();
                var pk = indexTable.Rows.Single(r => (string)r[0]! == schema && (string)r[2]! == "items_pkey");
                keyMarked = (string)pk[Column(indexTable, "Kind")]! == "primary key" && (bool)pk[Column(indexTable, "Valid")]!;
                scans = (long)pk[Column(indexTable, "Scans")]!;
                if (scans == 0) await Task.Delay(250);
            }
            check(keyMarked, "Index Usage Statistics marks the primary key and its validity");
            check(scans >= 1, "Index Usage Statistics counts an index scan");

            // Blocking: one session holds a lock, another waits for it.
            await using var holder = new NpgsqlConnection(connection); await holder.OpenAsync();
            await using var waiter = new NpgsqlConnection(connection); await waiter.OpenAsync();
            await Sql(holder, $"BEGIN; LOCK TABLE {schema}.items IN ACCESS EXCLUSIVE MODE");
            await using var blocked = new NpgsqlCommand($"SELECT count(*) FROM {schema}.items", waiter);
            var waiting = blocked.ExecuteScalarAsync();
            int holderPid = holder.ProcessID, waiterPid = waiter.ProcessID;
            for (int i = 0; i < 100 && (long)(await Scalar($"SELECT count(*) FROM pg_locks WHERE pid = {waiterPid} AND NOT granted"))! == 0; i++) await Task.Delay(50);
            foreach (var kind in new[] { ServerReportKind.AllBlockingTransactions, ServerReportKind.DatabaseBlockingTransactions })
            {
                var blocking = (await service.RunAsync(kind)).Tables.Single();
                check(blocking.Rows.Any(r => (int)r[Column(blocking, "Blocked PID")]! == waiterPid && (int)r[Column(blocking, "Blocking PID")]! == holderPid
                    && ((string?)r[Column(blocking, "Blocked statement")])?.Contains(schema, StringComparison.Ordinal) == true), $"{kind} pairs the waiting session with the lock holder");
            }
            var ages = (await service.RunAsync(ServerReportKind.TopTransactionsByAge)).Tables.Single();
            check(ages.Rows.Any(r => (int)r[0]! == holderPid), "Top Transactions by Age lists the open transaction");
            var sessions = (await service.RunAsync(ServerReportKind.AllSessions)).Tables.Single();
            check(sessions.Rows.Any(r => (int)r[0]! == holderPid) && sessions.Rows.Any(r => (int)r[0]! == waiterPid && (string?)r[Column(sessions, "Wait type")] == "Lock"), "All Sessions shows both sessions and the lock wait");
            await Sql(holder, "ROLLBACK");
            check((long)(await waiting)! == 3000, "The blocked statement completes after the holder rolls back");

            // Server Dashboard and Configuration.
            var dashboard = await service.RunAsync(ServerReportKind.ServerDashboard);
            var server = dashboard.Tables[0];
            check((string?)server.Rows.Single(r => (string)r[0]! == "Version")[1] == (string?)await Scalar("SHOW server_version") && dashboard.Tables[1].Rows.Any(r => (string)r[0]! == admin.Database && r[1] is decimal),
                "Server Dashboard shows the version and the connected database with its size");
            var configuration = (await service.RunAsync(ServerReportKind.Configuration)).Tables.Single();
            check(configuration.Rows.Any(r => (string)r[0]! == "shared_preload_libraries" && ((string)r[1]!).Contains("pg_stat_statements", StringComparison.Ordinal))
                && configuration.Rows.All(r => (string)r[Column(configuration, "Source")]! is not ("default" or "client" or "session")), "Configuration lists non-default server settings and leaves out session values");

            // Top Queries: pg_stat_statements when it exists, an explanation when it does not.
            var missing = await service.RunAsync(ServerReportKind.TopQueriesByTotalTime);
            if ((long)(await Scalar("SELECT count(*) FROM pg_extension WHERE extname = 'pg_stat_statements'"))! == 0)
            {
                check(missing.Tables.Count == 0 && missing.Notes.Any(n => n.Contains("CREATE EXTENSION pg_stat_statements", StringComparison.Ordinal)), "Top Queries explains how the extension is created when it is missing");
                await Sql(admin, "CREATE EXTENSION pg_stat_statements"); extensionCreated = true;
            }
            for (int i = 0; i < 3; i++) await Sql(admin, $"SELECT count(*), pg_sleep(0.02) FROM {schema}.items");
            foreach (var kind in new[] { ServerReportKind.TopQueriesByTotalTime, ServerReportKind.TopQueriesByAverageTime, ServerReportKind.TopQueriesByTotalIo })
            {
                var queries = (await service.RunAsync(kind)).Tables.Single();
                check(queries.Rows.Count is > 0 and <= ServerReportService.TopQueries && queries.Columns[0] == "Query", $"{kind} reads up to {ServerReportService.TopQueries} statements");
            }
            var byTotal = (await service.RunAsync(ServerReportKind.TopQueriesByTotalTime)).Tables.Single();
            var totals = byTotal.Rows.Select(r => (double)r[Column(byTotal, "Total time (ms)")]!).ToList();
            check(totals.SequenceEqual(totals.OrderByDescending(t => t)), "Top Queries by Total Time is ordered by total time");

            // A role without privileges reads the reports without errors; hidden values stay empty.
            var reader = new ServerReportService(new NpgsqlConnectionStringBuilder(connection) { Username = role }.ConnectionString);
            bool allRead = true;
            foreach (var definition in ServerReportService.ServerReports.Concat(ServerReportService.DatabaseReports))
                try { await reader.RunAsync(definition.Kind); }
                catch (PostgresException ex) { allRead = false; Console.WriteLine($"{definition.Title}: {ex.SqlState} {ex.MessageText}"); }
            check(allRead, "Every report runs for a role without monitoring privileges");
        }
        finally
        {
            await Sql(admin, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await Sql(admin, $"DROP ROLE IF EXISTS {role}");
            if (extensionCreated) await Sql(admin, "DROP EXTENSION pg_stat_statements");
        }
    }
}
