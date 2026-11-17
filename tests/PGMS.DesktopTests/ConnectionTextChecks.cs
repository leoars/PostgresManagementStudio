using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AvalonDock.Layout;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;

/// <summary>PGMS-051 (M1094): connection texts as in SSMS 22.11 — server node with version and login, yellow bar segments, tab tooltip and window title.</summary>
internal static class ConnectionTextChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static async Task Until(Func<bool> done) { for (int i = 0; i < 600 && !done(); i++) { await Task.Delay(50); await Idle(); } }
        var builder = new NpgsqlConnectionStringBuilder(connection);
        string server = builder.Port == 5432 ? builder.Host! : $"{builder.Host}:{builder.Port}";
        check(MainWindow.ServerNodeText("Host=db;Port=5433;Username=ana", "17.11") == "db:5433 (PostgreSQL 17.11 - ana)" && MainWindow.ServerNodeText("Host=db;Port=5433;Username=ana", null) == "db:5433 (ana)",
            "The server node text carries the version and login, and falls back to the login alone");
        check(MainWindow.QueryTabToolTip("SQLQuery1.sql", null, "db.app (ana (42))") == "SQLQuery1.sql - db.app (ana (42))" && MainWindow.QueryTabToolTip("a.sql", @"C:\x\a.sql", null) == @"C:\x\a.sql",
            "The tab tooltip is the path or name, then the connection when connected");

        var window = new MainWindow { ShowInTaskbar = false, Width = 1200, Height = 850 }; window.Show();
        var errors = new List<string>(); window.ShowErrorOverride = ex => errors.Add(ex.Message);
        window.OpenTransactionPrompt = _ => MessageBoxResult.No;
        try
        {
            await Idle();
            string baseTitle = window.Title;
            var dock = (AvalonDock.DockingManager)window.FindName("DockManager");
            var tree = (TreeView)window.FindName("ExplorerTree");
            await window.ConnectExplorerAsync(connection);
            await Until(() => tree.Items.OfType<CatalogNode>().FirstOrDefault()?.Name.Contains("PostgreSQL", StringComparison.Ordinal) == true);
            var root = tree.Items.OfType<CatalogNode>().FirstOrDefault();
            await using (var probe = new NpgsqlConnection(connection)) { await probe.OpenAsync(); check(root?.Name == $"{builder.Host}:{builder.Port} (PostgreSQL {probe.ServerVersion} - {builder.Username})", "The Object Explorer server node reads host:port (PostgreSQL version - login): " + root?.Name); }
            check(root?.Entry.Name == $"{builder.Host}:{builder.Port} ({builder.Username})", "The node's entry keeps its name; only the label changed");

            var query = await window.OpenQueryTextAsync(connection, "SELECT g FROM generate_series(1, 3) AS g");
            await Idle();
            var segments = (StackPanel)query.FindName("ConnectionSegments");
            string Seg(string name) => ((TextBlock)query.FindName(name)).Text;
            int pid = query.ConnectionDetails!.BackendPid;
            check(segments.Visibility == Visibility.Visible && Seg("SegmentServer") == server && Seg("SegmentLogin") == $"{builder.Username} ({pid})" && Seg("SegmentDatabase") == builder.Database,
                "The yellow bar shows server, login (pid) and database on the right");
            check(Seg("SegmentElapsed") == "00:00:00" && Seg("SegmentRows") == "0 rows", "Before executing: 00:00:00 and 0 rows, as in SSMS");
            await query.ExecuteAsync();
            await Until(() => Seg("SegmentRows") == "3 rows");
            check(Seg("SegmentRows") == "3 rows" && Seg("SegmentElapsed").Length == 8, "After executing, the rows and elapsed time of the last execution");
            check(((TextBlock)query.FindName("QueryStatus")).Text.StartsWith("Query completed", StringComparison.Ordinal), "The status text on the left is unchanged");

            var document = dock.Layout.Descendents().OfType<LayoutDocument>().First(d => d.Content == query);
            string caption = $"{server}.{builder.Database} ({builder.Username} ({pid}))";
            check(query.ConnectionCaption == caption && document.ToolTip as string == $"{document.Title.TrimEnd('*')} - {caption}", "The tab tooltip reads name - server.database (login (pid)): " + document.ToolTip);
            check(!document.Title.Contains(caption, StringComparison.Ordinal), "The tab title stays the file name");
            check(window.Title == $"{document.Title} - {caption} - {baseTitle}", "The window title carries the document and its connection: " + window.Title);

            var empty = window.NewQueryTab("SELECT 1"); await Idle();
            var emptyDocument = dock.Layout.Descendents().OfType<LayoutDocument>().First(d => d.Content == empty);
            check(((StackPanel)empty.FindName("ConnectionSegments")).Visibility == Visibility.Collapsed && empty.ConnectionCaption is null, "A query without a connection hides the segments");
            check(window.Title == $"{emptyDocument.Title} - {baseTitle}" && emptyDocument.ToolTip as string == emptyDocument.Title.TrimEnd('*'), "Without a connection the window title and tooltip show only the document: " + window.Title);

            dock.Layout.Descendents().OfType<LayoutAnchorable>().First(a => a.ContentId == "explorer").IsActive = true; await Idle();
            check(window.Title == $"{emptyDocument.Title} - {baseTitle}", "Activating Object Explorer keeps the last document in the title");

            foreach (var q in window.QueryDocuments.ToArray()) { q.IsDirty = false; await window.CloseQueryAsync(q); }
            await Until(() => window.QueryDocuments.Count == 0);
            await Idle();
            check(window.Title == baseTitle, "With no document open the title is the application name: " + window.Title);
            check(errors.Count == 0, "No error was shown: " + string.Join(" | ", errors));
        }
        finally
        {
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
            NpgsqlConnection.ClearAllPools();
        }
    }
}
