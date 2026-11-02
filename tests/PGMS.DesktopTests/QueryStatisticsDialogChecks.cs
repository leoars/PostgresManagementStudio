using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>Query Statistics window against the isolated cluster. Covers filters, ordering, truncation, retained snapshot, cancellation, reset and the unavailable state.</summary>
internal static class QueryStatisticsDialogChecks
{
    private static T Find<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static async Task Capture(QueryStatisticsWindow window, string name, bool small, Action<bool, string> check)
    {
        window.Width = small ? 850 : 1100; window.Height = small ? 540 : 680; await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); window.UpdateLayout(); var root = (FrameworkElement)window.Content;
        foreach (string field in new[] { "RefreshButton", "ResetSelectedButton", "ResetButton", "ApplyFiltersButton", "OrderSelector", "AutoRefreshIntervalSelector", "WindowSelector", "SetBaselineButton" })
        {
            var control = Find<FrameworkElement>(window, field); var point = control.TranslatePoint(new Point(), root);
            check(point.X >= 0 && point.X + control.ActualWidth <= root.ActualWidth + 0.1 && point.Y + control.ActualHeight <= root.ActualHeight + 0.1, "Statistics action fits " + name + "/" + field);
        }
        check(Find<DataGrid>(window, "Statements").ActualHeight >= 70, "Statistics grid retains a visible data row: " + name);
        var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) { var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight); dc.DrawRectangle(window.Background, null, bounds); dc.DrawRectangle(new VisualBrush(root), null, bounds); }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory("artifacts/review"); using var file = File.Create("artifacts/review/" + name + ".png"); encoder.Save(file);
    }
    // Show() raises Loaded, whose refresh is already running when the test regains control: wait for that first outcome.
    private static async Task WaitFirstRefresh(QueryStatisticsWindow window)
    {
        var status = Find<TextBox>(window, "Status"); var refresh = Find<Button>(window, "RefreshButton"); var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (!refresh.IsEnabled || (window.SnapshotVersion == 0 && !status.Text.Contains("unavailable") && !status.Text.Contains("No successful")))) await Task.Delay(25);
        if (!refresh.IsEnabled) throw new TimeoutException("Initial statistics refresh did not finish.");
    }
    // Raises the header OnClick (same path as a mouse click, as in QueryResultCopyChecks) and waits for any reload it starts.
    private static async Task ClickHeader(QueryStatisticsWindow window, DataGrid grid, string header)
    {
        grid.UpdateLayout();
        var element = Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).First(h => h.Column?.Header as string == header);
        PressHeader(element);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var refresh = Find<Button>(window, "RefreshButton"); var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !refresh.IsEnabled) await Task.Delay(25);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task ClickHeaderWhileBusy(DataGrid grid, string header)
    {
        grid.UpdateLayout();
        var element = Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).First(h => h.Column?.Header as string == header);
        PressHeader(element);
        await grid.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    private static void PressHeader(System.Windows.Controls.Primitives.DataGridColumnHeader element) =>
        typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(element, null);
    private static System.ComponentModel.ListSortDirection? Direction(DataGrid grid, string header) => grid.Columns.First(c => c.Header as string == header).SortDirection;
    private static QueryStatisticsEntry Entry(long id, double total, long calls, string query) =>
        new(id, "db", "user", true, calls, total, calls == 0 ? 0 : total / calls, 0, total, 0, calls, 0, 0, 0, 0, 0, query, null);
    // Chart model without a server: server order kept, top-N cut, scaling, zero metrics, labels and accessible names.
    private static void ChartBuildChecks(Action<bool, string> check)
    {
        var entries = Enumerable.Range(1, 60).Select(i => Entry(i, 600 - i * 10, i, $"SELECT {i}\n  FROM   t")).ToList();
        var bars = QueryStatisticsChart.Build(entries, QueryStatisticsOrder.TotalExecTime, 25);
        check(bars.Count == 25 && bars.Select(b => b.Rank).SequenceEqual(Enumerable.Range(1, 25)) && bars.Select(b => b.Entry).SequenceEqual(entries.Take(25)), "Chart keeps the first N statements in the given server order");
        check(bars[0].Fraction == 1 && bars.Zip(bars.Skip(1)).All(p => p.First.Fraction >= p.Second.Fraction) && Math.Abs(bars[24].Fraction - 350.0 / 590) < 1e-9, "Bars scale to the largest value of the chart");
        check(bars[0].Caption == "#1  SELECT 1 FROM t" && bars[0].ValueLabel == entries[0].TotalExecLabel, "Bar caption collapses whitespace and the value reuses the grid label");
        check(bars[0].AccessibleName.StartsWith("#1 Total ms") && bars[0].AccessibleName.Contains("database db") && bars[0].Detail.Contains("Query ID 1"), "Bar exposes rank, metric, value, database and statement as its name");
        check(QueryStatisticsChart.Build(entries, QueryStatisticsOrder.Calls, 50).Count == 50 && QueryStatisticsChart.Build(entries.Take(3), QueryStatisticsOrder.Calls, 10).Count == 3, "Chart shows at most N bars and fewer when fewer statements are loaded");
        var calls = QueryStatisticsChart.Build(entries, QueryStatisticsOrder.Calls, 10);
        check(calls[0].Value == 1 && calls[9].Fraction == 1 && calls[0].ValueLabel == "1", "Calls chart keeps the server order even when values ascend, scaling to the largest shown");
        var zero = QueryStatisticsChart.Build(entries, QueryStatisticsOrder.WalBytes, 10);
        check(zero.All(b => b.Fraction == 0 && b.ValueLabel == "0"), "All-zero metric draws empty bars instead of dividing by zero");
        check(QueryStatisticsChart.Build([], QueryStatisticsOrder.TotalExecTime, 25).Count == 0, "Empty snapshot gives an empty chart");
        string emoji = new string('a', QueryStatisticsChart.CaptionLength - 2) + "\U0001F600\U0001F600";
        var cut = QueryStatisticsChart.Build([Entry(9, 1, 1, emoji)], QueryStatisticsOrder.Rows, 10)[0].Caption;
        check(cut.EndsWith("…") && !char.IsHighSurrogate(cut[^2]), "Long caption is shortened without splitting a surrogate pair");
        var hidden = QueryStatisticsChart.Build([new QueryStatisticsEntry(null, "db", "other", true, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, "<insufficient privilege>", null)], QueryStatisticsOrder.TotalExecTime, 10)[0];
        check(hidden.Detail.Contains("Query ID hidden"), "Statement hidden by PostgreSQL still gets a bar, labelled as hidden");
        foreach (var order in Enum.GetValues<QueryStatisticsOrder>()) check(QueryStatisticsChart.Measure(entries[0], order).Metric.Length > 0, "Chart measures every server ordering: " + order);
        bool rejected = false; try { QueryStatisticsChart.Build(entries, QueryStatisticsOrder.Calls, 0); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected, "Chart rejects a non-positive bar count");
        string legacy = Path.Combine(Path.GetDirectoryName(QueryStatisticsPreferenceStore.DefaultPath)!, "query-statistics-legacy-check.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
            File.WriteAllText(legacy, "{\"Version\":1,\"Preferences\":[{\"Target\":\"h:1/d/u\",\"Database\":\"\",\"User\":\"\",\"Query\":\"\",\"MinimumCalls\":0,\"Order\":2,\"AutoRefreshSeconds\":10,\"LastUsed\":\"2026-11-01T00:00:00+00:00\"}]}");
            check(new QueryStatisticsPreferenceStore(legacy).Find("h:1/d/u") is { ChartCount: 25, ShowChart: true, Order: QueryStatisticsOrder.Calls, Window: QueryStatisticsWindowMode.Cumulative }, "Preference file written before the chart reads with the default chart (shown, 25 bars)");
        }
        finally { if (File.Exists(legacy)) File.Delete(legacy); }
    }
    private static ListBoxItem ChartItem(ListBox chart, int index) { chart.UpdateLayout(); return (ListBoxItem)chart.ItemContainerGenerator.ContainerFromIndex(index); }
    // Chart against the loaded snapshot: no server read, server order, two-way selection, keyboard, names, hide/show and persistence.
    private static async Task ChartChecks(QueryStatisticsWindow window, DataGrid grid, Action<bool, string> check)
    {
        var order = Find<ComboBox>(window, "OrderSelector"); var chart = Find<ListBox>(window, "ChartBars"); var title = Find<TextBlock>(window, "ChartTitle");
        order.SelectedIndex = 0; await window.ApplyFiltersAsync(); await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var source = ((IEnumerable<QueryStatisticsEntry>)grid.ItemsSource).ToList();
        check(window.IsChartShown && window.ChartCount == 25 && window.ChartItems.Count == 3 && window.ChartItems.Select(b => b.Entry).SequenceEqual(source), "Chart shows the loaded statements in server order by default");
        check(window.ChartItems[0].Entry.Query.Contains("pg_sleep") && window.ChartItems[0].Fraction == 1 && title.Text.Contains("Total execution time"), "Chart leads with the slowest statement and names the metric: " + title.Text);
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(ChartItem(chart, 0));
        check(peer.GetName().StartsWith("#1 Total ms") && !peer.GetName().Contains("QueryStatisticsChartBar"), "First bar's UI Automation name is short and readable: " + peer.GetName());
        check(System.Windows.Automation.AutomationProperties.GetName(chart) == "Top statements chart" && System.Windows.Automation.AutomationProperties.GetName(Find<CheckBox>(window, "ShowChartCheck")).Length > 0 && System.Windows.Automation.AutomationProperties.GetName(Find<ComboBox>(window, "ChartCountSelector")).Length > 0, "Chart and its options have accessible names");
        int version = window.SnapshotVersion;
        chart.SelectedIndex = 1;
        check(ReferenceEquals(grid.SelectedItem, window.ChartItems[1].Entry) && window.SnapshotVersion == version, "Selecting a bar selects the same statement in the grid without reading the server");
        grid.SelectedItem = window.ChartItems[2].Entry;
        check(chart.SelectedIndex == 2, "Selecting a grid row selects its bar");
        var item = ChartItem(chart, 2); item.Focus();
        item.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(item), 0, System.Windows.Input.Key.Up) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        check(chart.SelectedIndex == 1 && ReferenceEquals(grid.SelectedItem, window.ChartItems[1].Entry), "Arrow keys move along the bars and keep the grid selection in step");
        order.SelectedIndex = 3; await window.ApplyFiltersAsync();
        check(window.ChartItems[0].Entry.Calls == 2 && window.ChartItems[0].ValueLabel == "2" && title.Text.Contains("Calls") && window.ChartItems.Select(b => b.Entry).SequenceEqual((IEnumerable<QueryStatisticsEntry>)grid.ItemsSource), "Chart follows a new server order");
        var selected = grid.SelectedItem; await ClickHeader(window, grid, "Database");
        check(window.IsLocallySorted && window.ChartItems[0].Entry.Calls == 2 && chart.SelectedItem is QueryStatisticsChartBar kept && ReferenceEquals(kept.Entry, selected), "Local header sort reorders the grid only; the chart keeps the server order and the selection");
        var store = new QueryStatisticsPreferenceStore(QueryStatisticsPreferenceStore.DefaultPath);
        Find<ComboBox>(window, "ChartCountSelector").SelectedIndex = 0;
        check(window.ChartCount == 10 && window.ChartItems.Count == 3 && store.Find(window.PreferenceTarget) is { ChartCount: 10, ShowChart: true }, "Changing the bar count redraws and persists it");
        window.UpdateLayout(); double before = grid.ActualWidth; Find<CheckBox>(window, "ShowChartCheck").IsChecked = false; window.UpdateLayout();
        check(Find<DockPanel>(window, "ChartPanel").Visibility == Visibility.Collapsed && window.ChartItems.Count == 0 && grid.ActualWidth > before + 150 && store.Find(window.PreferenceTarget) is { ShowChart: false }, "Hiding the chart gives its width back to the grid and persists the choice");
        Find<CheckBox>(window, "ShowChartCheck").IsChecked = true; Find<ComboBox>(window, "ChartCountSelector").SelectedIndex = 1; order.SelectedIndex = 0; await window.ApplyFiltersAsync(); window.UpdateLayout();
        check(window.ChartItems.Count == 3 && Math.Abs(grid.ActualWidth - before) < 1 && store.Find(window.PreferenceTarget) is { ChartCount: 25, ShowChart: true }, "Showing the chart again restores its width and bars");
        chart.SelectedIndex = 0;
        foreach (bool small in new[] { false, true })
        {
            await Capture(window, small ? "m1021-chart-small" : "m1021-chart-normal", small, check);
            var root = (FrameworkElement)window.Content; var first = ChartItem(chart, 0); var point = first.TranslatePoint(new Point(), root);
            check(chart.ActualWidth >= 175 && grid.ActualWidth >= 375 && point.X >= 0 && point.Y >= 0 && point.X + first.ActualWidth <= root.ActualWidth + 0.1 && first.ActualHeight >= 20, $"Chart and grid stay readable side by side ({(small ? "minimum" : "normal")} size): chart {chart.ActualWidth:0}, grid {grid.ActualWidth:0}");
        }
        window.Width = 1100; window.Height = 680;
    }
    // Preference store without a server: round trip, replacement per target, eviction, invalid content and absence of secrets.
    private static void StoreChecks(Action<bool, string> check)
    {
        string path = Path.Combine(Path.GetDirectoryName(QueryStatisticsPreferenceStore.DefaultPath)!, "query-statistics-store-check.json");
        var store = new QueryStatisticsPreferenceStore(path);
        try
        {
            var first = new QueryStatisticsPreference("h:5432/db/u", " dbé ", "", "SELECT 'ünïcode'", 3, QueryStatisticsOrder.Calls, 30, DateTimeOffset.Now);
            store.Save(first);
            var found = store.Find("h:5432/db/u");
            check(found is { Database: "dbé", Query: "SELECT 'ünïcode'", MinimumCalls: 3, Order: QueryStatisticsOrder.Calls, AutoRefreshSeconds: 30 }, "Preference store round-trips trimmed Unicode filters, order and interval");
            check(store.Find("h:5432/db/other") is null, "Preference store returns null for an unknown target");
            store.Save(first with { Order = QueryStatisticsOrder.WalBytes, LastUsed = DateTimeOffset.Now.AddSeconds(1) }); check(store.Find("h:5432/db/u") is { Order: QueryStatisticsOrder.WalBytes }, "Preference store round-trips a new metric ordering"); store.Save(first with { Query = "", Order = QueryStatisticsOrder.Rows, LastUsed = DateTimeOffset.Now.AddSeconds(2) });
            check(store.Load().Count == 1 && store.Find("h:5432/db/u") is { Query: "", Order: QueryStatisticsOrder.Rows }, "Saving the same target replaces its entry instead of duplicating it");
            for (int i = 0; i < QueryStatisticsPreferenceStore.MaximumTargets; i++) store.Save(new QueryStatisticsPreference("h:5432/db/t" + i, "", "", "", 0, QueryStatisticsOrder.TotalExecTime, 5, DateTimeOffset.Now.AddMinutes(i + 1)));
            check(store.Load().Count == QueryStatisticsPreferenceStore.MaximumTargets && store.Find("h:5432/db/u") is null && store.Find("h:5432/db/t0") is not null, "Store keeps only the most recently used targets and evicts the oldest");
            bool rejected = false; try { store.Save(new QueryStatisticsPreference("x", "", "", "", 0, QueryStatisticsOrder.Calls, 7, DateTimeOffset.Now)); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Unsupported auto-refresh interval is rejected before writing");
            rejected = false; try { store.Save(new QueryStatisticsPreference("x", "", "", "", 0, QueryStatisticsOrder.Calls, 5, DateTimeOffset.Now) { ChartCount = 7 }); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Unsupported chart size is rejected before writing");
            rejected = false; try { store.Save(new QueryStatisticsPreference("x", new string('a', 300), "", "", 0, QueryStatisticsOrder.Calls, 5, DateTimeOffset.Now)); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Filter longer than the service limit is rejected before writing");
            check(!File.ReadAllText(path).Contains("Password", StringComparison.OrdinalIgnoreCase), "Preference file never names a password");
            File.WriteAllText(path, "{\"Version\":2,\"Preferences\":[]}");
            rejected = false; try { store.Load(); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Unsupported preference file version is reported, not repaired");
            File.WriteAllText(path, "{not json");
            rejected = false; try { store.Find("h:5432/db/u"); } catch (System.Text.Json.JsonException) { rejected = true; }
            check(rejected, "Malformed preference file surfaces a JSON error");
        }
        finally { File.Delete(path); if (File.Exists(path + ".lock")) File.Delete(path + ".lock"); }
    }
    // Baseline store without a server: round trip, hashed file names, foreign or unsupported content refused, eviction, and preference window rules.
    private static void BaselineStoreChecks(Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetDirectoryName(QueryStatisticsPreferenceStore.DefaultPath)!, "baseline-store-check-" + Guid.NewGuid().ToString("N")[..8]);
        var store = new QueryStatisticsBaselineStore(dir);
        try
        {
            var at = DateTime.UtcNow; var entry = new QueryStatisticsCounters(10, 5, -42, true, at.AddMinutes(-1), 3, 1.5, 0.9, 3, 1, 2, 3, 4, 5, 0.25);
            store.Save("h:1/d/u", new QueryStatisticsBaseline(at, null, 7, [entry]));
            var found = store.Find("h:1/d/u");
            check(found is { Deallocations: 7, StatsReset: null } && found.TakenAt == at && found.TakenAt.Kind == DateTimeKind.Utc && found.Entries.Single() == entry, "Baseline store round-trips the instant, deallocations and counters exactly");
            check(store.Find("h:1/d/other") is null, "Baseline store returns null for a target without a baseline");
            check(!Path.GetFileName(store.PathOf("secret-host:5432/sales/alice")).Contains("secret") && !Path.GetFileName(store.PathOf("secret-host:5432/sales/alice")).Contains("alice"), "Baseline file name does not reveal host or user");
            File.Copy(store.PathOf("h:1/d/u"), store.PathOf("h:2/d/u"));
            bool rejected = false; try { store.Find("h:2/d/u"); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Baseline file of another target is refused");
            File.WriteAllText(store.PathOf("h:3/d/u"), "{\"Version\":2,\"Target\":\"h:3/d/u\",\"Baseline\":null}");
            rejected = false; try { store.Find("h:3/d/u"); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Unsupported baseline file version is reported, not repaired");
            rejected = false; try { store.Save("h:4/d/u", new QueryStatisticsBaseline(at, null, 0, [entry with { Calls = -5 }])); } catch (InvalidDataException) { rejected = true; }
            check(rejected && !File.Exists(store.PathOf("h:4/d/u")), "Invalid counters are rejected before writing");
            for (int i = 0; i < QueryStatisticsBaselineStore.MaximumTargets + 3; i++) { store.Save("e:" + i, new QueryStatisticsBaseline(at, null, 0, [])); if (File.Exists(store.PathOf("e:" + i))) File.SetLastWriteTimeUtc(store.PathOf("e:" + i), DateTime.UtcNow.AddMinutes(i - 100)); }
            store.Save("e:last", new QueryStatisticsBaseline(at, null, 0, []));
            check(Directory.GetFiles(dir, "*.json").Length == QueryStatisticsBaselineStore.MaximumTargets && store.Find("e:last") is not null && !File.Exists(store.PathOf("e:0")), "Baseline store keeps only the most recently written targets");
            rejected = false; try { new QueryStatisticsPreferenceStore(Path.Combine(dir, "p.json")).Save(new QueryStatisticsPreference("x", "", "", "", 0, QueryStatisticsOrder.Calls, 5, DateTimeOffset.Now) { Window = QueryStatisticsWindowMode.Last15Minutes }); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "A sliding window is never persisted as the window to restore");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    // Since baseline and sliding windows in the real window, with the baseline saved per target and restored by a new window.
    private static async Task WindowModeChecks(string connection, NpgsqlConnection admin, string marker, Action<bool, string> check)
    {
        async Task Run(string sql, int times) { for (int i = 0; i < times; i++) { await using var cmd = new NpgsqlCommand(sql, admin); await cmd.ExecuteNonQueryAsync(); } }
        string repeated = $"SELECT count(*) FROM {SqlIdentifier.Quote(marker)}";
        var probe = new QueryStatisticsWindow(connection) { ShowInTaskbar = false };
        string baselinePath = probe.BaselinePath, target = probe.PreferenceTarget;
        if (File.Exists(baselinePath)) File.Delete(baselinePath);
        var window = new QueryStatisticsWindow(connection) { ShowInTaskbar = false };
        Find<TextBox>(window, "QueryFilter").Text = marker; window.Show();
        DateTime takenAt;
        try
        {
            await WaitFirstRefresh(window);
            var selector = Find<ComboBox>(window, "WindowSelector"); var status = Find<TextBox>(window, "Status"); var grid = Find<DataGrid>(window, "Statements");
            var info = Find<TextBlock>(window, "WindowInfo"); var note = Find<TextBlock>(window, "ScopeNote");
            check(window.WindowMode == QueryStatisticsWindowMode.Cumulative && info.Text == "No baseline set." && note.Text.StartsWith("Cumulative"), "Window starts cumulative without a baseline");
            foreach (string name in new[] { "WindowSelector", "SetBaselineButton", "WindowInfo" }) check(!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(Find<FrameworkElement>(window, name))), "Window control has an accessible name: " + name);
            selector.SelectedIndex = 1; int version = window.SnapshotVersion; await window.ApplyFiltersAsync();
            check(status.Text.Contains("No baseline is set") && status.Text.Contains("Previous snapshot retained") && window.SnapshotVersion == version && note.Text.StartsWith("Activity within the window"), "Since baseline without a baseline keeps the snapshot and asks for Set Baseline: " + status.Text);
            await window.SetBaselineAsync();
            takenAt = window.SavedBaseline?.TakenAt ?? default;
            check(window.WindowMode == QueryStatisticsWindowMode.SinceBaseline && window.SavedBaseline is not null && File.Exists(baselinePath) && grid.Items.Count == 0, "Set Baseline saves the counters for the target and shows an empty window");
            check(status.Text.Contains("Baseline set at") && status.Text.Contains("ran in the window") && info.Text.StartsWith("Baseline "), "Status and window info report the baseline instant: " + status.Text);
            string saved = File.ReadAllText(baselinePath);
            check(!saved.Contains(marker) && !saved.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && !saved.Contains("Password", StringComparison.OrdinalIgnoreCase), "Saved baseline holds counters only: no SQL text, no secret");
            await Run(repeated, 2); await window.ApplyFiltersAsync();
            var row = grid.Items.OfType<QueryStatisticsEntry>().Single();
            check(row.Calls == 2 && row.IsWindowed && row.MaxExecLabel == "—" && row.MinExecLabel == "—" && window.ChartItems.Count == 1 && window.ChartItems[0].ValueLabel == row.TotalExecLabel, "Since baseline shows only the new executions, without min/max, and charts them");
            check(status.Text.Contains("Window ") && status.Text.Contains(takenAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)), "Status names the window start and end: " + status.Text);
            int beforeHeader = window.SnapshotVersion; var appliedBefore = window.AppliedOrder; await ClickHeader(window, grid, "Max ms");
            check(window.SnapshotVersion == beforeHeader && window.AppliedOrder == appliedBefore && status.Text.Contains("cannot be derived"), "Max ms header is refused in a window and keeps the order");
            grid.SelectedItem = row;
            await Capture(window, "m1022-window-normal", false, check); await Capture(window, "m1022-window-small", true, check);
        }
        finally { window.Close(); }

        var reopened = new QueryStatisticsWindow(connection) { ShowInTaskbar = false };
        check(reopened.WindowMode == QueryStatisticsWindowMode.SinceBaseline && reopened.SavedBaseline?.TakenAt == takenAt && !reopened.IsSampling, "A new window restores the saved baseline and the since-baseline mode");
        reopened.Show();
        try
        {
            await WaitFirstRefresh(reopened);
            var grid = Find<DataGrid>(reopened, "Statements"); var status = Find<TextBox>(reopened, "Status");
            check(grid.Items.OfType<QueryStatisticsEntry>().Single().Calls == 2, "The restored baseline measures the same window after reopening");
            Find<ComboBox>(reopened, "WindowSelector").SelectedIndex = 2;
            check(reopened.IsSampling, "Choosing a sliding window starts background sampling");
            await reopened.SamplingCompletion;
            check(reopened.SampleCount == 1 && Find<TextBlock>(reopened, "WindowInfo").Text.Contains("available from"), "First sample is taken at once and the window says when it becomes available");
            int version = reopened.SnapshotVersion; await reopened.ApplyFiltersAsync();
            check(reopened.SnapshotVersion == version && status.Text.Contains("not available yet") && grid.Items.Count == 1, "A sliding window younger than its length is refused and the previous snapshot kept: " + status.Text);
            reopened.Clock = () => DateTimeOffset.Now.AddMinutes(6);
            await Run(repeated, 3); await reopened.ApplyFiltersAsync();
            check(grid.Items.OfType<QueryStatisticsEntry>().Single() is { Calls: 3, IsWindowed: true } && status.Text.Contains("ran in the window"), "Last 5 minutes measures from the oldest sample old enough");
        }
        finally { reopened.Close(); }
        check(!reopened.IsSampling && new QueryStatisticsPreferenceStore(QueryStatisticsPreferenceStore.DefaultPath).Find(target) is { Window: QueryStatisticsWindowMode.Cumulative }, "Closing stops sampling and a sliding window is saved as cumulative");
        var third = new QueryStatisticsWindow(connection) { ShowInTaskbar = false };
        check(third.WindowMode == QueryStatisticsWindowMode.Cumulative && !third.IsSampling && third.SavedBaseline is not null, "A reopened window never resumes sampling on its own");
        File.WriteAllText(baselinePath, "{not json");
        var corrupted = new QueryStatisticsWindow(connection) { ShowInTaskbar = false };
        Find<TextBox>(corrupted, "QueryFilter").Text = marker; corrupted.Show();
        try
        {
            await WaitFirstRefresh(corrupted);
            check(corrupted.SavedBaseline is null && Find<TextBox>(corrupted, "Status").Text.Contains("saved baseline could not be read"), "Unreadable baseline file is reported and the window stays usable");
        }
        finally { corrupted.Close(); }
        check(File.ReadAllText(baselinePath) == "{not json", "Unreadable baseline file is preserved, never overwritten");
        File.Delete(baselinePath);
    }
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        StoreChecks(check);
        BaselineStoreChecks(check);
        ChartBuildChecks(check);
        string suffix = Guid.NewGuid().ToString("N")[..12], marker = "pgms_stats_ui_" + suffix, missingDb = "pgms_stats_ui_missing_" + suffix;
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        bool extensionCreated = false, dbCreated = false;
        try
        {
            await using (var exists = new NpgsqlCommand("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname='pg_stat_statements'", admin))
                if ((long)(await exists.ExecuteScalarAsync())! == 0) { await using var create = new NpgsqlCommand("CREATE EXTENSION pg_stat_statements", admin); await create.ExecuteNonQueryAsync(); extensionCreated = true; }
            await using (var create = new NpgsqlCommand($"CREATE TABLE {SqlIdentifier.Quote(marker)}(id int)", admin)) await create.ExecuteNonQueryAsync();
            for (int i = 0; i < 2; i++) await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {SqlIdentifier.Quote(marker)}", admin)) await count.ExecuteScalarAsync();
            await using (var slow = new NpgsqlCommand($"SELECT count(*), pg_sleep(0.02) FROM {SqlIdentifier.Quote(marker)}", admin)) await slow.ExecuteScalarAsync();

            string application = "PGMS.StatsUI." + suffix;
            string pooled = new NpgsqlConnectionStringBuilder(connection) { ApplicationName = application, Pooling = true, MinPoolSize = 1, MaxPoolSize = 1 }.ConnectionString;
            (string Connection, string Sql)? planRequest = null;
            var window = new QueryStatisticsWindow(pooled, (target, sql) => { planRequest = (target, sql); return Task.CompletedTask; }) { ShowInTaskbar = false };
            var grid = Find<DataGrid>(window, "Statements"); var status = Find<TextBox>(window, "Status"); var query = Find<TextBox>(window, "QueryFilter"); var minCalls = Find<TextBox>(window, "MinCallsFilter"); var order = Find<ComboBox>(window, "OrderSelector");
            var banner = Find<Border>(window, "AvailabilityBanner"); var reset = Find<Button>(window, "ResetButton");
            query.Text = marker; window.Show();
            try
            {
                await WaitFirstRefresh(window);
                check(grid.Items.Count == 3 && status.Text.Contains("complete filtered snapshot") && status.Text.Contains("UTC"), "Window loads the three statements naming the table with server time in the status");
                check(banner.Visibility == Visibility.Collapsed && reset.IsEnabled && window.Availability?.CanReadAllQueries == true, "Readable superuser view hides the availability banner and enables reset");
                foreach (string name in new[] { "DatabaseFilter", "UserFilter", "QueryFilter", "MinCallsFilter", "OrderSelector", "Statements", "Status" }) check(!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(Find<FrameworkElement>(window, name))), "Statistics control has accessible name: " + name);
                check(grid.Items.OfType<QueryStatisticsEntry>().First().Query.Contains("pg_sleep"), "Default ordering by total time shows the slow statement first");
                var repeated = grid.Items.OfType<QueryStatisticsEntry>().Single(r => r.Calls == 2); grid.SelectedItem = repeated; int version = window.SnapshotVersion;
                await window.ReloadAsync();
                check(window.SnapshotVersion == version + 1 && grid.SelectedItem is QueryStatisticsEntry kept && kept.QueryId == repeated.QueryId && kept.Calls == 2, "Refresh preserves the selected statement identity");
                order.SelectedIndex = 3; await window.ReloadAsync();
                check(grid.Items.OfType<QueryStatisticsEntry>().First().Calls == 2, "Calls ordering places the repeated statement first"); var spoken = DataGridRowNameChecks.RowName(grid, grid.Items[0]); var spokenEntry = (QueryStatisticsEntry)grid.Items[0]; check(spoken == $"Query ID {spokenEntry.QueryId}, Database {spokenEntry.Database}, User {spokenEntry.User}, Calls 2" && !spoken.Contains("pg_sleep") && !spoken.Contains(nameof(QueryStatisticsEntry)), "Statistics row is announced by ID, database, user and calls only: " + spoken);
                int beforeHeader = window.SnapshotVersion; await ClickHeader(window, grid, "Total ms");
                check(window.SnapshotVersion == beforeHeader + 1 && window.AppliedOrder == QueryStatisticsOrder.TotalExecTime && (order.SelectedItem as ComboBoxItem)?.Tag as string == "TotalExecTime", "Total ms header reloads in server total-time order and syncs Order by");
                check(grid.Items.OfType<QueryStatisticsEntry>().First().Query.Contains("pg_sleep") && !window.IsLocallySorted, "Server header order shows the slow statement first without a local sort");
                check(Direction(grid, "Total ms") == System.ComponentModel.ListSortDirection.Descending && Direction(grid, "Calls") is null && Direction(grid, "Database") is null, "Only the server-ordered column shows the descending arrow");
                beforeHeader = window.SnapshotVersion; await ClickHeader(window, grid, "Database");
                check(window.SnapshotVersion == beforeHeader && window.IsLocallySorted && Direction(grid, "Database") == System.ComponentModel.ListSortDirection.Ascending, "Database header sorts the loaded rows locally without reloading");
                check(status.Text.Contains("Sorted locally by Database") && status.Text.Contains("Total execution time"), "Local sort explains that the server order still chooses the loaded rows: " + status.Text);
                await window.ReloadAsync();
                check(window.IsLocallySorted && Direction(grid, "Database") == System.ComponentModel.ListSortDirection.Ascending && Direction(grid, "Total ms") is null, "Refresh with the same server order keeps the local sort");
                beforeHeader = window.SnapshotVersion; await ClickHeader(window, grid, "Calls");
                check(window.SnapshotVersion == beforeHeader + 1 && !window.IsLocallySorted && window.AppliedOrder == QueryStatisticsOrder.Calls && grid.Items.OfType<QueryStatisticsEntry>().First().Calls == 2, "Calls header clears the local sort and reloads by calls");
                check(Direction(grid, "Calls") == System.ComponentModel.ListSortDirection.Descending && Direction(grid, "Database") is null, "Arrow moves to the Calls column");
                order.SelectedIndex = 1; await window.ApplyFiltersAsync();
                check(window.AppliedOrder == QueryStatisticsOrder.MeanExecTime && Direction(grid, "Mean ms") == System.ComponentModel.ListSortDirection.Descending && Direction(grid, "Calls") is null, "Order by list and header arrow stay in sync after Apply Filters");
                beforeHeader = window.SnapshotVersion; await ClickHeader(window, grid, "Temp blocks");
                check(window.SnapshotVersion == beforeHeader + 1 && window.AppliedOrder == QueryStatisticsOrder.TempBlocks && (order.SelectedItem as ComboBoxItem)?.Tag as string == "TempBlocks" && Direction(grid, "Temp blocks") == System.ComponentModel.ListSortDirection.Descending, "Temp blocks header reloads in server temp-block order");
                foreach (var (header, tag) in new[] { ("Std dev ms", "StddevExecTime"), ("Shared blocks", "SharedBlocks"), ("Blocks read", "SharedBlocksRead"), ("I/O ms", "IoTime"), ("WAL bytes", "WalBytes") })
                    check(grid.Columns.Any(c => c.Header as string == header) && order.Items.OfType<ComboBoxItem>().Any(i => i.Tag as string == tag), "New metric is shown and offered in Order by: " + header);
                check(status.Text.Contains("track_io_timing is off"), "Status explains that I/O time stays 0 while track_io_timing is off");
                check(grid.Items.OfType<QueryStatisticsEntry>().All(r => r.SharedBlocks == r.SharedBlocksHit + r.SharedBlocksRead), "Shared blocks column binds hits plus reads");
                var showPlan = Find<Button>(window, "ShowPlanButton");
                grid.SelectedItem = grid.Items.OfType<QueryStatisticsEntry>().Single(r => r.Query.StartsWith("CREATE TABLE", StringComparison.Ordinal));
                check(!showPlan.IsEnabled, "Show Plan is disabled for a utility command");
                var planned = grid.Items.OfType<QueryStatisticsEntry>().Single(r => r.Query.Contains("pg_sleep", StringComparison.Ordinal)); grid.SelectedItem = planned;
                check(showPlan.IsEnabled && !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(showPlan)), "Show Plan is enabled with an accessible name for a visible SELECT");
                showPlan.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                check(planRequest is { } request && request.Sql == planned.Query.TrimEnd() && new NpgsqlConnectionStringBuilder(request.Connection).Database == planned.Database && new NpgsqlConnectionStringBuilder(request.Connection).ApplicationName == application, "Show Plan hands the recorded SQL and a connection to the statement's database to the owner");
                check(status.Text.Contains("generic plan") && status.Text.Contains("nothing is executed") && status.Text.Contains(planned.Database), "Status explains the generic plan opens unexecuted for the statement's database: " + status.Text);
                order.SelectedIndex = 3; await window.ReloadAsync();
                minCalls.Text = "abc"; await window.ReloadAsync();
                check(status.Text.Contains("non-negative integer") && status.Text.Contains("Previous snapshot retained") && grid.Items.Count == 3, "Invalid minimum calls keeps the previous snapshot and explains the filter was not applied");
                minCalls.Text = "5"; await window.ReloadAsync();
                check(grid.Items.Count == 0 && status.Text.Contains("0 statement(s)"), "Minimum calls above every count yields an empty complete snapshot");
                minCalls.Text = ""; order.SelectedIndex = 0;
                await using (var blocker = new NpgsqlConnection(connection))
                {
                    await blocker.OpenAsync(); await window.ReloadAsync(); version = window.SnapshotVersion;
                    int windowPid; await using (var pid = new NpgsqlCommand("SELECT pid FROM pg_stat_get_activity(NULL) WHERE application_name=@app", admin)) { pid.Parameters.AddWithValue("app", application); windowPid = Convert.ToInt32(await pid.ExecuteScalarAsync()); }
                    await using var blockedQuery = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid))>0", admin); blockedQuery.Parameters.AddWithValue("pid", windowPid);
                    await using var transaction = await blocker.BeginTransactionAsync();
                    await using (var lockCatalog = new NpgsqlCommand("LOCK TABLE pg_catalog.pg_extension IN ACCESS EXCLUSIVE MODE", blocker, transaction)) await lockCatalog.ExecuteNonQueryAsync();
                    Task pending = window.ReloadAsync(); bool observed = false; var deadline = DateTime.UtcNow.AddSeconds(10);
                    try
                    {
                        while (DateTime.UtcNow < deadline && !pending.IsCompleted) { observed = (bool)(await blockedQuery.ExecuteScalarAsync())!; if (observed) break; await Task.Delay(25); }
                        check(observed, "Statistics refresh is observed waiting on an actual catalog lock");
                        var cancel = Find<Button>(window, "CancelRefreshButton");
                        check(cancel.IsVisible && cancel.IsEnabled && !reset.IsEnabled && !Find<Grid>(window, "FilterFields").IsEnabled, "Cancel Refresh is offered while reset and filters are protected");
                        int busyIndex = order.SelectedIndex; await ClickHeaderWhileBusy(grid, "Calls");
                        check(order.SelectedIndex == busyIndex && status.Text.Contains("Wait for the current refresh") && !pending.IsCompleted, "Header click during a blocked refresh neither reloads nor changes the order");
                        window.Close(); check(window.IsVisible, "Closing a blocked window waits for cancellation");
                        await Capture(window, "m1012-stats-refresh-small", true, check);
                        Find<CheckBox>(window, "AutoRefreshCheck").IsChecked = true; check(window.IsAutoRefreshRunning, "Auto-refresh can be armed while a refresh is blocked");
                        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); check(!cancel.IsEnabled, "Cancel Refresh disables repeated cancellation"); await pending.WaitAsync(TimeSpan.FromSeconds(10));
                        check(window.SnapshotVersion == version && status.Text.Contains("Previous snapshot retained") && grid.Items.Count == 3, "Canceled refresh retains the prior snapshot explicitly");
                        check(Find<Button>(window, "RefreshButton").IsEnabled && reset.IsEnabled && !cancel.IsVisible, "Canceled refresh releases controls");
                        check(!window.IsAutoRefreshRunning && Find<CheckBox>(window, "AutoRefreshCheck").IsChecked == false, "Cancel Refresh pauses auto-refresh");
                    }
                    finally { Find<Button>(window, "CancelRefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transaction.RollbackAsync(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                }
                await window.ReloadAsync(); check(window.SnapshotVersion == version + 1 && grid.Items.Count == 3, "Explicit refresh succeeds after the lock is released");
                await ChartChecks(window, grid, check);
                grid.SelectedItem = grid.Items[0];
                await Capture(window, "m1012-stats-normal", false, check); await Capture(window, "m1012-stats-small", true, check);
                var resetSelected = Find<Button>(window, "ResetSelectedButton"); grid.SelectedItem = null; check(!resetSelected.IsEnabled, "Reset Selected is disabled without a selection");
                var target = grid.Items.OfType<QueryStatisticsEntry>().Single(r => r.Calls == 2); grid.SelectedItem = target; check(resetSelected.IsEnabled && reset.IsEnabled, "Reset Selected enables for a statement with visible query id");
                await window.ResetSelectedAsync(confirmed: false); check(status.Text == "Selective reset not performed." && grid.Items.Count == 3, "Declined selective reset keeps every entry");
                await window.ResetSelectedAsync(confirmed: true); check(status.Text.Contains("Selected statement reset confirmed") && grid.Items.Count == 2 && !grid.Items.OfType<QueryStatisticsEntry>().Any(r => r.QueryId == target.QueryId), "Confirmed selective reset removes only the selected entry from the grid");
                await window.ReloadAsync(); check(grid.Items.Count == 2 && !grid.Items.OfType<QueryStatisticsEntry>().Any(r => r.QueryId == target.QueryId) && grid.Items.OfType<QueryStatisticsEntry>().Any(r => r.Query.Contains("pg_sleep")), "Server confirms the other statements survived the selective reset");
                version = window.SnapshotVersion; Find<CheckBox>(window, "AutoRefreshCheck").IsChecked = true; window.AutoRefreshInterval = TimeSpan.FromMilliseconds(300); // arming reapplies the selector interval, so shorten it afterwards
                var tickDeadline = DateTime.UtcNow.AddSeconds(5); while (DateTime.UtcNow < tickDeadline && window.SnapshotVersion == version) await Task.Delay(50);
                check(window.IsAutoRefreshRunning && window.SnapshotVersion > version, "Auto-refresh timer produces a real refresh");
                Find<CheckBox>(window, "AutoRefreshCheck").IsChecked = false; check(!window.IsAutoRefreshRunning, "Unchecking auto-refresh stops the timer");
                await window.ResetAsync(confirmed: false);
                check(status.Text == "Reset not performed." && grid.Items.Count == 2 && (await new QueryStatisticsService(connection).ReadSnapshotAsync(new QueryStatisticsFilter(Query: marker))).Rows.Count == 2, "Declined reset keeps server statistics and the displayed snapshot");
                await window.ResetAsync(confirmed: true);
                check(status.Text.Contains("reset confirmed") && grid.Items.Count == 0, "Confirmed reset clears the grid and reports the server instant");
                await window.ReloadAsync(); check(grid.Items.Count == 0 && status.Text.Contains("complete filtered snapshot"), "Statements are gone from the server after reset");
                window.Close(); check(!window.IsVisible, "Idle window closes normally");
            }
            finally { if (window.IsVisible) window.Close(); await using var poolKey = new NpgsqlConnection(pooled); NpgsqlConnection.ClearPool(poolKey); }

            // Persisted filters: the closed window saved its last valid filters for this target; a new window restores them before its first refresh.
            var store = new QueryStatisticsPreferenceStore(QueryStatisticsPreferenceStore.DefaultPath);
            var reopened = new QueryStatisticsWindow(pooled) { ShowInTaskbar = false };
            try
            {
                check(reopened.PreferenceTarget == new QueryStatisticsWindow(connection, 1).PreferenceTarget && !reopened.PreferenceTarget.Contains("Application", StringComparison.OrdinalIgnoreCase), "Preference target depends on host, port, database and user only");
                check(Find<TextBox>(reopened, "QueryFilter").Text == marker && Find<TextBox>(reopened, "MinCallsFilter").Text == "" && Find<ComboBox>(reopened, "OrderSelector").SelectedIndex == 0, "Reopened window restores the last applied filters before refreshing");
                check(Find<CheckBox>(reopened, "AutoRefreshCheck").IsChecked == false && !reopened.IsAutoRefreshRunning, "Reopened window never starts auto-refresh on its own");
                reopened.Show(); await WaitFirstRefresh(reopened);
                check(Find<TextBox>(reopened, "Status").Text.Contains("Filters restored") && Find<DataGrid>(reopened, "Statements").Items.Count == 0, "Restored filters are applied to the first refresh and announced once");
                Find<ComboBox>(reopened, "OrderSelector").SelectedIndex = 2; Find<TextBox>(reopened, "MinCallsFilter").Text = "7"; Find<ComboBox>(reopened, "AutoRefreshIntervalSelector").SelectedIndex = 2;
                await reopened.ApplyFiltersAsync();
                var saved = store.Find(reopened.PreferenceTarget);
                check(saved is { MinimumCalls: 7, Order: QueryStatisticsOrder.MaxExecTime, AutoRefreshSeconds: 30 } && saved.Query == marker, "Applying filters persists the validated filter, ordering and interval");
                check(!Find<TextBox>(reopened, "Status").Text.Contains("restored"), "Restore note is not repeated on later refreshes");
                Find<TextBox>(reopened, "MinCallsFilter").Text = "-1"; await reopened.ReloadAsync();
                check(store.Find(reopened.PreferenceTarget) is { MinimumCalls: 7 }, "Invalid filters are neither applied nor persisted");
                string filePath = store.FilePath; string before = File.ReadAllText(filePath);
                check(!before.Contains("Password", StringComparison.OrdinalIgnoreCase) && before.Contains(marker), "Preference file names the filter text but no secret");
                Find<TextBox>(reopened, "MinCallsFilter").Text = "7"; reopened.Close(); check(!reopened.IsVisible, "Reopened window closes idle");
                var third = new QueryStatisticsWindow(pooled) { ShowInTaskbar = false };
                check(Find<TextBox>(third, "MinCallsFilter").Text == "7" && Find<ComboBox>(third, "OrderSelector").SelectedIndex == 2 && Find<ComboBox>(third, "AutoRefreshIntervalSelector").SelectedIndex == 2, "Third window restores min calls, ordering and interval saved by the second");
                third.Close();
                File.WriteAllText(filePath, "{not json");
                var corrupted = new QueryStatisticsWindow(pooled) { ShowInTaskbar = false };
                check(Find<TextBox>(corrupted, "QueryFilter").Text == "" && Find<ComboBox>(corrupted, "OrderSelector").SelectedIndex == 0, "Unreadable preference file falls back to defaults");
                Find<TextBox>(corrupted, "QueryFilter").Text = marker; corrupted.Show();
                try
                {
                    await WaitFirstRefresh(corrupted); string text = Find<TextBox>(corrupted, "Status").Text;
                    check(text.Contains("could not be read") && text.Contains("could not be saved") && text.Contains("complete filtered snapshot"), "Unreadable file is reported for both restore and save while the refresh still succeeds");
                }
                finally { corrupted.Close(); }
                check(File.ReadAllText(filePath) == "{not json", "Unreadable preference file is preserved for diagnosis, never overwritten");
                File.Delete(filePath);
            }
            finally { if (reopened.IsVisible) reopened.Close(); if (File.Exists(store.FilePath)) File.Delete(store.FilePath); }

            // Truncation banner through the internal row limit.
            foreach (string shape in new[] { "count(*)", "max(id)" }) await using (var again = new NpgsqlCommand($"SELECT {shape} FROM {SqlIdentifier.Quote(marker)}", admin)) await again.ExecuteScalarAsync();
            var limited = new QueryStatisticsWindow(connection, 1) { ShowInTaskbar = false }; Find<TextBox>(limited, "QueryFilter").Text = marker; limited.Show();
            try { await WaitFirstRefresh(limited); check(Find<DataGrid>(limited, "Statements").Items.Count == 1 && Find<TextBox>(limited, "Status").Text.Contains("more statements match"), "Row limit reports truncation and asks to narrow filters"); }
            finally { limited.Close(); }

            await WindowModeChecks(connection, admin, marker, check);

            // Unavailable: extension absent in a fresh database.
            await using (var create = new NpgsqlCommand($"CREATE DATABASE {SqlIdentifier.Quote(missingDb)}", admin)) { await create.ExecuteNonQueryAsync(); dbCreated = true; }
            var missing = new QueryStatisticsWindow(new NpgsqlConnectionStringBuilder(connection) { Database = missingDb }.ConnectionString) { ShowInTaskbar = false }; missing.Show();
            try
            {
                await WaitFirstRefresh(missing);
                var text = Find<TextBlock>(missing, "AvailabilityText").Text;
                check(Find<Border>(missing, "AvailabilityBanner").Visibility == Visibility.Visible && text.Contains("CREATE EXTENSION") && text.Contains(missingDb), "Unavailable database shows the banner naming the manual step");
                check(Find<DataGrid>(missing, "Statements").Items.Count == 0 && !Find<Button>(missing, "ResetButton").IsEnabled && Find<TextBox>(missing, "Status").Text.Contains("unavailable") && Find<Button>(missing, "RefreshButton").IsEnabled, "Unavailable state disables reset, keeps refresh and reports nothing was changed");
                await missing.ResetAsync(confirmed: true);
                check(Find<TextBox>(missing, "Status").Text.Contains("unavailable"), "Reset cannot run while unavailable");
                await Capture(missing, "m1012-stats-unavailable", false, check);
            }
            finally { missing.Close(); }
            await using (var still = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = missingDb }.ConnectionString))
            {
                await still.OpenAsync(); await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname='pg_stat_statements'", still);
                check((long)(await count.ExecuteScalarAsync())! == 0, "Window did not create the extension in the unavailable database");
            }
        }
        finally
        {
            await using (var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {SqlIdentifier.Quote(marker)}", admin)) await drop.ExecuteNonQueryAsync();
            if (dbCreated) { await using var drop = new NpgsqlCommand($"DROP DATABASE {SqlIdentifier.Quote(missingDb)}", admin); await drop.ExecuteNonQueryAsync(); }
            if (extensionCreated) { await using var drop = new NpgsqlCommand("DROP EXTENSION pg_stat_statements", admin); await drop.ExecuteNonQueryAsync(); }
        }
    }
}
