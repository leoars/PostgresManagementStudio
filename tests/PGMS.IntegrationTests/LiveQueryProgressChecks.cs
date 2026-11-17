using Npgsql;
using PGMS.PostgreSql;

/// <summary>PGMS-067 (M1101): samples of a running backend for Include Live Query Statistics. A held snapshot keeps CREATE INDEX CONCURRENTLY in
/// its "waiting for old snapshots" phase, so the progress row is there to read; a held lock gives a blocked backend.</summary>
internal static class LiveQueryProgressChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_live_" + Guid.NewGuid().ToString("N")[..8];
        var service = new LiveQueryProgressService(connection);
        await using var setup = new NpgsqlConnection(connection); await setup.OpenAsync();
        async Task Sql(NpgsqlConnection c, string sql) { await using var command = new NpgsqlCommand(sql, c); await command.ExecuteNonQueryAsync(); }
        async Task<int> Pid(NpgsqlConnection c) { await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", c); return (int)(await command.ExecuteScalarAsync())!; }
        async Task<LiveQuerySample?> Until(int pid, Func<LiveQuerySample, bool> done)
        {
            LiveQuerySample? last = null;
            for (int i = 0; i < 100; i++) { last = await service.SampleAsync(pid); if (last is not null && done(last)) return last; await Task.Delay(100); }
            return last;
        }
        await Sql(setup, $"CREATE TABLE public.{table}(id integer, value text); INSERT INTO public.{table} SELECT n, 'v' || n FROM generate_series(1, 1000) n");
        try
        {
            await using var idle = new NpgsqlConnection(connection); await idle.OpenAsync();
            int idlePid = await Pid(idle);
            var idleSample = await service.SampleAsync(idlePid);
            check(idleSample is { State: "idle", Command: null }, "An idle backend is sampled as idle, without progress: " + idleSample?.State);
            check(await service.SampleAsync(2_000_000_000) is null, "A backend that does not exist gives no sample");

            // A sleeping statement: active, elapsed growing, waiting on a timeout.
            await using var sleeper = new NpgsqlConnection(connection); await sleeper.OpenAsync();
            int sleeperPid = await Pid(sleeper);
            var sleeping = Task.Run(async () => { await using var command = new NpgsqlCommand("SELECT pg_sleep(2)", sleeper); await command.ExecuteNonQueryAsync(); });
            var active = await Until(sleeperPid, s => s.State == "active" && s.QueryElapsed > TimeSpan.FromMilliseconds(200));
            check(active is { State: "active", WaitEventType: "Timeout", WaitEvent: "PgSleep" } && active.QueryElapsed > TimeSpan.Zero, "A running pg_sleep is active and waiting on PgSleep: " + active?.WaitEvent);
            var lines = LiveQueryProgressService.Describe(active!);
            check(lines[0] == "State: active" && lines.Any(l => l.StartsWith("Waiting: Timeout / PgSleep", StringComparison.Ordinal)) && lines[^1].StartsWith("No progress counters", StringComparison.Ordinal), "The description says the state, the wait and that there are no counters");
            await sleeping;

            // Blocked by a lock: the blocking backend is named.
            await using var holder = new NpgsqlConnection(connection); await holder.OpenAsync();
            int holderPid = await Pid(holder);
            await Sql(holder, $"BEGIN; LOCK TABLE public.{table} IN ACCESS EXCLUSIVE MODE");
            await using var blocked = new NpgsqlConnection(connection); await blocked.OpenAsync();
            int blockedPid = await Pid(blocked);
            var blockedTask = Task.Run(async () => { await using var command = new NpgsqlCommand($"SELECT count(*) FROM public.{table}", blocked) { CommandTimeout = 30 }; await command.ExecuteScalarAsync(); });
            var waiting = await Until(blockedPid, s => s.BlockedBy.Count > 0);
            check(waiting is { WaitEventType: "Lock" } && waiting.BlockedBy.Contains(holderPid) && LiveQueryProgressService.Describe(waiting).Any(l => l.Contains(holderPid.ToString(), StringComparison.Ordinal)),
                "A blocked statement shows the Lock wait and the blocking backend PID");
            await Sql(holder, "COMMIT");
            await blockedTask;

            // CREATE INDEX CONCURRENTLY held by an old snapshot: progress counters and phase.
            await Sql(holder, $"BEGIN ISOLATION LEVEL REPEATABLE READ; SELECT count(*) FROM public.{table}");
            await using var builder = new NpgsqlConnection(connection); await builder.OpenAsync();
            int builderPid = await Pid(builder);
            var building = Task.Run(async () => { await using var command = new NpgsqlCommand($"CREATE INDEX CONCURRENTLY {table}_value ON public.{table}(value)", builder) { CommandTimeout = 60 }; await command.ExecuteNonQueryAsync(); });
            var progress = await Until(builderPid, s => s.Command == "CREATE INDEX" && s.Phase?.Contains("old snapshots", StringComparison.Ordinal) == true);
            check(progress is { Command: "CREATE INDEX" } && progress.Phase!.Contains("waiting for old snapshots", StringComparison.Ordinal) && progress.Progress.Any(p => p.Name == "command" && p.Value.Contains("CONCURRENTLY", StringComparison.Ordinal)),
                "CREATE INDEX CONCURRENTLY reports its phase and counters: " + progress?.Phase + " " + string.Join(", ", progress?.Progress.Select(p => p.Name + "=" + p.Value) ?? []));
            check(LiveQueryProgressService.Describe(progress!).Any(l => l.StartsWith("Progress of CREATE INDEX: waiting for old snapshots", StringComparison.Ordinal)), "The description names the command and its phase");
            await Sql(holder, "COMMIT");
            await building;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var cleanup = new NpgsqlConnection(connection); await cleanup.OpenAsync();
            await Sql(cleanup, $"DROP TABLE IF EXISTS public.{table}");
        }
    }
}
