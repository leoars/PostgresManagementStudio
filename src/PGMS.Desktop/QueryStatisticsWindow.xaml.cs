using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Read-only view over pg_stat_statements for the connected database. Mirrors Activity Monitor: explicit refresh, cancelable read, retained snapshot on failure.</summary>
public partial class QueryStatisticsWindow : Window
{
    private readonly QueryStatisticsService service;
    private readonly int maximumRows;
    private readonly QueryStatisticsPreferenceStore preferences = new(QueryStatisticsPreferenceStore.DefaultPath);
    private readonly string preferenceTarget;
    private QueryStatisticsPreference? persisted;
    private string? preferenceNote;
    private readonly QueryStatisticsBaselineStore baselineStore = new(QueryStatisticsBaselineStore.DefaultDirectory);
    private QueryStatisticsBaseline? savedBaseline;
    // Sliding windows: counters sampled in the background while the window is open, newest last. Never written to disk.
    private readonly List<(DateTimeOffset At, QueryStatisticsBaseline Counters)> samples = [];
    private readonly System.Windows.Threading.DispatcherTimer samplingTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly CancellationTokenSource samplingCancellation = new();
    private Task<QueryStatisticsBaseline>? sampling;
    private DateTimeOffset? samplingSince;
    private string? samplingError;
    private readonly System.Windows.Threading.DispatcherTimer autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool busy;
    private DateTimeOffset? lastRefresh;
    private CancellationTokenSource? refreshCancellation;
    private readonly System.Collections.ObjectModel.ObservableCollection<QueryStatisticsEntry> rows = [];
    /// <summary>Incremented after each successfully applied snapshot.</summary>
    public int SnapshotVersion { get; private set; }
    /// <summary>Last detection result, null until the first refresh completes or fails with a detection answer.</summary>
    public QueryStatisticsAvailability? Availability { get; private set; }
    public bool IsAutoRefreshRunning => autoRefreshTimer.IsEnabled;
    /// <summary>Timer interval; tests shorten it to observe a real tick without waiting the smallest offered choice.</summary>
    internal TimeSpan AutoRefreshInterval { get => autoRefreshTimer.Interval; set => autoRefreshTimer.Interval = value; }
    private readonly string connection;
    private readonly Func<string, string, Task>? showPlan;
    public QueryStatisticsWindow(string connection) : this(connection, QueryStatisticsService.DefaultMaximumRows) { }
    /// <summary>showPlan receives a connection string for the statement's database and its SQL, and opens the generic plan.</summary>
    public QueryStatisticsWindow(string connection, Func<string, string, Task>? showPlan) : this(connection, QueryStatisticsService.DefaultMaximumRows, showPlan) { }
    internal QueryStatisticsWindow(string connection, int maximumRows, Func<string, string, Task>? showPlan = null)
    {
        this.connection = connection; this.showPlan = showPlan;
        if (maximumRows is < 1 or > QueryStatisticsService.DefaultMaximumRows) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        this.maximumRows = maximumRows;
        InitializeComponent(); WindowWorkArea.Attach(this); service = new(connection); Statements.ItemsSource = rows;
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Query Statistics — {c.Host}:{c.Port}/{c.Database} ({c.Username})\npg_stat_statements is server-wide; visibility of other users' SQL and the reset actions depend on PostgreSQL permissions.";
        preferenceTarget = QueryStatisticsPreference.TargetOf(c.Host ?? "", c.Port, c.Database ?? "", c.Username ?? "");
        RestorePreference(); RestoreBaseline();
        if (WindowMode == QueryStatisticsWindowMode.SinceBaseline && savedBaseline is null)
        {
            SelectTag(WindowSelector, nameof(QueryStatisticsWindowMode.Cumulative));
            preferenceNote = (preferenceNote is null ? "" : preferenceNote + " ") + "No saved baseline for this target: showing cumulative counters.";
        }
        ShowWindowMode();
        autoRefreshTimer.Tick += async (_, _) => await ReloadAsync();
        samplingTimer.Tick += async (_, _) => await TakeSampleAsync();
    }
    // Time windows ---------------------------------------------------------------------------------------------------
    /// <summary>Window selected in the toolbar; cumulative unless the user chose otherwise.</summary>
    internal QueryStatisticsWindowMode WindowMode => WindowSelector?.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse(tag, out QueryStatisticsWindowMode mode) ? mode : QueryStatisticsWindowMode.Cumulative;
    private bool IsWindowed => WindowMode != QueryStatisticsWindowMode.Cumulative;
    private static TimeSpan? SlidingLength(QueryStatisticsWindowMode mode) => mode switch
    {
        QueryStatisticsWindowMode.Last5Minutes => TimeSpan.FromMinutes(5),
        QueryStatisticsWindowMode.Last15Minutes => TimeSpan.FromMinutes(15),
        QueryStatisticsWindowMode.Last60Minutes => TimeSpan.FromMinutes(60),
        _ => null
    };
    /// <summary>Saved baseline of this target, restored when the window opens; null when none was set.</summary>
    internal QueryStatisticsBaseline? SavedBaseline => savedBaseline;
    internal string BaselinePath => baselineStore.PathOf(preferenceTarget);
    /// <summary>Clock for sliding windows; tests move it forward instead of waiting minutes.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;
    internal TimeSpan SamplingInterval { get => samplingTimer.Interval; set => samplingTimer.Interval = value; }
    internal bool IsSampling => samplingTimer.IsEnabled;
    internal int SampleCount => samples.Count;
    /// <summary>Completes when the sample being read (if any) is stored.</summary>
    internal Task SamplingCompletion => sampling is { } task ? task.ContinueWith(_ => { }, TaskScheduler.Default) : Task.CompletedTask;
    private void RestoreBaseline()
    {
        try { savedBaseline = baselineStore.Find(preferenceTarget); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            preferenceNote = (preferenceNote is null ? "" : preferenceNote + " ") + "The saved baseline could not be read and was left untouched; set a new baseline. " + ex.Message;
        }
    }
    private void WindowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScopeNote is null) return;
        ShowWindowMode();
        if (SlidingLength(WindowMode) is not null && !samplingTimer.IsEnabled && IsLoaded) StartSampling();
    }
    private void ShowWindowMode()
    {
        ScopeNote.Text = IsWindowed
            ? "Activity within the window only, per statement/database/user: PGMS subtracts an earlier read from the current counters. Min/Max ms cannot be derived for a window. Times are server ms. Snapshot, not live."
            : "Cumulative since the last reset, per statement/database/user. Times are server ms; Hit % = shared hits / (hits + reads). Snapshot, not live.";
        UpdateWindowInfo();
    }
    private void UpdateWindowInfo()
    {
        if (WindowInfo is null) return;
        string baseline = savedBaseline is { } b ? $"Baseline {b.TakenAt:yyyy-MM-dd HH:mm:ss} UTC ({b.Entries.Count} statements)." : "No baseline set.";
        if (SlidingLength(WindowMode) is not { } length) { WindowInfo.Text = baseline; return; }
        if (samplingError is not null) { WindowInfo.Text = "Sampling failed: " + samplingError; return; }
        if (samplingSince is not { } since) { WindowInfo.Text = "Sampling not started."; return; }
        WindowInfo.Text = $"Sampling every {SamplingInterval.TotalSeconds:0} s since {since:HH:mm:ss} ({samples.Count} kept); this window is available from {since + length:HH:mm:ss}.";
    }
    private void StartSampling()
    {
        samplingSince ??= Clock();
        samplingTimer.Start();
        _ = TakeSampleAsync();
        UpdateWindowInfo();
    }
    // One sample at a time on its own connection, independent of refreshes. Samples older than the longest window are pruned; beyond
    // 16 minutes only one per 5-minute bucket is kept, so memory stays near 30 samples.
    private async Task TakeSampleAsync()
    {
        if (sampling is { IsCompleted: false } || samplingCancellation.IsCancellationRequested) return;
        var task = service.CaptureBaselineAsync(samplingCancellation.Token); sampling = task;
        try
        {
            var counters = await task;
            DateTimeOffset now = Clock();
            samples.Add((now, counters)); samplingError = null;
            samples.RemoveAll(s => now - s.At > TimeSpan.FromMinutes(65));
            var kept = samples.Where(s => now - s.At <= TimeSpan.FromMinutes(16))
                .Concat(samples.Where(s => now - s.At > TimeSpan.FromMinutes(16)).GroupBy(s => s.At.ToUnixTimeSeconds() / 300).Select(g => g.MaxBy(s => s.At)))
                .OrderBy(s => s.At).ToList();
            samples.Clear(); samples.AddRange(kept);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException) { samplingError = Describe(ex); }
        UpdateWindowInfo();
    }
    /// <summary>Counters the selected window subtracts: none for cumulative, the saved baseline, or the newest sample old enough for a sliding window.</summary>
    private QueryStatisticsBaseline? WindowBaseline()
    {
        var mode = WindowMode;
        if (mode == QueryStatisticsWindowMode.Cumulative) return null;
        if (mode == QueryStatisticsWindowMode.SinceBaseline) return savedBaseline ?? throw new ArgumentException("No baseline is set for this target. Use Set Baseline first.");
        var length = SlidingLength(mode)!.Value;
        if (!samplingTimer.IsEnabled) StartSampling();
        DateTimeOffset cutoff = Clock() - length;
        if (samples.LastOrDefault(s => s.At <= cutoff) is { Counters: { } counters }) return counters;
        throw new ArgumentException(samplingSince is { } since
            ? $"The last {length.TotalMinutes:0} minutes are not available yet: sampling started at {since:HH:mm:ss} and this window opens at {since + length:HH:mm:ss}."
            : "Sliding windows need sampling, which has not started.");
    }
    private async void SetBaseline(object sender, RoutedEventArgs e) => await SetBaselineAsync();
    /// <summary>Reads and saves the current counters as this target's baseline, then shows the activity since it.</summary>
    internal async Task SetBaselineAsync()
    {
        if (busy) return;
        try
        {
            refreshCancellation = new CancellationTokenSource();
            SetBusy(true); Status.Text = "Reading baseline counters…";
            var baseline = await service.CaptureBaselineAsync(refreshCancellation.Token); refreshCancellation.Token.ThrowIfCancellationRequested();
            savedBaseline = baseline;
            string note = $"Baseline set at {baseline.TakenAt:yyyy-MM-dd HH:mm:ss} UTC with {baseline.Entries.Count} statement(s).";
            try { baselineStore.Save(preferenceTarget, baseline); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException) { note += " It could not be saved for next time: " + ex.Message; }
            preferenceNote = (preferenceNote is null ? "" : preferenceNote + " ") + note;
        }
        catch (OperationCanceledException) when (refreshCancellation?.IsCancellationRequested == true) { Status.Text = "Baseline not changed."; return; }
        catch (QueryStatisticsUnavailableException ex) { ShowAvailability(ex.Availability); Status.Text = "Baseline not set: query statistics are unavailable for this connection."; return; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or QueryStatisticsBaselineException) { Status.Text = "Baseline not set. " + Describe(ex); return; }
        finally { refreshCancellation?.Dispose(); refreshCancellation = null; SetBusy(false); }
        SelectTag(WindowSelector, nameof(QueryStatisticsWindowMode.SinceBaseline)); ShowWindowMode();
        await ReloadAsync();
    }
    /// <summary>Target key of the persisted filters: host, port, database and user. Exposed for tests; never includes a password.</summary>
    internal string PreferenceTarget => preferenceTarget;
    // Restores the last filters used for this target before the first refresh. Auto-refresh stays off; only its interval is restored.
    private void RestorePreference()
    {
        try
        {
            if (preferences.Find(preferenceTarget) is not { } saved) return;
            DatabaseFilter.Text = saved.Database; UserFilter.Text = saved.User; QueryFilter.Text = saved.Query;
            MinCallsFilter.Text = saved.MinimumCalls == 0 ? "" : saved.MinimumCalls.ToString(CultureInfo.InvariantCulture);
            SelectTag(OrderSelector, saved.Order.ToString()); SelectTag(AutoRefreshIntervalSelector, saved.AutoRefreshSeconds.ToString(CultureInfo.InvariantCulture));
            SelectTag(ChartCountSelector, saved.ChartCount.ToString(CultureInfo.InvariantCulture)); ShowChartCheck.IsChecked = saved.ShowChart; ApplyChartVisibility(); SelectTag(WindowSelector, saved.Window.ToString());
            persisted = saved; preferenceNote = "Filters restored from the last use of this target.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            preferenceNote = "Saved filters could not be read; defaults used. " + ex.Message;
        }
    }
    private static void SelectTag(ComboBox selector, string tag)
    {
        foreach (var item in selector.Items.OfType<ComboBoxItem>()) if (item.Tag is string current && current == tag) { selector.SelectedItem = item; return; }
    }
    // Writes the validated filters when they differ from the last persisted ones. Failure never blocks the refresh; it is reported in the status.
    private void Persist(QueryStatisticsFilter filter)
    {
        int seconds = AutoRefreshIntervalSelector.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int parsed) ? parsed : 5;
        var next = new QueryStatisticsPreference(preferenceTarget, filter.Database, filter.User, filter.Query, filter.MinimumCalls, filter.Order, seconds, DateTimeOffset.Now) { ChartCount = ChartCount, ShowChart = IsChartShown, Window = WindowMode == QueryStatisticsWindowMode.SinceBaseline ? QueryStatisticsWindowMode.SinceBaseline : QueryStatisticsWindowMode.Cumulative };
        if (persisted is { } last && last with { LastUsed = next.LastUsed } == next) return;
        try { preferences.Save(next); persisted = next; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            preferenceNote = (preferenceNote is null ? "" : preferenceNote + " ") + "Filters could not be saved for next time. " + ex.Message;
        }
    }
    private void TryPersistCurrent() { try { Persist(ReadFilter().Validate()); } catch (ArgumentException) { } }
    private string TakeNote() { string note = preferenceNote is null ? "" : " " + preferenceNote; preferenceNote = null; return note; }
    private void AutoRefreshChanged(object sender, RoutedEventArgs e) => ConfigureAutoRefresh();
    private void AutoRefreshIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        ConfigureAutoRefresh();
        if (!IsLoaded || busy) return;
        TryPersistCurrent(); if (preferenceNote is not null) Status.Text = TakeNote().Trim();
    }
    private void ConfigureAutoRefresh()
    {
        if (AutoRefreshIntervalSelector?.SelectedItem is ComboBoxItem { Tag: string seconds } && int.TryParse(seconds, out int interval)) autoRefreshTimer.Interval = TimeSpan.FromSeconds(interval);
        if (AutoRefreshCheck?.IsChecked == true) autoRefreshTimer.Start(); else autoRefreshTimer.Stop();
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ResetSelectedButton is not null) UpdateActions(); SyncChartSelection(); }
    // Top-N chart: bars come from the loaded snapshot in server order for the applied metric; it never reads the server.
    private bool syncingSelection;
    private GridLength chartWidth = new(280);
    /// <summary>Bars currently drawn; empty while the chart is hidden or no snapshot is loaded.</summary>
    internal IReadOnlyList<QueryStatisticsChartBar> ChartItems => ChartBars.ItemsSource as IReadOnlyList<QueryStatisticsChartBar> ?? [];
    internal int ChartCount => ChartCountSelector?.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int count) ? count : 25;
    internal bool IsChartShown => ShowChartCheck?.IsChecked == true;
    private void ChartOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (ChartBars is null || ChartColumn is null) return;
        ApplyChartVisibility(); RebuildChart();
        if (!IsLoaded || busy) return;
        TryPersistCurrent(); if (preferenceNote is not null) Status.Text = TakeNote().Trim();
    }
    private void ApplyChartVisibility()
    {
        bool shown = IsChartShown;
        if (!shown && ChartColumn.Width.Value > 0) chartWidth = ChartColumn.Width;
        ChartPanel.Visibility = ChartSplitter.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ChartColumn.MinWidth = shown ? 180 : 0; ChartColumn.Width = shown ? chartWidth : new GridLength(0); ChartSplitterColumn.Width = new GridLength(shown ? 6 : 0);
    }
    private void RebuildChart()
    {
        if (ChartBars is null) return;
        if (!IsChartShown || appliedOrder is not { } order) { ChartBars.ItemsSource = Array.Empty<QueryStatisticsChartBar>(); ChartTitle.Text = "Top statements: no snapshot loaded."; return; }
        var bars = QueryStatisticsChart.Build(rows, order, ChartCount);
        ChartBars.ItemsSource = bars;
        ChartTitle.Text = bars.Count == 0 ? $"Top statements by {OrderLabel(order)}: none loaded." : $"Top {bars.Count} by {OrderLabel(order)}";
        SyncChartSelection();
    }
    private void SyncChartSelection()
    {
        if (ChartBars is null || syncingSelection) return;
        syncingSelection = true;
        try { ChartBars.SelectedItem = ChartItems.FirstOrDefault(b => ReferenceEquals(b.Entry, Statements.SelectedItem)); if (ChartBars.SelectedItem is { } bar) ChartBars.ScrollIntoView(bar); }
        finally { syncingSelection = false; }
    }
    private void ChartSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSelection || ChartBars.SelectedItem is not QueryStatisticsChartBar bar) return;
        syncingSelection = true;
        try { Statements.SelectedItem = bar.Entry; Statements.ScrollIntoView(bar.Entry); }
        finally { syncingSelection = false; }
        UpdateActions();
    }
    // Headers backed by a server ordering reload in that order (descending, like the Order by list), so the
    // row limit keeps the top statements. Other headers sort only the rows already loaded.
    private static readonly Dictionary<string, QueryStatisticsOrder> ServerOrders = new()
    {
        ["TotalExecMs"] = QueryStatisticsOrder.TotalExecTime, ["MeanExecMs"] = QueryStatisticsOrder.MeanExecTime,
        ["MaxExecMs"] = QueryStatisticsOrder.MaxExecTime, ["Calls"] = QueryStatisticsOrder.Calls, ["Rows"] = QueryStatisticsOrder.Rows,
        ["StddevExecMs"] = QueryStatisticsOrder.StddevExecTime, ["SharedBlocks"] = QueryStatisticsOrder.SharedBlocks, ["SharedBlocksRead"] = QueryStatisticsOrder.SharedBlocksRead,
        ["TempBlocks"] = QueryStatisticsOrder.TempBlocks, ["IoTimeMs"] = QueryStatisticsOrder.IoTime, ["WalBytes"] = QueryStatisticsOrder.WalBytes
    };
    private QueryStatisticsOrder? appliedOrder;
    private bool clearLocalSort;
    /// <summary>Order of the last applied snapshot; null before the first successful refresh.</summary>
    internal QueryStatisticsOrder? AppliedOrder => appliedOrder;
    /// <summary>True while a header sort reorders only the loaded rows.</summary>
    internal bool IsLocallySorted => Statements.Items.SortDescriptions.Count > 0;
    private async void StatementsSorting(object sender, DataGridSortingEventArgs e)
    {
        if (!ServerOrders.TryGetValue(e.Column.SortMemberPath, out var order))
        {
            if (busy) { e.Handled = true; Status.Text = "Wait for the current refresh before sorting."; return; }
            string header = e.Column.Header as string ?? "column";
            _ = Dispatcher.BeginInvoke(() => Status.Text = $"Sorted locally by {header} within the {rows.Count} loaded statement(s). The server order ({OrderLabel(appliedOrder)}) still decides which statements are loaded.");
            return;
        }
        e.Handled = true;
        if (busy) { Status.Text = "Wait for the current refresh before sorting."; return; }
        if (order == QueryStatisticsOrder.MaxExecTime && IsWindowed) { Status.Text = "Max execution time cannot be derived for a time window; the current order is kept."; return; }
        SelectTag(OrderSelector, order.ToString()); clearLocalSort = true;
        await ReloadAsync();
    }
    private string OrderLabel(QueryStatisticsOrder? order) =>
        OrderSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == order?.ToString())?.Content as string ?? "not loaded";
    // Clears a local sort when the server order changed or a server header was clicked; otherwise a refresh keeps it.
    private void ShowServerOrder(QueryStatisticsOrder order)
    {
        if (clearLocalSort || (appliedOrder is { } previous && previous != order)) Statements.Items.SortDescriptions.Clear();
        clearLocalSort = false; appliedOrder = order;
        bool local = IsLocallySorted;
        foreach (var column in Statements.Columns)
            column.SortDirection = local
                ? Statements.Items.SortDescriptions.FirstOrDefault(s => s.PropertyName == column.SortMemberPath) is { PropertyName: not null } sort ? sort.Direction : null
                : ServerOrders.TryGetValue(column.SortMemberPath, out var mapped) && mapped == order ? ListSortDirection.Descending : null;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void Refresh(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void ApplyFilters(object sender, RoutedEventArgs e) => await ReloadAsync();
    public async Task ApplyFiltersAsync() => await ReloadAsync();
    private QueryStatisticsFilter ReadFilter()
    {
        long minimumCalls = 0; string text = MinCallsFilter.Text.Trim();
        if (text.Length > 0 && (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out minimumCalls) || minimumCalls < 0)) throw new ArgumentException("Minimum calls must be a non-negative integer.");
        var order = (OrderSelector.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse(tag, out QueryStatisticsOrder parsed) ? parsed : QueryStatisticsOrder.TotalExecTime;
        return new(DatabaseFilter.Text, UserFilter.Text, QueryFilter.Text, minimumCalls, order);
    }
    public async Task ReloadAsync()
    {
        if (busy) return;
        try
        {
            refreshCancellation = new CancellationTokenSource();
            SetBusy(true);
            var filter = ReadFilter().Validate(); var baseline = WindowBaseline(); Persist(filter); Status.Text = "Loading query statistics…";
            var snapshot = await service.ReadSnapshotAsync(filter, baseline, maximumRows, refreshCancellation.Token); refreshCancellation.Token.ThrowIfCancellationRequested();
            ShowAvailability(snapshot.Availability);
            Merge(snapshot.Rows);
            ShowServerOrder(filter.Order);
            RebuildChart();
            SnapshotVersion++;
            lastRefresh = DateTimeOffset.Now;
            string restricted = snapshot.Availability.CanReadAllQueries ? "" : snapshot.WindowStart is null ? " Other users' SQL is hidden by PostgreSQL." : " Other users' statements have no visible ID and cannot be measured in a window.";
            if (snapshot.Availability.TrackIoTiming == false) restricted += " track_io_timing is off: I/O ms stays 0 for new executions.";
            string window = "";
            if (snapshot.WindowStart is { } start)
            {
                window = $" Window {start:yyyy-MM-dd HH:mm:ss} to {snapshot.ObservedAt:HH:mm:ss} UTC ({Span(snapshot.ObservedAt - start)}).";
                if (snapshot.UnusableEntries > 0) window += snapshot.Availability.HasEntryTimestamps
                    ? $" {snapshot.UnusableEntries} statement(s) existed before the window start without counters in the earlier read and were left out."
                    : $" {snapshot.UnusableEntries} statement(s) have fewer calls than in the earlier read (tracked again since) and were left out.";
                // Only windows depend on stats_since, so the 1.10 limit is stated here instead of in the availability banner.
                if (!snapshot.Availability.HasEntryTimestamps) window += $" pg_stat_statements {snapshot.Availability.InstalledVersion} has no stats_since: a statement removed and tracked again within the window can count low.";
                if (snapshot.EvictedSinceWindowStart > 0) window += $" pg_stat_statements evicted {snapshot.EvictedSinceWindowStart} entr(ies) since then, so some counts can be low.";
                if (snapshot.ResetSinceWindowStart) window += " Statistics were reset after the window start; entries are counted from their re-creation.";
            }
            Status.Text = (snapshot.IsTruncated
                ? $"First {rows.Count} statement(s) by the chosen order; more statements match. Narrow filters."
                : snapshot.WindowStart is null ? $"{rows.Count} statement(s) loaded; complete filtered snapshot." : $"{rows.Count} statement(s) ran in the window; complete filtered snapshot.")
                + window + $" Server time {snapshot.ObservedAt:yyyy-MM-dd HH:mm:ss} UTC; refreshed locally at {lastRefresh:HH:mm:ss}.{restricted}" + TakeNote();
        }
        catch (OperationCanceledException) when (refreshCancellation?.IsCancellationRequested == true) { Status.Text = "Refresh canceled. Previous snapshot retained."; }
        catch (QueryStatisticsUnavailableException ex)
        {
            ShowAvailability(ex.Availability);
            rows.Clear(); lastRefresh = null; RebuildChart();
            Status.Text = "Query statistics are unavailable for this connection. Nothing was installed or changed." + TakeNote();
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or ArgumentException or QueryStatisticsBaselineException)
        {
            Status.Text = Describe(ex) + " " + (lastRefresh is null ? "No successful snapshot loaded." : $"Previous snapshot retained (local refresh {lastRefresh:HH:mm:ss}); proposed filters were not applied.") + TakeNote();
        }
        finally { refreshCancellation?.Dispose(); refreshCancellation = null; clearLocalSort = false; SetBusy(false); }
    }
    private void ShowAvailability(QueryStatisticsAvailability availability)
    {
        Availability = availability;
        AvailabilityText.Text = availability.Describe();
        AvailabilityBanner.Visibility = availability.IsReadable && availability.CanReadAllQueries ?Visibility.Collapsed : Visibility.Visible;
    }
    // Keeps server order; rows are keyed by (queryid, database, user, toplevel) so a sort or selection survives refresh.
    // Statements hidden from the role have no queryid and therefore no stable identity: they are replaced on every refresh.
    private void Merge(IReadOnlyList<QueryStatisticsEntry> next)
    {
        static (long, string, string, bool)? Key(QueryStatisticsEntry e) => e.QueryId is { } id ? (id, e.Database, e.User, e.TopLevel) : null;
        var keys = next.Select(Key).Where(k => k is not null).ToHashSet();
        for (int i = rows.Count - 1; i >= 0; i--) if (Key(rows[i]) is not { } key || !keys.Contains(key)) rows.RemoveAt(i);
        for (int i = 0; i < next.Count; i++)
        {
            var item = next[i]; int current = -1;
            if (Key(item) is { } wanted) for (int j = i; j < rows.Count; j++) if (Key(rows[j]) == wanted) { current = j; break; }
            if (current < 0) { rows.Insert(i, item); continue; }
            if (current != i) rows.Move(current, i);
            if (rows[i] == item) continue;
            bool reselect = ReferenceEquals(Statements.SelectedItem, rows[i]);
            rows[i] = item;
            if (reselect) Statements.SelectedItem = item;
        }
    }
    private void CancelRefresh(object sender, RoutedEventArgs e)
    {
        if (refreshCancellation is null) return;
        AutoRefreshCheck.IsChecked = false; refreshCancellation.Cancel(); CancelRefreshButton.IsEnabled = false; Status.Text = "Canceling refresh… Previous snapshot retained; auto-refresh paused.";
    }
    private void SetBusy(bool value)
    {
        busy = value;
        CancelRefreshButton.Visibility = refreshCancellation is null ? Visibility.Collapsed : Visibility.Visible;
        CancelRefreshButton.IsEnabled = refreshCancellation?.IsCancellationRequested == false;
        RefreshButton.IsEnabled = ApplyFiltersButton.IsEnabled = !value; FilterFields.IsEnabled = !value;
        UpdateActions();
    }
    private void UpdateActions()
    {
        ResetButton.IsEnabled = !busy && Availability?.IsReadable == true;
        ResetSelectedButton.IsEnabled = !busy && Availability?.IsReadable == true && Statements.SelectedItem is QueryStatisticsEntry { CanResetIndividually: true };
        ShowPlanButton.IsEnabled = !busy && showPlan is not null && Statements.SelectedItem is QueryStatisticsEntry { CanShowPlan: true };
    }
    private async void ShowPlan(object sender, RoutedEventArgs e) => await ShowPlanAsync();
    /// <summary>Hands the selected statement to the owner, which opens it connected to the statement's database and loads its generic plan.</summary>
    internal async Task ShowPlanAsync()
    {
        if (busy || showPlan is null || Statements.SelectedItem is not QueryStatisticsEntry { CanShowPlan: true } selected) return;
        var target = new NpgsqlConnectionStringBuilder(connection);
        if (selected.Database.Length > 0) target.Database = selected.Database;
        string other = selected.User.Length > 0 && !string.Equals(selected.User, target.Username, StringComparison.Ordinal) ? $" Recorded for user \"{selected.User}\"; planned as \"{target.Username}\" with its own permissions and search_path." : $" Planned with this connection's search_path.";
        Status.Text = $"Opening the generic plan of query {selected.QueryId} in a new query tab for database \"{target.Database}\". Parameters stay unbound; nothing is executed." + other;
        await showPlan(target.ConnectionString, selected.Query.TrimEnd());
    }
    private async void ResetStatistics(object sender, RoutedEventArgs e) => await ResetAsync(confirmed: null);
    private async void ResetSelected(object sender, RoutedEventArgs e) => await ResetSelectedAsync(confirmed: null);
    /// <summary>Resets only the selected statement entry. Tests pass an explicit answer; the UI asks with No as the default.</summary>
    internal async Task ResetSelectedAsync(bool? confirmed)
    {
        if (busy || Availability?.IsReadable != true || Statements.SelectedItem is not QueryStatisticsEntry { CanResetIndividually: true } selected) return;
        bool proceed = confirmed ?? MessageBox.Show(this, $"Discard the statistics of query {selected.QueryId} for database \"{selected.Database}\" and user \"{selected.User}\"?\n\nOther statements keep their counters. This cannot be undone and PostgreSQL must authorize the call.", "Reset Selected Statement", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!proceed) { Status.Text = "Selective reset not performed."; return; }
        SetBusy(true);
        try
        {
            var at = await service.ResetAsync(selected);
            rows.Remove(selected); SnapshotVersion++; RebuildChart();
            Status.Text = $"Selected statement reset confirmed by the server at {at:yyyy-MM-dd HH:mm:ss} UTC. Refresh to confirm the remaining entries.";
        }
        catch (QueryStatisticsUnavailableException ex) { ShowAvailability(ex.Availability); Status.Text = "Selective reset not performed: query statistics are unavailable."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException) { Status.Text = "Selective reset not confirmed. " + Describe(ex) + " Existing entries were kept."; }
        finally { SetBusy(false); }
    }
    /// <summary>Resets server statistics. Tests pass an explicit answer; the UI asks with No as the default.</summary>
    internal async Task ResetAsync(bool? confirmed)
    {
        if (busy || Availability?.IsReadable != true) return;
        bool proceed = confirmed ?? MessageBox.Show(this, "Discard all pg_stat_statements entries on this server?\n\nThis affects every database and user on the server and cannot be undone. PostgreSQL must authorize the call.", "Reset Statistics", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!proceed) { Status.Text = "Reset not performed."; return; }
        SetBusy(true);
        try
        {
            var at = await service.ResetAsync();
            rows.Clear(); SnapshotVersion++; RebuildChart();
            Status.Text = $"Statistics reset confirmed by the server at {at:yyyy-MM-dd HH:mm:ss} UTC. Refresh to load new entries.";
        }
        catch (QueryStatisticsUnavailableException ex) { ShowAvailability(ex.Availability); Status.Text = "Reset not performed: query statistics are unavailable."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException) { Status.Text = "Reset not confirmed. " + Describe(ex) + " Existing entries were kept."; }
        finally { SetBusy(false); }
    }
    private static string Span(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:00}m" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s" : $"{Math.Max(0, span.Seconds)}s";
    private static string Describe(Exception ex) => ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; Status.Text = refreshCancellation is null ? "Wait for the current operation before closing." : "Cancel Refresh before closing this window."; } else { autoRefreshTimer.Stop(); samplingTimer.Stop(); samplingCancellation.Cancel(); TryPersistCurrent(); } }
}
