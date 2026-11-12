using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>PGMS-039: the Unindexed Foreign Keys window.</summary>
internal static class ForeignKeyIndexScanUiChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static T Named<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name);
        string schema = "qfkx_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<long> Indexes() { await using var command = new NpgsqlCommand($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{schema}' AND indexname LIKE 'ix\\_%'", admin); return (long)(await command.ExecuteScalarAsync())!; }
        await Sql($"""
            CREATE SCHEMA {schema};
            CREATE TABLE {schema}.pai(id int PRIMARY KEY);
            CREATE TABLE {schema}.filha(id int PRIMARY KEY, pai_id int REFERENCES {schema}.pai(id), outro_id int REFERENCES {schema}.pai(id));
            CREATE INDEX filha_parcial ON {schema}.filha(outro_id) WHERE outro_id > 0;
            CREATE TABLE {schema}.coberta(id int PRIMARY KEY, pai_id int REFERENCES {schema}.pai(id)); CREATE INDEX ON {schema}.coberta(pai_id);
            CREATE TABLE {schema}.parts(id int, pai_id int REFERENCES {schema}.pai(id)) PARTITION BY RANGE (id);
            CREATE TABLE {schema}.parts_1 PARTITION OF {schema}.parts FOR VALUES FROM (0) TO (100);
            INSERT INTO {schema}.pai VALUES (1); INSERT INTO {schema}.filha SELECT g, 1, 1 FROM generate_series(1, 200) g; ANALYZE {schema}.filha;
            """);
        var window = new ForeignKeyIndexScanWindow(connection) { ShowInTaskbar = false };
        window.Show();
        try
        {
            await Idle();
            var apply = Named<Button>(window, "ApplyButton"); var open = Named<Button>(window, "OpenScriptButton"); var script = Named<TextBox>(window, "Script"); var status = Named<TextBlock>(window, "ScanStatus");
            var concurrently = Named<CheckBox>(window, "Concurrently"); var selectAll = Named<Button>(window, "SelectAllButton");
            check(!apply.IsEnabled && !open.IsEnabled && !selectAll.IsEnabled && status.Text == "Not scanned." && window.Rows.Count == 0 && concurrently.IsChecked == true, "Nothing is read until Scan, and Create concurrently starts checked");
            await window.ScanAsync(); await Idle();
            List<UnindexedForeignKeyRow> Mine() => window.Rows.Where(r => r.Key.Key.Schema == schema).ToList();
            UnindexedForeignKeyRow Row(string table, string column) => Mine().Single(r => r.Key.Key.Table == table && r.Key.Key.Columns[0] == column);
            var plain = Row("filha", "pai_id"); var partial = Row("filha", "outro_id"); var parts = Row("parts", "pai_id");
            check(Mine().Count == 3, "The key with an index is not listed: " + string.Join(",", Mine().Select(r => r.Source)));
            check(plain.Source == $"{schema}.filha (pai_id)" && plain.Target == $"{schema}.pai" && plain.EstimatedRows == 200.ToString("N0") && plain.Size.Length > 0 && plain.Result == "No index" && plain.NearIndexes.Length == 0 && plain.ParentDeletes.Length > 0, $"A row shows the table, the target, the size and the estimate: {plain.Size}, {plain.EstimatedRows}");
            check(partial.NearIndexes == "filha_parcial is partial", "An index that does not cover the key is shown with the reason");
            check(parts.Result == "No index. Partitioned: created without CONCURRENTLY", "A partitioned table says how its index is created");
            check(!window.Rows.Any(r => r.Selected) && !apply.IsEnabled && selectAll.IsEnabled && status.Text.Contains("have no index", StringComparison.Ordinal), "Nothing starts selected: " + status.Text);
            check(UnindexedForeignKeyRow.FormatSize(512).EndsWith(" bytes", StringComparison.Ordinal) && UnindexedForeignKeyRow.FormatSize(1536) == 1.5.ToString("N1") + " kB" && UnindexedForeignKeyRow.FormatSize(3L << 30) == 3.0.ToString("N1") + " GB", "Sizes are shown in readable units");

            plain.Selected = true; parts.Selected = true; await Idle();
            check(script.Text == ForeignKeyIndexScan.Statement(plain.Key, true) + Environment.NewLine + ForeignKeyIndexScan.Statement(parts.Key, true) && script.Text.Split("CONCURRENTLY").Length == 2 && apply.IsEnabled && open.IsEnabled, "Selecting fills the script; only the ordinary table gets CONCURRENTLY: " + script.Text);
            concurrently.IsChecked = false; concurrently.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Idle();
            check(!script.Text.Contains("CONCURRENTLY", StringComparison.Ordinal), "Unchecking Create concurrently rewrites the script");

            string? sent = null;
            window.OpenInQuery = text => { sent = text; return Task.CompletedTask; };
            open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Idle();
            check(sent == script.Text && await Indexes() == 0, "Open Script in Query sends the text and runs nothing");

            TableDesignChange? reviewed = null;
            window.ReviewScript = change => { reviewed = change; return false; };
            await window.ApplyAsync(); await Idle();
            check(reviewed is { Statements.Count: 2, Warnings.Count: 1 } && reviewed.Warnings[0].Contains("one transaction", StringComparison.Ordinal) && await Indexes() == 0, "Without CONCURRENTLY the review warns about one transaction and blocked writes; declining creates nothing");
            window.CreateConcurrently = true; await Idle();
            await window.ApplyAsync(); await Idle();
            check(reviewed is { Statements.Count: 2, Warnings.Count: 2 } && reviewed.Warnings[0].Contains("one at a time", StringComparison.Ordinal) && reviewed.Warnings[1].Contains("is partitioned", StringComparison.Ordinal) && await Indexes() == 0, "With CONCURRENTLY the review warns about partial results and about the partitioned table");

            // One at a time: the first is created, the second fails and the row says so.
            await Sql($"CREATE TABLE {schema}.\"{parts.Key.IndexName}\"(x int)");
            window.ReviewScript = _ => true;
            await window.ApplyAsync(); await Idle();
            check(await Indexes() == 1 && plain.Result == "Created" && !plain.Selected && parts.Result.StartsWith("Failed: 42P07", StringComparison.Ordinal) && parts.Selected && window.Created && status.Text.StartsWith("1 index(es) created, then stopped", StringComparison.Ordinal) && apply.IsEnabled, $"A failure stops there, keeps what was created and marks the row: {status.Text}");
            await Sql($"DROP TABLE {schema}.\"{parts.Key.IndexName}\"");

            // In one transaction: a failure creates nothing.
            await Sql($"CREATE TABLE {schema}.\"{partial.Key.IndexName}\"(x int)");
            window.CreateConcurrently = false; partial.Selected = true; await Idle();
            await window.ApplyAsync(); await Idle();
            check(await Indexes() == 1 && status.Text.StartsWith("Nothing was created: 42P07", StringComparison.Ordinal) && parts.Selected && partial.Selected, $"In one transaction a failure creates nothing: {status.Text}");
            await Sql($"DROP TABLE {schema}.\"{partial.Key.IndexName}\"");
            await window.ApplyAsync(); await Idle();
            check(await Indexes() == 3 && parts.Result == "Created" && partial.Result == "Created" && status.Text == "2 index(es) created." && !apply.IsEnabled && script.Text.Length == 0, $"Accepting creates the indexes and marks the rows: {status.Text}");

            await window.ScanAsync(); await Idle();
            check(Mine().Count == 0, "A new scan no longer lists them");
        }
        finally
        {
            window.Close();
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
