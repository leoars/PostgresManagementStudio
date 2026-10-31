using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class ActivityWindow : Window
{
    private readonly ActivityService service;
    private readonly DispatcherTimer autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool busy;
    private readonly int maximumRows;
    private DateTimeOffset? lastRefresh;
    private CancellationTokenSource? refreshCancellation;
    // One collection for the window's lifetime: refreshes replace only changed sessions, so rows,
    // sorting and automation peers survive (docs/en/validation.md measured the full rebuild).
    private readonly System.Collections.ObjectModel.ObservableCollection<ActivitySession> rows = [];
    /// <summary>Incremented after each successfully applied snapshot.</summary>
    public int SnapshotVersion { get; private set; }
    public bool IsAutoRefreshRunning => autoRefreshTimer.IsEnabled;
    public TimeSpan AutoRefreshInterval => autoRefreshTimer.Interval;
    public ActivityWindow(string connection):this(connection,1000){}
    internal ActivityWindow(string connection,int maximumRows)
    {
        if(maximumRows is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(maximumRows));
        this.maximumRows=maximumRows;
        InitializeComponent(); WindowWorkArea.Attach(this); service = new(connection); Sessions.ItemsSource = rows;
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Activity Monitor — {c.Host}:{c.Port} ({c.Username})\nServer-wide client sessions. Visibility and actions depend on PostgreSQL permissions.";
        autoRefreshTimer.Tick += async (_, _) => await ReloadAsync();
    }
    private async void OnLoaded(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void Refresh(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void ApplyFilters(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void AutoRefreshChanged(object sender, RoutedEventArgs e) => ConfigureAutoRefresh();
    private void AutoRefreshIntervalChanged(object sender, SelectionChangedEventArgs e) => ConfigureAutoRefresh();
    private void ConfigureAutoRefresh()
    {
        if (AutoRefreshIntervalSelector?.SelectedItem is ComboBoxItem { Tag: string seconds } && int.TryParse(seconds, out int interval))
            autoRefreshTimer.Interval = TimeSpan.FromSeconds(interval);
        if (AutoRefreshCheck?.IsChecked == true) autoRefreshTimer.Start(); else autoRefreshTimer.Stop();
    }
    private ActivityFilter ReadFilter()
    {
        int? pid = null; string pidText = PidFilter.Text.Trim();
        if (pidText.Length > 0)
        {
            if (!int.TryParse(pidText, out int value) || value <= 0) throw new ArgumentException("PID must be a positive integer.");
            pid = value;
        }
        string state = (StateFilter.SelectedItem as ComboBoxItem)?.Content as string ?? "All states";
        return new(DatabaseFilter.Text, UserFilter.Text, ApplicationFilter.Text, state == "All states" ? "" : state, pid){BlockedOnly=BlockedOnlyFilter.IsChecked==true};
    }
    public async Task ReloadAsync()
    {
        if (busy) return;
        try
        {
            refreshCancellation=new CancellationTokenSource();
            SetBusy(true);
            var filter = ReadFilter(); Status.Text = "Loading filtered sessions…";
            var snapshot = await service.ReadSnapshotAsync(filter,maximumRows,refreshCancellation.Token);refreshCancellation.Token.ThrowIfCancellationRequested();
            Merge(snapshot.Rows);
            SnapshotVersion++;
            lastRefresh=DateTimeOffset.Now;
            Status.Text = snapshot.IsTruncated?$"First {rows.Count} session(s); more sessions match. Narrow filters. Refreshed locally at {lastRefresh:HH:mm:ss}. Snapshot.":$"{rows.Count} session(s) loaded; complete filtered snapshot. Refreshed locally at {lastRefresh:HH:mm:ss}.";
        }
        catch(OperationCanceledException) when(refreshCancellation?.IsCancellationRequested==true){Status.Text="Refresh canceled. Previous snapshot retained; auto-refresh paused.";}
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or ArgumentException) { Status.Text = Describe(ex)+" "+(lastRefresh is null?"No successful snapshot loaded.":$"Previous snapshot retained (local refresh {lastRefresh:HH:mm:ss}); proposed filters were not applied."); }
        finally { refreshCancellation?.Dispose();refreshCancellation=null;SetBusy(false); }
    }
    // Keeps server order in the collection; the view applies the user's sort on top of it.
    // The grid stays enabled while refreshing: toggling it re-rendered every visible cell and
    // raised an automation event per cell; row actions are already blocked through busy.
    private void Merge(IReadOnlyList<ActivitySession> next)
    {
        var keys=next.Select(s=>(s.Pid,s.BackendStart)).ToHashSet();
        for(int i=rows.Count-1;i>=0;i--)if(!keys.Contains((rows[i].Pid,rows[i].BackendStart)))rows.RemoveAt(i);
        for(int i=0;i<next.Count;i++)
        {
            var item=next[i];int current=-1;
            for(int j=i;j<rows.Count;j++)if(rows[j].Pid==item.Pid && rows[j].BackendStart==item.BackendStart){current=j;break;}
            if(current<0){rows.Insert(i,item);continue;}
            if(current!=i)rows.Move(current,i);
            if(SameDisplay(rows[i],item))continue;
            bool reselect=ReferenceEquals(Sessions.SelectedItem,rows[i]);
            rows[i]=item;
            if(reselect)Sessions.SelectedItem=item;
        }
    }
    private static bool SameDisplay(ActivitySession a,ActivitySession b)=>a.Database==b.Database && a.User==b.User && a.Application==b.Application
        && a.State==b.State && a.Wait==b.Wait && a.Query==b.Query && a.BlockingPids.SequenceEqual(b.BlockingPids)
        && a.QueryDuration==b.QueryDuration && a.TransactionDuration==b.TransactionDuration;
    private void CancelRefresh(object sender,RoutedEventArgs e)
    {
        if(refreshCancellation is null)return;
        AutoRefreshCheck.IsChecked=false;refreshCancellation.Cancel();CancelRefreshButton.IsEnabled=false;Status.Text="Canceling refresh… Previous snapshot retained.";
    }
    public async Task ApplyFiltersAsync() => await ReloadAsync();
    private void SelectionChanged(object sender, RoutedEventArgs e) { if (CancelButton is not null) UpdateActions(); }
    private void UpdateActions() => CancelButton.IsEnabled = TerminateButton.IsEnabled = !busy && Sessions.SelectedItem is ActivitySession;
    private void SetBusy(bool value) { busy = value; CancelRefreshButton.Visibility=refreshCancellation is null?Visibility.Collapsed:Visibility.Visible;CancelRefreshButton.IsEnabled=refreshCancellation?.IsCancellationRequested==false; RefreshButton.IsEnabled = ApplyFiltersButton.IsEnabled = !value; FilterFields.IsEnabled = !value; UpdateActions(); }
    private async void CancelQuery(object sender, RoutedEventArgs e) => await SignalAsync(false);
    private async void Terminate(object sender, RoutedEventArgs e) => await SignalAsync(true);
    private async Task SignalAsync(bool terminate)
    {
        if (busy || Sessions.SelectedItem is not ActivitySession session) return;
        if (terminate && MessageBox.Show(this, $"Terminate PID {session.Pid}?\nDatabase: {session.Database}\nUser: {session.User}\nApplication: {session.Application}\n\nThe connection will close and its open transaction will roll back.", "Terminate Session", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            bool sent = await service.SignalAsync(session, terminate);
            Status.Text = sent ? (terminate ? "Session termination confirmed. Refresh to update the list." : "Cancel signal sent. The session stays connected; an open transaction may require ROLLBACK.") : "No signal confirmed. The selected session may have ended or changed; refresh before retrying.";
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException) { Status.Text = Describe(ex); }
        finally { SetBusy(false); }
    }
    private static string Describe(Exception ex) => ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; Status.Text = refreshCancellation is null?"Wait for the current operation before closing.":"Cancel Refresh before closing this window."; } else autoRefreshTimer.Stop(); }
}
