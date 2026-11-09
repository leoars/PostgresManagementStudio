using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

internal static class ReportViewChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);
            var tree = (TreeView)window.FindName("ExplorerTree");
            var catalog = new CatalogService(connection);
            string database = new NpgsqlConnectionStringBuilder(connection).Database!;
            async Task<MenuItem> ReportsMenu(CatalogEntry entry)
            {
                tree.Items.Clear(); var node = new CatalogNode(entry, catalog); tree.Items.Add(node); node.IsSelected = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var menu = tree.ContextMenu; menu.PlacementTarget = tree; menu.IsOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var reports = menu.Items.OfType<MenuItem>().Single(m => m.Name == "ExplorerReports"); menu.IsOpen = false;
                return (MenuItem)reports.Items.Cast<object>().Single();
            }
            static async Task<ReportView> Loaded(ReportView view)
            {
                for (int i = 0; i < 200 && (view.Report is null || ((Button)view.FindName("RefreshButton")).IsEnabled == false); i++) await Task.Delay(50);
                return view;
            }

            var serverStandard = await ReportsMenu(new CatalogEntry(CatalogKind.Server, "server"));
            check(serverStandard.Name == "ExplorerStandardReports" && serverStandard.Items.OfType<MenuItem>().Select(m => m.Name).SequenceEqual(ServerReportService.ServerReports.Select(d => "ExplorerReport" + d.Kind)),
                "Server menu has Reports ▸ Standard Reports with the server reports");
            var databaseStandard = await ReportsMenu(new CatalogEntry(CatalogKind.Database, database, database, Oid: 5));
            check(databaseStandard.Items.OfType<MenuItem>().Select(m => m.Name).SequenceEqual(ServerReportService.DatabaseReports.Select(d => "ExplorerReport" + d.Kind)), "Database menu has Reports ▸ Standard Reports with the database reports");

            // Disk Usage lists user tables in its second section: a clean cluster has none, so the test brings its own.
            await using var fixture = new NpgsqlConnection(connection);
            await fixture.OpenAsync();
            async Task FixtureSql(string sql) { await using var command = new NpgsqlCommand(sql, fixture); await command.ExecuteNonQueryAsync(); }
            await FixtureSql("CREATE TABLE public.report_view_fixture(id int); INSERT INTO public.report_view_fixture SELECT generate_series(1, 100)");
            // Choosing a report from the database menu opens it as a document tab for that database.
            databaseStandard.Items.OfType<MenuItem>().Single(m => m.Name == "ExplorerReport" + ServerReportKind.DiskUsage).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var disk = await Loaded(window.Reports.Single());
            var c = new NpgsqlConnectionStringBuilder(connection);
            check(disk.Definition.Kind == ServerReportKind.DiskUsage && disk.Title == $"Disk Usage - {database} ({c.Host}:{c.Port})" && disk.Report?.Tables.Count == 2, $"Disk Usage opens as a document tab titled {disk.Title}");
            var grids = ((StackPanel)disk.FindName("Sections")).Children.OfType<DataGrid>().ToList();
            var sizeColumn = grids[0].Columns[1];
            check(grids.Count == 2 && (string)sizeColumn.Header == "Size (MB)" && ((DataView)grids[0].ItemsSource).Table!.Columns[1].DataType == typeof(decimal) && sizeColumn.SortMemberPath == "c1",
                "Report grids keep headers such as \"Size (MB)\" and numeric column types for sorting");
            await FixtureSql("DROP TABLE public.report_view_fixture");

            // The same report for the same target reuses its tab; a server report opens another.
            check(ReferenceEquals(window.OpenReport(catalog.ConnectionFor(database)!, ServerReportKind.DiskUsage), disk) && window.Reports.Count == 1, "Opening the same report again activates its tab");
            var blocking = await Loaded(window.OpenReport(connection, ServerReportKind.AllBlockingTransactions));
            check(window.Reports.Count == 2 && blocking.Title == $"Activity - All Blocking Transactions - {c.Host}:{c.Port}", "A server report opens in its own tab, titled with the server");
            var sections = ((StackPanel)blocking.FindName("Sections")).Children;
            check(sections.OfType<TextBlock>().Any(t => t.Text == "No rows.") && !sections.OfType<DataGrid>().Any(), "An empty report table says No rows instead of an empty grid");

            // F5 reads the active report again; Ctrl+F4 closes it.
            var first = blocking.Report;
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.F5) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Loaded(blocking);
            for (int i = 0; i < 100 && ReferenceEquals(blocking.Report, first); i++) await Task.Delay(50);
            check(!ReferenceEquals(blocking.Report, first), "F5 reads the active report again");
            ApplicationCommands.Close.Execute(null, window);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(window.Reports.Count == 1 && window.Reports.Single() == disk, "Ctrl+F4 closes the active report tab");
        }
        finally { window.Close(); }
    }
}
