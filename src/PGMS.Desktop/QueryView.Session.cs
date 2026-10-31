using System.IO;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class QueryView
{
    private QuerySessionSnapshot? sessionSnapshot;
    private bool inspectingSession;
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
