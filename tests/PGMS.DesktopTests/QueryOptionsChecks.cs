using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>PGMS-033: Query ▸ Query Options per tab, defaults in Tools ▸ Options, their effect on execution, JSON links, Retain CR/LF and open transactions at close.</summary>
internal static class QueryOptionsChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static T Named<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name);
        string schema = "qopt_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<bool> Exists(string table) { await using var command = new NpgsqlCommand($"SELECT to_regclass('{schema}.{table}') IS NOT NULL", admin); return (bool)(await command.ExecuteScalarAsync())!; }
        await Sql($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.t (id integer, nome text, doc jsonb, ids integer[]); INSERT INTO {schema}.t VALUES (7, 'ab' || chr(10) || 'cd', '{{\"a\":1}}', ARRAY[1,2]), (1000, '{{\"texto\":true}}', NULL, NULL);");
        var window = new MainWindow { ShowInTaskbar = false, Width = 1200, Height = 850 }; window.Show();
        var original = window.EnvironmentSettings.Load();
        string file = Path.Combine(Path.GetTempPath(), "pgms-options-" + Guid.NewGuid().ToString("N") + ".rpt");
        string export = Path.ChangeExtension(file, ".csv");
        var prompts = new List<bool>();
        var promptAnswer = MessageBoxResult.Cancel;
        window.OpenTransactionPrompt = aborted => { prompts.Add(aborted); return promptAnswer; };
        try
        {
            await Idle();
            window.EnvironmentSettings.Update(d => d with { QueryDefaults = null });

            // Dialog
            var dialog = new QueryOptionsDialog(new QueryOptions(ExecutionTimeoutSeconds: 42, TextFormat: TextOutputFormat.CustomDelimited, CustomDelimiter: "|"));
            var pages = Named<QueryOptionsPages>(dialog, "OptionPages");
            dialog.Show(); await Idle();
            var timeoutBox = Named<TextBox>(pages, "TimeoutBox"); var lockBox = Named<TextBox>(pages, "LockBox"); var delimiter = Named<TextBox>(pages, "DelimiterBox"); var errorText = Named<TextBlock>(pages, "ErrorText");
            check(timeoutBox.Text == "42" && delimiter.Text == "|" && delimiter.IsEnabled && Named<StackPanel>(pages, "ExecutionGeneralPanel").IsVisible, "Query Options opens on Execution ▸ General with the values of the tab");
            Named<TreeViewItem>(dialog, "ResultsTextNode").IsSelected = true; await Idle();
            check(Named<StackPanel>(pages, "ResultsTextPanel").IsVisible && !Named<StackPanel>(pages, "ExecutionGeneralPanel").IsVisible, "The tree changes the page");
            pages.ResetCurrentPage(); await Idle();
            check(Named<ComboBox>(pages, "FormatBox").SelectedIndex == 0 && !delimiter.IsEnabled && timeoutBox.Text == "42", "Reset to Default changes only the page on screen");
            lockBox.Text = "-2";
            dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
            check(dialog.IsVisible && dialog.Result is null && errorText.IsVisible && Named<StackPanel>(pages, "ExecutionAdvancedPanel").IsVisible && Named<TreeViewItem>(dialog, "ExecutionAdvancedNode").IsSelected, "An invalid lock timeout is refused on its own page");
            lockBox.Text = "2500"; timeoutBox.Text = "86401";
            dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
            check(dialog.IsVisible && Named<StackPanel>(pages, "ExecutionGeneralPanel").IsVisible && errorText.Text.Contains("86400", StringComparison.Ordinal), "A time-out out of range is refused");
            timeoutBox.Text = "9";
            dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
            check(dialog.Result is { ExecutionTimeoutSeconds: 9, LockTimeoutMilliseconds: 2500, TextFormat: TextOutputFormat.ColumnAligned }, "OK returns the values on every page");
            dialog.Close();

            // Plan warning thresholds (PGMS-040)
            var planDialog = new QueryOptionsDialog(new QueryOptions(PlanWarningRatio: 25, PlanWarningMinimumRows: 400)) { ShowInTaskbar = false };
            var planPages = Named<QueryOptionsPages>(planDialog, "OptionPages");
            planDialog.Show(); await Idle();
            var ratioBox = Named<TextBox>(planPages, "PlanRatioBox"); var minimumBox = Named<TextBox>(planPages, "PlanMinimumBox");
            check(ratioBox.Text == "25" && minimumBox.Text == "400", "The plan warning thresholds open with the values of the tab");
            ratioBox.Text = "1";
            check(planPages.TryRead() is null && planPages.RejectedPage == QueryOptionsPages.ExecutionAdvanced, "A ratio below 2 is refused on Execution ▸ Advanced");
            ratioBox.Text = "30"; minimumBox.Text = "x";
            check(planPages.TryRead() is null && planPages.RejectedPage == QueryOptionsPages.ExecutionAdvanced, "A minimum that is not a number is refused");
            minimumBox.Text = "0";
            check(planPages.TryRead() is { PlanWarningRatio: 30, PlanWarningMinimumRows: 0 }, "The thresholds are read from the page");
            planPages.ShowPage(QueryOptionsPages.ExecutionAdvanced); planPages.ResetCurrentPage(); await Idle(); planDialog.UpdateLayout();
            check(ratioBox.Text == "10" && minimumBox.Text == "1000", "Reset to Default restores 10 times and 1000 rows");
            var advancedPanel = Named<StackPanel>(planPages, "ExecutionAdvancedPanel"); var lastHelp = (FrameworkElement)advancedPanel.Children[^1]; var resetButton = Named<Button>(planPages, "ResetButton");
            double helpBottom = lastHelp.TranslatePoint(new Point(0, lastHelp.ActualHeight), planPages).Y, resetTop = resetButton.TranslatePoint(new Point(), planPages).Y;
            check(helpBottom <= resetTop + 0.1, $"Execution ▸ Advanced fits the dialog: text ends at {helpBottom:0}, Reset starts at {resetTop:0}");
            var planRoot = (FrameworkElement)planDialog.Content;
            var planBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(planRoot.ActualWidth), (int)Math.Ceiling(planRoot.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var planDrawing = new System.Windows.Media.DrawingVisual();
            using (var dc = planDrawing.RenderOpen()) { var bounds = new Rect(0, 0, planRoot.ActualWidth, planRoot.ActualHeight); dc.DrawRectangle(planDialog.Background, null, bounds); dc.DrawRectangle(new System.Windows.Media.VisualBrush(planRoot), null, bounds); }
            planBitmap.Render(planDrawing);
            var planEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); planEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(planBitmap));
            System.IO.Directory.CreateDirectory("artifacts/review");
            using (var planImage = System.IO.File.Create("artifacts/review/m1057-query-options-advanced.png")) planEncoder.Save(planImage);
            planDialog.Close();
            check(new QueryOptions(PlanWarningRatio: 1, PlanWarningMinimumRows: -4).Normalized() == new QueryOptions(), "Hand-edited plan thresholds go back to the defaults");

            // Menu and per-tab scope
            var otherTab = await window.OpenQueryTextAsync(connection, "SELECT 1");
            var query = await window.OpenQueryTextAsync(connection, $"SELECT id, nome FROM {schema}.t ORDER BY id; UPDATE {schema}.t SET id = id WHERE false;"); // last opened: the visible tab, so its grid has cells
            await Idle();
            check(Named<MenuItem>(window, "QueryOptionsMenu").Command == MainWindow.ShowQueryOptions && Named<Button>(window, "QueryOptionsButton").Command == MainWindow.ShowQueryOptions, "Query Options is in the Query menu and on the toolbar");
            check(((MenuItem)query.Editor.ContextMenu.Items[^1]).Command == MainWindow.ShowQueryOptions, "Query Options is in the editor's menu");
            query.ApplyQueryOptions(new QueryOptions(ExecutionTimeoutSeconds: 11, NoCount: true, ShowCompletionTime: false));
            check(Named<TextBox>(query, "QueryTimeout").Text == "11" && query.CommandTimeoutSeconds == 11 && query.CurrentQueryOptions.ExecutionTimeoutSeconds == 11, "The time-out of Query Options is the one on the toolbar");
            check(otherTab.QueryOptions == new QueryOptions() && otherTab.CommandTimeoutSeconds == 0, "Query Options changes only its own tab");
            Named<TextBox>(query, "QueryTimeout").Text = "12";
            check(query.CurrentQueryOptions.ExecutionTimeoutSeconds == 12, "The dialog opens with what the toolbar box holds");

            // Grid: NOCOUNT and completion time
            await query.ExecuteAsync(); await Idle();
            var messages = Named<TextBox>(query, "Messages");
            check(!messages.Text.Contains("Rows affected", StringComparison.Ordinal) && !messages.Text.Contains("Completion time", StringComparison.Ordinal), "NOCOUNT and no completion time apply to Grid messages");
            query.ApplyQueryOptions(new QueryOptions());
            await query.ExecuteAsync(); await Idle();
            check(messages.Text.Contains("Rows affected: 0.", StringComparison.Ordinal) && messages.Text.Contains("\n\nCompletion time: ", StringComparison.Ordinal), "Grid messages show the count and the completion time by default");

            // Text
            query.ResultsDestination = ResultsDestination.Text;
            query.ApplyQueryOptions(new QueryOptions(TextFormat: TextOutputFormat.CommaDelimited, TextIncludeQuery: true, NoCount: true, ShowCompletionTime: false));
            await query.ExecuteAsync(); await Idle();
            string shown = Named<TextBox>(query, "ResultsText").Text.Replace("\r\n", "\n");
            check(shown.StartsWith($"SELECT id, nome FROM {schema}.t ORDER BY id; UPDATE {schema}.t SET id = id WHERE false;\n\nid,nome\n", StringComparison.Ordinal) && shown.Contains("\n7,ab\ncd\n", StringComparison.Ordinal) && !shown.Contains("rows affected", StringComparison.Ordinal) && !shown.Contains("Completion", StringComparison.Ordinal),
                "Results to Text uses the format, includes the query and honors NOCOUNT: " + shown);
            query.ApplyQueryOptions(new QueryOptions(TextRightAlignNumeric: true, TextIncludeHeaders: false));
            query.Editor.Text = $"SELECT id FROM {schema}.t ORDER BY id"; await query.ExecuteAsync(); await Idle();
            check(Named<TextBox>(query, "ResultsText").Text.Replace("\r\n", "\n").StartsWith("   7\n1000\n", StringComparison.Ordinal), "Results to Text right-aligns numbers and leaves out headers using the real column type");
            query.ApplyQueryOptions(new QueryOptions(TextDiscardResults: true));
            await query.ExecuteAsync(); await Idle();
            string discarded = Named<TextBox>(query, "ResultsText").Text;
            check(discarded.Contains("(2 rows affected)", StringComparison.Ordinal) && !discarded.Contains("1000", StringComparison.Ordinal), "Discard in Text keeps only the counts");

            // File
            query.ResultsDestination = ResultsDestination.File; query.PickResultsFile = () => file;
            query.ApplyQueryOptions(new QueryOptions(TextMaxCharacters: 2, NoCount: true));
            query.Editor.Text = "SELECT 'abcdef' AS c"; await query.ExecuteAsync(); await Idle();
            string written = File.ReadAllText(file).Replace("\r\n", "\n");
            check(written.Contains("\nab\n", StringComparison.Ordinal) && !written.Contains("abc", StringComparison.Ordinal) && !written.Contains("rows affected", StringComparison.Ordinal) && !messages.Text.Contains("rows affected", StringComparison.Ordinal) && messages.Text.Contains("Completion time", StringComparison.Ordinal), "Results to File follows the column limit and NOCOUNT");

            // Grid: discard, JSON links, Retain CR/LF
            query.ResultsDestination = ResultsDestination.Grid;
            var results = Named<TabControl>(query, "ResultSets");
            query.ApplyQueryOptions(new QueryOptions(GridDiscardResults: true));
            query.Editor.Text = $"SELECT id FROM {schema}.t"; await query.ExecuteAsync(); await Idle();
            check(results.Items.Count == 0 && messages.Text.Contains("Results discarded: 2 row(s) read.", StringComparison.Ordinal), "Discard in Grid shows no result and says how many rows were read");

            query.ApplyQueryOptions(new QueryOptions());
            Named<TabControl>(query, "OutputTabs").SelectedIndex = 0;
            query.Editor.Text = $"SELECT id, nome, doc, ids FROM {schema}.t ORDER BY id"; await query.ExecuteAsync(); await Idle();
            var grid = (DataGrid)((TabItem)results.Items[0]).Content;
            grid.UpdateLayout();
            TextBlock Cell(int row, int column) => (TextBlock)grid.Columns[column].GetCellContent(grid.Items[row]);
            static bool IsLink(TextBlock text) => text.TextDecorations is { Count: > 0 } && text.Cursor == Cursors.Hand;
            check(IsLink(Cell(0, 2)) && IsLink(Cell(1, 1)), "A jsonb value and JSON in a text column are links");
            check(!IsLink(Cell(0, 1)) && !IsLink(Cell(1, 2)) && !IsLink(Cell(0, 0)) && !IsLink(Cell(0, 3)), "Plain text, NULL, numbers and arrays are not links");
            var tabsBefore = window.QueryDocuments.ToArray(); int before = tabsBefore.Length;
            void Mouse(TextBlock target, RoutedEvent routed) => target.RaiseEvent(new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routed });
            Mouse(Cell(0, 2), UIElement.MouseLeftButtonDownEvent); Mouse(Cell(1, 2), UIElement.MouseLeftButtonUpEvent); await Idle();
            check(window.QueryDocuments.Count == before, "A press and release on different cells is not a click");
            Mouse(Cell(0, 2), UIElement.MouseLeftButtonDownEvent); Mouse(Cell(0, 2), UIElement.MouseLeftButtonUpEvent); await Idle();
            var opened = window.QueryDocuments.Except(tabsBefore).SingleOrDefault();
            check(opened is { IsConnected: false, IsDirty: false, Editor.SyntaxHighlighting: null } && opened.Editor.Text.Replace("\r\n", "\n") == "{\n  \"a\": 1\n}", "Clicking a link opens the JSON formatted in a new tab, without SQL colors");
            if (opened is not null) await window.CloseQueryAsync(opened);

            grid.SelectedCells.Clear(); grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[0], grid.Columns[1]));
            check(query.PrepareSelectedResultCopy(grid, false) == "\"ab\ncd\"\r\n", "Copy retains the line break by default");
            query.ApplyQueryOptions(new QueryOptions(RetainLineBreaks: false));
            check(query.PrepareSelectedResultCopy(grid, false) == "\"ab cd\"\r\n", "With Retain CR/LF off, copy replaces the line break");
            await query.ExportSelectedCsvAsync(export, true); await Idle();
            check(File.ReadAllText(export).Contains("ab cd", StringComparison.Ordinal), "With Retain CR/LF off, export replaces the line break");

            query.ApplyQueryOptions(new QueryOptions(DisplayJsonAsLink: false));
            await query.ExecuteAsync(); await Idle();
            grid = (DataGrid)((TabItem)results.Items[0]).Content; grid.UpdateLayout();
            check(!IsLink(Cell(0, 2)), "Links can be turned off");

            // Session options
            query.ApplyQueryOptions(new QueryOptions(Isolation: QueryIsolationLevel.RepeatableRead, LockTimeoutMilliseconds: 3456));
            async Task<string> Show(string setting)
            {
                query.Editor.Text = $"SELECT current_setting('{setting}')"; await query.ExecuteAsync(); await Idle();
                return Convert.ToString(((System.Data.DataView)((DataGrid)((TabItem)results.Items[0]).Content).ItemsSource)[0][0]) ?? "";
            }
            check(await Show("default_transaction_isolation") == "repeatable read" && await Show("lock_timeout") == "3456ms", "Isolation level and lock timeout reach the tab's session at the next execution");
            query.Editor.Text = "BEGIN"; await query.ExecuteAsync(); await Idle();
            query.ApplyQueryOptions(new QueryOptions(Isolation: QueryIsolationLevel.Serializable, LockTimeoutMilliseconds: 99));
            string insideBlock = await Show("lock_timeout");
            check(insideBlock == "3456ms" && messages.Text.Contains("after the open transaction ends", StringComparison.Ordinal), "A change made inside a transaction waits, and says so");
            query.Editor.Text = "COMMIT"; await query.ExecuteAsync(); await Idle();
            check(await Show("lock_timeout") == "99ms" && await Show("default_transaction_isolation") == "serializable", "The change is applied after the transaction ends");
            query.ApplyQueryOptions(new QueryOptions());
            check(await Show("lock_timeout") == "0" && await Show("default_transaction_isolation") == "read committed", "Going back to the defaults restores the server's values");

            // Open transactions at close
            prompts.Clear();
            async Task<QueryView> Tab(string sql) { var tab = await window.OpenQueryTextAsync(connection, sql); await tab.ExecuteAsync(); await Idle(); tab.IsDirty = false; return tab; }
            var plain = await Tab("SELECT 1");
            check(await window.CloseQueryAsync(plain) && prompts.Count == 0, "A query that never opened a transaction closes without asking");

            var keep = await Tab($"BEGIN; CREATE TABLE {schema}.tx_cancel(id int)");
            promptAnswer = MessageBoxResult.Cancel;
            check(!await window.CloseQueryAsync(keep) && prompts.SequenceEqual([false]) && window.QueryDocuments.Contains(keep) && !await Exists("tx_cancel"), "Cancel keeps the tab and the transaction open");
            promptAnswer = MessageBoxResult.Yes; prompts.Clear();
            check(await window.CloseQueryAsync(keep) && prompts.Count == 1 && await Exists("tx_cancel"), "Yes commits and closes");

            var discard = await Tab($"BEGIN; CREATE TABLE {schema}.tx_no(id int)");
            promptAnswer = MessageBoxResult.No;
            bool closed = await window.CloseQueryAsync(discard);
            for (int i = 0; i < 50 && await Exists("tx_no"); i++) await Task.Delay(50);
            check(closed && !await Exists("tx_no"), "No closes and the transaction is rolled back");

            var failed = await Tab("BEGIN"); failed.Editor.Text = "SELECT 1/0"; await failed.ExecuteAsync(); await Idle(); failed.IsDirty = false;
            promptAnswer = MessageBoxResult.OK; prompts.Clear();
            check(await window.CloseQueryAsync(failed) && prompts.SequenceEqual([true]), "A failed transaction is asked differently and closes on OK");

            var committed = await Tab("BEGIN"); committed.Editor.Text = "COMMIT"; await committed.ExecuteAsync(); await Idle(); committed.IsDirty = false; prompts.Clear();
            check(await window.CloseQueryAsync(committed) && prompts.Count == 0, "After COMMIT nothing is asked");

            var off = await Tab($"BEGIN; CREATE TABLE {schema}.tx_off(id int)");
            off.ApplyQueryOptions(new QueryOptions(CheckOpenTransactions: false)); prompts.Clear();
            check(await window.CloseQueryAsync(off) && prompts.Count == 0, "With the option off, nothing is asked");

            // Tools ▸ Options
            var options = new OptionsDialog(window.EnvironmentSettings);
            options.Show(); await Idle();
            var shared = Named<QueryOptionsPages>(options, "QueryPages");
            check(!shared.IsVisible, "The Query Options pages stay hidden on the other Options pages");
            Named<TreeViewItem>(options, "ExecutionAdvancedPage").IsSelected = true; await Idle();
            check(shared.IsVisible && Named<StackPanel>(shared, "ExecutionAdvancedPanel").IsVisible, "Tools ▸ Options shows the same pages");
            Named<CheckBox>(shared, "NoCountBox").IsChecked = true;
            Named<TreeViewItem>(options, "ResultsTextPage").IsSelected = true; await Idle();
            Named<ComboBox>(shared, "FormatBox").SelectedIndex = 2;
            Named<TextBox>(shared, "MaxCharactersBox").Text = "100";
            Named<TreeViewItem>(options, "ExecutionGeneralPage").IsSelected = true; await Idle();
            Named<TextBox>(shared, "TimeoutBox").Text = "33";
            options.Accept(options, new RoutedEventArgs()); await Idle();
            var fresh = window.EnvironmentSettings.Load().QueryOptionDefaults;
            check(fresh is { NoCount: true, TextFormat: TextOutputFormat.TabDelimited, TextMaxCharacters: 100, ExecutionTimeoutSeconds: 33 }, "Tools ▸ Options saves the defaults");
            var newTab = await window.OpenQueryTextAsync(connection, "SELECT 1"); await Idle();
            check(newTab.QueryOptions.NoCount && newTab.QueryOptions.TextFormat == TextOutputFormat.TabDelimited && newTab.CommandTimeoutSeconds == 33 && Named<TextBox>(newTab, "QueryTimeout").Text == "33", "New tabs start from the defaults");
            check(query.QueryOptions == new QueryOptions(), "Tabs already open keep their options");
            newTab.IsDirty = false; await window.CloseQueryAsync(newTab);

            var bad = new OptionsDialog(window.EnvironmentSettings); bad.Show(); await Idle();
            var badPages = Named<QueryOptionsPages>(bad, "QueryPages");
            Named<TextBox>(badPages, "MaxCharactersBox").Text = "0";
            bad.Accept(bad, new RoutedEventArgs()); await Idle();
            check(bad.IsVisible && Named<TreeViewItem>(bad, "ResultsTextPage").IsSelected && Named<TextBlock>(badPages, "ErrorText").IsVisible, "Tools ▸ Options refuses a wrong value on its page");
            bad.Close();
            check(new QueryOptions(ExecutionTimeoutSeconds: -5, LockTimeoutMilliseconds: -9, TextMaxCharacters: 0, CustomDelimiter: "ab", Isolation: (QueryIsolationLevel)77, TextFormat: (TextOutputFormat)9).Normalized() == new QueryOptions(), "Hand-edited values go back to the defaults");

            foreach (var tab in window.QueryDocuments.ToArray()) tab.IsDirty = false;
        }
        finally
        {
            // A failed check must not leave the Save question open: it would block the run with no output.
            window.OpenTransactionPrompt = _ => MessageBoxResult.No;
            foreach (var tab in window.QueryDocuments.ToArray()) tab.IsDirty = false;
            window.EnvironmentSettings.Update(_ => original);
            window.Close();
            try { File.Delete(file); File.Delete(export); } catch (IOException) { }
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
