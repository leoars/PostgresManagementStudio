using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class QueryPlanUiChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        var query=new QueryView();var window=new Window{Content=query,Width=900,Height=650,ShowInTaskbar=false};window.Show();
        try
        {
            await query.ConnectAsync(connection);var editor=(TextEditor)query.FindName("Editor");
            editor.Text="SELECT * FROM generate_series(1,20) AS items WHERE items>10";
            string original=editor.Text;long revision=query.EditRevision;await query.ExplainPlanAsync();
            var tree=(TreeView)query.FindName("PlanTree");
            check(tree.Items.Count==1 && ((TextBlock)query.FindName("PlanStatus")).Text.Contains("Estimated plan"),"Estimated plan appears in dedicated native tree");
            check(editor.Text==original && query.EditRevision==revision && !query.IsBusy,"Plan request preserves SQL and editor revision");
            await query.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var item=(TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);item.IsSelected=true;
            var properties=(DataGrid)query.FindName("PlanProperties");
            check(properties.Items.Count>2 && properties.IsReadOnly,"Selecting a plan node exposes readonly server properties");
            check(((Button)query.FindName("ExplainButton")).IsEnabled,"Estimated plan action is enabled for connected idle query");
            editor.Text="SELECT 1; SELECT 2";await query.ExplainPlanAsync();
            check(tree.Items.Count==0 && ((TextBlock)query.FindName("PlanStatus")).Text.Contains("one statement"),"Rejected plan clears stale tree and explains single-statement limit");
            editor.Text="SELECT 1";await query.ExplainPlanAsync();
            check(tree.Items.Count==1,"Plan can be requested again after rejected script");
            editor.AppendText(" ");check(((TextBlock)query.FindName("PlanStale")).IsVisible,"Editing SQL labels prior plan as a snapshot of an earlier revision");
            await query.ExplainPlanAsync();check(!((TextBlock)query.FindName("PlanStale")).IsVisible,"Fresh plan clears stale revision notice");
            await query.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected=true;
            string directory=System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts","plan-ui",Guid.NewGuid().ToString("N")));System.IO.Directory.CreateDirectory(directory);
            string destination=System.IO.Path.Combine(directory,"plan.json");await query.ExportPlanAsync(destination);
            using var saved=System.Text.Json.JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(destination));
            check(saved.RootElement[0].GetProperty("Plan").GetProperty("Node Type").GetString()=="Result" && ((Button)query.FindName("PlanExportButton")).IsEnabled,"Native UI exports complete original PostgreSQL plan and restores action");
            editor.Text="CREATE TEMP TABLE plan_ui(id int)";await query.ExecuteAsync();
            editor.Text="INSERT INTO plan_ui VALUES(1)";query.ReviewAnalyzedPlan();
            check(((FrameworkElement)query.FindName("AnalysisPanel")).IsVisible && !query.IsBusy && ((TextBox)query.FindName("AnalysisSql")).Text==editor.Text,"Analyze action presents exact SQL review without execution");
            await ResultExportUiChecks.Capture(window,query,"m918-review-normal",900,650,check);
            await ResultExportUiChecks.Capture(window,query,"m918-review-small",600,440,check);
            ((Button)query.FindName("AnalysisClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.Text="SELECT count(*) FROM plan_ui";await query.ExecuteAsync();
            var results=(TabControl)query.FindName("ResultSets");
            string Count()=> (string)((System.Data.DataRowView)((DataGrid)((TabItem)results.Items[0]).Content).Items[0])[0];
            check(Count()=="0","Closing analyzed-plan review sends no INSERT");
            editor.Text="INSERT INTO plan_ui VALUES(1)";query.ReviewAnalyzedPlan();editor.AppendText(" ");await query.ConfirmAnalyzedPlanAsync();
            check(((TextBlock)query.FindName("QueryStatus")).Text.Contains("changed"),"Edited SQL invalidates analyzed-plan review");
            query.ReviewAnalyzedPlan();((TextBox)query.FindName("QueryTimeout")).Text="1";await query.ConfirmAnalyzedPlanAsync();
            check(((TextBlock)query.FindName("QueryStatus")).Text.Contains("changed"),"Changed timeout invalidates analyzed-plan review");
            ((TextBox)query.FindName("QueryTimeout")).Text="0";query.ReviewAnalyzedPlan();await query.ConfirmAnalyzedPlanAsync();
            check(((TextBlock)query.FindName("PlanStatus")).Text.Contains("Analyzed plan"),"Explicit reviewed action loads analyzed plan");
            check(((TextBlock)query.FindName("PlanStatus")).Text.Contains("Execution:") && ((TextBlock)query.FindName("PlanStatus")).Text.Contains("Document SQL snapshot"),"Analyzed view identifies server milliseconds and SQL scope");
            await ResultExportUiChecks.Capture(window,query,"m919-actual-normal",900,650,check);
            await ResultExportUiChecks.Capture(window,query,"m919-actual-small",600,440,check);
            await query.ConfirmAnalyzedPlanAsync();editor.Text="SELECT count(*) FROM plan_ui";await query.ExecuteAsync();
            check(Count()=="1","Reviewed INSERT executes once; stale and repeated confirmations send no extra SQL");
            editor.Text="INSERT INTO plan_ui VALUES(2)";query.ReviewAnalyzedPlan();await query.ExecuteAsync();await query.ConfirmAnalyzedPlanAsync();
            editor.Text="SELECT count(*) FROM plan_ui";await query.ExecuteAsync();
            check(Count()=="2" && !((FrameworkElement)query.FindName("AnalysisPanel")).IsVisible,"Normal execution invalidates pending analysis review and prevents duplicate INSERT");
            editor.Text="SELECT "+new string('x',65536);query.ReviewAnalyzedPlan();
            check(!((FrameworkElement)query.FindName("AnalysisPanel")).IsVisible && ((TextBlock)query.FindName("QueryStatus")).Text.Contains("64 Ki"),"Oversize analysis review is refused before execution");
            editor.Text="SELECT 1";query.ReviewAnalyzedPlan();
            var escape=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(query),Environment.TickCount,System.Windows.Input.Key.Escape){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
            ((FrameworkElement)query.FindName("AnalysisPanel")).RaiseEvent(escape);
            check(escape.Handled && !((FrameworkElement)query.FindName("AnalysisPanel")).IsVisible,"Escape closes analysis review without executing SQL");
            foreach(Action open in new Action[]{query.Find,query.Replace,query.GoToLine})
            {
                editor.Text="SELECT 1";query.ReviewAnalyzedPlan();open();await query.ConfirmAnalyzedPlanAsync();
                check(!((FrameworkElement)query.FindName("AnalysisPanel")).IsVisible && ((TextBlock)query.FindName("QueryStatus")).Text.Contains("changed"),"Editor navigation discards analyzed-plan review before a hidden confirmation");
            }
            editor.Text="SELECT pg_sleep(10)";query.ReviewAnalyzedPlan();var pending=query.ConfirmAnalyzedPlanAsync();await Task.Delay(150);query.Cancel();await pending.WaitAsync(TimeSpan.FromSeconds(5));
            check(!query.IsBusy && tree.Items.Count==0 && !((Button)query.FindName("PlanExportButton")).IsEnabled && ((TextBlock)query.FindName("PlanStatus")).Text.Contains("may have executed"),"Canceled analyzed request clears plan and reports uncertain effects before retry");
            await ResultExportUiChecks.Capture(window,query,"m916-plan-normal",900,650,check);
            await ResultExportUiChecks.Capture(window,query,"m916-plan-small",600,440,check);
        }
        finally{query.Cancel();await query.DisposeAsync();window.Close();}
    }
}
