using AvalonDock.Layout;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;

/// <summary>PGMS-068 (M1084): New ▸ Database Engine Query, Open ▸ File… with the current connection, File Disconnected… and Disconnect All Queries.</summary>
internal static class QueryConnectionChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string root = Path.Combine(Path.GetTempPath(), "pgms-query-connection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string fileA = Path.Combine(root, "a.sql"), fileB = Path.Combine(root, "b.sql"), fileC = Path.Combine(root, "c.sql");
        File.WriteAllText(fileA, "select 'a';"); File.WriteAllText(fileB, "select 'b';"); File.WriteAllText(fileC, "select 'c';");
        string schema = "qcon_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<bool> Exists(string table) { await using var command = new NpgsqlCommand($"SELECT to_regclass('{schema}.{table}') IS NOT NULL", admin); return (bool)(await command.ExecuteScalarAsync())!; }
        await Sql($"CREATE SCHEMA {schema}");
        var window = new MainWindow { ShowInTaskbar = false, Width = 1200, Height = 850 }; window.Show();
        var prompts = new List<bool>(); var answer = MessageBoxResult.Cancel;
        window.OpenTransactionPrompt = aborted => { prompts.Add(aborted); return answer; };
        var errors = new List<string>(); window.ShowErrorOverride = ex => errors.Add(ex.Message);
        try
        {
            await Idle();
            var file = (MenuItem)window.FindName("FileMenu");
            var fileItems = file.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            var newItems = ((MenuItem)window.FindName("NewMenu")).Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            check(fileItems[0] == "_New" && newItems.SequenceEqual(["_Query with Current Connection", "_Database Engine Query", "_File…"]) && ((MenuItem)window.FindName("NewQueryCurrentMenu")).InputGestureText == "Ctrl+N", "File ▸ New lists Query with Current Connection (Ctrl+N) and Database Engine Query, as in SSMS");
            var openItems = ((MenuItem)window.FindName("OpenMenu")).Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            check(openItems.SequenceEqual(["_File…", "File with New _Connection…", "File _Disconnected…"]), "File ▸ Open lists File, File with New Connection and File Disconnected, in the SSMS order");
            var connectionItems = ((MenuItem)window.FindName("ConnectionMenu")).Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            check(connectionItems.SequenceEqual(["_Connect…", "_Disconnect", "Disconnect _All Queries", "Change C_onnection…"]), "Query ▸ Connection lists Disconnect All Queries after Disconnect, as in SSMS");

            // New ▸ Database Engine Query
            int before = window.QueryDocuments.Count();
            window.ConnectionPicker = () => null;
            check(await window.NewDatabaseEngineQueryAsync() is null && window.QueryDocuments.Count() == before, "Database Engine Query: Cancel in the Connect dialog opens nothing");
            window.ConnectionPicker = () => connection;
            var engine = await window.NewDatabaseEngineQueryAsync(); await Idle();
            check(engine is { IsConnected: true } && window.QueryDocuments.Count() == before + 1, "Database Engine Query opens a tab connected to the chosen server");

            // Open ▸ File… uses the current connection; File Disconnected… never connects
            window.ConnectionPicker = () => throw new InvalidOperationException("Open ▸ File… must not ask for a connection");
            await window.OpenFilesAsync([fileA], connect: true); await Idle();
            var a = window.QueryDocuments.Single(q => q.FilePath == fileA);
            check(a.IsConnected && a.Editor.Text == "select 'a';", "Open ▸ File… connects the new tab with the current connection, without asking and without executing");
            await window.OpenFilesAsync([fileB, fileC], connect: false); await Idle();
            check(window.QueryDocuments.Where(q => q.FilePath == fileB || q.FilePath == fileC).All(q => !q.HasSession), "File Disconnected… opens the files without a connection");
            var dock = (AvalonDock.DockingManager)window.FindName("DockManager");
            var aDocument = dock.Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>().Single(d => ReferenceEquals(d.Content, a));
            aDocument.IsSelected = true; aDocument.IsActive = true; await Idle();
            await window.OpenFilesAsync([fileC], connect: true); await Idle();
            check(!window.QueryDocuments.Single(q => q.FilePath == fileC).HasSession, "A file already open is only activated; its connection is not changed");

            // Disconnect All Queries
            var connectionMenu = (MenuItem)window.FindName("ConnectionMenu");
            connectionMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent)); await Idle();
            check(((MenuItem)window.FindName("DisconnectAllQueriesMenu")).IsEnabled, "Disconnect All Queries is offered while a tab is connected");
            a.Editor.Text = $"BEGIN; CREATE TABLE {schema}.pending(id int)"; await a.ExecuteAsync(); await Idle();
            answer = MessageBoxResult.Cancel; prompts.Clear();
            int done = await window.DisconnectAllQueriesAsync(); await Idle();
            check(prompts.Count == 1 && a.IsConnected && window.QueryDocuments.Count(q => q.HasSession) >= 1, "Cancel at the open-transaction question stops Disconnect All Queries and keeps that connection");
            answer = MessageBoxResult.No;
            done = await window.DisconnectAllQueriesAsync(); await Idle();
            check(done >= 1 && window.QueryDocuments.All(q => !q.HasSession) && !await Exists("pending"), "No rolls back the open transaction and every query tab is disconnected");
            check(((TextBlock)window.FindName("Status")).Text.StartsWith("Disconnected ", StringComparison.Ordinal), "The status bar says how many were disconnected");
            connectionMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent)); await Idle();
            check(!((MenuItem)window.FindName("DisconnectAllQueriesMenu")).IsEnabled, "Without connected tabs Disconnect All Queries is unavailable");
            check(errors.Count == 0, "No errors: " + string.Join(" | ", errors));
        }
        finally
        {
            window.OpenTransactionPrompt = _ => MessageBoxResult.No;
            foreach (var tab in window.QueryDocuments.ToArray()) tab.IsDirty = false;
            window.Close();
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
