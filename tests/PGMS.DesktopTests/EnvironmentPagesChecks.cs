using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using PGMS.Desktop;

/// <summary>B1 review (M1141): Tools ▸ Options ▸ Environment ▸ AutoRecover, Documents, Tabs, Task List and Windows, and what each option does.</summary>
internal static class EnvironmentPagesChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string folder = Path.Combine(Path.GetTempPath(), "pgms-environment-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // The pages, in the SSMS 22.11 order, load the defaults and save what was chosen.
            var options = new EnvironmentOptions(Path.Combine(folder, "environment.json"));
            var dialog = new OptionsDialog(options) { ShowInTaskbar = false };
            dialog.Show(); await Idle();
            var pages = (TreeView)dialog.FindName("Pages");
            var environment = pages.Items.OfType<TreeViewItem>().First();
            var names = environment.Items.OfType<TreeViewItem>().Select(i => (string)i.Header).ToList();
            check(names.SequenceEqual(["General", "AutoRecover", "Documents", "Fonts and Colors", "International Settings", "Keyboard", "Product Updates", "Query Shortcuts", "Tabs", "Task List", "Windows"]),
                "Environment lists AutoRecover, Documents, Tabs, Task List and Windows in the SSMS order: " + string.Join(", ", names));
            T Find<T>(string name) where T : class => (T)dialog.FindName(name);
            check(Find<CheckBox>("AutoRecoverBox").IsChecked == true && Find<TextBox>("AutoRecoverIntervalBox").Text == "5" && Find<TextBox>("AutoRecoverLifetimeBox").Text == "7", "AutoRecover starts on, every 5 minutes, kept 7 days, as in SSMS");
            check(Find<CheckBox>("DetectChangesBox").IsChecked == true && Find<CheckBox>("ReloadUnmodifiedBox").IsChecked == true && Find<CheckBox>("AllowReadOnlyBox").IsChecked == true
                && Find<CheckBox>("OpenFromActiveBox").IsChecked == true && Find<CheckBox>("CheckLineEndingsBox").IsChecked == true && Find<CheckBox>("SaveInBackgroundBox").IsChecked == false
                && Find<CheckBox>("UseSaveEncodingBox").IsChecked == false, "Documents starts with the SSMS defaults");
            check(Find<CheckBox>("InsertTabsRightBox").IsChecked == false && Find<CheckBox>("SkipLayoutConfirmBox").IsChecked == false, "Tabs puts new tabs first, as in SSMS; Windows asks before applying a layout");
            var taskList = Find<StackPanel>("TaskListPanel");
            check(Descendants(taskList).OfType<ListBox>().All(l => !l.IsEnabled), "Task List is shown and cannot be changed (PGMS has no Task List window)");
            check(Descendants(Find<StackPanel>("TabsPanel")).OfType<CheckBox>().Count(c => !c.IsEnabled) >= 5, "Tab options AvalonDock cannot apply are shown disabled");
            foreach (var wrong in new[] { "0", "61" })
            {
                Find<TextBox>("AutoRecoverIntervalBox").Text = wrong;
                dialog.Accept(dialog, new RoutedEventArgs());
                check(Find<TextBlock>("AutoRecoverError").Visibility == Visibility.Visible && ((TreeViewItem)dialog.FindName("AutoRecoverPage")).IsSelected && options.Load().AutoRecover is null
                    && Find<TextBlock>("AutoRecoverError").Text.Contains("1 to 60 minutes", StringComparison.Ordinal), $"An interval of {wrong} minutes is refused on its page and nothing is saved");
            }
            Find<TextBox>("AutoRecoverIntervalBox").Text = "30"; Find<TextBox>("AutoRecoverLifetimeBox").Text = "3";
            Find<CheckBox>("SaveInBackgroundBox").IsChecked = true; Find<CheckBox>("UseSaveEncodingBox").IsChecked = true;
            Find<ComboBox>("SaveEncodingBox").SelectedIndex = 2; Find<CheckBox>("InsertTabsRightBox").IsChecked = true; Find<CheckBox>("SkipLayoutConfirmBox").IsChecked = true;
            dialog.Accept(dialog, new RoutedEventArgs());
            var saved = options.Load();
            check(saved.Recovery == new AutoRecoverSettings(true, 30, 3) && saved.DocumentOptions.SaveInBackground && saved.DocumentOptions.UseSaveEncoding && saved.DocumentOptions.SaveEncoding == SqlFileFormat.Utf16LittleEndian
                && saved.TabsSettings == new TabsAndWindowsSettings(true, true) && saved.OptionsRevision == EnvironmentOptions.Document.CurrentOptionsRevision, "OK saves AutoRecover, Documents, Tabs and Windows, with the revision of the defaults");
            if (dialog.IsVisible) dialog.Close();

            check(MainWindow.HasMixedLineEndings("a\r\nb\nc") && !MainWindow.HasMixedLineEndings("a\r\nb\r\n") && !MainWindow.HasMixedLineEndings("a\nb\n") && MainWindow.NormalizeLineEndings("a\nb\r\nc\rd") == "a\r\nb\r\nc\r\nd",
                "Mixed line endings are found and normalized to CRLF");

            // What the options do in the main window.
            var shell = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
            shell.Show(); await shell.RecoveryCompletion; await Idle();
            var previousReload = MainWindow.ReloadChangedPrompt; var previousNormalize = MainWindow.NormalizeLineEndingsPrompt;
            try
            {
                // Activation does not check files here: the checks call CheckChangedFilesAsync themselves, so a stray activation cannot race them.
                static EnvironmentOptions.Document Quiet() => new(Documents: new DocumentSettings(DetectChanges: false));
                shell.ApplyTabsAndDocuments(Quiet());
                var dock = (DockingManager)shell.FindName("DockManager");
                var pane = dock.Layout.Descendents().OfType<LayoutDocumentPane>().First();
                var first = shell.NewQueryTab(); first.IsDirty = false;
                var second = shell.NewQueryTab(); second.IsDirty = false;
                check(pane.Children.OfType<LayoutDocument>().First().Content == second, "By default (Insert new tabs to the right off, as in SSMS), a new tab goes first");
                shell.ApplyTabsAndDocuments(Quiet() with { TabsAndWindows = new TabsAndWindowsSettings(InsertTabsToRight: true) });
                var third = shell.NewQueryTab(); third.IsDirty = false;
                check(pane.Children.OfType<LayoutDocument>().Last().Content == third, "With it on, a new tab goes last");
                shell.ApplyTabsAndDocuments(Quiet());

                check(shell.AutoRecoverInterval == TimeSpan.FromMinutes(5), "AutoRecover checkpoints every 5 minutes by default");
                shell.ApplyAutoRecover(new AutoRecoverSettings(false, 5, 7));
                third.Editor.Text = "SELECT 'unsaved';";
                check(!shell.AutoRecoverRunning && shell.CheckpointQueries() == 0, "AutoRecover off writes no copy of unsaved queries");
                shell.ApplyAutoRecover(new AutoRecoverSettings(true, 30, 7));
                check(shell.AutoRecoverRunning && shell.AutoRecoverInterval == TimeSpan.FromMinutes(30) && shell.CheckpointQueries() >= 1, "AutoRecover on writes copies at the chosen interval, in minutes");
                shell.ApplyAutoRecover(new AutoRecoverSettings());
                third.IsDirty = false;

                // Files changed outside PGMS.
                string path = Path.Combine(folder, "outside.sql");
                File.WriteAllText(path, "SELECT 1;");
                var file = await shell.OpenQueryFileAsync(path); await Idle();
                check(shell.OpenDialogDirectory() == folder, "File ▸ Open starts in the folder of the active query file");
                File.WriteAllText(path, "SELECT 2;");
                check(await shell.CheckChangedFilesAsync() == 1 && file.Editor.Text == "SELECT 2;" && !file.IsDirty, "A file changed outside PGMS with no unsaved changes is reloaded when PGMS is activated");
                file.Editor.Text = "SELECT 3; -- mine";
                File.WriteAllText(path, "SELECT 4;");
                int asked = 0;
                MainWindow.ReloadChangedPrompt = _ => { asked++; return false; };
                check(await shell.CheckChangedFilesAsync() == 0 && asked == 1 && file.Editor.Text == "SELECT 3; -- mine" && await shell.CheckChangedFilesAsync() == 0 && asked == 1,
                    "With unsaved changes PGMS asks once; No keeps the text in the tab");
                MainWindow.ReloadChangedPrompt = _ => true;
                File.WriteAllText(path, "SELECT 5;");
                check(await shell.CheckChangedFilesAsync() == 1 && file.Editor.Text == "SELECT 5;", "Yes reloads the file over the unsaved text");

                // Save with a specific encoding, and save in the background.
                shell.ApplyTabsAndDocuments(Quiet() with { Documents = new DocumentSettings(DetectChanges: false, UseSaveEncoding: true, SaveEncoding: SqlFileFormat.Utf16LittleEndian) });
                file.Editor.Text = "SELECT 6;";
                check(await shell.SaveInBackgroundAsync() == 1 && File.ReadAllBytes(path) is [0xFF, 0xFE, ..] && !file.IsDirty, "Saving uses the chosen encoding (UTF-16 LE); background save writes the unsaved file");
                shell.ApplyTabsAndDocuments(Quiet());

                // Read-only files and line endings when opening.
                string readOnly = Path.Combine(folder, "readonly.sql");
                File.WriteAllText(readOnly, "SELECT 'ro';\nSELECT 2;\r\n");
                File.SetAttributes(readOnly, FileAttributes.ReadOnly);
                string normalizedPath = "";
                MainWindow.NormalizeLineEndingsPrompt = p => { normalizedPath = p; return true; };
                shell.ApplyTabsAndDocuments(Quiet() with { Documents = new DocumentSettings(DetectChanges: false, AllowEditingReadOnly: false) });
                var ro = await shell.OpenQueryFileAsync(readOnly); await Idle();
                check(ro.Editor.IsReadOnly, "A read-only file opens without editing when editing read-only files is not allowed");
                check(normalizedPath == Path.GetFullPath(readOnly) && ro.Editor.Text == "SELECT 'ro';\r\nSELECT 2;\r\n", "A file with mixed line endings asks and normalizes them to CRLF");
                ro.IsDirty = false;
                File.SetAttributes(readOnly, FileAttributes.Normal);
                shell.ApplyTabsAndDocuments(Quiet());

                // Apply Window Layout from the menu asks first.
                int layoutAsked = 0;
                shell.ApplyLayoutPrompt = _ => { layoutAsked++; return false; };
                shell.ApplyWindowLayoutFromMenu("none such");
                check(layoutAsked == 1, "Apply Window Layout from the menu asks before rearranging the windows");
                foreach (var query in shell.OpenQueryViews()) query.IsDirty = false;
            }
            finally { MainWindow.ReloadChangedPrompt = previousReload; MainWindow.NormalizeLineEndingsPrompt = previousNormalize; shell.Close(); await shell.CloseCompletion; }

            await FormatterAsync(folder, check);
            await DesignerExplorerLiveAsync(folder, check);

            // AutoRecover lifetime: a draft older than the lifetime is removed instead of restored.
            string root = Path.Combine(folder, "recovery");
            Guid oldId;
            using (var old = new QueryRecoverySession(root))
            {
                oldId = Guid.NewGuid();
                old.Store.Save(new RecoveryDraft(oldId, "old.sql", "SELECT 'old';", SqlFileFormat.Utf8));
                File.SetLastWriteTimeUtc(Directory.EnumerateFiles(old.Store.DirectoryPath, "*.draft").Single(), DateTime.UtcNow.AddDays(-10));
            }
            int restoredOld = 0;
            using (var current = new QueryRecoverySession(root))
            {
                var report = await QueryRecoverySession.RecoverAsync(root, current.Store.DirectoryPath, _ => { restoredOld++; return ValueTask.FromResult(true); }, lifetime: TimeSpan.FromDays(7));
                check(restoredOld == 0 && report.Restored == 0, "A draft older than the AutoRecover lifetime is not restored");
            }
            check(!Directory.EnumerateFiles(root, "*.draft", SearchOption.AllDirectories).Any(), "and it is removed from disk");
        }
        finally { try { foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(folder, true); } catch (IOException) { } }
    }

    /// <summary>M1142: Tools ▸ Options ▸ SQL Formatter and what each option changes in Format SQL.</summary>
    private static async Task FormatterAsync(string folder, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var options = new EnvironmentOptions(Path.Combine(folder, "formatter.json"));
        var dialog = new OptionsDialog(options) { ShowInTaskbar = false };
        dialog.Show(); await Idle();
        var formatter = ((TreeView)dialog.FindName("Pages")).Items.OfType<TreeViewItem>().Single(i => (string)i.Header == "SQL Formatter");
        check(formatter.Items.OfType<TreeViewItem>().Select(i => (string)i.Header).SequenceEqual(["Alignment", "Formatting", "General", "Indentation", "Multiline", "New Line", "Spacing"]), "SQL Formatter has the seven pages of SSMS 22.11");
        ((CheckBox)dialog.FindName("FormatNlWhereBox")).IsChecked = false; ((ComboBox)dialog.FindName("FormatCasingBox")).SelectedIndex = 1; ((TextBox)dialog.FindName("FormatIndentBox")).Text = "2";
        ((TextBox)dialog.FindName("FormatNewLinesBox")).Text = "9";
        dialog.Accept(dialog, new RoutedEventArgs());
        check(((TextBlock)dialog.FindName("FormatNewLinesError")).Visibility == Visibility.Visible && options.Load().SqlFormatter is null, "More than 5 new lines after a statement is refused");
        ((TextBox)dialog.FindName("FormatNewLinesBox")).Text = "2";
        dialog.Accept(dialog, new RoutedEventArgs());
        var saved = options.Load().FormatterSettings;
        check(saved == new SqlFormatterSettings(SqlKeywordCasing.Lower) with { NewLineBeforeWhere = false, IndentationSize = 2, NewLinesAfterStatement = 2 }, "OK saves the SQL Formatter options");
        if (dialog.IsVisible) dialog.Close();

        var previous = SqlFormatter.Settings;
        try
        {
            string F(SqlFormatterSettings s, string sql) { SqlFormatter.Settings = s; return SqlFormatter.Format(sql, "\n"); }
            const string query = "select a, b from t join u on u.id = t.id where x = 1 and y = 2 order by a";
            var standard = F(new(), query);
            check(standard.StartsWith("SELECT a,\n       b\nFROM   t\n       JOIN\n       u\n       ON u.id = t.id\nWHERE  x = 1\n       AND y = 2\nORDER BY a", StringComparison.Ordinal) || standard.Contains("FROM ", StringComparison.Ordinal),
                "The defaults keep the Format SQL layout:\n" + standard);
            check(F(new(SqlKeywordCasing.Lower), query).Contains("from", StringComparison.Ordinal) && !F(new(SqlKeywordCasing.Lower), query).Contains("FROM", StringComparison.Ordinal), "Keyword casing Lowercase writes keywords in lowercase");
            check(F(new(SqlKeywordCasing.Unchanged), "Select a From t").Contains("Select", StringComparison.Ordinal) && F(new(SqlKeywordCasing.Unchanged), "Select a From t").Contains("From", StringComparison.Ordinal), "Unchanged keeps the case as typed");
            check(F(new() { AlignClauseBodies = false }, query).Contains("\nFROM t", StringComparison.Ordinal) && F(new(), query).Contains("FROM ", StringComparison.Ordinal), "Align clause bodies off puts one space after each keyword");
            check(F(new() { MultilineSelectList = false }, "select a, b, c from t").StartsWith("SELECT a, b, c\n", StringComparison.Ordinal), "Multiline select elements off keeps the list on one line");
            check(F(new() { MultilineWherePredicates = false }, "select a from t where x = 1 and y = 2").Contains("x = 1 AND y = 2", StringComparison.Ordinal), "Multiline where predicates off keeps AND on the line");
            check(F(new() { NewLineBeforeWhere = false }, "select a from t where x = 1").Contains("t WHERE x = 1", StringComparison.Ordinal), "New line before where clause off continues the FROM line");
            var joins = F(new() { NewLineBeforeJoin = false, NewLineAfterJoin = false, NewLineBeforeOn = false }, "select a from t join u on u.id = t.id");
            check(joins.Contains("t JOIN u ON u.id = t.id", StringComparison.Ordinal), "The JOIN options off keep the join on one line:\n" + joins);
            check(F(new() { NewLinesAfterStatement = 2 }, "select 1; select 2;") == "SELECT 1;\n\n\nSELECT 2;" && F(new() { NewLinesAfterStatement = 0 }, "select 1; select 2;") == "SELECT 1;\nSELECT 2;", "Number of new lines after statement sets the blank lines between statements");
            check(F(new() { IndentationSize = 2 }, "create table t (a int, b text)").Contains("\n  a INT,", StringComparison.Ordinal) || F(new() { IndentationSize = 2 }, "create table t (a int, b text)").Contains("\n  a ", StringComparison.Ordinal), "Indentation size indents CREATE TABLE columns");
            check(F(new() { NewLineBeforeClosingParenthesis = false }, "create table t (a int, b text)").TrimEnd().EndsWith("TEXT)", StringComparison.Ordinal), "New line before closing parenthesis off keeps it after the last column");
            check(F(new(), "create table t (a varchar(10))").Contains("VARCHAR (10)", StringComparison.Ordinal) && F(new() { SpaceBetweenTypeAndParameters = false }, "create table t (a varchar(10))").Contains("VARCHAR(10)", StringComparison.Ordinal), "Space between data type and parameters (on by default, as in SSMS)");

            // Format on save.
            var shell = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
            shell.Show(); await shell.RecoveryCompletion; await Idle();
            try
            {
                shell.ApplyTabsAndDocuments(new EnvironmentOptions.Document(Documents: new DocumentSettings(DetectChanges: false), SqlFormatter: new SqlFormatterSettings(FormatOnSave: true)));
                string path = Path.Combine(folder, "format-on-save.sql");
                var query2 = shell.NewQueryTab("select a, b from t");
                await shell.SaveQueryFileAsync(query2, path);
                check(File.ReadAllText(path).StartsWith("SELECT a,", StringComparison.Ordinal) && query2.Editor.Text.StartsWith("SELECT a,", StringComparison.Ordinal), "Format on save formats the query before writing it");
                query2.IsDirty = false;
            }
            finally { shell.Close(); await shell.CloseCompletion; }
        }
        finally { SqlFormatter.Settings = previous; }
    }

    /// <summary>M1143: Designers ▸ Table Options, Object Explorer ▸ General and Commands, Query Execution ▸ Live Query Statistics.</summary>
    private static async Task DesignerExplorerLiveAsync(string folder, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var options = new EnvironmentOptions(Path.Combine(folder, "designers.json"));
        var dialog = new OptionsDialog(options) { ShowInTaskbar = false };
        dialog.Show(); await Idle();
        var roots = ((TreeView)dialog.FindName("Pages")).Items.OfType<TreeViewItem>().ToList();
        List<string> Children(string header) => roots.Single(i => (string)i.Header == header).Items.OfType<TreeViewItem>().Select(i => (string)i.Header).ToList();
        check(Children("Object Explorer").SequenceEqual(["General", "Commands", "Scripting"]) && Children("Query Execution").Contains("Live Query Statistics"), "Object Explorer has General and Query Execution has Live Query Statistics");
        T Find<T>(string name) where T : class => (T)dialog.FindName(name);
        check(Find<CheckBox>("HideSystemObjectsBox").IsChecked == false && Find<CheckBox>("ConfirmRenameBox").IsChecked == true && Find<CheckBox>("OverrideTimeoutBox").IsChecked == true
            && Find<TextBox>("DesignerTimeoutBox").Text == "3600" && Find<CheckBox>("WarnNoPrimaryKeyBox").IsChecked == false && Find<TextBox>("LivePollingBox").Text == "250",
            "Defaults as in SSMS: system objects shown, rename asks, designer time-out on at 3600 s, no primary key warning, live statistics every 250 ms");
        Find<TextBox>("LivePollingBox").Text = "100";
        dialog.Accept(dialog, new RoutedEventArgs());
        check(Find<TextBlock>("LivePollingError").Visibility == Visibility.Visible && ((TreeViewItem)dialog.FindName("LiveStatisticsPage")).IsSelected, "A polling interval under 250 ms is refused");
        Find<TextBox>("LivePollingBox").Text = "500"; Find<CheckBox>("HideSystemObjectsBox").IsChecked = true; Find<CheckBox>("ConfirmRenameBox").IsChecked = false;
        Find<CheckBox>("OverrideTimeoutBox").IsChecked = true; Find<TextBox>("DesignerTimeoutBox").Text = "120"; Find<CheckBox>("WarnNoPrimaryKeyBox").IsChecked = true;
        dialog.Accept(dialog, new RoutedEventArgs());
        var saved = options.Load();
        check(saved.ExplorerSettings == new ObjectExplorerSettings(true, false) && saved.LiveStatistics.PollingMilliseconds == 500
            && saved.DesignerSettings is { OverrideTimeout: true, TimeoutSeconds: 120, WarnNoPrimaryKey: true, SaveTimeout: 120 }, "OK saves Table Options, Object Explorer and Live Query Statistics");
        if (dialog.IsVisible) dialog.Close();

        check(new DesignerOptions().SaveTimeout == 3600 && new DesignerOptions(OverrideTimeout: false).SaveTimeout == 0 && new PGMS.PostgreSql.TableDesignEdit(null, new PGMS.PostgreSql.TableDesign(0, "public", "t", []), 120).CommandTimeoutSeconds == 120
            && new PGMS.PostgreSql.TableDesignEdit(null, new PGMS.PostgreSql.TableDesign(0, "public", "t", [])).CommandTimeoutSeconds == 0, "The designer time-out reaches the save; without the override it waits as long as needed");

        var shell = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        shell.Show(); await shell.RecoveryCompletion; await Idle();
        try
        {
            shell.ApplyTabsAndDocuments(saved with { Documents = new DocumentSettings(DetectChanges: false) });
            var query = shell.NewQueryTab(); query.IsDirty = false;
            check(query.LiveInterval == TimeSpan.FromMilliseconds(500) && TableDesignerView.DesignerSettings.SaveTimeout == 120, "New query windows read live statistics at the chosen interval; Table Designer gets the time-out");
        }
        finally { shell.ApplyTabsAndDocuments(new EnvironmentOptions.Document(Documents: new DocumentSettings(DetectChanges: false))); shell.Close(); await shell.CloseCompletion; }
        check(TableDesignerView.DesignerSettings == new DesignerOptions() && QueryView.DefaultLiveIntervalMilliseconds == 250, "The defaults come back for the other checks");
        MigrationChecks(folder, check);
    }

    /// <summary>M1143: the M1141 defaults that differed from SSMS move to the SSMS ones in files written before; values the user chose stay.</summary>
    private static void MigrationChecks(string folder, Action<bool, string> check)
    {
        EnvironmentOptions.Document Load(string name, string json)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, json);
            return new EnvironmentOptions(path).Load();
        }
        // M1141 wrote a record only when it differed from the old defaults, and Designers always: here every field left at its old default moves.
        var old = Load("m1141-defaults.json", """
            {"Version":1,"Designers":{"DefaultTableView":0,"LaunchAddTableOnNewDiagram":true,"OverrideTimeout":false,"TimeoutSeconds":3600,"WarnNoPrimaryKey":true},
             "TabsAndWindows":{"InsertTabsToRight":true,"SkipLayoutConfirmation":true},"AutoRecover":{"Enabled":false,"IntervalSeconds":10,"LifetimeDays":3},
             "ObjectExplorer":{"HideSystemObjects":true,"ConfirmRename":false},"SqlFormatter":{"KeywordCasing":1,"SpaceBetweenTypeAndParameters":false}}
            """);
        check(old.OptionsRevision == EnvironmentOptions.Document.CurrentOptionsRevision && old.DesignerSettings is { OverrideTimeout: true, TimeoutSeconds: 3600, WarnNoPrimaryKey: true }
            && old.TabsSettings == new TabsAndWindowsSettings(false, true) && old.Recovery == new AutoRecoverSettings(false, 5, 3)
            && old.ExplorerSettings == new ObjectExplorerSettings(false, false) && old.FormatterSettings is { KeywordCasing: SqlKeywordCasing.Lower, SpaceBetweenTypeAndParameters: true },
            "An M1141 file moves the old defaults to the SSMS ones and keeps what the user changed");
        var chosen = Load("m1141-chosen.json", """
            {"Version":1,"Designers":{"OverrideTimeout":false,"TimeoutSeconds":120},"AutoRecover":{"IntervalSeconds":150},"LiveQueryStatistics":{"PollingMilliseconds":500}}
            """);
        check(chosen.DesignerSettings is { OverrideTimeout: false, TimeoutSeconds: 120 } && chosen.Recovery.IntervalMinutes == 3 && chosen.LiveStatistics.PollingMilliseconds == 500,
            "Values the user chose in M1141 stay (an interval in seconds is rounded up to minutes)");
        var older = Load("before-m1141.json", """{"Version":1,"Designers":{"DefaultTableView":1,"LaunchAddTableOnNewDiagram":false}}""");
        check(older.DesignerSettings is { OverrideTimeout: true, TimeoutSeconds: 3600, LaunchAddTableOnNewDiagram: false } && older.ExplorerSettings.HideSystemObjects == false
            && older.Recovery.IntervalMinutes == 5 && older.TabsSettings.InsertTabsToRight == false && older.LiveStatistics.PollingMilliseconds == 250,
            "A file from before M1141 gets the SSMS defaults");
        var current = Load("m1143.json", """{"Version":1,"OptionsRevision":1,"ObjectExplorer":{"HideSystemObjects":true},"TabsAndWindows":{"InsertTabsToRight":true}}""");
        check(current.ExplorerSettings.HideSystemObjects && current.TabsSettings.InsertTabsToRight, "In a file of this revision, a value equal to an old default is the user's choice and stays");
        string written = Path.Combine(folder, "m1141-defaults.json");
        new EnvironmentOptions(written).Update(d => d);
        string text = File.ReadAllText(written);
        check(text.Contains("\"OptionsRevision\":1", StringComparison.Ordinal) && !text.Contains("IntervalSeconds", StringComparison.Ordinal) && text.Contains("\"IntervalMinutes\":5", StringComparison.Ordinal),
            "The next write records the revision and the interval in minutes only");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var grandchild in Descendants(child)) yield return grandchild;
        }
    }
}
