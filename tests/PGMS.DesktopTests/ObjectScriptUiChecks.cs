using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

/// <summary>PGMS-026 (M1054): Script … as in the Object Explorer menus.</summary>
internal static class ObjectScriptUiChecks
{
    internal static async Task RunAsync(MainWindow window, string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string schema = "qsui_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        await Sql($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.t(id int PRIMARY KEY, v text);");
        uint oid; { await using var command = new NpgsqlCommand($"SELECT '{schema}.t'::regclass::oid", admin); oid = (uint)(await command.ExecuteScalarAsync())!; }
        try
        {
            string database = new NpgsqlConnectionStringBuilder(connection).Database!;
            var table = new CatalogEntry(CatalogKind.Relation, "t", database, schema, oid) { RelationType = "r" };
            var catalog = new CatalogService(connection);
            MenuItem[] Fill(CatalogEntry entry) { var menu = new ContextMenu(); window.FillExplorerMenu(menu, new CatalogNode(entry, catalog)); return menu.Items.OfType<MenuItem>().ToArray(); }
            static MenuItem Sub(MenuItem parent, string name) => parent.Items.OfType<MenuItem>().Single(i => i.Name == name);

            var items = Fill(table);
            int position = Array.FindIndex(items, i => i.Name == "ExplorerScriptAs");
            check(position == Array.FindIndex(items, i => i.Name == "ExplorerEditTop") + 1 && (string)items[position].Header == "Script Table as", "Table: Script Table as comes right after Edit Top, as in SSMS");
            var script = items[position];
            check(script.Items.OfType<MenuItem>().Select(i => (string)i.Header).SequenceEqual(["CREATE To", "ALTER To", "CREATE OR REPLACE To", "DROP To", "DROP And CREATE To", "SELECT To", "INSERT To", "UPDATE To", "DELETE To", "EXECUTE To"]) && script.Items.OfType<Separator>().Count() == 2, "Every verb is listed in the SSMS order, in two groups after the first");
            check(script.Items.OfType<MenuItem>().Where(i => i.IsEnabled).Select(i => (string)i.Header).SequenceEqual(["CREATE To", "DROP To", "DROP And CREATE To", "SELECT To", "INSERT To", "UPDATE To", "DELETE To"]), "On a table the unavailable verbs are gray, as observed");
            var create = Sub(script, "ExplorerScriptCreate");
            check(create.Items.OfType<MenuItem>().Select(i => (string)i.Header).SequenceEqual(["New Query Editor Window", "File ...", "Clipboard"]), "Each verb goes to a new query, a file or the clipboard; Agent Job does not apply");
            check(Sub(script, "ExplorerScriptAlter").Items.Count == 0 && Sub(script, "ExplorerScriptExecute").Items.Count == 0, "Gray verbs have no destinations");

            var query = await window.ScriptFromExplorerAsync(table, ScriptVerb.Select, ScriptDestination.NewQuery);
            await Idle();
            check(query is not null && !query.IsDirty && query.Editor.Text.Contains($"FROM \"{schema}\".\"t\"", StringComparison.Ordinal) && ((TabControl)query.FindName("ResultSets")).Items.Count == 0, "New Query Editor Window opens the script unchanged and does not run it");
            check(query is not null && new NpgsqlConnectionStringBuilder(query.ConnectionString!).Database == database, "The new query is connected to the object's database");

            string? copied = null;
            window.ScriptClipboard = text => copied = text;
            await window.ScriptFromExplorerAsync(table, ScriptVerb.Delete, ScriptDestination.Clipboard);
            check(copied?.Contains("DELETE FROM", StringComparison.Ordinal) == true, "Clipboard receives the script");

            string path = Path.Combine(Path.GetTempPath(), "pgms_script_" + Guid.NewGuid().ToString("N") + ".sql"), suggested = "";
            window.ScriptFilePicker = name => { suggested = name; return path; };
            try
            {
                await window.ScriptFromExplorerAsync(table, ScriptVerb.Create, ScriptDestination.File);
                check(suggested == schema + ".t.sql" && File.ReadAllText(path).Contains("CREATE TABLE", StringComparison.Ordinal), "File suggests schema.object.sql and saves the script");
                File.Delete(path);
                window.ScriptFilePicker = _ => null;
                await window.ScriptFromExplorerAsync(table, ScriptVerb.Create, ScriptDestination.File);
                check(!File.Exists(path), "Cancelling the file dialog saves nothing");
            }
            finally { File.Delete(path); }

            { await using var command = new NpgsqlCommand($"SELECT count(*) FROM {schema}.t", admin); check((long)(await command.ExecuteScalarAsync())! == 0, "Scripting changes nothing"); }

            check(Fill(new CatalogEntry(CatalogKind.Index, "i", database, schema, 1)).Any(i => (string)i.Header == "Script Index as"), "Index nodes have Script Index as");
            check(Fill(new CatalogEntry(CatalogKind.Schema, schema, database, schema, 1)).Any(i => (string)i.Header == "Script Schema as"), "Schema nodes have Script Schema as");
            check(Fill(new CatalogEntry(CatalogKind.Routine, "p()", database, schema, 1) { RoutineKind = "p" }).Any(i => (string)i.Header == "Script Procedure as"), "Procedures have Script Procedure as");
            foreach (var (kind, noun) in new[] { (CatalogKind.Key, "Key"), (CatalogKind.Constraint, "Constraint"), (CatalogKind.Trigger, "Trigger") })
                check(Fill(new CatalogEntry(kind, "x", database, schema, 1)).Any(i => (string)i.Header == $"Script {noun} as"), $"{noun} nodes have Script {noun} as");
            var indexMenu = Fill(new CatalogEntry(CatalogKind.Index, "i", database, schema, 1));
            check(Array.FindIndex(indexMenu, i => i.Name == "ExplorerRebuildIndex") > Array.FindIndex(indexMenu, i => i.Name == "ExplorerScriptAs"), "Rebuild follows Script Index as");
            check(Fill(new CatalogEntry(CatalogKind.Indexes, "Indexes", database, schema, oid)).Any(i => i.Name == "ExplorerRebuildAll"), "The Indexes folder has Rebuild All");
            string rebuild = MainWindow.RebuildAllSql("a b", "t\"x");
            check(rebuild.EndsWith("\r\nREINDEX TABLE \"a b\".\"t\"\"x\";\r\n", StringComparison.Ordinal) && rebuild.Contains("CONCURRENTLY", StringComparison.Ordinal), "Rebuild All opens a quoted REINDEX and shows the concurrent form");

            // Tasks ▸ Take Offline / Bring Online
            MenuItem Tasks(CatalogEntry entry) => Fill(entry).Single(i => i.Name == "ExplorerDatabaseTasks");
            string victim = "qsoff_" + Guid.NewGuid().ToString("N")[..8];
            await Sql($"CREATE DATABASE {victim} TEMPLATE template0");
            try
            {
                var online = new CatalogEntry(CatalogKind.Database, victim, victim, null, 1);
                var offline = online with { Name = victim + " (Offline)", IsOffline = true };
                check(Sub(Tasks(online), "ExplorerTakeOffline").IsEnabled && !Sub(Tasks(online), "ExplorerBringOnline").IsEnabled, "An online database can go offline");
                check(!Sub(Tasks(new CatalogEntry(CatalogKind.Database, database, database, null, 1)), "ExplorerTakeOffline").IsEnabled, "The connected database cannot");
                var offlineMenu = Fill(offline);
                check(Sub(Tasks(offline), "ExplorerBringOnline").IsEnabled && offlineMenu.All(i => i.Name is not ("ExplorerNewQuery" or "ExplorerScriptAs" or "ExplorerObjectProperties")), "An offline database only offers Bring Online and Refresh");
                async Task<bool> Allowed() { await using var command = new NpgsqlCommand($"SELECT datallowconn FROM pg_database WHERE datname='{victim}'", admin); return (bool)(await command.ExecuteScalarAsync())!; }
                string? asked = null;
                window.ConfirmDatabaseOffline = text => { asked = text; return false; };
                await window.SetDatabaseOnlineAsync(online, false);
                check(asked?.Contains("already connected keep running", StringComparison.Ordinal) == true && await Allowed(), "Take Offline asks first, says sessions keep running, and No changes nothing");
                window.ConfirmDatabaseOffline = _ => true;
                await window.SetDatabaseOnlineAsync(online, false);
                check(!await Allowed(), "Yes takes it offline");
                await window.SetDatabaseOnlineAsync(offline, true);
                check(await Allowed(), "Bring Online reverses it, without asking");
            }
            finally { window.ConfirmDatabaseOffline = null; await Sql($"DROP DATABASE IF EXISTS {victim} WITH (FORCE)"); }

            check(Fill(new CatalogEntry(CatalogKind.Role, "pg_monitor", null, null, 1) { RoleName = "pg_monitor" }).All(i => i.Name != "ExplorerScriptAs") && Fill(new CatalogEntry(CatalogKind.Tables, "Tables", database, schema, 0)).All(i => i.Name != "ExplorerScriptAs"), "Predefined roles and folders have none");
        }
        finally { await Sql($"DROP SCHEMA {schema} CASCADE"); }
    }
}
