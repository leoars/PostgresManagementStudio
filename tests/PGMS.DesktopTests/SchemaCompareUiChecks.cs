using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>PGMS-075 (M1102): Tools ▸ Schema Compare on two databases created here. Generate Script opens a query tab on the target and runs nothing.</summary>
internal static class SchemaCompareUiChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string suffix = Guid.NewGuid().ToString("N")[..8], source = "pgms_cmpui_src_" + suffix, target = "pgms_cmpui_tgt_" + suffix;
        string On(string db) => new NpgsqlConnectionStringBuilder(connection) { Database = db, Pooling = false }.ConnectionString;
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(NpgsqlConnection c, string sql) { await using var command = new NpgsqlCommand(sql, c); await command.ExecuteNonQueryAsync(); }
        async Task In(string db, string sql) { await using var c = new NpgsqlConnection(On(db)); await c.OpenAsync(); await Sql(c, sql); }
        var created = new List<string>();
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        var errors = new List<string>(); window.ShowErrorOverride = ex => errors.Add(ex.Message);
        window.OpenTransactionPrompt = _ => System.Windows.MessageBoxResult.No;
        try
        {
            foreach (var db in new[] { source, target }) { await Sql(admin, $"CREATE DATABASE {db}"); created.Add(db); }
            await In(source, "CREATE TABLE public.items (id integer PRIMARY KEY, label text NOT NULL); CREATE VIEW public.labels AS SELECT label FROM public.items;");
            await In(target, "CREATE TABLE public.items (id integer PRIMARY KEY, label varchar(20)); CREATE TABLE public.extra (x integer);");
            await Idle();
            var menu = (MenuItem)window.FindName("SchemaCompareMenu");
            var tools = (ItemsControl)menu.Parent;
            int at = tools.Items.IndexOf(menu);
            check((string)menu.Header == "S_chema Compare" && tools.Items[at - 1] is Separator && tools.Items[at + 1] is Separator && tools.Items.IndexOf(window.FindName("RestoreGlobalsMenu")) == at - 2,
                "Tools ▸ Schema Compare is on its own after the backup commands");
            var view = window.ShowSchemaCompare();
            await Idle();
            check(view.Title == "Schema Compare" && !view.CanCompare && !view.CanGenerateScript && view.SourceText == "" && view.TargetText == "", "A new Schema Compare opens empty, Compare and Generate Script unavailable");
            view.SetEndpoint(On(source), isSource: true);
            check(!view.CanCompare, "With only the source chosen Compare stays unavailable");
            view.SetEndpoint(On(target), isSource: false);
            var builder = new NpgsqlConnectionStringBuilder(connection);
            check(view.CanCompare && view.SourceText == $"{builder.Host}:{builder.Port}.{source}" && view.TargetText == $"{builder.Host}:{builder.Port}.{target}", "Source and target show server and database: " + view.SourceText);
            check(!view.SourceText.Contains("assword", StringComparison.OrdinalIgnoreCase), "No password is shown");

            await view.CompareAsync(); await Idle();
            var rows = view.Differences.Select(d => $"{d.Action} {d.TypeName} {d.SourceName}{d.TargetName}").ToList();
            check(rows.SequenceEqual(["Delete Table public.extra", "Change Table public.itemspublic.items", "Add View public.labels"]) && view.MessageText == "Comparison completed. Found 3 differences (1 Add, 1 Change, 1 Delete).",
                "Compare lists the differences by type with the SSMS message: " + string.Join(" | ", rows) + " / " + view.MessageText);
            var groups = view.Grid.Items.Groups?.Cast<System.Windows.Data.CollectionViewGroup>().Select(g => $"{g.Name}:{g.ItemCount}").ToList();
            check(groups is not null && groups.SequenceEqual(["Table:2", "View:1"]), "Rows are grouped by type: " + string.Join(", ", groups ?? []));
            view.GroupBy = "Action"; await Idle();
            groups = view.Grid.Items.Groups?.Cast<System.Windows.Data.CollectionViewGroup>().Select(g => $"{g.Name}:{g.ItemCount}").ToList();
            check(groups is not null && groups.Count == 3 && groups.Contains("Delete:1"), "Group By: Action regroups the rows: " + string.Join(", ", groups ?? []));
            view.GroupBy = "Type";

            var items = view.Differences.Single(d => d.Action == SchemaDifferenceAction.Change);
            view.Select(items); await Idle();
            check(view.SourceDefinition.Contains("label text NOT NULL", StringComparison.Ordinal) && view.TargetDefinition.Contains("label character varying(20)", StringComparison.Ordinal), "Selecting a row shows both definitions");
            check(view.SourceChangedLines.Count == 1 && view.TargetChangedLines.Count == 1, "Only the line that differs is highlighted on each side: " + string.Join(",", view.SourceChangedLines) + " / " + string.Join(",", view.TargetChangedLines));

            var before = window.QueryDocuments.ToHashSet();
            string? script = await view.GenerateScriptAsync(); await Idle();
            var query = window.QueryDocuments.Single(q => !before.Contains(q));
            for (int i = 0; i < 100 && !query.HasSession; i++) { await Task.Delay(50); await Idle(); }
            check(script is not null && query.Editor.Text == script && script.Contains("CREATE OR REPLACE VIEW public.labels AS", StringComparison.Ordinal) && script.Contains("DROP TABLE public.extra;", StringComparison.Ordinal),
                "Generate Script opens the script in a new query tab");
            check(query.HasSession && new NpgsqlConnectionStringBuilder(query.ConnectionString!).Database == target && query.LastExecution is null, "The tab is connected to the target and nothing ran");
            await using (var check2 = new NpgsqlConnection(On(target)))
            {
                await check2.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT to_regclass('public.labels') IS NULL AND to_regclass('public.extra') IS NOT NULL", check2);
                check((bool)(await command.ExecuteScalarAsync())!, "The target is unchanged");
            }
            check(view.MessageText == "Script generated in a new query window on the target. Nothing was run.", "The message says nothing was run");

            foreach (var d in view.Differences) d.Include = false;
            check(await view.GenerateScriptAsync() is null && view.MessageText.StartsWith("No differences are included", StringComparison.Ordinal), "With every row excluded there is no script");

            view.SwitchDirection(); await Idle();
            check(view.SourceText.EndsWith(target, StringComparison.Ordinal) && view.TargetText.EndsWith(source, StringComparison.Ordinal) && view.Differences.Count == 0 && !view.CanGenerateScript && view.MessageText.StartsWith("Source and target switched", StringComparison.Ordinal),
                "Switch Direction swaps source and target and clears the differences");
            await view.CompareAsync(); await Idle();
            check(view.Differences.Any(d => d.Action == SchemaDifferenceAction.Add && d.SourceName == "public.extra"), "Compared again, the other way round, the extra table is to be added");
            check(errors.Count == 0, "No error was shown: " + string.Join(" | ", errors));
        }
        finally
        {
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
            NpgsqlConnection.ClearAllPools();
            foreach (var db in created) await Sql(admin, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
        }
    }
}
