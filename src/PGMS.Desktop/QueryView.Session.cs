using System.IO;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class QueryView
{
    private QuerySessionSnapshot? sessionSnapshot;
    private bool inspectingSession;
    internal Task<QuerySessionLossRisk> InspectSessionLossRiskAsync()=>session?.InspectLossRiskAsync()??Task.FromResult(new QuerySessionLossRisk(false,false));
    /// <summary>Moves this tab to another connection. The new one opens first, so a failure leaves the current session untouched.</summary>
    internal async Task ChangeDatabaseAsync(string connectionString)
    {
        if(IsBusy || session is null)return;
        IsBusy=true;cancellation=new();
        UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);
        var candidate=new QuerySession(connectionString);
        QuerySession previous=session;
        try
        {
            await candidate.OpenAsync(cancellation.Token);
            HideSessionSettings();ClearAnalysisReview();sessionSnapshot=null;SessionProperties.ItemsSource=null;
            session=candidate;ConnectionString=connectionString;ForgetSessionState();
            QueryStatus.Text=$"Connected | {Database}";
        }
        catch{await candidate.DisposeAsync();throw;}
        finally{IsBusy=false;cancellation.Dispose();cancellation=null;UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);}
        await previous.DisposeAsync();
    }
    // ---- Query History (PGMS-043) ----
    /// <summary>Receives every execution of this tab after it ends; MainWindow points it at Query History. Null records nothing.</summary>
    internal Action<QueryHistoryCapture>? HistorySink { get; set; }
    private readonly List<(string Normalized, QueryHistoryOrigin Origin)> originMarks = [];
    /// <summary>Text put in the editor by a tool (Query Designer, AI, Script as and similar). It keeps that origin while it runs unchanged.</summary>
    internal void MarkOrigin(string sql, QueryHistoryOrigin origin)
    {
        if (origin == QueryHistoryOrigin.Typed || QueryHistoryText.Normalize(sql) is not { Length: > 0 } normalized) return;
        originMarks.RemoveAll(m => m.Normalized == normalized);
        originMarks.Add((normalized, origin));
        if (originMarks.Count > 8) originMarks.RemoveAt(0);
    }
    /// <summary>Origin of the SQL that runs: a marked text that it equals or contains, the latest first; otherwise typed.</summary>
    internal QueryHistoryOrigin OriginOf(string sql)
    {
        string normalized = QueryHistoryText.Normalize(sql);
        for (int i = originMarks.Count - 1; i >= 0; i--)
            if (normalized.Contains(originMarks[i].Normalized, StringComparison.Ordinal)) return originMarks[i].Origin;
        return QueryHistoryOrigin.Typed;
    }
    /// <summary>The one place an execution is handed to Query History, after it ended. Never throws and never waits for the disk.</summary>
    private void RecordHistory(string sql, DateTime startedUtc, TimeSpan elapsed, Exception? error, bool canceled, long? rowsReturned, long? rowsAffected, QueryHistoryOrigin? origin = null)
    {
        if (HistorySink is not { } sink || ConnectionString is not { } connection) return;
        var outcome = canceled ? QueryHistoryOutcome.Canceled : error is null ? QueryHistoryOutcome.Succeeded : QueryHistoryOutcome.Failed;
        try { sink(QueryHistoryCapture.FromConnection(connection, session?.Database, sql, startedUtc, elapsed, origin ?? OriginOf(sql), outcome, rowsReturned, rowsAffected, error)); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or IOException or InvalidDataException)
        { Messages.AppendText("\nQuery History did not record this execution: " + ex.Message); }
    }

    /// <summary>Execute SQL inside Design Query in Editor runs on the designer's own connection to this tab's server and database.</summary>
    internal void RecordDesignerExecution(string sql, DateTime startedUtc, TimeSpan elapsed, Exception? error, bool canceled, long? rows) =>
        RecordHistory(sql, startedUtc, elapsed, error, canceled, rows, null, QueryHistoryOrigin.QueryDesigner);

    private async void InspectSession(object sender,RoutedEventArgs e)=>await InspectSessionAsync();
    private void ShowSessionSnapshot(QuerySessionSnapshot snapshot)
    {
        sessionSnapshot=snapshot;
        var rows=new List<KeyValuePair<string,string>>
        {new("Backend PID",snapshot.BackendPid.ToString()),new("Database",snapshot.Database),new("Session user",snapshot.SessionUser),new("Current user",snapshot.CurrentUser)};
        rows.AddRange(snapshot.Settings);SessionProperties.ItemsSource=rows;
        SessionStatus.Text=$"Snapshot at {snapshot.CapturedAt.ToLocalTime():T}. Refresh after SQL changes. Isolation/read-only describe the inspection transaction.";
    }
    public async Task InspectSessionAsync()
    {
        if(IsBusy || session?.IsConnected!=true)return;
        ClearAnalysisReview();ClearSettingReview();IsBusy=inspectingSession=true;cancellation=new();
        sessionSnapshot=null;SessionProperties.ItemsSource=null;SessionStatus.Text="Inspecting this query's session…";
        OutputTabs.SelectedItem=SessionTab;UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);
        try
        {
            ShowSessionSnapshot(await session.InspectSessionAsync(cancellation.Token));
            QueryStatus.Text="Session inspected | "+Database;
        }
        catch(Exception ex)when(ex is NpgsqlException or OperationCanceledException or InvalidOperationException or IOException)
        {
            SessionStatus.Text=ex is PostgresException pg?QueryErrorFormatter.Format(pg):ex is OperationCanceledException?"Session inspection canceled.":ex.Message;
            QueryStatus.Text="Session inspection did not complete | "+Database;
        }
        finally{IsBusy=inspectingSession=false;cancellation.Dispose();cancellation=null;UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);}
    }
}
