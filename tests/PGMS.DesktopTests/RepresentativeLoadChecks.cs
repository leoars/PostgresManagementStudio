using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

// Representative-performance gate (docs/en/validation.md). Each process runs one
// scenario against the synthetic pgms_perf database seeded by Measure-RepresentativeLoad.ps1.
// The input heartbeat measures UI-thread stalls; it is not a compositor presentation timestamp.
internal static class RepresentativeLoadChecks
{
    public const string Database = "pgms_perf";

    private sealed class Probe
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly List<long> beats = [0];
        private readonly DispatcherTimer timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        public readonly List<Dictionary<string, object>> Steps = [];
        public long Now => watch.ElapsedMilliseconds;
        public Probe() { timer.Tick += (_, _) => beats.Add(watch.ElapsedMilliseconds); timer.Start(); }

        public async Task<long> StepAsync(string name, Func<Task> action, FrameworkElement layoutRoot, Dictionary<string, object>? extra = null)
        {
            long start = Now;
            await action();
            layoutRoot.UpdateLayout();
            await layoutRoot.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            long end = Now;
            var step = new Dictionary<string, object> { ["name"] = name, ["startMs"] = start, ["elapsedMs"] = end - start };
            if (extra is not null) foreach (var pair in extra) step[pair.Key] = pair.Value;
            Steps.Add(step);
            return end - start;
        }

        public void Record(string name, long start, long end, Dictionary<string, object>? extra = null)
        {
            var step = new Dictionary<string, object> { ["name"] = name, ["startMs"] = start, ["elapsedMs"] = end - start };
            if (extra is not null) foreach (var pair in extra) step[pair.Key] = pair.Value;
            Steps.Add(step);
        }

        // Longest interval without an input-priority tick inside [start,end], boundaries included.
        public long MaxGap(long start, long end)
        {
            long last = start, gap = 0;
            foreach (long beat in beats.Where(b => b > start && b <= end)) { gap = Math.Max(gap, beat - last); last = beat; }
            return Math.Max(gap, end - last);
        }

        public object Finish()
        {
            timer.Stop();
            foreach (var step in Steps)
            {
                long start = (long)step["startMs"];
                step["maxInputGapMs"] = MaxGap(start, start + (long)step["elapsedMs"]);
            }
            return Steps;
        }
    }

    public static string PerfConnection(string baseConnection) =>
        new NpgsqlConnectionStringBuilder(baseConnection) { Database = Database, Pooling = false }.ConnectionString;

    public static async Task RunAsync(string baseConnection, string scenario, int repetition, Action<bool, string> check)
    {
        string connection = PerfConnection(baseConnection);
        string root = Path.GetFullPath(Path.Combine("artifacts", "representative-load", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var memory = new LoadMemorySampler();
        var probe = new Probe();
        var counters = new LoadIntervalCounters();
        object? details = null;
        try
        {
            details = scenario switch
            {
                "explorer" => await ExplorerAsync(connection, root, probe, check),
                "editor" => await EditorAsync(root, probe, check),
                "query" => await QueryAsync(connection, root, probe, check),
                "memory" => await MemoryAsync(connection, root, probe, check),
                "recovery" => await RecoveryAsync(root, probe, check),
                "activity" => await ActivityAsync(connection, probe, check),
                "backup" => await BackupAsync(connection, root, repetition, check),
                _ => throw new ArgumentException("Unknown representative scenario: " + scenario)
            };
        }
        finally { await memory.DisposeAsync(); }
        var steps = probe.Finish();
        string server;
        await using (var c = new NpgsqlConnection(connection)) { await c.OpenAsync(); await using var v = new NpgsqlCommand("SHOW server_version", c); server = (string)(await v.ExecuteScalarAsync())!; }
        string json = JsonSerializer.Serialize(new
        {
            measurementSchemaVersion = 1, milestone = BuildInfo.Milestone, scenario, repetition, serverVersion = server,
            machine = new { Environment.ProcessorCount, Environment.OSVersion.VersionString, totalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes },
            steps, details, intervalCounters = counters.Complete(),
            memory = new { memory.Baseline, memory.Maximum, memory.Samples, intervalMs = 20 },
            limitation = "Synthetic workload on the isolated loopback cluster; one process per run. maxInputGapMs is the longest interval without an Input-priority Dispatcher tick (10 ms timer, ~15.6 ms OS resolution), not compositor presentation. Memory maxima are 20 ms samples of the whole process."
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, "measurement.json"), json);
        Console.WriteLine(json); Console.WriteLine("Representative load report: " + root);
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var limit = Stopwatch.StartNew();
        while (!condition())
        {
            if (limit.Elapsed > timeout) throw new TimeoutException("Representative load condition not reached.");
            await Task.Delay(2);
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    // Startup is recorded but is not a "typical operation" criterion. A person needs at least
    // this long after the window appears before choosing a file or expanding the tree.
    internal const int UserThinkTimeMs = 1500;

    private static async Task StartupAsync(MainWindow window, Probe probe)
    {
        await probe.StepAsync("startup", async () => { window.Show(); await window.RecoveryCompletion; }, window);
        await Task.Delay(UserThinkTimeMs);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static bool Loaded(CatalogNode node) => !node.IsBusy && node.Children.Count > 0 && node.Children[0].Entry.Name != "Expand to load…";

    private static async Task<CatalogNode> ExpandAsync(CatalogNode node)
    {
        node.IsExpanded = true;
        await WaitAsync(() => Loaded(node) || node.Error is not null, TimeSpan.FromSeconds(60));
        if (node.Error is not null) throw new InvalidOperationException(node.Error);
        return node;
    }

    private static async Task<object> ExplorerAsync(string connection, string root, Probe probe, Action<bool, string> check)
    {
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", Path.Combine(root, "state"));
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0, Width = 1280, Height = 800 };
        try
        {
            await StartupAsync(window, probe);
            var tree = (TreeView)window.FindName("ExplorerTree");
            await probe.StepAsync("connect-explorer", () => window.ConnectExplorerAsync(connection), window);
            var server = (CatalogNode)tree.Items[0];
            await ExpandAsync(server);
            var databases = await ExpandAsync(server.Children.Single(n => n.Entry.Kind == CatalogKind.Databases));
            var database = await ExpandAsync(databases.Children.Single(n => n.Entry.Name == Database));
            var schemas = await ExpandAsync(database.Children.Single(n => n.Entry.Kind == CatalogKind.Schemas));
            var schema = await ExpandAsync(schemas.Children.Single(n => n.Entry.Name == "perf"));
            window.UpdateLayout();
            var tables = schema.Children.Single(n => n.Entry.Kind == CatalogKind.Tables);
            await probe.StepAsync("expand-200-tables", () => ExpandAsync(tables), window);
            check(tables.Children.Count == 200, "Explorer expands the 200-table folder of the synthetic database");
            var big = tables.Children.Single(n => n.Entry.Name == "big");
            await ExpandAsync(big);
            var columns = big.Children.Single(n => n.Entry.Kind == CatalogKind.Columns);
            await probe.StepAsync("expand-columns", () => ExpandAsync(columns), window);
            check(columns.Children.Count == 20, "Explorer expands the columns of the large table");
            await probe.StepAsync("refresh-200-tables", () => tables.LoadAsync(refresh: true), window);
            await probe.StepAsync("filter-tables", () => { tables.SetFilter("t1"); return Task.CompletedTask; }, window);
            tables.SetFilter("");
            var properties = new TablePropertiesDialog(connection, "perf", "big") { ShowInTaskbar = false, Opacity = 0 };
            try
            {
                properties.Show();
                await probe.StepAsync("table-properties-1M-rows", () => properties.InspectAsync(), properties);
                check(properties.Snapshot is not null, "Table Properties inspects the 1 million row table");
            }
            finally { properties.Close(); }
            return new { tables = tables.Children.Count, columns = columns.Children.Count };
        }
        finally { window.Close(); await window.CloseCompletion; }
    }

    internal static string TypicalScript()
    {
        var text = new StringBuilder();
        for (int i = 1; i <= 5000; i++)
        {
            int table = (i % 199) + 1;
            text.Append((i % 5) switch
            {
                0 => $"SELECT c1, c2, c3 FROM perf.t{table:000} WHERE id = {i};",
                1 => $"UPDATE perf.t{table:000} SET c2 = 'v{i}' WHERE id = {i};",
                2 => $"-- step {i}: verify perf.t{table:000}",
                3 => $"INSERT INTO perf.t{table:000}(id, c1) VALUES ({i}, 'é');",
                _ => $"DELETE FROM perf.t{table:000} WHERE id = {i};"
            }).Append("\r\n");
        }
        return text.ToString();
    }

    private static string[] WriteFiles(string root)
    {
        string big = Path.Combine(root, "script-5000-lines.sql");
        SqlFileStore.Write(big, TypicalScript(), SqlFileFormat.Utf8);
        var paths = new List<string> { big };
        for (int i = 1; i <= 4; i++)
        {
            string path = Path.Combine(root, $"small-{i}.sql");
            SqlFileStore.Write(path, string.Concat(Enumerable.Range(1, 40).Select(n => $"SELECT * FROM perf.t{n:000} WHERE id = {n * i};\r\n")), SqlFileFormat.Utf8);
            paths.Add(path);
        }
        return paths.ToArray();
    }

    private static void Activate(MainWindow window, QueryView query) =>
        ((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Single(d => d.Content == query).IsActive = true;

    private static async Task CloseCleanAsync(MainWindow window)
    {
        foreach (var q in ((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>()) q.IsDirty = false;
        window.Close(); await window.CloseCompletion;
    }

    private static async Task<object> EditorAsync(string root, Probe probe, Action<bool, string> check)
    {
        string[] paths = WriteFiles(root);
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", Path.Combine(root, "state"));
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0, Width = 1280, Height = 800 };
        try
        {
            await StartupAsync(window, probe);
            QueryView big = null!;
            await probe.StepAsync("open-200KB-script", async () => big = await window.OpenQueryFileAsync(paths[0]), window);
            foreach (string small in paths.Skip(1)) await probe.StepAsync("open-small-script", () => window.OpenQueryFileAsync(small), window);
            var editor = (TextEditor)big.FindName("Editor");
            check(editor.Document.LineCount == 5001 && new FileInfo(paths[0]).Length is > 150_000 and < 260_000, "Typical script has 5000 lines of about 200 KB");
            Activate(window, big);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            editor.TextArea.Caret.Offset = editor.Document.GetLineByNumber(2500).Offset;
            const string typed = "SELECT count(*) FROM perf.big WHERE c01 > 10; ";
            foreach (char c in typed)
                await probe.StepAsync("keystroke", () => { editor.TextArea.PerformTextInput(c.ToString()); return Task.CompletedTask; }, window);
            check(editor.Text.Contains(typed), "Typed text is in the document");
            big.Replace();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var bar = (ReplaceBar)big.FindName("ReplacementBar");
            var find = (TextBox)bar.FindName("FindText"); var replacement = (TextBox)bar.FindName("ReplacementText");
            void Click(string name) => ((Button)bar.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            find.Text = "perf.t100"; replacement.Text = "perf.t200";
            for (int i = 0; i < 5; i++) await probe.StepAsync("find-next", () => { Click("NextButton"); return Task.CompletedTask; }, window);
            find.Text = "WHERE id"; replacement.Text = "WHERE  id";
            await probe.StepAsync("replace-all-4000", () => { Click("ReplaceAllButton"); return Task.CompletedTask; }, window);
            check(!editor.Text.Contains("WHERE id ="), "Replace All replaced every occurrence");
            string saved = Path.Combine(root, "saved.sql");
            await probe.StepAsync("save-200KB-script", () => window.SaveQueryFileAsync(big, saved), window);
            check(SqlFileStore.Read(saved).Text == editor.Text, "Saved script matches the editor");
            return new { scriptBytes = new FileInfo(paths[0]).Length, scriptLines = 5000, tabs = paths.Length, keystrokes = typed.Length };
        }
        finally { await CloseCleanAsync(window); }
    }

    internal const string ResultQuery = "SELECT * FROM perf.big ORDER BY id LIMIT 10000";

    private static async Task<object> QueryAsync(string connection, string root, Probe probe, Action<bool, string> check)
    {
        var writes = new List<string>();
        var query = new QueryView(Guid.NewGuid(), writes.Add);
        var window = new Window { Content = query, Width = 1280, Height = 800, ShowInTaskbar = false, Opacity = 0 };
        window.Show();
        try
        {
            await query.ConnectAsync(connection);
            var editor = (TextEditor)query.FindName("Editor");
            var sets = (TabControl)query.FindName("ResultSets");
            editor.Text = ResultQuery;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            long start = probe.Now, first = -1;
            var running = query.ExecuteAsync();
            int Rows() => sets.Items.Count > 0 && ((TabItem)sets.Items[0]).Content is DataGrid { ItemsSource: DataView view } ? view.Count : 0;
            while (!running.IsCompleted && first < 0) { if (Rows() > 0) first = probe.Now; else await Task.Delay(1); }
            await running;
            if (first < 0) first = probe.Now;
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            probe.Record("first-rows-visible", start, first);
            probe.Record("query-10000x20-complete", start, probe.Now);
            var grid = (DataGrid)((TabItem)sets.Items[0]).Content;
            check(Rows() == 10000 && grid.Columns.Count == 20, "Query displays 10000 rows x 20 columns");
            var scroller = FindChild<ScrollViewer>(grid)!;
            for (int offset = 500; offset <= 10000; offset += 500)
                await probe.StepAsync("scroll-grid", () => { scroller.ScrollToVerticalOffset(offset); return Task.CompletedTask; }, window);
            await probe.StepAsync("scroll-grid", () => { scroller.ScrollToVerticalOffset(0); return Task.CompletedTask; }, window);
            string csv = Path.Combine(root, "export.csv");
            await probe.StepAsync("export-csv-10000", () => query.ExportSelectedCsvAsync(csv), window);
            check(File.ReadLines(csv).Count() == 10001, "CSV export contains header and 10000 rows");
            var select = Stopwatch.StartNew();
            var rows = ((DataView)grid.ItemsSource).Cast<object>().Take(1000).ToList();
            foreach (var column in grid.Columns) foreach (var row in rows) grid.SelectedCells.Add(new DataGridCellInfo(row, column));
            long selectionMs = select.ElapsedMilliseconds;
            await probe.StepAsync("copy-1000-rows", () => { ApplicationCommands.Copy.Execute(null, grid); return Task.CompletedTask; }, window);
            check(writes.Count == 1 && writes[0].Split('\n').Length >= 1001, "Copy writes 1000 rows plus header to the test destination, never the system clipboard");
            return new { rows = Rows(), columns = grid.Columns.Count, copiedCharacters = writes.Single().Length, unmeasuredSelectionSetupMs = selectionMs };
        }
        finally { query.Cancel(); await query.DisposeAsync(); window.Close(); }
    }

    private static async Task<object> MemoryAsync(string connection, string root, Probe probe, Action<bool, string> check)
    {
        string[] paths = WriteFiles(root);
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", Path.Combine(root, "state"));
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0, Width = 1280, Height = 800 };
        try
        {
            await StartupAsync(window, probe);
            var tabs = new List<QueryView>();
            foreach (string path in paths) tabs.Add(await window.OpenQueryFileAsync(path));
            var target = tabs[1];
            await target.ConnectAsync(connection);
            ((TextEditor)target.FindName("Editor")).Text = ResultQuery;
            Activate(window, target);
            await probe.StepAsync("execute-10000x20-with-5-tabs", () => target.ExecuteAsync(), window);
            var sets = (TabControl)target.FindName("ResultSets");
            check(((DataView)((DataGrid)((TabItem)sets.Items[0]).Content).ItemsSource).Count == 10000, "Memory scenario retains a 10000-row result with five tabs open");
            await Task.Delay(2000);
            using var process = Process.GetCurrentProcess(); process.Refresh();
            return new { tabs = tabs.Count, settledWorkingSetBytes = process.WorkingSet64, settledPrivateBytes = process.PrivateMemorySize64, settledManagedBytes = GC.GetTotalMemory(false) };
        }
        finally { await CloseCleanAsync(window); }
    }

    private static async Task<object> RecoveryAsync(string root, Probe probe, Action<bool, string> check)
    {
        string state = Path.Combine(root, "state");
        string script = TypicalScript();
        using (var source = new QueryRecoverySession(Path.Combine(state, "recovery")))
        {
            source.Store.Save(new(Guid.NewGuid(), "script-5000-lines.sql", script, SqlFileFormat.Utf8));
            for (int i = 1; i <= 4; i++)
                source.Store.Save(new(Guid.NewGuid(), $"draft-{i}.sql", string.Concat(Enumerable.Range(1, 40).Select(n => $"SELECT * FROM perf.t{n:000} WHERE id = {n * i};\r\n")), SqlFileFormat.Utf8));
        }
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", state);
        MainWindow window = null!;
        await probe.StepAsync("restore-5-drafts", async () =>
        {
            window = new MainWindow { ShowInTaskbar = false, Opacity = 0, Width = 1280, Height = 800 };
            window.Show(); await window.RecoveryCompletion;
        }, new Border());
        try
        {
            var restored = ((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>().ToArray();
            check(restored.Length == 5 && restored.Any(q => ((TextEditor)q.FindName("Editor")).Text == script), "Five drafts restored, including the complete 5000-line script");
            return new { drafts = restored.Length };
        }
        finally { await CloseCleanAsync(window); }
    }

    private static async Task<object> ActivityAsync(string connection, Probe probe, Action<bool, string> check)
    {
        var sessions = new List<NpgsqlConnection>();
        var sleepers = new List<Task>();
        try
        {
            for (int i = 0; i < 100; i++)
            {
                var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { ApplicationName = $"pgms_perf_session_{i:000}" }.ConnectionString);
                await c.OpenAsync(); sessions.Add(c);
            }
            foreach (var c in sessions.Take(10))
                sleepers.Add(Task.Run(async () => { try { await using var cmd = new NpgsqlCommand("SELECT pg_sleep(60)", c); await cmd.ExecuteNonQueryAsync(); } catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { } }));
            var window = new ActivityWindow(connection) { ShowInTaskbar = false, Opacity = 0 };
            try
            {
                var grid = (DataGrid)window.FindName("Sessions");
                var refresh = (Button)window.FindName("RefreshButton");
                // Opening the window starts its own first refresh; measure an explicit refresh after it.
                await probe.StepAsync("open-activity-monitor", async () => { window.Show(); await WaitAsync(() => grid.Items.Count > 0 && refresh.IsEnabled, TimeSpan.FromSeconds(60)); }, window);
                for (int i = 0; i < 3; i++) await probe.StepAsync("refresh-100-sessions", () => window.ReloadAsync(), window);
                int rows = grid.Items.Count;
                check(rows >= 100, "Activity Monitor lists the 100 synthetic sessions");
                var selector = (ComboBox)window.FindName("AutoRefreshIntervalSelector");
                selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "5");
                ((CheckBox)window.FindName("AutoRefreshCheck")).IsChecked = true;
                check(window.IsAutoRefreshRunning, "Auto-refresh runs every 5 seconds");
                await probe.StepAsync("auto-refresh-12s", () => Task.Delay(12000), window);
                ((CheckBox)window.FindName("AutoRefreshCheck")).IsChecked = false;
                return new { sessionsListed = rows, autoRefreshSeconds = 5 };
            }
            finally { window.Close(); }
        }
        finally
        {
            // Cancel the sleeping backends server-side first so no close waits on a running command.
            await using (var admin = new NpgsqlConnection(connection))
            {
                await admin.OpenAsync();
                await using var cancel = new NpgsqlCommand("SELECT pg_cancel_backend(pid) FROM pg_stat_activity WHERE application_name LIKE 'pgms_perf_session_%' AND state = 'active'", admin);
                await cancel.ExecuteNonQueryAsync();
            }
            await Task.WhenAll(sleepers).WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var c in sessions) await c.DisposeAsync();
        }
    }

    private static async Task<long> DirectToolAsync(string tool, IEnumerable<string> arguments, string connection, string workingDirectory)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection);
        string bin = Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")!;
        var start = new ProcessStartInfo(Path.Combine(bin, tool + ".exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = workingDirectory };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.ArgumentList.Add("--no-password"); start.ArgumentList.Add("--dbname");
        start.ArgumentList.Add($"postgresql:///?host={builder.Host}&port={builder.Port}&user={builder.Username}&dbname={builder.Database}");
        foreach (string a in arguments) start.ArgumentList.Add(a);
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"{tool} failed: " + await errors);
        await output;
        return watch.ElapsedMilliseconds;
    }

    private static async Task RecreateAsync(string connection, string database)
    {
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database}", admin)) await drop.ExecuteNonQueryAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
    }

    private static async Task<object> BackupAsync(string connection, string root, int repetition, Action<bool, string> check)
    {
        string bin = Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN") ?? throw new InvalidOperationException("PGMS_TEST_POSTGRES_BIN is required.");
        var options = new BackupOptions();
        var restore = new RestoreOptions();
        bool pgmsFirst = repetition % 2 == 0;
        long direct = 0, pgms = 0, directRestore = 0, pgmsRestore = 0;
        string directFile = Path.Combine(root, "direct.dump"), pgmsFile = Path.Combine(root, "pgms.dump");
        async Task DirectBackup() => direct = await DirectToolAsync("pg_dump", [.. options.ToArguments(), "--file", Path.GetFileName(directFile)], connection, root);
        async Task PgmsBackup() { var w = Stopwatch.StartNew(); await new BackupService(connection, bin).BackupWithOptionsAsync(pgmsFile, options); pgms = w.ElapsedMilliseconds; }
        // Untimed warm-up: the first dump/restore of a pair ran 10-30% slower whichever tool went first
        // (OS file cache), so both timed runs must start from the same cache state.
        string warmup = Path.Combine(root, "warmup.dump");
        await DirectToolAsync("pg_dump", [.. options.ToArguments(), "--file", Path.GetFileName(warmup)], connection, root);
        if (pgmsFirst) { await PgmsBackup(); await DirectBackup(); } else { await DirectBackup(); await PgmsBackup(); }
        check(new FileInfo(pgmsFile).Length > 0 && Math.Abs(new FileInfo(pgmsFile).Length - new FileInfo(directFile).Length) < new FileInfo(directFile).Length / 100, "PGMS and direct pg_dump produce archives of the same size within 1%");
        string directTarget = Database + "_restore_direct", pgmsTarget = Database + "_restore_pgms";
        var targetDirect = new NpgsqlConnectionStringBuilder(connection) { Database = directTarget }.ConnectionString;
        var targetPgms = new NpgsqlConnectionStringBuilder(connection) { Database = pgmsTarget }.ConnectionString;
        async Task DirectRestore() { await RecreateAsync(connection, directTarget); directRestore = await DirectToolAsync("pg_restore", [.. restore.ToArguments(), "--", Path.GetFileName(directFile)], targetDirect, root); }
        async Task PgmsRestore() { await RecreateAsync(connection, pgmsTarget); var w = Stopwatch.StartNew(); await new BackupService(targetPgms, bin).RestoreWithOptionsAsync(directFile, restore); pgmsRestore = w.ElapsedMilliseconds; }
        string warmTarget = Database + "_restore_warmup";
        await RecreateAsync(connection, warmTarget);
        await DirectToolAsync("pg_restore", [.. restore.ToArguments(), "--", Path.GetFileName(warmup)], new NpgsqlConnectionStringBuilder(connection) { Database = warmTarget }.ConnectionString, root);
        if (pgmsFirst) { await PgmsRestore(); await DirectRestore(); } else { await DirectRestore(); await PgmsRestore(); }
        long restoredRows;
        await using (var c = new NpgsqlConnection(targetPgms)) { await c.OpenAsync(); await using var q = new NpgsqlCommand("SELECT count(*) FROM perf.big", c); restoredRows = (long)(await q.ExecuteScalarAsync())!; }
        check(restoredRows == 1_000_000, "PGMS restore reproduces the 1 million rows");
        foreach (var target in new[] { directTarget, pgmsTarget, warmTarget })
        {
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync(); await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {target}", admin); await drop.ExecuteNonQueryAsync();
        }
        long archive = new FileInfo(directFile).Length;
        File.Delete(directFile); File.Delete(pgmsFile); File.Delete(warmup);
        return new { order = pgmsFirst ? "pgms-first" : "direct-first", archiveBytes = archive, directBackupMs = direct, pgmsBackupMs = pgms, directRestoreMs = directRestore, pgmsRestoreMs = pgmsRestore };
    }
}
