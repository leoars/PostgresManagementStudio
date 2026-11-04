using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
/// <summary>Activity Monitor document: Overview charts and the Processes, Resource Waits, Data File I/O and expensive query panels of SSMS 22.11.</summary>
public partial class ActivityMonitorView : UserControl
{
    private readonly string connection;
    private readonly ActivityService service;
    private readonly ActivityMetricsService metrics;
    private readonly QueryStatisticsService statements;
    private ActivityMetricsHistory history = new();
    // Counters of the previous refresh while Recent Expensive Queries is expanded; each refresh shows the activity since it.
    private QueryStatisticsBaseline? statementsBaseline;
    private readonly DispatcherTimer autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool busy, loaded, shutDown;
    private readonly int maximumRows;
    private DateTimeOffset? lastRefresh;
    // Current page as a keyset anchor on PID; refreshes reload the same anchor so new sessions inside the range appear.
    private ActivityPage page=ActivityPage.First;
    private ActivityFilter? appliedFilter;
    private ActivitySnapshot? lastSnapshot;
    private CancellationTokenSource? refreshCancellation;
    // One collection for the monitor's lifetime: refreshes replace only changed sessions, so rows,
    // sorting and automation peers survive (docs/GATE-DESEMPENHO-M948.md measured the full rebuild).
    private readonly ObservableCollection<ActivitySession> rows = [];
    private readonly ObservableCollection<ResourceWaitRow> waitRows = [];
    private readonly ObservableCollection<DataFileIoRow> fileRows = [];
    private readonly ObservableCollection<RecentExpensiveQuery> recentRows = [];
    private readonly ObservableCollection<ActiveExpensiveQuery> activeRows = [];
    /// <summary>Incremented after each successfully applied snapshot.</summary>
    public int SnapshotVersion { get; private set; }
    /// <summary>Incremented after each successful server-wide metrics reading.</summary>
    public int MetricsVersion { get; private set; }
    public bool IsAutoRefreshRunning => autoRefreshTimer.IsEnabled;
    public TimeSpan AutoRefreshInterval => autoRefreshTimer.Interval;
    public ActivityMetricsHistory History => history;
    /// <summary>Document tab title: server first, like SSMS "(server) - Activity Monitor".</summary>
    public string Title { get; }
    /// <summary>Opens SQL in a new query tab connected with the given connection string, without running it (Active Expensive Queries → Edit Query Text).</summary>
    internal Func<string, string, Task>? OpenQueryText { get; init; }
    /// <summary>Opens SQL in a new query tab and shows its estimated plan; never executes it (Active Expensive Queries → Show Execution Plan).</summary>
    internal Func<string, string, Task>? OpenQueryPlan { get; init; }
    public ActivityMonitorView(string connection):this(connection,1000){}
    internal ActivityMonitorView(string connection,int maximumRows)
    {
        if(maximumRows is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(maximumRows));
        this.maximumRows=maximumRows;this.connection=connection;
        InitializeComponent(); service = new(connection); metrics = new(connection); statements = new(connection);
        Sessions.ItemsSource = rows; ResourceWaits.ItemsSource = waitRows; DataFileIo.ItemsSource = fileRows; RecentQueries.ItemsSource = recentRows; ActiveQueries.ItemsSource = activeRows;
        var c = new NpgsqlConnectionStringBuilder(connection);
        Title = $"{c.Host}:{c.Port} ({c.Username}) - Activity Monitor";
        Target.Text = $"{Title}\nServer-wide activity. Visibility and actions depend on PostgreSQL permissions.";
        autoRefreshTimer.Tick += async (_, _) => await ReloadAsync();
        ApplyHeaderTips(Sessions, ColumnDescriptions); ApplyHeaderTips(ResourceWaits, PanelColumnDescriptions); ApplyHeaderTips(DataFileIo, PanelColumnDescriptions);
        ApplyHeaderTips(RecentQueries, PanelColumnDescriptions); ApplyHeaderTips(ActiveQueries, PanelColumnDescriptions);
        ShowCharts();
        RecentQueriesNote.Text = "Expand to measure pg_stat_statements between refreshes.";
        foreach (var option in AutoRefreshIntervalSelector.Items.OfType<ComboBoxItem>())
        {
            var item = new MenuItem { Header = IntervalLabel(int.Parse((string)option.Tag, CultureInfo.InvariantCulture)), IsCheckable = true, Tag = option };
            item.Click += (_, _) => AutoRefreshIntervalSelector.SelectedItem = option;
            RefreshIntervalMenu.Items.Add(item);
        }
        // Server order is PID; marking the column lets the first header click reverse it, as in SSMS (M1030).
        Sessions.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(ActivitySession.Pid), System.ComponentModel.ListSortDirection.Ascending));
        Sessions.Columns[0].SortDirection = System.ComponentModel.ListSortDirection.Ascending;
        // SSMS 22.11 opens its monitor refreshing every 10 seconds (M1026).
        AutoRefreshCheck.IsChecked = true;
    }
    /// <summary>Same server, port and user: the monitor is server-wide, so Activity Monitor reuses that tab instead of opening another one.
    /// Only Recent Expensive Queries depends on the database, and its note names the database it reads.</summary>
    public bool Targets(string other)
    {
        var a = new NpgsqlConnectionStringBuilder(connection); var b = new NpgsqlConnectionStringBuilder(other);
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port && a.Username == b.Username;
    }
    private static void ApplyHeaderTips(DataGrid grid, IReadOnlyDictionary<string, string> descriptions)
    {
        foreach (var column in grid.Columns)
            if (column.Header is string header && descriptions.TryGetValue(header, out string? description))
                column.HeaderStyle = new Style(typeof(DataGridColumnHeader)) { Setters = { new Setter(ToolTipProperty, description) } };
    }
    /// <summary>Header tooltips: meaning and pg_stat_activity source, like the SSMS column descriptions.</summary>
    internal static IReadOnlyDictionary<string, string> ColumnDescriptions { get; } = new Dictionary<string, string>
    {
        ["PID"] = "Backend process ID (pg_stat_activity.pid)",
        ["Database"] = "Database the session is connected to (datname)",
        ["User"] = "Role that logged in (usename)",
        ["Application"] = "Name set by the client (application_name)",
        ["Host"] = "Client host name when log_hostname is on, otherwise client address (client_hostname, client_addr); local means a Unix socket",
        ["State"] = "Current state of the backend (state)",
        ["Query age"] = "Time the current query has been running; only for active sessions (server time minus query_start)",
        ["Transaction age"] = "Time the current transaction has been open (server time minus xact_start)",
        ["Wait"] = "What the backend is waiting for, if anything (wait_event_type: wait_event)",
        ["Blocked by PID"] = "Sessions holding the lock this one waits for (pg_blocking_pids)",
        ["Head blocker"] = "Blocks at least one lock waiter on the server and is not waiting on a lock itself (pg_blocking_pids over pg_locks)",
    };
    /// <summary>Header tooltips of the other panels, keyed by header; Database, PID, User and Wait describe the same source in every panel.</summary>
    internal static IReadOnlyDictionary<string, string> PanelColumnDescriptions { get; } = new Dictionary<string, string>
    {
        ["Wait Category"] = "PostgreSQL wait_event_type of the waiting backends",
        ["Wait Time (ms/sec)"] = "Backends waiting in this category at the latest refresh × 1000: each counts as waiting the whole second (sampled estimate)",
        ["Recent Wait Time (ms/sec)"] = $"Same estimate averaged over the last {ActivityMetricsHistory.RecentReadings} refreshes",
        ["Average Waiter Count"] = "Mean number of backends waiting in this category over all refreshes since the monitor opened",
        ["Cumulative Wait Time (sec)"] = "Waiters × time between refreshes, summed since the monitor opened (sampled estimate)",
        ["Database"] = "Database name (pg_stat_database.datname / pg_stat_statements dbid / pg_stat_activity.datname)",
        ["MB/sec Read"] = "Blocks read from outside shared buffers × block_size per second (pg_stat_database.blks_read); the OS cache may still serve them",
        ["Temp MB/sec Written"] = "Temporary file bytes written per second by queries (pg_stat_database.temp_bytes)",
        ["Average Read Time (ms)"] = "Read time per block read in the interval (blk_read_time / blks_read); needs track_io_timing",
        ["Buffer Hit %"] = "Share of block requests found in shared buffers in the interval (blks_hit / (blks_hit + blks_read))",
        ["Query"] = "Statement text (normalized by pg_stat_statements, or the current query of the backend)",
        ["Executions/min"] = "Calls in the interval per minute (pg_stat_statements.calls)",
        ["Execution (ms/sec)"] = "Execution time spent per second of interval (total_exec_time); PostgreSQL does not report CPU time",
        ["Physical Reads/sec"] = "Shared blocks read from outside shared buffers per second (shared_blks_read)",
        ["Logical Reads/sec"] = "Shared block requests per second (shared_blks_hit + shared_blks_read)",
        ["Average Duration (ms)"] = "Mean execution time of the calls in the interval",
        ["PID"] = "Backend process ID (pg_stat_activity.pid)",
        ["User"] = "Role that logged in (usename)",
        ["Elapsed"] = "Time the current query has been running (server time minus query_start)",
        ["Wait"] = "What the backend is waiting for, if anything (wait_event_type: wait_event)",
    };
    // AvalonDock raises Loaded each time the tab is shown again; only the first one reads the server.
    private async void OnLoaded(object sender, RoutedEventArgs e) { if (loaded) return; loaded = true; await ReloadAsync(); }
    private async void Refresh(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void ApplyFilters(object sender, RoutedEventArgs e) => await ApplyFiltersAsync();
    private async void FirstPage(object sender, RoutedEventArgs e) => await FirstPageAsync();
    private async void PreviousPage(object sender, RoutedEventArgs e) => await PreviousPageAsync();
    private async void NextPage(object sender, RoutedEventArgs e) => await NextPageAsync();
    private async void PanelExpanded(object sender, RoutedEventArgs e) { if (e.OriginalSource == sender) await ReloadAsync(); }
    // A collapsed panel stops measuring; expanding it again starts a new interval instead of spanning the gap.
    private void RecentQueriesCollapsed(object sender, RoutedEventArgs e) { if (e.OriginalSource == sender) { statementsBaseline = null; recentRows.Clear(); RecentQueriesNote.Text = "Expand to measure pg_stat_statements between refreshes."; } }
    public Task FirstPageAsync() => LoadAsync(ActivityPage.First);
    public Task PreviousPageAsync() => lastSnapshot?.HasPrevious==true && rows.Count>0 ? LoadAsync(new(BeforePid:rows[0].Pid)) : Task.CompletedTask;
    public Task NextPageAsync() => lastSnapshot?.IsTruncated==true && rows.Count>0 ? LoadAsync(new(AfterPid:rows[^1].Pid)) : Task.CompletedTask;
    private void AutoRefreshChanged(object sender, RoutedEventArgs e) => ConfigureAutoRefresh();
    private void AutoRefreshIntervalChanged(object sender, SelectionChangedEventArgs e) => ConfigureAutoRefresh();
    // Overview menu of SSMS 22.11 (M1026): it drives the same Auto-refresh box and interval as the toolbar, so there is one state.
    // Choosing an interval keeps a paused monitor paused.
    private void PauseAutoRefresh(object sender, RoutedEventArgs e) => AutoRefreshCheck.IsChecked = false;
    private void ResumeAutoRefresh(object sender, RoutedEventArgs e) => AutoRefreshCheck.IsChecked = true;
    private void OverviewMenuOpening(object sender, ContextMenuEventArgs e) => UpdateOverviewMenu();
    internal void UpdateOverviewMenu()
    {
        foreach (var item in RefreshIntervalMenu.Items.OfType<MenuItem>()) item.IsChecked = ReferenceEquals(item.Tag, AutoRefreshIntervalSelector.SelectedItem);
        PauseMenu.IsEnabled = AutoRefreshCheck.IsChecked == true; ResumeMenu.IsEnabled = !PauseMenu.IsEnabled; RefreshMenu.IsEnabled = !busy;
    }
    internal static string IntervalLabel(int seconds) => seconds switch
    {
        < 60 => seconds == 1 ? "1 second" : $"{seconds} seconds",
        < 3600 => seconds == 60 ? "1 minute" : $"{seconds / 60} minutes",
        _ => seconds == 3600 ? "1 hour" : $"{seconds / 3600} hours",
    };
    private void ConfigureAutoRefresh()
    {
        if (AutoRefreshIntervalSelector?.SelectedItem is ComboBoxItem { Tag: string seconds } && int.TryParse(seconds, out int interval))
            autoRefreshTimer.Interval = TimeSpan.FromSeconds(interval);
        if (AutoRefreshCheck?.IsChecked == true && !shutDown) autoRefreshTimer.Start(); else autoRefreshTimer.Stop();
        // SSMS titles the panel "Overview (paused)" while it does not refresh by itself (M1030).
        if (OverviewPanel is not null) OverviewPanel.Header = AutoRefreshCheck?.IsChecked == true ? "Overview" : "Overview (paused)";
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
    public Task ReloadAsync() => LoadAsync(page);
    // A request made while a refresh runs joins it instead of starting another one, so callers resume once current data is shown.
    private Task running = Task.CompletedTask;
    private Task LoadAsync(ActivityPage requested)
    {
        if (shutDown) return Task.CompletedTask;
        if (busy) return running;
        return running = LoadCoreAsync(requested);
    }
    private async Task LoadCoreAsync(ActivityPage requested)
    {
        try
        {
            refreshCancellation=new CancellationTokenSource();
            var token=refreshCancellation.Token;
            SetBusy(true);
            try
            {
                var filter = ReadFilter(); Status.Text = "Loading filtered sessions…";
                if(filter!=appliedFilter)requested=ActivityPage.First;
                var snapshot = await service.ReadSnapshotAsync(filter,requested,maximumRows,token);token.ThrowIfCancellationRequested();
                bool emptied=snapshot.Rows.Count==0 && requested!=ActivityPage.First;
                if(emptied){snapshot=await service.ReadSnapshotAsync(filter,ActivityPage.First,maximumRows,token);token.ThrowIfCancellationRequested();}
                Merge(snapshot.Rows);
                SnapshotVersion++;
                appliedFilter=filter;lastSnapshot=snapshot;
                page=snapshot.HasPrevious && rows.Count>0?new(AfterPid:rows[0].Pid-1):ActivityPage.First;
                lastRefresh=DateTimeOffset.Now;
                int first=snapshot.Preceding+1,last=snapshot.Preceding+rows.Count;
                PageInfo.Text=rows.Count==0||(!snapshot.IsTruncated && !snapshot.HasPrevious)?"":$"Sessions {first}–{last} of {snapshot.TotalMatching}";
                Status.Text = (emptied?"No sessions remain on that page; showing the first page. ":"")+(!snapshot.IsTruncated && !snapshot.HasPrevious
                    ?$"{rows.Count} session(s) loaded; complete filtered snapshot. Refreshed locally at {lastRefresh:HH:mm:ss}."
                    :$"Sessions {first}–{last} of {snapshot.TotalMatching} matching filters"+(snapshot.IsTruncated?"; more sessions match after this page (Next Page).":".")
                      +$" Pages follow live PID order, so sessions can start or end between pages; sorting applies within the page. Refreshed locally at {lastRefresh:HH:mm:ss}.");
            }
            catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or ArgumentException) { Status.Text = Describe(ex)+" "+(lastRefresh is null?"No successful snapshot loaded.":$"Previous snapshot retained (local refresh {lastRefresh:HH:mm:ss}); proposed filters were not applied."); }
            // Each panel reads and fails on its own, so a hidden or missing statistic never blocks the session list.
            await RefreshPanelsAsync(token);
        }
        catch(OperationCanceledException) when(refreshCancellation?.IsCancellationRequested==true){Status.Text="Refresh canceled. Previous snapshot retained; auto-refresh paused.";}
        finally { refreshCancellation?.Dispose();refreshCancellation=null;SetBusy(false); }
    }
    private async Task RefreshPanelsAsync(CancellationToken token)
    {
        try
        {
            var sample = await metrics.ReadSampleAsync(token); token.ThrowIfCancellationRequested();
            // A server clock that moved back cannot give rates against the old readings; start a new history.
            if (history.Points.Count > 0 && sample.ObservedAt < history.Points[^1].ObservedAt) history = new();
            history.Add(sample); MetricsVersion++;
            Replace(waitRows, history.ResourceWaits); Replace(fileRows, history.DataFileIo);
            ShowCharts(sample);
            OverviewNote.Text = $"{history.Points.Count} reading(s), one per refresh; rates compare the last two. Database I/O source: {sample.IoSource}.";
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException) { OverviewNote.Text = "Server metrics not read: " + Describe(ex) + " Previous readings retained."; }
        if (ActiveQueriesPanel.IsExpanded)
        {
            try
            {
                var active = await metrics.ReadActiveQueriesAsync(token); token.ThrowIfCancellationRequested();
                Replace(activeRows, active);
                ActiveQueriesNote.Text = $"{active.Count} running quer{(active.Count == 1 ? "y" : "ies")}, oldest first (at most {ActiveExpensiveQuery.MaximumRows}); the monitor's own reads are left out.";
            }
            catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException) { ActiveQueriesNote.Text = "Running queries not read: " + Describe(ex); }
        }
        if (RecentQueriesPanel.IsExpanded) await RefreshRecentQueriesAsync(token);
    }
    private async Task RefreshRecentQueriesAsync(CancellationToken token)
    {
        try
        {
            if (statementsBaseline is { } baseline)
            {
                var snapshot = await statements.ReadSnapshotAsync(new QueryStatisticsFilter(), baseline, RecentExpensiveQuery.MaximumRows, token); token.ThrowIfCancellationRequested();
                Replace(recentRows, RecentExpensiveQuery.FromWindow(snapshot));
                RecentQueriesNote.Text = $"Statements of database \"{snapshot.Availability.Database}\" by execution time between refreshes at {baseline.TakenAt.ToLocalTime():HH:mm:ss} and {snapshot.ObservedAt.ToLocalTime():HH:mm:ss} (server times, local zone); at most {RecentExpensiveQuery.MaximumRows}."
                    + (snapshot.ResetSinceWindowStart ? " pg_stat_statements was reset in the interval." : "") + (snapshot.EvictedSinceWindowStart > 0 ? " Some entries were evicted in the interval; counts can be low." : "")
                    + (snapshot.Availability.HasEntryTimestamps ? "" : $" pg_stat_statements {snapshot.Availability.InstalledVersion} has no stats_since, so a statement tracked again during the interval can count low.");
            }
            bool first = statementsBaseline is null;
            statementsBaseline = await statements.CaptureBaselineAsync(token); token.ThrowIfCancellationRequested();
            if (first) RecentQueriesNote.Text = $"Measuring from {statementsBaseline.TakenAt.ToLocalTime():HH:mm:ss}; statements appear after the next refresh.";
        }
        catch (QueryStatisticsUnavailableException ex) { statementsBaseline = null; recentRows.Clear(); RecentQueriesNote.Text = ex.Message; }
        catch (QueryStatisticsBaselineException ex) { statementsBaseline = null; recentRows.Clear(); RecentQueriesNote.Text = ex.Message + " Measuring again from the next refresh."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException) { statementsBaseline = null; RecentQueriesNote.Text = "pg_stat_statements not read: " + Describe(ex); }
    }
    private void ShowCharts(ActivityMetricsSample? sample = null)
    {
        var points = history.Points; var last = points.Count > 0 ? points[^1] : null;
        static string Rate(double? value, string format) => value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "—";
        ActiveSessionsTitle.Text = $"Active Sessions ({(last is null ? "—" : last.ActiveSessions.ToString(CultureInfo.InvariantCulture))})";
        WaitingSessionsTitle.Text = $"Waiting Sessions ({(last is null ? "—" : last.WaitingSessions.ToString(CultureInfo.InvariantCulture))})";
        DatabaseIoTitle.Text = $"Database I/O ({Rate(last?.IoMegabytesPerSecond, "N2")} MB/sec)";
        TransactionsTitle.Text = $"Transactions/sec ({Rate(last?.TransactionsPerSecond, "N1")})";
        if (sample is not null)
            DatabaseIoChart.ToolTip = sample.IoSource == "pg_stat_io" ? "Data file reads, writes and extends of all backends per second (pg_stat_io)" : "Blocks read by all databases per second (pg_stat_database; this server has no pg_stat_io, so writes are not counted)";
        ActiveSessionsChart.SetValues(points.Select(p => (double?)p.ActiveSessions).ToArray());
        WaitingSessionsChart.SetValues(points.Select(p => (double?)p.WaitingSessions).ToArray());
        DatabaseIoChart.SetValues(points.Select(p => p.IoMegabytesPerSecond).ToArray());
        TransactionsChart.SetValues(points.Select(p => p.TransactionsPerSecond).ToArray());
        foreach (var (title, chart) in new[] { (ActiveSessionsTitle, ActiveSessionsChart), (WaitingSessionsTitle, WaitingSessionsChart), (DatabaseIoTitle, DatabaseIoChart), (TransactionsTitle, TransactionsChart) })
            System.Windows.Automation.AutomationProperties.SetName(chart, $"{title.Text}, last {chart.Values.Count} reading(s), scale 0 to {chart.Scale.ToString("G6", CultureInfo.InvariantCulture)}");
    }
    // Clear and refill keep the collection, so the grid's sort survives a refresh.
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) { target.Clear(); foreach (var item in items) target.Add(item); }
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
    private static bool SameDisplay(ActivitySession a,ActivitySession b)=>a.Database==b.Database && a.User==b.User && a.Application==b.Application && a.Host==b.Host && a.IsHeadBlocker==b.IsHeadBlocker
        && a.State==b.State && a.Wait==b.Wait && a.Query==b.Query && a.BlockingPids.SequenceEqual(b.BlockingPids)
        && a.QueryDuration==b.QueryDuration && a.TransactionDuration==b.TransactionDuration;
    private void CancelRefresh(object sender,RoutedEventArgs e)
    {
        if(refreshCancellation is null)return;
        AutoRefreshCheck.IsChecked=false;refreshCancellation.Cancel();CancelRefreshButton.IsEnabled=false;Status.Text="Canceling refresh… Previous snapshot retained.";
    }
    public Task ApplyFiltersAsync() => LoadAsync(ActivityPage.First);
    private void SelectionChanged(object sender, RoutedEventArgs e) { if (CancelButton is not null) UpdateActions(); }
    private void UpdateActions() => CancelButton.IsEnabled = TerminateButton.IsEnabled = !busy && Sessions.SelectedItem is ActivitySession;
    private void SetBusy(bool value) { busy = value; CancelRefreshButton.Visibility=refreshCancellation is null?Visibility.Collapsed:Visibility.Visible;CancelRefreshButton.IsEnabled=refreshCancellation?.IsCancellationRequested==false; RefreshButton.IsEnabled = ApplyFiltersButton.IsEnabled = !value; FirstPageButton.IsEnabled = PreviousPageButton.IsEnabled = !value && lastSnapshot?.HasPrevious==true; NextPageButton.IsEnabled = !value && lastSnapshot?.IsTruncated==true; FilterFields.IsEnabled = !value; UpdateActions(); }
    private async void CancelQuery(object sender, RoutedEventArgs e) => await SignalAsync(false);
    private async void Terminate(object sender, RoutedEventArgs e) => await SignalAsync(true);
    internal static bool ConfirmTerminate(Window? owner, ActivitySession session)
    {
        string text = $"Terminate PID {session.Pid}?\nDatabase: {session.Database}\nUser: {session.User}\nApplication: {session.Application}\n\nThe connection will close and its open transaction will roll back.";
        var answer = owner is null ? MessageBox.Show(text, "Terminate Session", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, text, "Terminate Session", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }
    // Right-click selects the row first, so the menu always acts on the row under the pointer.
    private void SelectRowUnderPointer(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RowUnderPointer(e) is { Item: ActivitySession session }) Sessions.SelectedItem = session;
    }
    private static DataGridRow? RowUnderPointer(RoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is DataGridRow row) return row;
        return null;
    }
    private void SessionMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Sessions.SelectedItem is not ActivitySession) { e.Handled = true; return; }
        DetailsMenu.IsEnabled = true; CancelQueryMenu.IsEnabled = TerminateMenu.IsEnabled = !busy;
    }
    private void SessionDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (RowUnderPointer(e) is not null) ShowDetails(sender, e); }
    private async void ShowDetails(object sender, RoutedEventArgs e)
    {
        if (CreateDetails() is not { } details) return;
        details.ShowDialog();
        if (details.Terminated) await ReloadAsync();
    }
    /// <summary>Details of the selected session, modal like the SSMS Session Details dialog; null without a selection.</summary>
    internal SessionDetailsWindow? CreateDetails() => Sessions.SelectedItem is ActivitySession session ? new SessionDetailsWindow(service, session) { Owner = Window.GetWindow(this) } : null;
    // Like the Processes grid, right-click selects the row first, so the menu acts on the query under the pointer.
    private void SelectActiveQueryUnderPointer(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RowUnderPointer(e) is { Item: ActiveExpensiveQuery query }) ActiveQueries.SelectedItem = query;
    }
    private void ActiveQueryMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ActiveQueries.SelectedItem is not ActiveExpensiveQuery query) { e.Handled = true; return; }
        bool readable = query.Database.Length > 0 && query.Query.Length > 0 && query.Query != "<restricted>";
        EditQueryTextMenu.IsEnabled = readable && OpenQueryText is not null; ShowPlanMenu.IsEnabled = readable && OpenQueryPlan is not null;
    }
    private async void EditQueryText(object sender, RoutedEventArgs e) => await OpenSelectedQueryAsync(false);
    private async void ShowQueryPlan(object sender, RoutedEventArgs e) => await OpenSelectedQueryAsync(true);
    /// <summary>Opens the selected running query in a new tab connected to its database, as SSMS Edit Query Text / Show Execution Plan do; false without a usable selection.</summary>
    internal async Task<bool> OpenSelectedQueryAsync(bool plan)
    {
        if (ActiveQueries.SelectedItem is not ActiveExpensiveQuery { Database.Length: > 0, Query.Length: > 0 } query || query.Query == "<restricted>") return false;
        if ((plan ? OpenQueryPlan : OpenQueryText) is not { } open) return false;
        string target = new NpgsqlConnectionStringBuilder(connection) { Database = query.Database }.ConnectionString;
        await open(target, query.Query);
        return true;
    }
    private async Task SignalAsync(bool terminate)
    {
        if (busy || Sessions.SelectedItem is not ActivitySession session) return;
        if (terminate && !ConfirmTerminate(Window.GetWindow(this), session)) return;
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
    /// <summary>False while an operation runs, with the reason in the status line; the tab then stays open.</summary>
    public bool RequestClose()
    {
        if (!busy) return true;
        Status.Text = refreshCancellation is null ? "Wait for the current operation before closing." : "Cancel Refresh before closing this Activity Monitor.";
        return false;
    }
    /// <summary>Stops the timer and any running refresh for good; used when the tab or the main window closes.</summary>
    public void Shutdown() { shutDown = true; autoRefreshTimer.Stop(); refreshCancellation?.Cancel(); }
}
