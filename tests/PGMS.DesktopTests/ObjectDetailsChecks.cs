using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

/// <summary>PGMS-052 (M1085): Object Explorer Details (F7) follows the Object Explorer selection and lists what a node contains.</summary>
internal static class ObjectDetailsChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string schema = "oed_" + Guid.NewGuid().ToString("N")[..8];
        string database = new NpgsqlConnectionStringBuilder(connection).Database!;
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        await Sql($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.alpha(id int, label text); CREATE TABLE {schema}.beta(id int); CREATE VIEW {schema}.gamma AS SELECT 1 AS one;");
        var window = new MainWindow { ShowInTaskbar = false, LocalServiceResolver = (_, _) => (null, LocalServiceState.Unknown) }; window.Show();
        try
        {
            var viewMenu = ((Menu)((System.Windows.FrameworkElement)window.FindName("FileMenu")).Parent).Items.OfType<MenuItem>().Single(m => (string)m.Header == "_View");
            var names = viewMenu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            check(names.IndexOf("Object Explorer _Details") == names.IndexOf("Object _Explorer") + 1 && ((MenuItem)window.FindName("ObjectExplorerDetailsMenu")).InputGestureText == "F7", "View lists Object Explorer Details (F7) after Object Explorer, as in SSMS");
            var empty = window.ShowObjectExplorerDetails(); await Idle();
            check(empty.Current is null && empty.Rows.Count == 0 && empty.PathText.StartsWith("Object Explorer is not connected", StringComparison.Ordinal), "Without a connection the tab opens and says Object Explorer is not connected");
            await window.ConnectExplorerAsync(connection); await Idle();
            var tree = (TreeView)window.FindName("ExplorerTree");
            var server = (CatalogNode)tree.Items[0];
            server.IsSelected = true; await Idle(); await Idle();
            var details = window.ShowObjectExplorerDetails();
            check(ReferenceEquals(details, empty) && window.QueryDocuments.Count == 0, "F7 again activates the same tab");
            await details.ShowAsync(server);
            check(ReferenceEquals(details.Current, server) && details.Rows.Any(r => r.Name == "Databases" && r.Type == "Folder"), "The server lists its folders");
            async Task Open(string name)
            {
                var row = details.Rows.SingleOrDefault(r => r.Name == name) ?? throw new InvalidOperationException($"No row {name} in {details.PathText}: " + string.Join(", ", details.Rows.Select(r => r.Name)));
                await details.OpenAsync(row); await Idle();
                for (int i = 0; i < 200 && (!ReferenceEquals(details.Current, row.Node) || details.IsLoading); i++) await Task.Delay(25);
                await Idle();
            }
            await Open("Databases"); check(details.Rows.Any(r => r.Name == database && r.Type == "Database"), "Opening Databases lists the databases");
            await Open(database); await Open("Schemas"); await Open(schema); await Open("Tables");
            check(details.Rows.Select(r => r.Name).SequenceEqual(["alpha", "beta"]) && details.Rows.All(r => r.Type == "Table" && r.Schema == schema && r.Owner.Length > 0), "Tables lists the tables with type, schema and owner");
            check(details.PathText.EndsWith($"{database} › Schemas › {schema} › Tables", StringComparison.Ordinal) && details.CountText == "2 item(s)", "The path and the item count are shown");
            details.Search.Text = "ALP";
            check(details.Rows.Select(r => r.Name).SequenceEqual(["alpha"]) && details.CountText == "1 of 2 item(s)", "Search filters by name without case");
            details.Search.Text = "";
            await details.UpAsync(); await Idle();
            check(details.Current?.Entry.Kind == CatalogKind.Schema && (details.Grid.SelectedItem as ObjectExplorerDetailsView.Row)?.Name == "Tables" && details.Rows.Any(r => r.Name == "Views"), "Up shows the containing node with the folder just left selected");
            await Open("Views"); check(details.Rows.Single().Type == "View", "Views lists the view as View");
            await details.UpAsync(); await Open("Tables"); await Open("alpha"); await Open("Columns");
            var column = details.Rows.Single(r => r.Name.StartsWith("label", StringComparison.Ordinal)).Node;
            await details.ShowAsync(column);
            check(details.Current?.Entry.Kind == CatalogKind.Columns && (details.Grid.SelectedItem as ObjectExplorerDetailsView.Row)?.Node == column, "Selecting an item that contains nothing lists its folder with the item selected");
            var tablesNode = details.Current!.Parent!.Parent!;
            await Sql($"CREATE TABLE {schema}.delta(id int)");
            await details.ShowAsync(tablesNode); await details.RefreshAsync();
            check(details.Rows.Select(r => r.Name).SequenceEqual(["alpha", "beta", "delta"]), "Refresh reads the list from the server again");
            server.IsSelected = false; server.IsSelected = true; await Idle(); await Idle();
            check(ReferenceEquals(details.Current, server), "Selecting a node in Object Explorer moves the tab to it");
            check(ObjectExplorerDetailsView.TypeOf(new CatalogEntry(CatalogKind.Relation, "m") { RelationType = "m" }) == "Materialized View" && ObjectExplorerDetailsView.TypeOf(new CatalogEntry(CatalogKind.Routine, "p") { RoutineKind = "p" }) == "Procedure" && ObjectExplorerDetailsView.TypeOf(new CatalogEntry(CatalogKind.DatabaseDiagram, "d")) == "Database Diagram", "Types use Object Explorer words");
        }
        finally
        {
            window.Close();
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

}
