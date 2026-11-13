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
    private void SavePlanFromDiagram(object? sender,EventArgs e)=>ExportPlan(this,new RoutedEventArgs());
    /// <summary>Show Execution Plan In New Tab: the host opens the plan on screen in a document of its own.</summary>
    internal event Action<IReadOnlyList<ExecutionPlanStatement>>? PlanTabRequested;
    private void ShowPlanInNewTab(object? sender,EventArgs e){if(PlanDiagram.Statements.Count>0)PlanTabRequested?.Invoke(PlanDiagram.Statements);}
    /// <summary>Missing Index Details…: the host opens the commented script in a new query tab of this connection. Nothing is executed.</summary>
    internal event Action<QueryView,IReadOnlyList<PlanIndexSuggestion>>? MissingIndexDetailsRequested;
    private void ShowMissingIndexDetails(IReadOnlyList<PlanIndexSuggestion> suggestions)=>MissingIndexDetailsRequested?.Invoke(this,suggestions);
    /// <summary>Adds the index suggestions that pass the catalog check (PGMS-041). A failure here, such as a transaction left aborted, only means no suggestion.</summary>
    private static async Task<IReadOnlyList<ExecutionPlanStatement>> WithIndexAdviceAsync(QuerySession session,IReadOnlyList<ExecutionPlanStatement> statements,CancellationToken token)
    {
        try{return await session.AdviseIndexesAsync(statements,token).ConfigureAwait(false);}
        catch(Exception ex)when(ex is NpgsqlException or InvalidOperationException or IOException or TimeoutException or ObjectDisposedException){return statements;}
    }
    // Export follows the statement of the selected operator; with no plan on screen there is nothing to export.
    private void SelectedPlanOperator(object? sender,EventArgs e)
    {
        PlanProperties.ItemsSource=PlanDiagram.SelectedOperator?.Properties;
        currentPlan=PlanDiagram.SelectedStatement?.Source;
        RefreshPlanRevision();UpdateExport();
    }
    private bool includeActualPlan;
    /// <summary>Include Actual Execution Plan (Ctrl+M): each execution of this tab also collects the plans it really used.</summary>
    public bool IncludeActualPlan
    {
        get=>includeActualPlan;
        set{if(includeActualPlan==value)return;includeActualPlan=value;StateChanged?.Invoke(this,EventArgs.Empty);}
    }
    /// <summary>Shows what an execution collected. As in SSMS the Results tab stays in front; the plan waits in its own tab.</summary>
    private async Task ShowActualPlansAsync(ActualPlanCapture capture,long revision)
    {
        PlanDiagram.Clear();
        if(capture.Unavailable is { } reason)
        {
            PlanStatus.Text="Actual plan not available: "+reason+"\nThe SQL ran normally. Include Actual Execution Plan needs a superuser connection and the auto_explain module; Analyze Plan works for any user, one statement at a time.";
            Messages.AppendText("\nActual plan not available: "+reason);
            return;
        }
        if(capture.Plans.Count==0){PlanStatus.Text="This execution produced no plan: statements such as SET or CREATE have none.";return;}
        try
        {
            var planWarnings=QueryOptions.PlanWarnings;
            var capturedSession=session;
            var statements=await Task.Run(async()=>
            {
                var built=ExecutionPlanBuilder.Build(capture.Plans,warnings:planWarnings);
                return capturedSession is { IsConnected: true }?await WithIndexAdviceAsync(capturedSession,built,CancellationToken.None).ConfigureAwait(false):built;
            });
            planRevision=revision;PlanDiagram.Show(statements);
            PlanStatus.Text=$"Actual plan · captured during this execution, which ran once. {statements.Count} statement(s)."+(capture.Omitted>0?$" {capture.Omitted} plan(s) omitted: limit {ActualPlanCapture.MaximumPlans} plans, 1 Mi characters each.":"");
        }
        catch(Exception ex)when(ex is JsonException or InvalidDataException){PlanStatus.Text="Actual plan could not be read: "+ex.Message;}
    }
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
                PlanDiagram.Clear();
                PlanStatus.Text=$"Not executed: the SQL contains parameter placeholder {unbound.Description}, which has no value. An analyzed plan runs the statement, so replace each $n with a literal value first. Estimated Plan shows the generic plan without values.\nNothing was sent to the server and the session stays open.";
                OutputTabs.SelectedItem=PlanTab;QueryStatus.Text=$"Not executed | {Database} | Unbound parameter ${unbound.Number}";
                return;
            }
            generic=switchedToGeneric=true;
        }
        var notices=new QueryNotices();var options=new QueryExecutionOptions(timeout,notices);
        IsBusy=executingQuery=true;cancellation=new();QueryTimeout.IsEnabled=false;
        PlanDiagram.Clear();PlanStatus.Text=analyze?"Executing SQL for analyzed plan…":generic?"Reading generic plan…":"Reading estimated plan…";
        OutputTabs.SelectedItem=PlanTab;QueryStatus.Text=PlanStatus.Text;
        UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);
        try
        {
            var capturedSession=session;var token=cancellation.Token;var planWarnings=QueryOptions.PlanWarnings;
            var loaded=await Task.Run(async()=>
            {
                var plan=await (analyze?capturedSession.AnalyzeAsync(sql,options,token):generic?capturedSession.ExplainGenericAsync(sql,options,token):capturedSession.ExplainAsync(sql,options,token)).ConfigureAwait(false);
                var built=ExecutionPlanBuilder.Build([plan],sql,token,planWarnings);
                return (Statements:await WithIndexAdviceAsync(capturedSession,built,token).ConfigureAwait(false),Timings:QueryPlanTimings.Read(plan));
            },token);
            planRevision=revision;PlanDiagram.Show(loaded.Statements);
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
