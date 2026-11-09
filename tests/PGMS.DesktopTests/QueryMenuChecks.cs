using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;

/// <summary>PGMS-034: Connection submenu, Open Server in Object Explorer, Parse, Client Statistics and the SSMS shortcuts.</summary>
internal static class QueryMenuChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static T Named<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name);
        string schema = "qmen_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<bool> Exists(string table) { await using var command = new NpgsqlCommand($"SELECT to_regclass('{schema}.{table}') IS NOT NULL", admin); return (bool)(await command.ExecuteScalarAsync())!; }
        await Sql($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.t(id int); INSERT INTO {schema}.t VALUES (1),(2);");
        var window = new MainWindow { ShowInTaskbar = false, Width = 1200, Height = 850 }; window.Show();
        var prompts = new List<bool>(); var answer = MessageBoxResult.Cancel;
        window.OpenTransactionPrompt = aborted => { prompts.Add(aborted); return answer; };
        try
        {
            await Idle();
            string builderDatabase = new NpgsqlConnectionStringBuilder(connection).Database!;
            check(MainWindow.ShortcutFor(Key.System, Key.X, ModifierKeys.Alt) == MainWindow.QueryMenuShortcut.Execute, "Alt+X executes");
            check(MainWindow.ShortcutFor(Key.System, Key.Pause, ModifierKeys.Alt) == MainWindow.QueryMenuShortcut.Cancel, "Alt+Break cancels");
            check(MainWindow.ShortcutFor(Key.F5, Key.None, ModifierKeys.Control) == MainWindow.QueryMenuShortcut.Parse, "Ctrl+F5 parses");
            check(MainWindow.ShortcutFor(Key.F5, Key.None, ModifierKeys.None) == MainWindow.QueryMenuShortcut.None, "F5 alone is still Execute's own path");
            check(MainWindow.ShortcutFor(Key.System, Key.F8, ModifierKeys.Alt) == MainWindow.QueryMenuShortcut.OpenServer, "Alt+F8 opens the server");
            check(MainWindow.ShortcutFor(Key.System, Key.S, ModifierKeys.Alt | ModifierKeys.Shift) == MainWindow.QueryMenuShortcut.ClientStatistics, "Shift+Alt+S toggles client statistics");
            check(MainWindow.ShortcutFor(Key.X, Key.None, ModifierKeys.None) == MainWindow.QueryMenuShortcut.None, "A letter without Alt is typing");
            check(Named<MenuItem>(window, "ExecuteMenu").InputGestureText == "Alt+X" && Named<MenuItem>(window, "CancelMenu").InputGestureText == "Alt+Break" && Named<MenuItem>(window, "ParseMenu").InputGestureText == "Ctrl+F5" && Named<MenuItem>(window, "OpenServerMenu").InputGestureText == "Alt+F8" && Named<MenuItem>(window, "ClientStatisticsMenu").InputGestureText == "Shift+Alt+S", "The Query menu shows the shortcuts");

            var query = await window.OpenQueryTextAsync(connection, $"CREATE TABLE {schema}.parsed(id int)"); await Idle();
            var messages = Named<TextBox>(query, "Messages"); var outputs = Named<TabControl>(query, "OutputTabs"); var messagesTab = Named<TabItem>(query, "MessagesTab");

            // Parse
            await query.ParseAsync(); await Idle();
            check(messages.Text == "Commands completed successfully." && outputs.SelectedItem == messagesTab && !await Exists("parsed"), "Parse reports success without running the SQL");
            query.Editor.Text = "SELEC 1"; await query.ParseAsync(); await Idle();
            check(messages.Text.Contains("42601", StringComparison.Ordinal) && Named<Button>(query, "ErrorLocation").IsEnabled, "Parse shows the error and offers Go to Error");
            query.ResultsDestination = ResultsDestination.Text;
            query.Editor.Text = "SELECT 1"; await query.ExecuteAsync(); await Idle();
            check(messagesTab.Visibility != Visibility.Visible, "Results to Text hides Messages");
            await query.ParseAsync(); await Idle();
            check(messagesTab.Visibility == Visibility.Visible && outputs.SelectedItem == messagesTab && messages.Text == "Commands completed successfully.", "Parse shows its outcome in Results to Text mode too");
            query.ResultsDestination = ResultsDestination.Grid;
            query.Editor.Text = "SELECT $1"; await query.ParseAsync(); await Idle();
            check(messages.Text.StartsWith("Not parsed", StringComparison.Ordinal) && query.IsConnected, "A bare parameter is refused before reaching the server");
            query.Editor.Text = "BEGIN"; await query.ExecuteAsync(); await Idle();
            query.Editor.Text = "SELEC 1"; await query.ParseAsync(); await Idle();
            query.Editor.Text = "SELECT 1"; await query.ExecuteAsync(); await Idle();
            check(messages.Text.Contains("Query completed", StringComparison.Ordinal), "A parse error inside a transaction leaves it usable");
            query.Editor.Text = "ROLLBACK"; await query.ExecuteAsync(); await Idle();

            // Client Statistics
            var statsTab = Named<TabItem>(query, "ClientStatisticsTab"); var statsGrid = Named<DataGrid>(query, "ClientStatisticsGrid");
            check(statsTab.Visibility != Visibility.Visible && !query.IncludeClientStatistics, "Client Statistics starts off and hidden");
            window.ToggleClientStatistics(); await Idle();
            check(query.IncludeClientStatistics && statsTab.Visibility == Visibility.Visible && Named<MenuItem>(window, "ClientStatisticsMenu").IsChecked, "Include Client Statistics turns on for the tab");
            query.Editor.Text = $"SELECT id FROM {schema}.t; UPDATE {schema}.t SET id = id"; await query.ExecuteAsync(); await Idle();
            string Cell(string label, int column) => ((string[])statsGrid.Items.Cast<object>().Single(r => ((string[])r)[0].Trim() == label))[column];
            check(query.ClientStatistics.Count == 1 && Convert.ToString(statsGrid.Columns[1].Header) == "Trial 1" && Cell("Rows returned by SELECT statements", 1) == "2" && Cell("Rows affected by INSERT, DELETE, or UPDATE statements", 1) == "2", "A grid execution adds a trial with its counts");
            query.ResultsDestination = ResultsDestination.Text;
            query.Editor.Text = $"SELECT id FROM {schema}.t WHERE id = 1"; await query.ExecuteAsync(); await Idle();
            check(Convert.ToString(statsGrid.Columns[1].Header) == "Trial 2" && Cell("Rows returned by SELECT statements", 1) == "1 ↓", "A text execution adds a trial too, compared with the previous one");
            query.ResultsDestination = ResultsDestination.Grid;
            query.Editor.Text = "SELECT 1/0"; await query.ExecuteAsync(); await Idle();
            check(query.ClientStatistics.Count == 2, "A failed execution adds no trial");
            query.ResetClientStatistics(); await Idle();
            check(query.ClientStatistics.Count == 0 && statsTab.Visibility == Visibility.Visible && statsGrid.Columns.Count == 2, "Reset clears the trials and keeps the tab while the option is on");
            window.ToggleClientStatistics(); await Idle();
            check(statsTab.Visibility != Visibility.Visible, "Turning it off with no trials hides the tab");

            // IntelliSense
            query.ResultsDestination = ResultsDestination.Grid;
            check(query.IntelliSenseEnabled && Named<MenuItem>(window, "IntelliSenseMenu").IsChecked && Named<MenuItem>(window, "IntelliSenseMenu").InputGestureText == "Ctrl+B, Ctrl+I" && Named<MenuItem>(window, "ListMembersMenu").InputGestureText == "Ctrl+J" && Named<MenuItem>(window, "CompleteWordMenu").InputGestureText == "Ctrl+Space" && Named<MenuItem>(window, "RefreshCacheMenu").InputGestureText == "Ctrl+Shift+R", "IntelliSense starts on and its menu items show the SSMS shortcuts");
            void Put(string text, string marker = "|") { int at = text.IndexOf(marker, StringComparison.Ordinal); query.Editor.Text = text.Replace(marker, "", StringComparison.Ordinal); query.Editor.CaretOffset = at; }
            IEnumerable<SqlCompletionData> Listed() => query.OpenCompletionWindow!.CompletionList.CompletionData.Cast<SqlCompletionData>();
            async Task Open() { await query.ShowCompletionAsync(); await Idle(); }
            async Task Dismiss() { query.OpenCompletionWindow?.Close(); await Idle(); }
            Put($"SELECT * FROM {schema}.|"); await Open();
            check(query.OpenCompletionWindow is not null && Listed().Any(d => d.Text == "t" && (string?)d.Description == $"table {builderDatabase}.{schema}.t"), "Ctrl+Space after a schema lists its tables, with the tip beside the list");
            query.OpenCompletionWindow!.CompletionList.SelectItem("t");
            query.OpenCompletionWindow.CompletionList.RequestInsertion(EventArgs.Empty); await Idle();
            check(query.Editor.Text == $"SELECT * FROM {schema}.t" && query.OpenCompletionWindow is null, "Accepting an item puts it in the text and closes the list");
            Put($"SELECT x.| FROM {schema}.t x"); await Open();
            check(Listed().Select(d => d.Text).SequenceEqual(["id"]) && (string?)Listed().Single().Description == "column id(integer, null)", "A dot after an alias lists the columns of its table");
            await Dismiss();
            Put($"SELECT x.i| FROM {schema}.t x"); await Open();
            check(query.OpenCompletionWindow!.CompletionList.SelectedItem is SqlCompletionData { Text: "id" }, "What was already typed selects the match");
            query.OpenCompletionWindow.CompletionList.RequestInsertion(EventArgs.Empty); await Idle();
            check(query.Editor.Text == $"SELECT x.id FROM {schema}.t x", "Accepting replaces what was typed");
            bool Press(Key key)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(query.Editor.TextArea)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                query.Editor.TextArea.RaiseEvent(args);
                return args.Handled;
            }
            Put($"SELECT x.| FROM {schema}.t x"); await Open();
            check(!Press(Key.Enter) && query.OpenCompletionWindow is null && query.Editor.Text == $"SELECT x. FROM {schema}.t x", "Enter with nothing typed closes the list and leaves the key to the editor");
            Put($"SELECT x.i| FROM {schema}.t x"); await Open();
            check(Press(Key.Tab) && query.Editor.Text == $"SELECT x.id FROM {schema}.t x", "Tab accepts the match");
            Put($"SELECT x.| FROM {schema}.t x"); await Open();
            check(Press(Key.Escape) && query.OpenCompletionWindow is null && query.Editor.Text == $"SELECT x. FROM {schema}.t x", "Esc closes the list and keeps the text");
            Put($"SELECT * FROM {schema}.|"); await Open();
            query.Editor.TextArea.PerformTextInput("t");
            await Idle();
            check(query.OpenCompletionWindow is not null && query.Editor.Text == $"SELECT * FROM {schema}.t", "Typing a letter goes on filtering");
            query.Editor.TextArea.PerformTextInput(" ");
            await Idle();
            check(query.OpenCompletionWindow is null && query.Editor.Text == $"SELECT * FROM {schema}.t ", "A space closes the list without inserting");
            Put($"SELECT * FROM {schema}|");
            query.Editor.TextArea.PerformTextInput(".");
            for (int i = 0; i < 100 && query.OpenCompletionWindow is null; i++) await Task.Delay(20);
            check(query.OpenCompletionWindow is not null && Listed().Any(d => d.Text == "t"), "Typing a dot opens the list by itself");
            await Dismiss();
            Put("SELECT 'a|b'"); await Open();
            check(query.OpenCompletionWindow is null, "Inside a string nothing is offered");
            await Sql($"CREATE TABLE {schema}.created_later(id int)");
            Put($"SELECT * FROM {schema}.|"); await Open();
            check(!Listed().Any(d => d.Text == "created_later"), "The list comes from the cache, without reading the server each time");
            await Dismiss();
            query.RefreshLocalCache();
            await Open();
            check(Listed().Any(d => d.Text == "created_later"), "Refresh Local Cache reads the catalog again");
            await Dismiss();
            await Sql($"CREATE TABLE {schema}.pai_x(id int PRIMARY KEY); CREATE TABLE {schema}.filho_x(id int, pai_x_id int REFERENCES {schema}.pai_x(id))");
            query.RefreshLocalCache();
            Put($"SELECT * FROM {schema}.pai_x p JOIN {schema}.fil|"); await Open();
            check(query.OpenCompletionWindow!.CompletionList.SelectedItem is SqlCompletionData { Kind: PGMS.PostgreSql.SqlCompletionKind.Join, Text: "filho_x fx ON fx.pai_x_id = p.id" }, "Typing after JOIN selects the whole join rather than the plain table");
            check(Press(Key.Tab) && query.Editor.Text == $"SELECT * FROM {schema}.pai_x p JOIN {schema}.filho_x fx ON fx.pai_x_id = p.id", "Tab writes the whole join");
            Put($"SELECT * FROM {schema}.filho_x f JOIN {schema}.pai_x p ON |"); await Open();
            check(Listed().First() is { Kind: PGMS.PostgreSql.SqlCompletionKind.Join, Text: "p.id = f.pai_x_id" }, "After ON the condition from the key is first");
            await Dismiss();
            await Sql($"CREATE TABLE {schema}.\"Usuários\"(id int)");
            query.RefreshLocalCache();
            Put("Select * from usuar|"); await Open();
            check(query.OpenCompletionWindow!.CompletionList.SelectedItem is SqlCompletionData { Content: var found } && (string)found == $"{schema}.\"Usuários\"", "Typing a table's name without its schema or accents finds it");
            check(Press(Key.Tab) && query.Editor.Text == $"Select * from {schema}.\"Usuários\"", "Tab writes the table with its schema");
            check(!query.ProcessIntelliSenseKey(Key.I, ModifierKeys.Control, 100) && query.ProcessIntelliSenseKey(Key.B, ModifierKeys.Control, 100) && query.ProcessIntelliSenseKey(Key.I, ModifierKeys.Control, 200) && !query.IntelliSenseEnabled, "Ctrl+B, Ctrl+I turns IntelliSense off and Ctrl+I alone does not");
            await Open();
            check(query.OpenCompletionWindow is null, "With IntelliSense off nothing opens");
            query.ProcessIntelliSenseKey(Key.B, ModifierKeys.Control, 300);
            check(!query.ProcessIntelliSenseKey(Key.I, ModifierKeys.Control, 3000) && !query.IntelliSenseEnabled, "The chord expires");
            query.IntelliSenseEnabled = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            // Connection
            var connectionMenu = Named<MenuItem>(window, "ConnectionMenu");
            connectionMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent)); await Idle();
            check(!Named<MenuItem>(window, "QueryConnectMenu").IsEnabled && Named<MenuItem>(window, "QueryDisconnectMenu").IsEnabled && Named<MenuItem>(window, "QueryChangeConnectionMenu").IsEnabled, "A connected tab offers Disconnect and Change Connection");
            query.Editor.Text = $"BEGIN; CREATE TABLE {schema}.disc(id int)"; await query.ExecuteAsync(); await Idle();
            answer = MessageBoxResult.Cancel;
            await window.DisconnectQueryAsync(query); await Idle();
            check(query.IsConnected && prompts.SequenceEqual([false]), "Cancel at the open-transaction question keeps the connection");
            answer = MessageBoxResult.Yes;
            await window.DisconnectQueryAsync(query); await Idle();
            check(!query.HasSession && query.Database == "Not connected" && query.Editor.Text.Contains("disc", StringComparison.Ordinal) && await Exists("disc"), "Yes commits, disconnects and keeps the text");
            connectionMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent)); await Idle();
            check(Named<MenuItem>(window, "QueryConnectMenu").IsEnabled && !Named<MenuItem>(window, "QueryDisconnectMenu").IsEnabled, "A disconnected tab offers Connect");
            window.ConnectionPicker = () => null;
            await window.ConnectActiveQueryAsync(); await Idle();
            check(!query.HasSession, "Cancelling the dialog connects nothing");
            window.ConnectionPicker = () => connection;
            await window.ConnectActiveQueryAsync(); await Idle();
            check(query.IsConnected && ((TreeView)window.FindName("ExplorerTree")).Items.Count == 0, "Connect connects only this tab, not Object Explorer");

            var builder = new NpgsqlConnectionStringBuilder(connection);
            string otherDatabase = builder.Database == "postgres" ? "template1" : "postgres";
            window.ConnectionPicker = () => new NpgsqlConnectionStringBuilder(connection) { Database = otherDatabase }.ConnectionString;
            await window.ChangeQueryConnectionAsync(query); await Idle();
            check(query.Database == otherDatabase, "Change Connection moves the tab");
            window.ConnectionPicker = () => new NpgsqlConnectionStringBuilder(connection) { Database = "pgms_missing_" + schema }.ConnectionString;
            window.ShowErrorOverride = _ => { };
            await window.ChangeQueryConnectionAsync(query); await Idle();
            check(query.IsConnected && query.Database == otherDatabase, "A connection that fails leaves the session as it was");
            window.ConnectionPicker = () => connection;
            query.Editor.Text = "BEGIN"; await query.ExecuteAsync(); await Idle();
            string? warning = null;
            window.DatabaseSwitchConfirm = text => { warning = text; return false; };
            await window.ChangeQueryConnectionAsync(query); await Idle();
            check(warning?.Contains("open transaction", StringComparison.Ordinal) == true && warning.Contains("Change connection?", StringComparison.Ordinal) && query.Database == otherDatabase, "An open transaction asks first; No keeps the connection");
            window.DatabaseSwitchConfirm = _ => true;
            await window.ChangeQueryConnectionAsync(query); await Idle();
            check(query.Database == builder.Database, "Yes changes the connection");

            // Open Server in Object Explorer
            var tree = (TreeView)window.FindName("ExplorerTree");
            await window.OpenServerInExplorerAsync(); await Idle();
            check(tree.Items.Count == 1, "Open Server in Object Explorer connects it to the tab's server");
            var root = tree.Items[0];
            await window.OpenServerInExplorerAsync(); await Idle();
            check(ReferenceEquals(tree.Items[0], root), "On the same server Object Explorer is only shown, not reloaded");
        }
        finally
        {
            window.OpenTransactionPrompt = _ => MessageBoxResult.No;
            foreach (var tab in window.QueryDocuments.ToArray()) tab.IsDirty = false;
            window.Close();
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
