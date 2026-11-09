using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>PGMS-037: the Missing Foreign Keys window.</summary>
internal static class ForeignKeyScanUiChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static T Named<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name);
        string schema = "qfku_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<long> Keys() { await using var command = new NpgsqlCommand($"SELECT count(*) FROM pg_constraint WHERE conrelid = '{schema}.pedidos'::regclass AND contype = 'f'", admin); return (long)(await command.ExecuteScalarAsync())!; }
        await Sql($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.clientes(id int PRIMARY KEY); CREATE TABLE {schema}.ativos(id int PRIMARY KEY); CREATE TABLE {schema}.pedidos(id int PRIMARY KEY, cliente_id int, ativo_id int); INSERT INTO {schema}.clientes VALUES (1); INSERT INTO {schema}.ativos VALUES (1); INSERT INTO {schema}.pedidos VALUES (1, 1, 1), (2, 1, 50);");
        var window = new ForeignKeyScanWindow(connection) { ShowInTaskbar = false };
        window.Show();
        try
        {
            await Idle();
            var apply = Named<Button>(window, "ApplyButton"); var open = Named<Button>(window, "OpenScriptButton"); var script = Named<TextBox>(window, "Script"); var status = Named<TextBlock>(window, "ScanStatus");
            check(!apply.IsEnabled && !open.IsEnabled && status.Text == "Not scanned." && window.Rows.Count == 0, "Nothing is read until Scan");
            await window.ScanAsync(); await Idle();
            ForeignKeyCandidateRow Row(string column) => window.Rows.Single(r => r.Candidate.SourceSchema == schema && r.Candidate.SourceColumn == column);
            var good = Row("cliente_id"); var orphaned = Row("ativo_id");
            check(good.Target == $"{schema}.clientes.id" && good.Result == "Every value has a match" && good.Rows == "2" && good.Orphans == "0", "A candidate shows what its rows said");
            check(orphaned.Result == "Rows without a match: NOT VALID" && orphaned.Orphans == "1", "Orphans are shown");
            check(!good.Selected && !apply.IsEnabled && status.Text.Contains("hint, not proof", StringComparison.Ordinal), "Nothing starts selected and the status says a name is only a hint");

            foreach (var row in window.Rows) row.Selected = false;
            good.Selected = true; await Idle();
            check(script.Text == ForeignKeyScan.Statement(good.Candidate, false) && apply.IsEnabled && open.IsEnabled, "Selecting fills the script and enables Apply");
            orphaned.Selected = true; await Idle();
            check(script.Text.Contains(" NOT VALID;", StringComparison.Ordinal) && script.Text.Split('\n').Length == 2, "A candidate with orphans gets NOT VALID in the script");

            string? sent = null;
            window.OpenInQuery = text => { sent = text; return Task.CompletedTask; };
            open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Idle();
            check(sent == script.Text && await Keys() == 0, "Open Script in Query sends the text and runs nothing");

            TableDesignChange? reviewed = null;
            window.ReviewScript = change => { reviewed = change; return false; };
            await window.ApplyAsync(); await Idle();
            check(reviewed is { Statements.Count: 2, Warnings.Count: 1 } && reviewed.Warnings[0].Contains("NOT VALID", StringComparison.Ordinal) && await Keys() == 0, "Apply shows the script and warnings first; declining creates nothing");
            window.ReviewScript = _ => true;
            await window.ApplyAsync(); await Idle();
            check(await Keys() == 2 && good.Result == "Created" && orphaned.Result == "Created" && window.Created && !apply.IsEnabled && script.Text.Length == 0, "Accepting creates the keys and marks them");

            await window.ScanAsync(); await Idle();
            check(!window.Rows.Any(r => r.Candidate.SourceSchema == schema && r.Candidate.SourceTable == "pedidos"), "A new scan no longer lists them");
            var matchesButton = Named<Button>(window, "SelectMatchesButton");
            await Sql($"CREATE TABLE {schema}.itens(id int PRIMARY KEY, cliente_id int); INSERT INTO {schema}.itens VALUES (1, 1)");
            await window.ScanAsync(); await Idle();
            matchesButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Idle();
            check(window.Rows.Single(r => r.Candidate.SourceTable == "itens" && r.Candidate.SourceSchema == schema).Selected, "Select All Matches selects the confirmed ones");
            await Sql($"ALTER TABLE {schema}.itens ADD CONSTRAINT \"fk_itens_cliente_id\" CHECK (true)");
            foreach (var row in window.Rows) row.Selected = row.Candidate.SourceSchema == schema && row.Candidate.SourceTable == "itens";
            window.ReviewScript = _ => true;
            await window.ApplyAsync(); await Idle();
            check(status.Text.StartsWith("Nothing was created", StringComparison.Ordinal) && apply.IsEnabled, "A failure says nothing was created and keeps the selection");
        }
        finally
        {
            window.Close();
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
