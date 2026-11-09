using Npgsql;

namespace PGMS.PostgreSql;

/// <summary>Actual plans of the statements of one execution, as the server reported them while running them.</summary>
public sealed class ActualPlanCapture
{
    public const int MaximumPlans = 64;
    private readonly List<QueryPlan> plans = [];
    public IReadOnlyList<QueryPlan> Plans => plans;
    /// <summary>Plans not kept: over <see cref="MaximumPlans"/> or larger than the plan size limit.</summary>
    public int Omitted { get; private set; }
    /// <summary>Why the server could not report plans; the SQL still ran.</summary>
    public string? Unavailable { get; internal set; }

    internal void Add(string json)
    {
        if (plans.Count >= MaximumPlans || json.Length + 2 > QuerySession.MaximumPlanCharacters) { Omitted++; return; }
        // auto_explain sends one object; EXPLAIN (FORMAT JSON), which the plan readers and the export expect, sends an array.
        plans.Add(new QueryPlan("[" + json + "]", true));
    }
}

public sealed partial class QuerySession
{
    private const string CaptureFailureMarker = "pgms-plan-capture:";
    private const string PlanMessageMarker = " ms  plan:\n";
    // INFO reaches the client whatever client_min_messages says and stays out of the server log with the default log_min_messages.
    // The block makes a refusal (not a superuser, module missing) harmless: its exception handler keeps an open transaction alive.
    private const string ArmCaptureSql = """
        DO $pgms$ BEGIN
          EXECUTE 'LOAD ''auto_explain''';
          PERFORM pg_catalog.set_config(s.name, s.value, false) FROM (VALUES
            ('auto_explain.log_level','info'),('auto_explain.log_format','json'),('auto_explain.log_analyze','on'),('auto_explain.log_buffers','on'),
            ('auto_explain.log_timing','on'),('auto_explain.log_verbose','on'),('auto_explain.log_nested_statements','off'),('auto_explain.sample_rate','1'),
            ('auto_explain.log_min_duration','0')) AS s(name, value);
        EXCEPTION WHEN OTHERS THEN
          RAISE INFO USING MESSAGE = 'pgms-plan-capture:' || SQLSTATE || ': ' || SQLERRM;
        END $pgms$
        """;
    private bool planCaptureArmed;

    private async Task ArmPlanCaptureAsync(ActualPlanCapture capture, CancellationToken token)
    {
        string? failure = null;
        void Failed(object sender, NpgsqlNoticeEventArgs args) { if (args.Notice.MessageText.StartsWith(CaptureFailureMarker, StringComparison.Ordinal)) failure = args.Notice.MessageText[CaptureFailureMarker.Length..]; }
        connection.Notice += Failed;
        try
        {
            await using var command = new NpgsqlCommand(ArmCaptureSql, connection) { CommandTimeout = 15 };
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (failure is null) planCaptureArmed = true; else capture.Unavailable = failure;
        }
        catch (PostgresException ex) { capture.Unavailable = $"{ex.SqlState}: {ex.MessageText}"; } // An aborted transaction refuses the block itself.
        finally { connection.Notice -= Failed; }
    }

    /// <summary>Best effort: in an aborted transaction this fails and is retried before the next execution; meanwhile the plan messages are dropped.</summary>
    private async Task DisarmPlanCaptureAsync()
    {
        if (!planCaptureArmed) return;
        if (!IsConnected) { planCaptureArmed = false; return; }
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_catalog.set_config('auto_explain.log_min_duration','-1',false)", connection) { CommandTimeout = 15 };
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            planCaptureArmed = false;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException) { }
    }

    /// <summary>True when the notice is a plan sent by auto_explain, which never belongs in Messages.</summary>
    private bool TakePlanNotice(PostgresNotice notice, ActualPlanCapture? capture)
    {
        if (!planCaptureArmed || notice.InvariantSeverity != "INFO" || !notice.MessageText.StartsWith("duration: ", StringComparison.Ordinal)) return false;
        int marker = notice.MessageText.IndexOf(PlanMessageMarker, StringComparison.Ordinal);
        if (marker < 0) return false;
        capture?.Add(notice.MessageText[(marker + PlanMessageMarker.Length)..]);
        return true;
    }
}
