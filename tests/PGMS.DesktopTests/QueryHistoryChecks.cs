using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-043 (M1060): Query History store, recording from query tabs on the isolated cluster, profile exclusion, options and the window.</summary>
internal static class QueryHistoryChecks
{
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static async Task Until(Func<bool> done) { for (int i = 0; i < 300 && !done(); i++) await Task.Delay(50); }
    /// <summary>Image of a window for visual review (artifacts are not committed).</summary>
    private static void Capture(Window window, string name)
    {
        string dir = Path.GetFullPath(Path.Combine("artifacts", "acceptance", "M1060")); Directory.CreateDirectory(dir);
        var content = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, name + ".png")); encoder.Save(file);
    }
    private static string NewRoot(string name) => Path.GetFullPath(Path.Combine("artifacts", "history-tests", name + "-" + Guid.NewGuid().ToString("N")));

    private static QueryHistoryEntry Entry(string sql, DateTime started, string database = "postgres", QueryHistoryOutcome outcome = QueryHistoryOutcome.Succeeded, QueryHistorySettings? settings = null, int port = 55439, QueryHistoryOrigin origin = QueryHistoryOrigin.Typed)
        => new QueryHistoryCapture(sql, "127.0.0.1", port, database, "pgms_test", started, TimeSpan.FromMilliseconds(12), origin, outcome, 1, null,
            outcome == QueryHistoryOutcome.Failed ? "42P01" : null, outcome == QueryHistoryOutcome.Failed ? "relation does not exist" : null).ToEntry(settings ?? new());

    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        check(Environment.GetEnvironmentVariable("PGMS_HISTORY_DIRECTORY") is { Length: > 0 } dir && !dir.Contains(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase),
            "Desktop tests record Query History in a temporary folder, never in the user's profile");
        StoreChecks(check);
        await RecorderChecks(check);
        await TabChecks(connection, check);
        await WindowChecks(connection, check);
        await DialogChecks(check);
    }

    private static void StoreChecks(Action<bool, string> check)
    {
        string root = NewRoot("store");
        var store = new QueryHistoryStore(root);
        var now = DateTime.UtcNow;
        var first = Entry("SELECT 'synthetic secret ação 😀'\r\n  FROM   pg_class;", now.AddMinutes(-3));
        store.Append(first, new(), now);
        var snapshot = store.ReadAll();
        check(snapshot.Entries.Count == 1 && snapshot.Entries[0] == first && snapshot.UnreadableFiles == 0, "Query History roundtrips an execution with DPAPI");
        string segment = Directory.GetFiles(root, "*.qhs").Single();
        check(!Encoding.UTF8.GetString(File.ReadAllBytes(segment)).Contains("synthetic secret") && !Encoding.Unicode.GetString(File.ReadAllBytes(segment)).Contains("synthetic secret"), "Query History writes no plaintext SQL to disk");
        check(Directory.GetFiles(root, "*.partial").Length == 0, "Query History leaves no staging file");

        // Deduplication: whitespace runs collapse, case is kept, server/port/database separate.
        store.Append(Entry("SELECT 'synthetic secret ação 😀' FROM pg_class;", now.AddMinutes(-2)), new(), now);
        store.Append(Entry("  SELECT 'synthetic secret ação 😀'\tFROM\npg_class;  ", now.AddMinutes(-1)), new(), now);
        store.Append(Entry("select 'synthetic secret ação 😀' from pg_class;", now), new(), now);
        store.Append(Entry("SELECT 'synthetic secret ação 😀' FROM pg_class;", now, database: "other"), new(), now);
        var groups = QueryHistoryGroup.Build(store.ReadAll());
        check(groups.Count == 3 && groups.Single(g => g.Key.Database == "postgres" && g.Latest.Sql.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)).Executions.Count == 3,
            "Executions group by normalized SQL per server and database; case differences stay separate");
        check(groups.Single(g => g.Executions.Count == 3).Executions.Select(e => e.StartedUtc).SequenceEqual(groups.Single(g => g.Executions.Count == 3).Executions.Select(e => e.StartedUtc).OrderByDescending(t => t)),
            "A group lists its executions newest first");

        // Size limit per entry.
        string big = string.Concat(Enumerable.Repeat("SELECT 1; -- 😀 padding\n", 4000));
        var cut = Entry(big, now);
        check(cut.Sql.Length <= 65536 && cut.OriginalLength == big.Length && cut.Truncated && cut.Hash == QueryHistoryText.HashOf(big) && !char.IsHighSurrogate(cut.Sql[^1]),
            "SQL over 64 KiB is cut without splitting a surrogate pair, marked, and grouped by the hash of the whole text");
        check(cut.ReopenText.EndsWith("when it was recorded.", StringComparison.Ordinal) && cut.ReopenText.Contains("-- PGMS Query History: this text was cut", StringComparison.Ordinal), "A cut entry reopens with a comment that says so");
        var smaller = Entry(big, now, settings: new(MaxSqlKiB: 1));
        check(smaller.Sql.Length <= 1024, "The per-entry size limit follows the setting");
        var lone = Entry("SELECT '\ud800';", now);
        check(lone.Sql == "SELECT '�';", "A lone surrogate is stored as U+FFFD instead of failing the record");

        // Tamper, copy under another name, unknown version and oversize are refused and kept.
        byte[] bytes = File.ReadAllBytes(segment); bytes[bytes.Length / 2] ^= 0xFF; File.WriteAllBytes(segment, bytes);
        var damaged = store.ReadAll();
        check(damaged.UnreadableFiles == 1 && File.ReadAllBytes(segment).SequenceEqual(bytes) && damaged.Entries.Count == 0, "A damaged history file is reported and kept byte for byte");
        store.Append(Entry("SELECT 'after damage';", now), new(), now);
        check(store.ReadAll().Entries.Count == 1 && Directory.GetFiles(root, "*.qhs").Length == 2, "Recording continues in a new file after a damaged one");
        string good = Directory.GetFiles(root, "*.qhs").Single(f => f != segment);
        string copied = Path.Combine(root, "20000101T000000000-" + Guid.NewGuid().ToString("N") + ".qhs");
        File.Copy(good, copied);
        check(store.ReadAll().UnreadableFiles == 2 && File.Exists(copied), "A history file copied under another name does not decrypt (entropy bound to its name) and is kept");
        string otherRoot = NewRoot("copy");
        Directory.CreateDirectory(otherRoot);
        string unknown = Path.Combine(otherRoot, "20000101T000000000-" + Guid.NewGuid().ToString("N") + ".qhs");
        File.WriteAllBytes(unknown, ProtectedData.Protect(Encoding.UTF8.GetBytes("{\"Version\":99,\"Name\":\"" + Path.GetFileName(unknown) + "\",\"Entries\":[]}"),
            [.. "PGMS query history 1\0"u8.ToArray(), .. Encoding.UTF8.GetBytes(Path.GetFileName(unknown).ToLowerInvariant())], DataProtectionScope.CurrentUser));
        string oversize = Path.Combine(otherRoot, "20000101T000000001-" + Guid.NewGuid().ToString("N") + ".qhs");
        using (var stream = File.Create(oversize)) stream.SetLength(3 * 1024 * 1024 + 1L);
        string plain = Path.Combine(otherRoot, "20000101T000000002-" + Guid.NewGuid().ToString("N") + ".qhs");
        File.WriteAllText(plain, "{\"Version\":1}");
        var refused = new QueryHistoryStore(otherRoot).ReadAll();
        check(refused.UnreadableFiles == 3 && File.Exists(unknown) && File.Exists(oversize) && File.Exists(plain), "Unknown version, oversize and unencrypted history files are refused and kept");
        check(new QueryHistoryStore(root).Clear() == 1 && Directory.GetFiles(root, "*.qhs").Length == 0, "Clear History removes every history file, readable or not");

        // Retention by count, keeping the newest execution of a pinned query.
        string countRoot = NewRoot("count");
        var counted = new QueryHistoryStore(countRoot);
        var limits = new QueryHistorySettings(MaxExecutions: 100);
        var pinnedOld = Entry("SELECT 'pinned';", now.AddDays(-5));
        counted.Append(pinnedOld, limits, now);
        counted.SetPinned(pinnedOld.Key, true);
        for (int i = 0; i < 100; i++) counted.Append(Entry($"SELECT {i};", now.AddMinutes(-200 + i)), limits, now);
        var afterCount = counted.ReadAll();
        check(afterCount.Entries.Count == 95 && afterCount.Entries.Any(e => e.Id == pinnedOld.Id) && afterCount.Entries.Any(e => e.Sql == "SELECT 99;") && !afterCount.Entries.Any(e => e.Sql == "SELECT 0;"),
            "Past the count limit the oldest executions go until 95% is left; the pinned query's newest execution stays");
        check(afterCount.Pins.Contains(pinnedOld.Key), "Pins persist encrypted");

        // Retention by age (startup), again keeping the pinned one.
        string ageRoot = NewRoot("age");
        var aged = new QueryHistoryStore(ageRoot);
        var pinnedAncient = Entry("SELECT 'pinned ancient';", now.AddDays(-400));
        aged.Append(pinnedAncient, new(), now.AddDays(-400));
        aged.SetPinned(pinnedAncient.Key, true);
        aged.Append(Entry("SELECT 'ancient';", now.AddDays(-100)), new(), now.AddDays(-100));
        aged.Append(Entry("SELECT 'recent';", now.AddDays(-10)), new(), now.AddDays(-10));
        int removed = new QueryHistoryStore(ageRoot).Prune(new(RetentionDays: 90), now);
        var afterAge = aged.ReadAll();
        check(removed == 1 && afterAge.Entries.Select(e => e.Sql).OrderBy(s => s).SequenceEqual(new[] { "SELECT 'pinned ancient';", "SELECT 'recent';" }), "Startup cleanup removes executions older than the retention days, except a pinned query's newest");

        // Delete one execution, unpin.
        var toDelete = afterAge.Entries.Single(e => e.Sql == "SELECT 'recent';");
        check(aged.Delete([toDelete.Id]) == 1 && aged.ReadAll().Entries.All(e => e.Id != toDelete.Id), "Delete removes exactly the chosen execution");
        aged.SetPinned(pinnedAncient.Key, false);
        check(aged.ReadAll().Pins.Count == 0, "Unpin is saved");

        // Segments roll over near 1 MiB and all stay readable.
        string rollRoot = NewRoot("roll");
        var roll = new QueryHistoryStore(rollRoot);
        string chunk = new('x', 200000);
        for (int i = 0; i < 8; i++) roll.Append(Entry($"SELECT '{i}{chunk}';", now.AddSeconds(i), settings: new(MaxSqlKiB: 256)), new(MaxSqlKiB: 256), now);
        var rolled = roll.ReadAll();
        check(Directory.GetFiles(rollRoot, "*.qhs").Length >= 2 && rolled.Entries.Count == 8 && rolled.UnreadableFiles == 0, "History files roll over near 1 MiB of JSON and every one stays readable");

        // Two PGMS processes of the same user: the lock file keeps every execution.
        string sharedRoot = NewRoot("shared");
        var a = new QueryHistoryStore(sharedRoot); var b = new QueryHistoryStore(sharedRoot);
        Parallel.For(0, 60, i => (i % 2 == 0 ? a : b).Append(Entry($"SELECT {i} AS concurrent;", now.AddMilliseconds(i)), new(), now));
        check(a.ReadAll().Entries.Count == 60, "Concurrent writers sharing the folder lose no execution");
        using (new FileStream(Path.Combine(sharedRoot, QueryHistoryStore.LockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var waiting = new QueryHistoryStore(sharedRoot) { LockTimeout = TimeSpan.FromMilliseconds(200) };
            bool timedOut = false;
            try { waiting.Append(Entry("SELECT 'locked';", now), new(), now); } catch (IOException) { timedOut = true; }
            check(timedOut, "A write gives up with an error when another process holds the history lock too long");
        }
    }

    private static async Task RecorderChecks(Action<bool, string> check)
    {
        string root = NewRoot("recorder");
        var settings = new QueryHistorySettings();
        bool exclude = false;
        var warnings = new List<string>();
        var recorder = new QueryHistoryRecorder(new QueryHistoryStore(root), () => settings, (_, _) => exclude);
        recorder.Warning += warnings.Add;
        var capture = new QueryHistoryCapture("SELECT 1;", "127.0.0.1", 55439, "postgres", "pgms_test", DateTime.UtcNow, TimeSpan.FromMilliseconds(3), QueryHistoryOrigin.Typed, QueryHistoryOutcome.Succeeded, 1);
        check(await recorder.RecordAsync(capture) && (await recorder.ReadAsync()).Entries.Count == 1, "The recorder writes an execution on its queue");
        settings = new(Enabled: false);
        check(!await recorder.RecordAsync(capture) && (await recorder.ReadAsync()).Entries.Count == 1, "Recording turned off in Options writes nothing");
        settings = new(); exclude = true;
        check(!await recorder.RecordAsync(capture) && (await recorder.ReadAsync()).Entries.Count == 1, "A server excluded by a profile writes nothing");

        string profiles = Path.Combine(NewRoot("profiles"), "connections.json");
        var profileStore = new ConnectionProfileStore(profiles);
        profileStore.Save(new ConnectionProfile(Guid.NewGuid(), "Production (synthetic)", "127.0.0.1", 55439, "postgres", "pgms_test", "Disable", DisableQueryHistory: true));
        profileStore.Save(new ConnectionProfile(Guid.NewGuid(), "Other port", "127.0.0.1", 55440, "postgres", "pgms_test", "Disable"));
        check(QueryHistoryRecorder.ProfileExcludes(profiles, "127.0.0.1", 55439) && !QueryHistoryRecorder.ProfileExcludes(profiles, "127.0.0.1", 55440) && QueryHistoryRecorder.ProfileExcludes(profiles, "127.0.0.1".ToUpperInvariant(), 55439),
            "A profile with Do not record query history excludes its host and port only");
        check(new ConnectionProfileStore(profiles).Load().Single(p => p.Port == 55439).DisableQueryHistory, "The history option is saved with the profile");
        File.WriteAllText(profiles, "{ not json");
        var guarded = new QueryHistoryRecorder(new QueryHistoryStore(root), () => new(), (s, p) => QueryHistoryRecorder.ProfileExcludes(profiles, s, p));
        var guardedWarnings = new List<string>(); guarded.Warning += guardedWarnings.Add;
        check(!await guarded.RecordAsync(capture) && guardedWarnings.Single().Contains("profiles could not be read", StringComparison.Ordinal), "Unreadable profiles record nothing and say why");

        string blocked = Path.Combine(NewRoot("blocked"), "file-not-folder");
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!); File.WriteAllText(blocked, "x");
        var failing = new QueryHistoryRecorder(new QueryHistoryStore(blocked), () => new(), (_, _) => false);
        var failures = new List<string>(); failing.Warning += failures.Add;
        check(!await failing.RecordAsync(capture) && failures.Single().Contains("the query itself was not affected", StringComparison.Ordinal), "A write failure becomes a warning, not an exception");
        check(warnings.Count == 0, "Normal recording raises no warning");
    }

    private static async Task TabChecks(string connection, Action<bool, string> check)
    {
        var window = new MainWindow { ShowInTaskbar = false, Width = 1100, Height = 800 }; window.Show();
        try
        {
            await Idle();
            var history = window.QueryHistory;
            check(history.Store.DirectoryPath == Path.GetFullPath(Environment.GetEnvironmentVariable("PGMS_HISTORY_DIRECTORY")!), "The main window records into the configured history folder");
            await history.ClearAsync();
            async Task<QueryHistoryEntry?> Last() { await history.Io.RunAsync(() => true); return (await history.ReadAsync()).Entries.OrderBy(e => e.StartedUtc).LastOrDefault(); }
            int Count() => history.Store.ReadAll().Entries.Count;

            var query = await window.OpenQueryTextAsync(connection, "-- typed below");
            query.Editor.Text = "SELECT 1 AS a UNION ALL SELECT 2;";
            await query.ExecuteAsync();
            var ok = await Last();
            int clusterPort = new Npgsql.NpgsqlConnectionStringBuilder(connection).Port;
            check(ok is { Outcome: QueryHistoryOutcome.Succeeded, Server: "127.0.0.1", Database: "postgres", User: "pgms_test", RowsReturned: 2, Origin: QueryHistoryOrigin.Typed, Sql: "SELECT 1 AS a UNION ALL SELECT 2;" }
                && ok.Port == clusterPort && ok.StartedUtc <= DateTime.UtcNow && ok.StartedUtc > DateTime.UtcNow.AddMinutes(-2) && ok.DurationMilliseconds >= 0,
                "F5 in the grid records SQL, server, database, user, start (UTC), duration, rows, success and typed origin");

            query.Editor.Text = "SELECT * FROM pgms_history_missing_table;";
            await query.ExecuteAsync();
            var failed = await Last();
            check(failed is { Outcome: QueryHistoryOutcome.Failed, SqlState: "42P01" } && failed.Error?.Contains("pgms_history_missing_table", StringComparison.Ordinal) == true, "An error is recorded with SQLSTATE and message");

            query.Editor.Text = "CREATE TEMP TABLE pgms_history_t(x int); INSERT INTO pgms_history_t VALUES (1), (2), (3);";
            await query.ExecuteAsync();
            var affected = await Last();
            check(affected is { Outcome: QueryHistoryOutcome.Succeeded, RowsAffected: 3 }, "Rows affected are recorded for DML");

            query.Editor.Text = "SELECT pg_sleep(30);";
            var run = query.ExecuteAsync();
            await Until(() => query.IsBusy); await Task.Delay(400);
            query.Cancel(); await run;
            var canceled = await Last();
            check(canceled is { Outcome: QueryHistoryOutcome.Canceled, Sql: "SELECT pg_sleep(30);" }, "A canceled execution is recorded as canceled");

            int before = Count();
            query.Editor.Text = "SELECT $1;";
            await query.ExecuteAsync(); await history.Io.RunAsync(() => true);
            check(Count() == before, "SQL refused before sending (unbound placeholder) is not recorded");

            query.ResultsDestination = ResultsDestination.Text;
            query.Editor.Text = "SELECT 10 AS t; SELECT 20 AS u;";
            await query.ExecuteAsync();
            var text = await Last();
            check(text is { Outcome: QueryHistoryOutcome.Succeeded, RowsReturned: 2, Sql: "SELECT 10 AS t; SELECT 20 AS u;" }, "Results to Text executions are recorded too");
            query.ResultsDestination = ResultsDestination.Grid;

            // Origins.
            query.Editor.Text = "";
            query.InsertGeneratedSql("SELECT 'from ai' AS origin;");
            await query.ExecuteAsync();
            check((await Last())?.Origin == QueryHistoryOrigin.Ai, "SQL inserted by the AI assistant and run unchanged is recorded with origin AI");
            query.Editor.Text = "";
            query.InsertDesignedSql("SELECT 'from designer' AS origin");
            await query.ExecuteAsync();
            check((await Last())?.Origin == QueryHistoryOrigin.QueryDesigner, "SQL written by Query Designer is recorded with origin Query Designer");
            query.Editor.Text = "SELECT 'from designer, edited' AS origin";
            await query.ExecuteAsync();
            check((await Last())?.Origin == QueryHistoryOrigin.Typed, "Once edited, generated SQL counts as typed");
            var script = await window.OpenQueryTextAsync(connection, "SELECT 'from a script' AS origin;");
            await script.ExecuteAsync();
            check((await Last())?.Origin == QueryHistoryOrigin.Other, "SQL a tool opened in a new tab (Script as, Activity Monitor) is recorded with origin Other");
            query.RecordDesignerExecution("SELECT 'designer pane';", DateTime.UtcNow, TimeSpan.FromMilliseconds(5), null, false, 1);
            check((await Last()) is { Origin: QueryHistoryOrigin.QueryDesigner, Database: "postgres", Sql: "SELECT 'designer pane';" }, "Execute SQL in the Query Designer is recorded with the tab's server and database");

            // The write never delays or fails the query.
            string slowRoot = NewRoot("slow");
            var slow = new QueryHistoryRecorder(new QueryHistoryStore(slowRoot), () => new(), (_, _) => false);
            Task<bool>? pending = null;
            query.HistorySink = c => pending = slow.RecordAsync(c);
            Directory.CreateDirectory(slowRoot);
            var gate = new FileStream(Path.Combine(slowRoot, QueryHistoryStore.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            query.Editor.Text = "SELECT 'while history is locked';";
            await query.ExecuteAsync();
            clock.Stop();
            bool stillWaiting = pending is { IsCompleted: false };
            gate.Dispose();
            check(stillWaiting && clock.ElapsedMilliseconds < 5000 && query.QueryStatus.Text.StartsWith("Query completed", StringComparison.Ordinal), $"The query completes while Query History waits for its file ({clock.ElapsedMilliseconds} ms)");
            check(pending is not null && await pending, "The waiting execution is written once the file is free");
            string brokenRoot = Path.Combine(NewRoot("broken"), "file");
            Directory.CreateDirectory(Path.GetDirectoryName(brokenRoot)!); File.WriteAllText(brokenRoot, "x");
            var broken = new QueryHistoryRecorder(new QueryHistoryStore(brokenRoot), () => new(), (_, _) => false);
            var brokenWarnings = new List<string>(); broken.Warning += brokenWarnings.Add;
            Task<bool>? brokenTask = null;
            query.HistorySink = c => brokenTask = broken.RecordAsync(c);
            query.Editor.Text = "SELECT 'history cannot be written';";
            await query.ExecuteAsync();
            check(query.QueryStatus.Text.StartsWith("Query completed", StringComparison.Ordinal) && brokenTask is not null && !await brokenTask && brokenWarnings.Count == 1, "A history write failure leaves the query completed and only warns");
            query.HistorySink = c => _ = history.RecordAsync(c);
        }
        finally { foreach (var tab in window.QueryDocuments.ToArray()) tab.IsDirty = false; window.Close(); await Idle(); }
    }

    private static async Task WindowChecks(string connection, Action<bool, string> check)
    {
        var window = new MainWindow { ShowInTaskbar = false, Width = 1100, Height = 800 }; window.Show();
        try
        {
            await Idle();
            var menu = (MenuItem)window.FindName("QueryHistoryMenu");
            check(menu.Header as string == "Query _History…" && menu.InputGestureText == "Ctrl+Alt+H" && menu.Parent is MenuItem { Header: "_View" }, "View ▸ Query History… with Ctrl+Alt+H");
            var history = window.QueryHistory;
            await history.ClearAsync();
            var now = DateTime.UtcNow;
            var store = history.Store;
            store.Append(Entry("SELECT 'weekly report' AS r;", now.AddDays(-10)), new(), now);
            store.Append(Entry("SELECT 'weekly report' AS r;", now.AddDays(-1)), new(), now);
            store.Append(Entry("SELECT  'weekly report'  AS r;", now.AddMinutes(-5)), new(), now);
            store.Append(Entry("SELECT * FROM missing;", now.AddMinutes(-4), outcome: QueryHistoryOutcome.Failed), new(), now);
            store.Append(Entry("SELECT 'other database';", now.AddMinutes(-3), database: "pgms_other"), new(), now);
            var view = window.ShowQueryHistory();
            check(ReferenceEquals(window.ShowQueryHistory(), view), "Asking again brings the same Query History window to the front");
            await view.Loading; await Idle();
            check(view.Rows.Count == 3 && view.Rows.All(r => r.IsGroup) && view.Rows[0].Sql == "SELECT 'other database';", "One row per distinct query, newest first");
            var weekly = view.Rows.Single(r => r.Sql.Contains("weekly", StringComparison.Ordinal));
            check(weekly.Executions == "3" && weekly.CanExpand, "A query row shows how many times it ran");
            view.SetExpanded(weekly, true); await Idle();
            check(view.Rows.Count == 6 && view.Rows.Count(r => !r.IsGroup) == 3, "Expanding a query lists its individual executions");
            view.Entries.SelectedItem = view.Rows.First(r => !r.IsGroup);
            await Idle(); Capture(view, "query-history");
            check(view.SqlText.Text == "SELECT  'weekly report'  AS r;" && view.Details.Text.Contains("Origin: Typed", StringComparison.Ordinal), "The lower pane shows the full SQL of the selected execution");
            var failedRow = view.Rows.Single(r => r.IsGroup && r.Entry.Outcome == QueryHistoryOutcome.Failed);
            check(failedRow.Result == "Error 42P01", "The result column shows the SQLSTATE of an error");

            view.TextFilter.Text = "MISSING"; await Idle();
            check(view.Rows.Count == 1 && view.Rows[0].Entry.Outcome == QueryHistoryOutcome.Failed, "The text filter matches the SQL ignoring case");
            view.TextFilter.Text = "";
            view.ConnectionFilter.SelectedItem = view.ConnectionFilter.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Tag == "127.0.0.1:55439 / pgms_other"); await Idle();
            check(view.Rows.Count == 1 && view.Rows[0].Database == "pgms_other", "The connection filter keeps one server and database");
            view.ConnectionFilter.SelectedIndex = 0;
            view.PeriodFilter.SelectedIndex = 1; await Idle();
            var today = view.Rows.Where(r => r.IsGroup && r.Sql.Contains("weekly", StringComparison.Ordinal)).ToList();
            check(today.Count == 1 && view.Rows.Count(r => !r.IsGroup) <= 1, "The period filter hides executions outside it");
            view.PeriodFilter.SelectedIndex = 0; await Idle();

            view.Entries.SelectedItem = view.Rows.Single(r => r.IsGroup && r.Sql.Contains("weekly", StringComparison.Ordinal));
            await view.TogglePinAsync(); await Idle();
            check(view.Rows[0].Group.Pinned && view.Rows[0].PinMark == "★" && (await history.ReadAsync()).Pins.Count == 1, "Pin puts the query first and is saved");
            view.PinnedOnly.IsChecked = true; await Idle();
            check(view.Rows.Where(r => r.IsGroup).All(r => r.Group.Pinned) && view.Rows.Any(), "Pinned only shows pinned queries");
            view.PinnedOnly.IsChecked = false; await Idle();

            string? copied = null; view.WriteClipboard = t => copied = t;
            view.Entries.SelectedItem = view.Rows.Single(r => r.IsGroup && r.Database == "pgms_other");
            view.CopySql();
            check(copied == "SELECT 'other database';", "Copy SQL writes the selected SQL");

            // Reopen: same server/port/user open in a tab, database switched; nothing executed.
            var open = await window.OpenQueryTextAsync(connection, "SELECT 'keep this session';");
            await using (var admin = new Npgsql.NpgsqlConnection(connection))
            {
                await admin.OpenAsync();
                await using (var dropOld = new Npgsql.NpgsqlCommand("DROP DATABASE IF EXISTS pgms_history_reopen WITH (FORCE)", admin)) await dropOld.ExecuteNonQueryAsync();
                await using var create = new Npgsql.NpgsqlCommand("CREATE DATABASE pgms_history_reopen", admin);
                await create.ExecuteNonQueryAsync();
            }
            int clusterPort = new Npgsql.NpgsqlConnectionStringBuilder(connection).Port;
            var reopenEntry = Entry("SELECT current_database();", now, database: "pgms_history_reopen", port: clusterPort);
            int before = history.Store.ReadAll().Entries.Count;
            var reopened = await window.OpenHistoryEntryAsync(reopenEntry);
            await history.Io.RunAsync(() => true);
            check(reopened.IsConnected && reopened.Database == "pgms_history_reopen" && reopened.EditorText == "SELECT current_database();" && history.Store.ReadAll().Entries.Count == before,
                "Open in New Query reuses an open connection to that server and user, switches to the entry's database, and executes nothing");
            var stranger = await window.OpenHistoryEntryAsync(Entry("SELECT 'elsewhere';", now, port: 55498));
            check(!stranger.HasSession && stranger.EditorText == "SELECT 'elsewhere';", "Without an open connection to that server the SQL opens disconnected");
            var cutEntry = Entry(new string('y', 70000), now, port: clusterPort);
            var cutTab = await window.OpenHistoryEntryAsync(cutEntry);
            check(cutTab.EditorText.EndsWith("when it was recorded.", StringComparison.Ordinal), "A cut entry reopens with the cut marked in a comment");
            QueryHistoryEntry? asked = null;
            var spy = new QueryHistoryWindow(history, e => { asked = e; return Task.CompletedTask; }) { ShowInTaskbar = false };
            spy.Show(); await spy.Loading; await Idle();
            spy.Entries.SelectedItem = spy.Rows.Single(r => r.IsGroup && r.Database == "pgms_other");
            await spy.OpenAsync();
            check(asked?.Database == "pgms_other", "Open in New Query hands the selected execution to the main window");

            spy.Confirm = _ => false;
            await spy.DeleteAsync();
            check(spy.Rows.Any(r => r.Database == "pgms_other"), "Delete asks first and does nothing on No");
            spy.Confirm = _ => true;
            spy.Entries.SelectedItem = spy.Rows.Single(r => r.IsGroup && r.Database == "pgms_other");
            await spy.DeleteAsync();
            check(!spy.Rows.Any(r => r.Database == "pgms_other") && history.Store.ReadAll().Entries.All(e => e.Database != "pgms_other"), "Delete on a query row removes all its executions");
            await spy.ClearAsync();
            check(spy.Rows.Count == 0 && history.Store.ReadAll().Entries.Count == 0, "Clear History in the window empties Query History");
            spy.Close();
            await using (var admin = new Npgsql.NpgsqlConnection(connection))
            {
                await admin.OpenAsync();
                await using var drop = new Npgsql.NpgsqlCommand("DROP DATABASE IF EXISTS pgms_history_reopen WITH (FORCE);", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
        finally { foreach (var tab in window.QueryDocuments.ToArray()) tab.IsDirty = false; window.Close(); await Idle(); }
    }

    private static async Task DialogChecks(Action<bool, string> check)
    {
        string state = NewRoot("dialogs"); Directory.CreateDirectory(state);
        var options = new EnvironmentOptions(Path.Combine(state, "environment.json"));
        check(options.Load().History == new QueryHistorySettings(true, 90, 10000, 64), "Defaults: on, 90 days, 10,000 executions, 64 KiB");
        int cleared = 0;
        var dialog = new OptionsDialog(options, () => { cleared++; return Task.FromResult(7); }) { ShowInTaskbar = false };
        dialog.Show(); await Idle();
        ((TreeViewItem)dialog.FindName("HistoryPage")).IsSelected = true; await Idle();
        check(((FrameworkElement)dialog.FindName("HistoryPanel")).IsVisible, "Tools ▸ Options ▸ Query Execution ▸ Query History shows its page");
        Capture(dialog, "options-history");
        ((TextBox)dialog.FindName("HistoryDaysBox")).Text = "0";
        dialog.Accept(dialog, new RoutedEventArgs());
        check(dialog.IsVisible && ((TextBlock)dialog.FindName("HistoryError")).Visibility == Visibility.Visible && options.Load().History.RetentionDays == 90, "An out-of-range retention is refused and nothing is saved");
        dialog.ConfirmClearHistory = () => true;
        await dialog.ClearHistoryAsync();
        check(cleared == 1 && ((TextBlock)dialog.FindName("ClearHistoryStatus")).Text.Contains("7 execution", StringComparison.Ordinal), "Clear History in Options clears at once and reports the count");
        ((TextBox)dialog.FindName("HistoryDaysBox")).Text = "30";
        ((TextBox)dialog.FindName("HistoryCountBox")).Text = "500";
        ((TextBox)dialog.FindName("HistorySizeBox")).Text = "16";
        ((CheckBox)dialog.FindName("HistoryEnabledBox")).IsChecked = false;
        dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
        check(options.Load().History == new QueryHistorySettings(false, 30, 500, 16) && !dialog.IsVisible, "OK saves the Query History settings");

        string profiles = Path.Combine(state, "connections.json");
        var connect = new ConnectionDialog(profilePath: profiles) { ShowInTaskbar = false };
        connect.Show(); await Idle();
        var box = (CheckBox)connect.FindName("DisableHistory");
        check(box.IsChecked == false, "Do not record query history starts off");
        ((TextBox)connect.FindName("ProfileName")).Text = "Production (synthetic)";
        ((TextBox)connect.FindName("Host")).Text = "127.0.0.1";
        ((TextBox)connect.FindName("Port")).Text = "55439";
        box.IsChecked = true;
        await Idle(); Capture(connect, "connect-history");
        ((Button)connect.FindName("SaveProfileButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
        var saved = new ConnectionProfileStore(profiles).Load().Single();
        check(saved.DisableQueryHistory, "Save Profile stores Do not record query history");
        ((Button)connect.FindName("NewProfileButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
        check(box.IsChecked == false, "A new profile starts with history recording on");
        ((ComboBox)connect.FindName("Profiles")).SelectedItem = ((ComboBox)connect.FindName("Profiles")).Items.OfType<ConnectionProfile>().Single(); await Idle();
        check(box.IsChecked == true, "Selecting the profile shows its history option");
        connect.Close();
        File.WriteAllText(Path.Combine(state, "old.json"), "{\"Version\":1,\"Profiles\":[{\"Id\":\"" + Guid.NewGuid() + "\",\"Name\":\"old\",\"Host\":\"h\",\"Port\":5432,\"Database\":\"d\",\"Username\":\"u\",\"SslMode\":\"Prefer\"}]}");
        check(!new ConnectionProfileStore(Path.Combine(state, "old.json")).Load().Single().DisableQueryHistory, "Profiles saved before this option record history");
    }
}
