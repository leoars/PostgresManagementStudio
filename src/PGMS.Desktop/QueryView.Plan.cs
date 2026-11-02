using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class QueryView
{
    private QueryPlan? currentPlan;
    private long planRevision;
    private async void ExportPlan(object sender,RoutedEventArgs e)
    {
        if(IsBusy || currentPlan is null)return;
        var picker=new Microsoft.Win32.SaveFileDialog{Filter="PostgreSQL plan JSON (*.json)|*.json",DefaultExt=".json",FileName="plan.json"};
        if(picker.ShowDialog(Window.GetWindow(this))!=true)return;
        try{await ExportPlanAsync(picker.FileName,File.Exists(picker.FileName));}
        catch(OperationCanceledException){QueryStatus.Text="Plan export canceled.";}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException){QueryStatus.Text=ex.Message;}
    }
    public async Task ExportPlanAsync(string path,bool overwrite=false,CancellationToken token=default)
    {
        if(IsBusy || currentPlan is null)throw new InvalidOperationException("Request a complete plan before exporting.");
        token.ThrowIfCancellationRequested();var snapshot=currentPlan;
        IsBusy=exporting=true;cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);
        try
        {
            await Task.Run(()=>QueryPlanFiles.SaveAsync(snapshot,path,overwrite,cancellation.Token));
            QueryStatus.Text="Plan snapshot exported as native PostgreSQL JSON.";
        }
        finally{IsBusy=exporting=false;cancellation.Dispose();cancellation=null;UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);}
    }
    private void SelectedPlanNode(object sender,RoutedPropertyChangedEventArgs<object> e)
        => PlanProperties.ItemsSource=(e.NewValue as QueryPlanNode)?.Properties;
    private void RefreshPlanRevision()
        => PlanStale.Visibility=currentPlan is not null && planRevision!=EditRevision?Visibility.Visible:Visibility.Collapsed;
    private async void ExplainPlan(object sender,RoutedEventArgs e)=>await ExplainPlanAsync();
    public Task ExplainPlanAsync(){ClearAnalysisReview();return LoadPlanAsync(false);}
    /// <summary>Generic estimated plan of the whole document, for SQL with unbound $n parameters (Query Statistics → Show Plan).</summary>
    public Task ExplainGenericPlanAsync(){ClearAnalysisReview();return LoadPlanAsync(false,generic:true);}
    private async Task LoadPlanAsync(bool analyze,string? reviewedSql=null,bool generic=false)
    {
        if(IsBusy || session?.IsConnected!=true)return;
        string sql=reviewedSql ?? (Editor.SelectionLength>0?Editor.SelectedText:Editor.Text);
        string scope=Editor.SelectionLength>0?"Selected SQL":"Document SQL";
        long revision=EditRevision;
        if(string.IsNullOrWhiteSpace(sql))return;
        if(!int.TryParse(QueryTimeout.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int timeout) || timeout is <0 or >86400)
        {QueryStatus.Text="Enter a read timeout from 0 to 86400 seconds.";QueryTimeout.Focus();return;}
        bool switchedToGeneric=false;
        if(!generic && QueryPlaceholders.Find(sql) is { } unbound)
        {
            if(analyze)
            {
                if(reviewedSql is null)MarkUnboundParameter(unbound,Editor.SelectionLength>0?Editor.SelectionStart:0,revision);
                currentPlan=null;PlanTree.ItemsSource=null;PlanProperties.ItemsSource=null;RefreshPlanRevision();UpdateExport();
                PlanStatus.Text=$"Not executed: the SQL contains parameter placeholder {unbound.Description}, which has no value. An analyzed plan runs the statement, so replace each $n with a literal value first. Estimated Plan shows the generic plan without values.\nNothing was sent to the server and the session stays open.";
                OutputTabs.SelectedItem=PlanTab;QueryStatus.Text=$"Not executed | {Database} | Unbound parameter ${unbound.Number}";
                return;
            }
            generic=switchedToGeneric=true;
        }
        var notices=new QueryNotices();var options=new QueryExecutionOptions(timeout,notices);
        IsBusy=executingQuery=true;cancellation=new();QueryTimeout.IsEnabled=false;
        currentPlan=null;PlanTree.ItemsSource=null;PlanProperties.ItemsSource=null;RefreshPlanRevision();PlanStatus.Text=analyze?"Executing SQL for analyzed plan…":generic?"Reading generic plan…":"Reading estimated plan…";
        OutputTabs.SelectedItem=PlanTab;QueryStatus.Text=PlanStatus.Text;
        UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);
        try
        {
            var capturedSession=session;var token=cancellation.Token;
            var loaded=await Task.Run(async()=>
            {
                var plan=await (analyze?capturedSession.AnalyzeAsync(sql,options,token):generic?capturedSession.ExplainGenericAsync(sql,options,token):capturedSession.ExplainAsync(sql,options,token)).ConfigureAwait(false);
                return (Plan:plan,Root:QueryPlanTree.Parse(plan,token),Timings:QueryPlanTimings.Read(plan));
            },token);
            currentPlan=loaded.Plan;planRevision=revision;RefreshPlanRevision();PlanTree.ItemsSource=new[]{loaded.Root};
            PlanStatus.Text=analyze?"Analyzed plan · SQL executed. Effects follow the current transaction; no automatic rollback.":generic?"Generic plan · $n parameters are not bound, so the planner cannot use their values; costs are planner units. Query rows were not executed.":"Estimated plan · costs are planner units, not milliseconds. Query rows were not executed.";
            PlanStatus.Text+="\n"+scope+" snapshot.";
            if(switchedToGeneric)PlanStatus.Text+=" The SQL contains $n placeholders, so Estimated Plan used GENERIC_PLAN instead of sending them unbound.";
            if(loaded.Timings.PlanningMilliseconds is double planning)PlanStatus.Text+=" Planning: "+planning.ToString("0.###",CultureInfo.InvariantCulture)+" ms.";
            if(loaded.Timings.ExecutionMilliseconds is double elapsed)PlanStatus.Text+=" Execution: "+elapsed.ToString("0.###",CultureInfo.InvariantCulture)+" ms (server).";
            QueryStatus.Text=(analyze?"Analyzed":generic?"Generic":"Estimated")+" plan loaded | "+Database;
        }
        catch(Exception ex) when(ex is NpgsqlException or OperationCanceledException or ArgumentException or InvalidOperationException or IOException or JsonException)
        {
            PlanStatus.Text=ex is PostgresException pg?QueryErrorFormatter.Format(pg):ex is OperationCanceledException?"Plan request canceled.":ex.Message;
            if(analyze)PlanStatus.Text+="\nSQL may have executed or committed changes. Check state before retrying; an explicit transaction may require ROLLBACK.";
            QueryStatus.Text="Plan request did not complete | "+Database;
        }
        finally
        {
            foreach(var notice in notices.Items)PlanStatus.Text+=$"\n{notice.Severity} {notice.SqlState}: {notice.Message}";
            if(notices.Omitted>0)PlanStatus.Text+=$"\n{notices.Omitted} notice(s) omitted.";
            IsBusy=executingQuery=false;cancellation.Dispose();cancellation=null;QueryTimeout.IsEnabled=true;
            UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);
        }
    }
}
