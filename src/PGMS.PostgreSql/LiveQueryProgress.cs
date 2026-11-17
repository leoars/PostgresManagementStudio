using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

/// <summary>One sample of a running backend for Include Live Query Statistics (PGMS-067, M1101). PostgreSQL has no per-operator counters during
/// execution (what SSMS draws on the live plan), so this is what the server does expose: pg_stat_activity and, for the commands that report it,
/// the pg_stat_progress_* views.</summary>
public sealed record LiveQuerySample(string State, string? WaitEventType, string? WaitEvent, TimeSpan? QueryElapsed, TimeSpan? TransactionElapsed, string? Command, string? Phase,
    IReadOnlyList<(string Name, string Value)> Progress, IReadOnlyList<int> BlockedBy)
{
    public bool IsWaiting => WaitEventType is not null;
}

public sealed class LiveQueryProgressService(string connectionString)
{
    // A progress view missing on an older server is skipped; to_jsonb keeps the columns of every version.
    private const string ActivitySql = """
        SELECT a.state, a.wait_event_type, a.wait_event, pg_catalog.now() - a.query_start, pg_catalog.now() - a.xact_start,
               pg_catalog.pg_blocking_pids(a.pid)
        FROM pg_catalog.pg_stat_activity a WHERE a.pid = @pid
        """;
    private static readonly (string View, string Command)[] ProgressViews =
    [
        ("pg_stat_progress_create_index", "CREATE INDEX"), ("pg_stat_progress_vacuum", "VACUUM"), ("pg_stat_progress_analyze", "ANALYZE"),
        ("pg_stat_progress_cluster", "CLUSTER"), ("pg_stat_progress_copy", "COPY"), ("pg_stat_progress_basebackup", "BASE BACKUP"),
    ];

    /// <summary>Reads the backend once from its own short connection; null when the backend is gone or idle.</summary>
    public async Task<LiveQuerySample?> SampleAsync(int pid, CancellationToken token = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = "PGMS live query statistics", Pooling = true, CommandTimeout = 5 };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        LiveQuerySample? sample;
        await using (var command = new NpgsqlCommand(ActivitySql, connection))
        {
            command.Parameters.AddWithValue("pid", NpgsqlDbType.Integer, pid);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            sample = new LiveQuerySample(reader.IsDBNull(0) ? "unknown" : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetTimeSpan(3), reader.IsDBNull(4) ? null : reader.GetTimeSpan(4), null, null, [], reader.IsDBNull(5) ? [] : reader.GetFieldValue<int[]>(5));
        }
        foreach (var (view, name) in ProgressViews)
        {
            await using var command = new NpgsqlCommand($"""
                SELECT pg_catalog.to_jsonb(p)::text FROM pg_catalog.{view} p WHERE p.pid = @pid
                """, connection);
            command.Parameters.AddWithValue("pid", NpgsqlDbType.Integer, pid);
            object? json;
            try { json = await command.ExecuteScalarAsync(token).ConfigureAwait(false); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { continue; }
            if (json is not string text) continue;
            var values = new List<(string, string)>();
            string? phase = null;
            using var document = System.Text.Json.JsonDocument.Parse(text);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is "pid" or "datid" or "datname" or "relid" or "index_relid") continue;
                string value = property.Value.ValueKind switch { System.Text.Json.JsonValueKind.Null => "", System.Text.Json.JsonValueKind.String => property.Value.GetString()!, _ => property.Value.GetRawText() };
                if (property.Name == "phase") phase = value; else if (value.Length > 0) values.Add((property.Name, value));
            }
            return sample with { Command = name, Phase = phase, Progress = values };
        }
        return sample;
    }

    /// <summary>Lines for the Live Query Statistics tab.</summary>
    public static IReadOnlyList<string> Describe(LiveQuerySample sample)
    {
        static string Time(TimeSpan? t) => t is { } v ? v.ToString(v.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss\.f", System.Globalization.CultureInfo.InvariantCulture) : "-";
        var lines = new List<string> { $"State: {sample.State}", $"Elapsed: {Time(sample.QueryElapsed)} (transaction {Time(sample.TransactionElapsed)})",
            sample.IsWaiting ? $"Waiting: {sample.WaitEventType} / {sample.WaitEvent}" : "Waiting: no (running on CPU or in I/O not reported as a wait)" };
        if (sample.BlockedBy.Count > 0) lines.Add("Blocked by backend PID(s): " + string.Join(", ", sample.BlockedBy));
        if (sample.Command is not null)
        {
            lines.Add($"Progress of {sample.Command}" + (sample.Phase is { Length: > 0 } p ? $": {p}" : ""));
            lines.AddRange(sample.Progress.Select(v => $"  {v.Name.Replace('_', ' ')}: {v.Value}"));
        }
        else lines.Add("No progress counters: PostgreSQL reports them only for CREATE INDEX, VACUUM, ANALYZE, CLUSTER, COPY and base backups.");
        return lines;
    }
}
