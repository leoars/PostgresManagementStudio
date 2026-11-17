using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

/// <summary>PGMS-061 (M1088): New Function / Stored Procedure / Trigger open templates; Enable and Disable triggers; the built-in templates run on PostgreSQL.</summary>
internal static class RoutineTemplateChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string schema = "rtp_" + Guid.NewGuid().ToString("N")[..8];
        string database = new NpgsqlConnectionStringBuilder(connection).Database!;
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        await Sql($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.child(id int, updated_at timestamptz);");
        var window = new MainWindow { ShowInTaskbar = false, LocalServiceResolver = (_, _) => (null, LocalServiceState.Unknown) }; window.Show();
        var errors = new List<string>(); window.ShowErrorOverride = ex => errors.Add(ex.Message);
        try
        {
            check(SqlTemplateParameters.WithDefaults("<schema_name, name, public>.<t,, x> <schema_name,name,public>", new Dictionary<string, string> { ["schema_name"] = "sales" }) == "<schema_name, name, sales>.<t,, x> <schema_name,name, sales>", "Template defaults change only the value of the named parameters");
            check(SqlTemplateParameters.WithDefaults("<a,, b>", new Dictionary<string, string> { ["a"] = "x>y" }) == "<a,, b>", "A value that would break the parameter is not used");

            // The built-in templates run on PostgreSQL with their defaults
            var templates = SqlTemplateCatalog.BuiltInTemplates;
            string Filled(string name, Dictionary<string, string>? extra = null)
            {
                var text = templates.Single(t => t.Name == name).Text;
                var values = SqlTemplateParameters.Parse(text).ToDictionary(p => p.Name, p => p.Value);
                values["schema_name"] = schema; values["source_schema"] = schema;
                foreach (var (k, v) in extra ?? []) values[k] = v;
                return SqlTemplateParameters.Replace(text, values);
            }
            var runs = new (string Name, Dictionary<string, string>? Extra)[]
            {
                ("Create Table", null), ("Add Column", null), ("Drop Column", null), ("Add Check Constraint", new() { ["column_name"] = "id" }), ("Create View", null), ("Create Materialized View", null), ("Refresh Materialized View", null),
                ("Create Index", new() { ["columns"] = "name" }), ("Create Unique Index", new() { ["columns"] = "id" }), ("Create Sequence", null), ("Create Enum", null), ("Create Composite Type", null), ("Create Domain", null),
                ("Create SQL Function", new() { ["function_name"] = "sample_sql" }), ("Create PL/pgSQL Function", null), ("Create Procedure", null), ("Create Trigger", new() { ["table_name"] = "child" }),
                ("Rename Table", null), ("Drop Trigger", new() { ["table_name"] = "child" }), ("Drop Index", new() { ["index_name"] = "sample_table_column1_key" }),
            };
            var failed = new List<string>();
            foreach (var (name, extra) in runs)
                try { await Sql(Filled(name, extra)); } catch (PostgresException ex) { failed.Add($"{name}: {ex.MessageText}"); }
            check(failed.Count == 0, "Built-in templates run on PostgreSQL with their default values: " + string.Join(" | ", failed));

            // Menus
            await window.ConnectExplorerAsync(connection); await Idle();
            var catalog = new CatalogService(connection);
            string[] Names(CatalogEntry entry) { var menu = new ContextMenu(); window.FillExplorerMenu(menu, new CatalogNode(entry, catalog)); return menu.Items.OfType<MenuItem>().Select(i => i.Name).ToArray(); }
            check(Names(new CatalogEntry(CatalogKind.Functions, "Functions", database, schema)).First() == "ExplorerNewFunction" && Names(new CatalogEntry(CatalogKind.Procedures, "Procedures", database, schema)).First() == "ExplorerNewProcedure" && Names(new CatalogEntry(CatalogKind.Triggers, "Triggers", database, schema, 1)).First() == "ExplorerNewTrigger", "Functions, Procedures and Triggers folders start with New Function…, New Stored Procedure… and New Trigger…");
            var trig = Names(new CatalogEntry(CatalogKind.Trigger, "t", database, schema, 1)).ToList();
            check(trig[0] == "ExplorerNewTrigger" && trig.IndexOf("ExplorerEnableTrigger") == trig.IndexOf("ExplorerModifyTableChild") + 1 && trig.IndexOf("ExplorerDisableTrigger") == trig.IndexOf("ExplorerEnableTrigger") + 1, "A trigger offers New Trigger…, then Modify, Enable and Disable");

            // New Trigger from the table's Triggers folder, filled and executed
            var tree = (TreeView)window.FindName("ExplorerTree");
            async Task<CatalogNode> Child(CatalogNode node, string name) { node.IsExpanded = true; await node.LoadAsync(); while (node.IsBusy) await Task.Delay(50); await Idle(); return node.Children.Single(c => c.Entry.Name == name); }
            var table = await Child(await Child(await Child(await Child(await Child(await Child((CatalogNode)tree.Items[0], "Databases"), database), "Schemas"), schema), "Tables"), "child");
            var triggers = await Child(table, "Triggers");
            triggers.IsSelected = true; await Idle(); await Idle();
            if (ReferenceEquals(tree.SelectedItem, triggers))
            {
                var query = await window.NewObjectFromTemplateAsync("Trigger", "Create Trigger"); await Idle();
                check(query is { IsConnected: true } && query.Editor.Text.Contains($"<schema_name, name, {schema}>", StringComparison.Ordinal) && query.Editor.Text.Contains("<table_name, name, child>", StringComparison.Ordinal) && query.Editor.Text.Contains("<trigger_name, name, child_trigger>", StringComparison.Ordinal), "New Trigger… opens the template connected, with the schema and table of the node as values");
                window.TemplateParametersPrompt = p => p.ToDictionary(x => x.Name, x => x.Value);
                check(window.SpecifyTemplateParameters() > 0 && !query!.Editor.Text.Contains('<'), "Ctrl+Shift+M fills it with those values");
                await query!.ExecuteAsync(); await Idle();
                check((long)(await Scalar($"SELECT count(*) FROM pg_trigger WHERE tgname='child_trigger' AND tgrelid='{schema}.child'::regclass"))! == 1, "The filled template creates the trigger");
                query.IsDirty = false;
            }
            else check(true, "New Trigger…: the tree did not realize the folder; the template text was checked by the template run above");

            // Enable / Disable
            await Sql($"CREATE OR REPLACE TRIGGER manual_t BEFORE UPDATE ON {schema}.child FOR EACH ROW EXECUTE FUNCTION {schema}.sample_table_trigger()");
            await triggers.LoadAsync(refresh: true); while (triggers.IsBusy) await Task.Delay(50);
            var manual = triggers.Children.Single(c => c.Entry.Name == "manual_t");
            check(await window.SetTriggerEnabledAsync(manual, false) && await Scalar($"SELECT tgenabled::text FROM pg_trigger WHERE tgname='manual_t'") is "D", "Disable runs ALTER TABLE … DISABLE TRIGGER");
            check(await window.SetTriggerEnabledAsync(manual, true) && await Scalar($"SELECT tgenabled::text FROM pg_trigger WHERE tgname='manual_t'") is "O", "Enable turns it back on");
            await Sql($"ALTER TRIGGER manual_t ON {schema}.child RENAME TO manual_renamed");
            check(!await window.SetTriggerEnabledAsync(manual, false) && errors.Count == 1 && errors[0].Contains("now named manual_renamed", StringComparison.Ordinal), "A trigger renamed since the tree was read is not touched");
        }
        finally
        {
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
