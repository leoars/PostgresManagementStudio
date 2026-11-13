using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

internal static class ExplorerMenuChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var window = new MainWindow { ShowInTaskbar = false, LocalServiceResolver = (_, _) => (null, LocalServiceState.Unknown) }; window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);
            var tree = (TreeView)window.FindName("ExplorerTree");
            var catalog = new CatalogService(connection);
            async Task<List<object>> MenuFor(CatalogEntry entry)
            {
                tree.Items.Clear(); var node = new CatalogNode(entry, catalog); tree.Items.Add(node); node.IsSelected = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var menu = tree.ContextMenu; menu.PlacementTarget = tree; menu.IsOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var items = menu.Items.Cast<object>().ToList(); menu.IsOpen = false; return items;
            }
            static List<string> Names(List<object> items) => items.Select(i => i is MenuItem m ? m.Name : "-").ToList();

            var server = await MenuFor(new CatalogEntry(CatalogKind.Server, "server"));
            var serverNames = Names(server);
            check(serverNames.SequenceEqual(["ExplorerConnect", "ExplorerDisconnect", "ExplorerRegister", "-", "ExplorerNewQuery", "-", "ExplorerActivityMonitor", "-", "ExplorerStartService", "ExplorerStopService", "ExplorerReloadConfiguration", "ExplorerRestartService", "-", "ExplorerServerTls", "-", "ExplorerStartPsql", "-", "ExplorerReports", "-", "ExplorerRefresh", "ExplorerServerProperties"]),
                "Server menu follows the SSMS order with PostgreSQL equivalents: " + string.Join(",", serverNames));
            check(server.OfType<MenuItem>().Single(m => m.Name == "ExplorerServerProperties").FontWeight == FontWeights.Bold && !serverNames.Contains("ExplorerTableProperties") && !serverNames.Contains("ExplorerNewDatabase"),
                "Server menu shows Properties as the default action and no table or database items");

            var database = await MenuFor(new CatalogEntry(CatalogKind.Database, "postgres", "postgres", Oid: 5));
            var tasks = database.OfType<MenuItem>().Single(m => m.Name == "ExplorerDatabaseTasks");
            check(tasks.Items.OfType<MenuItem>().Select(m => m.Name).SequenceEqual(["ExplorerTakeOffline", "ExplorerBringOnline", "ExplorerBackupDatabase", "ExplorerRestoreDatabase", "ExplorerDatabaseOperations"]) && Names(database).Contains("ExplorerObjectProperties"), "Database menu groups Take Offline, Bring Online, Back Up and Restore under Tasks, as in SSMS");
            check(Names(await MenuFor(new CatalogEntry(CatalogKind.Databases, "Databases"))).SequenceEqual(["ExplorerNewDatabase", "-", "ExplorerRestoreDatabase", "-", "ExplorerStartPsql", "-", "ExplorerRefresh"]), "Databases folder menu matches SSMS minus SQL Server-only items");
            var predefined = Names(await MenuFor(new CatalogEntry(CatalogKind.Role, "pg_monitor") { RoleName = "pg_monitor" }));
            check(predefined.SequenceEqual(["ExplorerStartPsql", "-", "ExplorerRefresh"]), "Predefined pg_ roles, like SSMS fixed server roles, offer no editing");
            var role = Names(await MenuFor(new CatalogEntry(CatalogKind.Role, "app (login)") { RoleName = "app" }));
            check(role.Contains("ExplorerRenameRole") && role.Contains("ExplorerDropRole") && role[^1] == "ExplorerRoleProperties", "Login role menu has Rename, Delete and Properties last");
            check(Names(await MenuFor(new CatalogEntry(CatalogKind.Columns, "Columns"))).SequenceEqual(["ExplorerNewQuery", "-", "ExplorerStartPsql", "-", "ExplorerRefresh"]), "Folders without actions keep New Query, Start psql and Refresh");
            check(!Names(await MenuFor(new CatalogEntry(CatalogKind.Tables, "Tables", "postgres", "public"))).Contains("ExplorerServerTls"), "Server-only items never appear on other nodes");
        }
        finally { window.Close(); }

        var dialog = new ServerPropertiesDialog(connection) { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            dialog.Show();
            for (int i = 0; i < 200 && dialog.Snapshot is null; i++) await Task.Delay(50);
            var snapshot = dialog.Snapshot!;
            string Value(string name) => snapshot.Properties.Single(p => p.Name == name).Value;
            await using (var direct = new NpgsqlConnection(connection))
            {
                await direct.OpenAsync();
                await using var version = new NpgsqlCommand("SHOW server_version", direct);
                check(Value("Version") == (string?)await version.ExecuteScalarAsync(), "Server Properties shows the server version");
            }
            var mode = new NpgsqlConnectionStringBuilder(connection).SslMode;
            string tls = Value("This connection uses TLS");
            bool tlsMatches = mode is SslMode.Disable ? tls == "False" : mode is SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull ? tls == "True" : tls is "True" or "False";
            check(Value("Product") == "PostgreSQL" && Value("Platform") == "Windows" && Value("Superuser") == "True" && tlsMatches && snapshot.Settings.Any(s => s.Name == "max_connections"),
                $"Server Properties reads general, security and settings pages (TLS {tls} with {mode})");
            var properties = (DataGrid)dialog.FindName("Properties");
            check(properties.Items.Count == snapshot.Properties.Count(p => p.Page == "General"), "Server Properties opens on the General page");
            ((ListBox)dialog.FindName("Pages")).SelectedItem = "Settings";
            ((TextBox)dialog.FindName("Filter")).Text = "shared_preload";
            var settings = (DataGrid)dialog.FindName("Settings");
            check(settings.Visibility == Visibility.Visible && settings.Items.Count == 1 && ((ServerSetting)settings.Items[0]).Value.Contains("pg_stat_statements", StringComparison.Ordinal), "Settings page filters pg_settings by name");
        }
        finally { dialog.Close(); }

        var pending = await new ServerPropertiesService(connection).ReloadAsync();
        check(pending is not null, "Reload Configuration signals the server and reports settings waiting for a restart");

        string withPassword = new NpgsqlConnectionStringBuilder(connection) { Password = "psql-secret-71", SslMode = SslMode.Require, RootCertificate = "C:\\certs\\root.crt" }.ConnectionString;
        Environment.SetEnvironmentVariable("PGSERVICE", "leaky-service");
        try
        {
            var start = PsqlLauncher.CreateStartInfo(withPassword, "C:\\fake\\psql.exe");
            var c = new NpgsqlConnectionStringBuilder(connection);
            check(start.ArgumentList.SequenceEqual(["-h", c.Host!, "-p", c.Port.ToString(), "-U", c.Username!, "-d", c.Database!]) && !start.ArgumentList.Any(a => a.Contains("psql-secret", StringComparison.Ordinal)), "psql gets host, port, user and database on the command line, never the password");
            check(start.Environment["PGPASSWORD"] == "psql-secret-71" && start.Environment["PGSSLMODE"] == "require" && start.Environment["PGSSLROOTCERT"] == "C:\\certs\\root.crt" && !start.Environment.ContainsKey("PGSERVICE"), "psql receives the password, TLS mode and root certificate by environment, without inherited PG variables");
        }
        finally { Environment.SetEnvironmentVariable("PGSERVICE", null); }
    }
}
