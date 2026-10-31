using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class QueryView
{
    private sealed record SettingReview(QuerySessionSnapshot Snapshot,string Name,string Value);
    private SettingReview? settingReview;
    private string SelectedSetting=>((ComboBoxItem)SessionSettingName.SelectedItem).Tag.ToString()!;
    private void ClearSettingReview()
    {settingReview=null;if(SessionPreview is not null)SessionPreview.Clear();if(SessionApplyButton is not null)SessionApplyButton.IsEnabled=false;}
    private void HideSessionSettings(){ClearSettingReview();SessionSettingPanel.Visibility=Visibility.Collapsed;}
    private void EditSessionSettings(object sender,RoutedEventArgs e)=>OpenSessionSettings();
    public void OpenSessionSettings()
    {
        if(IsBusy || sessionSnapshot is null)return;
        ClearAnalysisReview();ReplacementBar.Hide();NavigationBar.Visibility=Visibility.Collapsed;search.Close();
        SessionSettingPanel.Visibility=Visibility.Visible;FillSessionSetting();SessionSettingValue.Focus();SessionSettingValue.SelectAll();
    }
    private void FillSessionSetting()
    {
        ClearSettingReview();if(sessionSnapshot is null)return;
        SessionSettingValue.MaxLength=SelectedSetting switch {"search_path"=>2048,"application_name"=>63,_=>10};
        SessionSettingValue.Text=SelectedSetting switch
        {"statement_timeout"=>sessionSnapshot.StatementTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),"lock_timeout"=>sessionSnapshot.LockTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),_=>sessionSnapshot.Settings[SelectedSetting]};
        SessionSettingHelp.Text=SelectedSetting switch
        {"application_name"=>"Up to 63 printable ASCII characters. Visible to permitted activity viewers and in configured server logs.","statement_timeout"=>"Server milliseconds (0–86400000). 0 disables the limit. Separate from the client's read timeout.","lock_timeout"=>"Server milliseconds per lock wait (0–86400000). 0 disables the limit. Statement timeout may fire earlier.",_=>"Missing or inaccessible schemas are ignored. This change grants no permissions."};
    }
    private void SessionSettingChanged(object sender,SelectionChangedEventArgs e){if(SessionSettingValue is not null)FillSessionSetting();}
    private void SessionSettingValueChanged(object sender,TextChangedEventArgs e)=>ClearSettingReview();
    private void CloseSessionSettings(object sender,RoutedEventArgs e){HideSessionSettings();Editor.Focus();}
    private void SessionSettingsKeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if(e.Key==System.Windows.Input.Key.Escape && !IsBusy){HideSessionSettings();Editor.Focus();e.Handled=true;}
    }
    private void ReviewSessionSetting(object sender,RoutedEventArgs e)=>ReviewSessionChange();
    public void ReviewSessionChange()
    {
        ClearSettingReview();if(IsBusy || sessionSnapshot is null)return;
        string value=SessionSettingValue.Text,name=SelectedSetting;
        try
        {
            string serverValue=value;
            if(name=="search_path")
            {
                if(value.Length>2048 || value.Contains('\0'))throw new ArgumentException("Use a search path of at most 2048 characters without NUL.");
                _=new UTF8Encoding(false,true).GetByteCount(value);
            }
            else if(name is "statement_timeout" or "lock_timeout")
            {
                if(!int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out int milliseconds) || milliseconds is <0 or >86400000)throw new ArgumentException("Enter milliseconds from 0 to 86400000 (0 disables the server limit).");
                serverValue=milliseconds.ToString(CultureInfo.InvariantCulture)+"ms";
            }
            else if(name=="application_name")
            {
                if(value.Length>63 || value.Any(c=>c is <' ' or >'~'))throw new ArgumentException("Use up to 63 printable ASCII characters for application name.");
            }
            else throw new ArgumentException("Unsupported session setting.");
            static string Literal(string text)=>"E'"+text.Replace("\\","\\\\").Replace("'","''")+"'";
            SessionPreview.Text=$"SELECT pg_catalog.set_config({Literal(name)}, {Literal(serverValue)}, false)\nWHERE pg_catalog.current_setting({Literal(name)}) OPERATOR(pg_catalog.=) {Literal(sessionSnapshot.Settings[name])}\nAND pg_catalog.pg_backend_pid() OPERATOR(pg_catalog.=) {sessionSnapshot.BackendPid.ToString(CultureInfo.InvariantCulture)} AND current_user::pg_catalog.text OPERATOR(pg_catalog.=) {Literal(sessionSnapshot.CurrentUser)};";
            settingReview=new(sessionSnapshot,name,value);SessionApplyButton.IsEnabled=true;SessionStatus.Text="Change reviewed. Apply affects this session; Close discards the proposal.";
        }
        catch(ArgumentException ex){SessionStatus.Text=ex.Message;}
    }
    private async void ApplySessionSetting(object sender,RoutedEventArgs e)=>await ApplySessionChangeAsync();
    public async Task ApplySessionChangeAsync()
    {
        if(IsBusy)return;var review=settingReview;
        if(review is null || session is null || !ReferenceEquals(review.Snapshot,sessionSnapshot) || review.Name!=SelectedSetting || review.Value!=SessionSettingValue.Text)
        {ClearSettingReview();SessionStatus.Text="Inspect and review the current change before applying.";return;}
        ClearSettingReview();IsBusy=inspectingSession=true;cancellation=new();sessionSnapshot=null;
        SessionStatus.Text="Applying session-local setting…";UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);bool applied=false;
        try
        {
            await (review.Name switch
            {"application_name"=>session.SetApplicationNameAsync(review.Snapshot,review.Value,cancellation.Token),"search_path"=>session.SetSearchPathAsync(review.Snapshot,review.Value,cancellation.Token),"statement_timeout"=>session.SetStatementTimeoutAsync(review.Snapshot,int.Parse(review.Value,CultureInfo.InvariantCulture),cancellation.Token),"lock_timeout"=>session.SetLockTimeoutAsync(review.Snapshot,int.Parse(review.Value,CultureInfo.InvariantCulture),cancellation.Token),_=>throw new InvalidOperationException("Unsupported session setting.")});applied=true;
            ShowSessionSnapshot(await session.InspectSessionAsync(cancellation.Token));FillSessionSetting();
            SessionStatus.Text="Session setting applied. "+SessionStatus.Text;QueryStatus.Text="Session setting applied | "+Database;
        }
        catch(Exception ex)when(ex is NpgsqlException or OperationCanceledException or InvalidOperationException or ArgumentException or IOException)
        {
            SessionProperties.ItemsSource=null;
            SessionStatus.Text=(applied?"Setting applied, but inspection did not complete. ":"Change not confirmed. ")+"Inspect before retrying. "+(ex is PostgresException pg?QueryErrorFormatter.Format(pg):ex.Message);
            QueryStatus.Text="Session setting requires inspection | "+Database;
        }
        finally{IsBusy=inspectingSession=false;cancellation.Dispose();cancellation=null;UpdateExport();StateChanged?.Invoke(this,EventArgs.Empty);}
    }
}
