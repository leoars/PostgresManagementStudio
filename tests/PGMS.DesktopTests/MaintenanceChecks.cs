using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

/// <summary>PGMS-062 (M1089): Object Explorer ▸ Maintenance (VACUUM, ANALYZE, REFRESH MATERIALIZED VIEW) reviewed and opened in a query editor.</summary>
internal static class MaintenanceChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string schema = "mnt_" + Guid.NewGuid().ToString("N")[..8];
        string database = new NpgsqlConnectionStringBuilder(connection).Database!;
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        await Sql($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.t(id int PRIMARY KEY); INSERT INTO {schema}.t SELECT generate_series(1,50); CREATE MATERIALIZED VIEW {schema}.m AS SELECT id FROM {schema}.t; CREATE UNIQUE INDEX ON {schema}.m(id);");
        var window = new MainWindow { ShowInTaskbar = false, LocalServiceResolver = (_, _) => (null, LocalServiceState.Unknown) }; window.Show();
        var errors = new List<string>(); window.ShowErrorOverride = ex => errors.Add(ex.Message);
        try
        {
            check(MaintenanceCommands.Vacuum(null, null) == "VACUUM;" && MaintenanceCommands.Vacuum("s", "t", full: true, analyze: true, verbose: true) == "VACUUM (FULL, VERBOSE, ANALYZE) \"s\".\"t\";", "VACUUM with options in parentheses and the quoted, schema-qualified table");
            check(MaintenanceCommands.Analyze("s", "t", verbose: true) == "ANALYZE (VERBOSE) \"s\".\"t\";" && MaintenanceCommands.Refresh("s", "m", concurrently: true) == "REFRESH MATERIALIZED VIEW CONCURRENTLY \"s\".\"m\";" && MaintenanceCommands.Refresh("s", "m", withData: false) == "REFRESH MATERIALIZED VIEW \"s\".\"m\" WITH NO DATA;", "ANALYZE and REFRESH MATERIALIZED VIEW forms");
            bool refused = false; try { MaintenanceCommands.Refresh("s", "m", concurrently: true, withData: false); } catch (ArgumentException) { refused = true; }
            check(refused, "CONCURRENTLY with WITH NO DATA is refused, as PostgreSQL does");
            var failed = new List<string>();
            foreach (var sql in new[] { MaintenanceCommands.Vacuum(schema, "t", full: true, freeze: true, analyze: true, verbose: true), MaintenanceCommands.Analyze(schema, "t", true), MaintenanceCommands.Refresh(schema, "m", concurrently: true), MaintenanceCommands.Refresh(schema, "m", withData: false), MaintenanceCommands.Refresh(schema, "m"), MaintenanceCommands.Analyze(null, null) })
                try { await Sql(sql); } catch (PostgresException ex) { failed.Add(sql + " → " + ex.MessageText); }
            check(failed.Count == 0, "The generated statements run on PostgreSQL: " + string.Join(" | ", failed));

            var dialog = new MaintenanceDialog(MaintenanceKind.Vacuum, database, schema, "t") { ShowInTaskbar = false };
            check(dialog.Sql == $"VACUUM \"{schema}\".\"t\";" && dialog.CanAccept, "The dialog starts with plain VACUUM of the table");
            dialog.Full.IsChecked = true; dialog.AnalyzeToo.IsChecked = true;
            check(dialog.Sql == $"VACUUM (FULL, ANALYZE) \"{schema}\".\"t\";", "Options update the statement shown");
            var refresh = new MaintenanceDialog(MaintenanceKind.Refresh, database, schema, "m") { ShowInTaskbar = false };
            refresh.Concurrently.IsChecked = true; refresh.NoData.IsChecked = true;
            check(!refresh.CanAccept && refresh.Sql.Length == 0, "An invalid combination disables Open in Query Editor");

            await window.ConnectExplorerAsync(connection); await Idle();
            var catalog = new CatalogService(connection);
            MenuItem? Maintenance(CatalogEntry entry) { var menu = new ContextMenu(); window.FillExplorerMenu(menu, new CatalogNode(entry, catalog)); return menu.Items.OfType<MenuItem>().SingleOrDefault(i => i.Name == "ExplorerMaintenance"); }
            static string[] Items(MenuItem? m) => m?.Items.OfType<MenuItem>().Select(i => i.Name).ToArray() ?? [];
            check(Items(Maintenance(new CatalogEntry(CatalogKind.Relation, "t", database, schema, 1) { RelationType = "r" })).SequenceEqual(["ExplorerVacuum", "ExplorerAnalyze"]), "Tables have Maintenance ▸ Vacuum… and Analyze…");
            check(Items(Maintenance(new CatalogEntry(CatalogKind.Relation, "m", database, schema, 1) { RelationType = "m" })).SequenceEqual(["ExplorerVacuum", "ExplorerAnalyze", "ExplorerRefreshMatview"]), "Materialized views add Refresh Materialized View…");
            check(Items(Maintenance(new CatalogEntry(CatalogKind.Database, database, database, Oid: 5))).SequenceEqual(["ExplorerVacuum", "ExplorerAnalyze"]) && Maintenance(new CatalogEntry(CatalogKind.Relation, "v", database, schema, 1) { RelationType = "v" }) is null, "Databases have Vacuum and Analyze; plain views have no Maintenance");

            window.MaintenancePrompt = _ => false;
            int before = window.QueryDocuments.Count;
            check(await window.OpenMaintenanceAsync(new CatalogEntry(CatalogKind.Relation, "t", database, schema, 1) { RelationType = "r" }, MaintenanceKind.Vacuum) is null && window.QueryDocuments.Count == before, "Cancel opens nothing");
            window.MaintenancePrompt = d => { d.AnalyzeToo.IsChecked = true; return true; };
            var query = await window.OpenMaintenanceAsync(new CatalogEntry(CatalogKind.Relation, "t", database, schema, 1) { RelationType = "r" }, MaintenanceKind.Vacuum); await Idle();
            check(query is { IsConnected: true } && query.Editor.Text == $"VACUUM (ANALYZE) \"{schema}\".\"t\";", "Open in Query Editor opens the statement connected to the database, without running it: " + (query is null ? "no query" : query.IsConnected + " " + query.Editor.Text));
            await query!.ExecuteAsync(); await Idle();
            check(!query.Messages.Text.Contains("ERROR", StringComparison.OrdinalIgnoreCase), "Executing it in the editor works (VACUUM runs outside a transaction)");
            check(errors.Count == 0, "No errors: " + string.Join(" | ", errors));
        }
        finally
        {
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
