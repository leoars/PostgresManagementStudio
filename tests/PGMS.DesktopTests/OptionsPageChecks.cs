using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>PGMS-073 (M1095): Tools ▸ Options ▸ Object Explorer ▸ Scripting, Designers ▸ Table and Database Designers and Connection Dialog.</summary>
internal static class OptionsPageChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string directory = Path.Combine(Path.GetTempPath(), "pgms-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string schema = "opt_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        await Sql($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.t (id integer PRIMARY KEY, nome text);");
        string defaultPath = EnvironmentOptions.DefaultPath;
        string? savedDefault = File.Exists(defaultPath) ? File.ReadAllText(defaultPath) : null;
        try
        {
            var store = new EnvironmentOptions(Path.Combine(directory, "environment.json"));
            var fresh = store.Load();
            check(fresh.ScriptingSettings == new ScriptingOptions(true, true, true) && fresh.DesignerSettings == new DesignerOptions(DiagramTableView.ColumnNames, true) && fresh.AutoSelectRecentConnection,
                "Defaults keep PGMS's scripts (headers, database comment, IF EXISTS), Column Names, Add Table on new diagram and the most recent connection");
            File.WriteAllText(store.FilePath, JsonSerializer.Serialize(fresh with { Designers = new DesignerOptions((DiagramTableView)42, false) }));
            check(store.Load().DesignerSettings == new DesignerOptions(DiagramTableView.ColumnNames, false), "An unknown table view in the file falls back to Column Names");
            File.Delete(store.FilePath);

            var dialog = new OptionsDialog(store) { ShowInTaskbar = false }; dialog.Show(); await Idle();
            var tree = (TreeView)dialog.FindName("Pages");
            var headers = tree.Items.OfType<TreeViewItem>().Select(i => (string)i.Header).ToList();
            check(headers.SequenceEqual(["Environment", "Connection Dialog", "Designers", "Query Execution", "Query Results", "Object Explorer"]), "Option pages in SSMS order: " + string.Join(", ", headers));
            void Show(string page) { ((TreeViewItem)dialog.FindName(page)).IsSelected = true; }
            bool Visible(string panel) => ((FrameworkElement)dialog.FindName(panel)).Visibility == Visibility.Visible;
            Show("ScriptingPage"); await Idle();
            check(Visible("ScriptingPanel") && !Visible("CommandsPanel") && !Visible("GeneralPanel"), "Object Explorer ▸ Scripting shows its page alone");
            var headersBox = (CheckBox)dialog.FindName("ScriptHeadersBox"); var databaseBox = (CheckBox)dialog.FindName("ScriptDatabaseBox"); var existenceBox = (CheckBox)dialog.FindName("ScriptExistenceBox");
            check(headersBox.IsChecked == true && databaseBox.IsChecked == true && existenceBox.IsChecked == true, "The scripting boxes start checked");
            Show("DesignersPage"); await Idle();
            var viewBox = (ComboBox)dialog.FindName("DefaultTableViewBox"); var addTableBox = (CheckBox)dialog.FindName("LaunchAddTableBox");
            check(Visible("DesignersPanel") && (string)((ComboBoxItem)viewBox.SelectedItem).Content == "Column Names" && addTableBox.IsChecked == true && viewBox.Items.Count == 5, "Designers shows Default table view (Column Names of five) and Launch Add Table");
            Show("ConnectionDialogPage"); await Idle();
            var recentBox = (CheckBox)dialog.FindName("SelectRecentBox");
            check(Visible("ConnectionDialogPanel") && recentBox.IsChecked == true, "Connection Dialog shows Automatically select the most recent connection, checked");

            headersBox.IsChecked = false; databaseBox.IsChecked = false; existenceBox.IsChecked = false;
            viewBox.SelectedItem = viewBox.Items.OfType<ComboBoxItem>().Single(i => (string)i.Content == "Keys");
            addTableBox.IsChecked = false; recentBox.IsChecked = false;
            dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
            var saved = store.Load();
            check(saved.ScriptingSettings == new ScriptingOptions(false, false, false) && saved.DesignerSettings == new DesignerOptions(DiagramTableView.Keys, false) && !saved.AutoSelectRecentConnection, "OK saves the three pages");
            var again = new OptionsDialog(store) { ShowInTaskbar = false }; again.Show(); await Idle();
            check(((CheckBox)again.FindName("ScriptHeadersBox")).IsChecked == false && (string)((ComboBoxItem)((ComboBox)again.FindName("DefaultTableViewBox")).SelectedItem).Content == "Keys" && ((CheckBox)again.FindName("SelectRecentBox")).IsChecked == false,
                "Opening Options again shows the saved values");
            again.Close();

            // Script as follows the scripting options.
            var catalog = new CatalogService(connection);
            uint oid = (uint)(await new NpgsqlCommand($"SELECT '{schema}.t'::regclass::oid", admin).ExecuteScalarAsync())!;
            var entry = new CatalogEntry(CatalogKind.Relation, "t", builderDatabase(connection), schema, oid) { RelationType = "r" };
            string standard = await new ObjectScriptService(connection).ScriptAsync(entry, ScriptVerb.Drop);
            string plain = await new ObjectScriptService(connection, new ScriptingOptions(false, false, false)).ScriptAsync(entry, ScriptVerb.Drop);
            check(standard.Contains("/****** Object:", StringComparison.Ordinal) && standard.StartsWith("-- Database:", StringComparison.Ordinal) && standard.Contains($"DROP TABLE IF EXISTS \"{schema}\".\"t\";", StringComparison.Ordinal), "By default DROP has the database comment, the header and IF EXISTS: " + standard);
            check(plain.Trim() == $"DROP TABLE \"{schema}\".\"t\";", "With the three options off, DROP is the bare statement: " + plain.Trim());
            string create = await new ObjectScriptService(connection, new ScriptingOptions(false, true, true)).ScriptAsync(entry, ScriptVerb.DropAndCreate);
            check(!create.Contains("/******", StringComparison.Ordinal) && create.Contains("DROP TABLE IF EXISTS", StringComparison.Ordinal) && create.Contains("CREATE TABLE", StringComparison.Ordinal), "Headers off leaves DROP and CREATE without the Object comment");

            // Default table view on tables a diagram adds.
            var diagram = new DatabaseDiagramView(connection, null, "Options") { DefaultTableView = DiagramTableView.Keys };
            var host = new Window { ShowInTaskbar = false, Content = diagram, Width = 600, Height = 400 }; host.Show(); await Idle();
            try
            {
                var candidates = await diagram.CandidateTablesAsync();
                await diagram.AddTablesAsync(candidates.Where(c => c.Schema == schema && c.Name == "t").ToArray());
                check(diagram.Tables.Count == 1 && diagram.Tables[0].View == DiagramTableView.Keys, "A table added to a diagram takes the default table view");
            }
            catch (Exception ex) { check(false, "A table added to a diagram takes the default table view: " + ex.Message); }
            finally { host.Close(); }

            // Connection Dialog: the most recent profile is selected only while the option is on.
            string profiles = Path.Combine(directory, "connections.json");
            var profileStore = new ConnectionProfileStore(profiles);
            var profile = new ConnectionProfile(Guid.NewGuid(), "Recent", "recent.example", 5432, "postgres", "app", "Prefer");
            profileStore.Save(profile);
            File.WriteAllText(Path.Combine(directory, "connect-dialog.json"), JsonSerializer.Serialize(new ConnectionDialogPreferences.Document(LastProfile: profile.Id)));
            async Task<object?> Selected()
            {
                var opened = new ConnectionDialog(null, null, profiles) { ShowInTaskbar = false, Opacity = 0 }; opened.Show(); await Idle();
                var item = ((ComboBox)opened.FindName("Profiles")).SelectedItem; opened.Close(); return item;
            }
            var global = new EnvironmentOptions(defaultPath);
            global.Update(d => d with { AutoSelectRecentConnection = true });
            check(await Selected() is ConnectionProfile { Name: "Recent" }, "With the option on, Connect opens on the most recent profile");
            global.Update(d => d with { AutoSelectRecentConnection = false });
            check(await Selected() is null, "With the option off, Connect opens with no profile selected");
        }
        finally
        {
            if (savedDefault is null) File.Delete(defaultPath); else File.WriteAllText(defaultPath, savedDefault);
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static string builderDatabase(string connection) => new NpgsqlConnectionStringBuilder(connection).Database!;
}
