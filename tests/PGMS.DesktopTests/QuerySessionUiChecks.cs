using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class QuerySessionUiChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        var query=new QueryView();var window=new Window{Content=query,Width=900,Height=650,ShowInTaskbar=false};window.Show();
        try
        {
            check(!((Button)query.FindName("SessionRefreshButton")).IsEnabled,"Disconnected query cannot inspect a session");
            await query.ConnectAsync(connection);var editor=(TextEditor)query.FindName("Editor");editor.Text="SELECT 1";
            long revision=query.EditRevision;await query.InspectSessionAsync();var grid=(DataGrid)query.FindName("SessionProperties");
            var rows=grid.Items.Cast<KeyValuePair<string,string>>().ToDictionary(p=>p.Key,p=>p.Value);
            check(grid.IsReadOnly && rows["Database"]=="postgres" && int.Parse(rows["Backend PID"])>0,"Session tab displays readonly real database and PID");
            check(query.EditRevision==revision && editor.Text=="SELECT 1" && !query.IsBusy,"Session inspection preserves SQL and restores idle state");
            editor.Text="SET application_name='PGMS session UI'";await query.ExecuteAsync();await query.InspectSessionAsync();
            rows=grid.Items.Cast<KeyValuePair<string,string>>().ToDictionary(p=>p.Key,p=>p.Value);
            check(rows["application_name"]=="PGMS session UI" && ((TextBlock)query.FindName("SessionStatus")).Text.Contains("Snapshot"),"Refresh shows actual changed setting with snapshot context");
            query.OpenSessionSettings();var value=(TextBox)query.FindName("SessionSettingValue");value.Text="pg_catalog";query.ReviewSessionChange();
            check(((TextBox)query.FindName("SessionPreview")).Text.Contains("pg_catalog.set_config") && ((Button)query.FindName("SessionApplyButton")).IsEnabled,"Session setting review shows guarded SQL before Apply");
            check(((TextBox)query.FindName("SessionPreview")).Text.Contains("OPERATOR(pg_catalog.=)"),"Session preview exposes catalog-qualified equality guards");
            value.Text="public";check(!((Button)query.FindName("SessionApplyButton")).IsEnabled,"Changing proposed setting invalidates review");
            value.Text="pg_catalog";query.ReviewSessionChange();revision=query.EditRevision;await query.ApplySessionChangeAsync();
            rows=grid.Items.Cast<KeyValuePair<string,string>>().ToDictionary(p=>p.Key,p=>p.Value);
            check(rows["search_path"]=="pg_catalog" && query.EditRevision==revision,"Reviewed Apply changes real session setting and refreshes without editing SQL");
            value.Text="public";query.ReviewSessionChange();editor.Text="SET search_path=pg_temp";await query.ExecuteAsync();await query.ApplySessionChangeAsync();
            check(((TextBlock)query.FindName("SessionStatus")).Text.Contains("Inspect before retrying") && !((Button)query.FindName("SessionEditButton")).IsEnabled,"Stale backend setting refuses Apply and requires fresh inspection");
            await query.InspectSessionAsync();query.OpenSessionSettings();value.Text="pg_catalog, pg_temp";query.ReviewSessionChange();
            var selector=(ComboBox)query.FindName("SessionSettingName");selector.SelectedIndex=1;value.Text="750";query.ReviewSessionChange();
            check(((TextBox)query.FindName("SessionPreview")).Text.Contains("750ms"),"Timeout review spells out server milliseconds");await query.ApplySessionChangeAsync();
            rows=grid.Items.Cast<KeyValuePair<string,string>>().ToDictionary(p=>p.Key,p=>p.Value);
            check(rows["statement_timeout"]=="750ms" && query.CommandTimeoutSeconds==0,"Server statement timeout changes independently of client read timeout");
            selector.SelectedIndex=2;value.Text="250";query.ReviewSessionChange();await query.ApplySessionChangeAsync();
            rows=grid.Items.Cast<KeyValuePair<string,string>>().ToDictionary(p=>p.Key,p=>p.Value);check(rows["lock_timeout"]=="250ms","UI applies server lock timeout with fresh snapshot");
            value.Text="1s";query.ReviewSessionChange();check(!((Button)query.FindName("SessionApplyButton")).IsEnabled,"Timeout UI refuses ambiguous units instead of guessing");
            selector.SelectedIndex=3;value.Text="PGMS reviewed session";query.ReviewSessionChange();await query.ApplySessionChangeAsync();
            rows=grid.Items.Cast<KeyValuePair<string,string>>().ToDictionary(p=>p.Key,p=>p.Value);check(rows["application_name"]=="PGMS reviewed session","UI applies reviewed application name to editor session");
            value.Text="inválido";query.ReviewSessionChange();check(!((Button)query.FindName("SessionApplyButton")).IsEnabled,"UI rejects application names the server would escape");
            value.Text="PGMS activity label";query.ReviewSessionChange();
            check(((TextBlock)query.FindName("SessionStatus")).Text.StartsWith("Change reviewed"),"Successful review replaces obsolete validation error");
            var escape=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(query),Environment.TickCount,System.Windows.Input.Key.Escape){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
            ((FrameworkElement)query.FindName("SessionSettingPanel")).RaiseEvent(escape);await query.ApplySessionChangeAsync();await query.InspectSessionAsync();
            rows=grid.Items.Cast<KeyValuePair<string,string>>().ToDictionary(p=>p.Key,p=>p.Value);
            check(escape.Handled && !((FrameworkElement)query.FindName("SessionSettingPanel")).IsVisible && rows["application_name"]=="PGMS reviewed session","Escape discards review and repeated Apply sends no setting change");
            foreach(Action open in new Action[]{query.Find,query.Replace,query.GoToLine})
            {
                query.OpenSessionSettings();value.Text="discard on navigation";query.ReviewSessionChange();open();await query.ApplySessionChangeAsync();await query.InspectSessionAsync();
                rows=grid.Items.Cast<KeyValuePair<string,string>>().ToDictionary(p=>p.Key,p=>p.Value);
                check(!((FrameworkElement)query.FindName("SessionSettingPanel")).IsVisible && rows["application_name"]=="PGMS reviewed session","Editor navigation closes session review and cannot apply hidden proposal");
            }
            query.OpenSessionSettings();value.Text="PGMS activity label";query.ReviewSessionChange();
            await ResultExportUiChecks.Capture(window,query,"m929-session-guards-normal",900,650,check);
            await ResultExportUiChecks.Capture(window,query,"m929-session-guards-small",600,440,check);
        }
        finally{query.Cancel();await query.DisposeAsync();window.Close();}
    }
}
