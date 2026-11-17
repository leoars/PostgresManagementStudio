using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

/// <summary>PGMS-060 (M1090): Extensions folder of a database with New Extension…, Update Extension… and Delete, in a database of its own.</summary>
internal static class ExtensionChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static async Task Until(Func<bool> done) { for (int i = 0; i < 200 && !done(); i++) { await Task.Delay(25); await Idle(); } }
        string database = "pgms_ext_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(NpgsqlConnection c, string sql) { await using var command = new NpgsqlCommand(sql, c); await command.ExecuteNonQueryAsync(); }
        await Sql(admin, $"CREATE DATABASE {database}");
        string target = new NpgsqlConnectionStringBuilder(connection) { Database = database, Pooling = false }.ConnectionString;
        var window = new MainWindow { ShowInTaskbar = false, LocalServiceResolver = (_, _) => (null, LocalServiceState.Unknown) }; window.Show();
        var errors = new List<string>(); window.ShowErrorOverride = ex => errors.Add(ex.Message);
        try
        {
            check(ExtensionEdits.CreateSql("pg_trgm") == "CREATE EXTENSION \"pg_trgm\";" && ExtensionEdits.CreateSql("pg_trgm", "my s", "1'5", cascade: true) == "CREATE EXTENSION \"pg_trgm\" SCHEMA \"my s\" VERSION '1''5' CASCADE;", "CREATE EXTENSION quotes the name and schema and makes the version a literal");
            check(ExtensionEdits.UpdateSql("pg_trgm", "1.6") == "ALTER EXTENSION \"pg_trgm\" UPDATE TO '1.6';" && ExtensionEdits.UpdateSql("pg_trgm") == "ALTER EXTENSION \"pg_trgm\" UPDATE;" && ExtensionEdits.DropSql("pg_trgm") == "DROP EXTENSION \"pg_trgm\";", "ALTER EXTENSION … UPDATE and DROP EXTENSION without CASCADE");
            var available = await ExtensionEdits.ListAvailableAsync(target);
            var trgm = available.FirstOrDefault(a => a.Name == "pg_trgm");
            check(trgm is { InstalledVersion: null, DefaultVersion: not null } && available.Any(a => a.Name == "earthdistance"), "The server lists its available extensions, none installed in a new database");
            var versions = await ExtensionEdits.VersionsAsync(target, "pg_trgm");
            check(versions.Count > 1 && versions[0] == trgm?.DefaultVersion && versions.Contains("1.5"), "Versions start with the default one: " + string.Join(",", versions));

            await window.ConnectExplorerAsync(connection); await Idle();
            var catalog = new CatalogService(connection);
            var databaseNode = new CatalogNode(new CatalogEntry(CatalogKind.Database, database, database, Oid: 1), catalog);
            await databaseNode.LoadAsync();
            var folder = databaseNode.Children.Single(n => n.Entry.Kind == CatalogKind.Extensions);
            // M1099 put Foreign Servers and Replication after it.
            check(databaseNode.Children.IndexOf(folder) == databaseNode.Children.IndexOf(databaseNode.Children.Single(n => n.Entry.Kind == CatalogKind.Schemas)) + 1 && folder.Entry.Name == "Extensions", "Extensions is a folder of the database, after Schemas");
            await folder.LoadAsync();
            bool hadPlpgsql = folder.Children.Any(n => n.Entry.Name == "plpgsql");
            check(hadPlpgsql && folder.Children.All(n => n.Entry.Kind == CatalogKind.Extension && !n.Entry.CanExpand), "The folder lists installed extensions (plpgsql) as leaves");

            string[] Items(CatalogNode node) { var menu = new ContextMenu(); window.FillExplorerMenu(menu, node); return menu.Items.OfType<MenuItem>().Select(i => i.Name).ToArray(); }
            check(Items(folder).Take(2).SequenceEqual(["ExplorerNewExtension", "ExplorerNewQuery"]), "The folder offers New Extension…");
            var plpgsql = folder.Children.First(n => n.Entry.Name == "plpgsql");
            var plMenu = Items(plpgsql);
            check(plMenu.Contains("ExplorerUpdateExtension") && plMenu.Contains("ExplorerDeleteExtension") && plMenu[0] == "ExplorerNewExtension", "An extension offers New, Update Extension… and Delete");

            // New Extension: pick pg_trgm 1.5 in a schema of its own, then OK runs it.
            await using (var db = new NpgsqlConnection(target)) { await db.OpenAsync(); await Sql(db, "CREATE SCHEMA ext"); }
            window.ExtensionPrompt = async d =>
            {
                await d.LoadAsync();
                check(d.Extension.Items.Cast<string>().Contains("pg_trgm") && !d.Extension.Items.Cast<string>().Contains("plpgsql"), "New Extension lists only extensions not yet installed");
                d.Extension.SelectedItem = "pg_trgm";
                await Until(() => d.Version.Items.Count > 1);
                check((string)d.Version.SelectedItem == ExtensionDialog.DefaultChoice && d.Sql == "CREATE EXTENSION \"pg_trgm\";", "The statement starts with the server defaults: " + d.Sql);
                d.Version.SelectedItem = "1.5"; d.Schema.Text = "ext"; await Idle();
                check(d.Sql == "CREATE EXTENSION \"pg_trgm\" SCHEMA \"ext\" VERSION '1.5';", "Schema and version update the statement: " + d.Sql);
                return await d.ApplyAsync();
            };
            var created = await window.ExtensionDialogAsync(folder, update: false);
            check(created is { WasApplied: true } && folder.Children.Any(n => n.Entry.Name == "pg_trgm"), "OK creates the extension and refreshes the folder: " + created?.Feedback);

            // Update Extension: 1.5 → newest target.
            var trgmNode = folder.Children.First(n => n.Entry.Name == "pg_trgm");
            string? updatedTo = null;
            window.ExtensionPrompt = async d =>
            {
                await d.LoadAsync();
                var targets = d.Version.Items.Cast<string>().ToArray();
                check(targets.Length > 0 && !targets.Contains("1.5") && d.Sql.StartsWith("ALTER EXTENSION \"pg_trgm\" UPDATE TO '", StringComparison.Ordinal), "Update lists the versions reachable from 1.5: " + string.Join(",", targets));
                updatedTo = (string)d.Version.SelectedItem;
                return await d.ApplyAsync();
            };
            var updated = await window.ExtensionDialogAsync(trgmNode, update: true);
            string version;
            await using (var db = new NpgsqlConnection(target)) { await db.OpenAsync(); await using var q = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname='pg_trgm'", db); version = (string)(await q.ExecuteScalarAsync())!; }
            check(updated is { WasApplied: true } && version == updatedTo, $"OK updates the extension ({version})");

            // Script: opens the statement in a connected query, nothing runs.
            window.ExtensionPrompt = async d =>
            {
                await d.LoadAsync(); d.Extension.SelectedItem = "earthdistance"; await Until(() => d.Version.Items.Count > 1);
                d.Cascade.IsChecked = true; await Idle();
                return d.Script();
            };
            var before = window.QueryDocuments.ToHashSet();
            await window.ExtensionDialogAsync(folder, update: false); await Idle();
            var query = window.QueryDocuments.FirstOrDefault(q => !before.Contains(q));
            check(query is { IsConnected: true } && query.Editor.Text == "CREATE EXTENSION \"earthdistance\" CASCADE;", "Script opens the statement connected to the database: " + query?.Editor.Text);
            await query!.ExecuteAsync(); await Idle();
            await folder.LoadAsync(refresh: true);
            check(folder.Children.Any(n => n.Entry.Name == "cube") && folder.Children.Any(n => n.Entry.Name == "earthdistance"), "Executing it there installs earthdistance with cube (CASCADE)");

            // Delete: No keeps it; a required extension is refused; Yes drops.
            string? asked = null;
            window.DeleteObjectConfirm = q => { asked = q; return false; };
            var cube = folder.Children.First(n => n.Entry.Name == "cube");
            check(!await window.DeleteExtensionAsync(cube) && asked?.Contains("DROP EXTENSION \"cube\";") == true, "Delete shows the exact statement; No deletes nothing");
            window.DeleteObjectConfirm = _ => true;
            check(!await window.DeleteExtensionAsync(cube) && errors.Count == 1 && errors[0].Contains("other objects depend on it"), "Deleting an extension another one requires is refused by the server, never CASCADE: " + string.Join(" | ", errors));
            errors.Clear();
            check(await window.DeleteExtensionAsync(folder.Children.First(n => n.Entry.Name == "earthdistance")) && !folder.Children.Any(n => n.Entry.Name == "earthdistance"), "Yes drops the extension and refreshes the folder");

            // A stale tree never touches another extension.
            var stale = new CatalogNode(new CatalogEntry(CatalogKind.Extension, "other", database, Oid: trgmNode.Entry.Oid), catalog);
            bool refused = false; try { await ExtensionEdits.DropAsync(target, stale.Entry.Oid, "other"); } catch (InvalidOperationException ex) { refused = ex.Message.Contains("now named pg_trgm", StringComparison.Ordinal); }
            check(refused, "An extension renamed or replaced since the tree loaded is refused by OID and name");
            check(errors.Count == 0, "No errors: " + string.Join(" | ", errors));
        }
        finally
        {
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
            NpgsqlConnection.ClearAllPools();
            await Sql(admin, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }
}
