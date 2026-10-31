using Npgsql;
using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class TablePropertyChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_properties_" + Guid.NewGuid().ToString("N");
        string target = "public." + SqlIdentifier.Quote(table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync($"CREATE TABLE {target}(id int, note text DEFAULT 'example'); COMMENT ON COLUMN {target}.note IS 'Column documentation'; COMMENT ON TABLE {target} IS 'Synthetic table for property inspection';");
        try
        {
            await session.ExecuteAsync($"CREATE VIEW public.{SqlIdentifier.Quote(table + "_view")} AS SELECT note FROM {target};");
            await session.ExecuteAsync($"CREATE INDEX {SqlIdentifier.Quote(table + "_idx")} ON {target}(lower(note)) INCLUDE(id) WHERE note IS NOT NULL;");
            await session.ExecuteAsync($"ALTER TABLE {target} ADD CONSTRAINT nonempty_note CHECK(note <> '') NOT VALID;");
            var dialog = new TablePropertiesDialog(connection, "public", table) { ShowInTaskbar = false };
            Exception? error = null;
            dialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    await dialog.InspectAsync();
                    check(dialog.Snapshot?.Name == table && ((TextBox)dialog.FindName("Summary")).Text.Contains("Synthetic table", StringComparison.Ordinal), "properties WPF reads actual table metadata");
                    TableEditChecks.Capture(dialog, "m33-properties");
                    ((TabControl)dialog.FindName("Details")).SelectedIndex = 1;
                    var grid = (DataGrid)dialog.FindName("ColumnsGrid");
                    grid.SelectedItem = dialog.Snapshot!.Columns.Single(c => c.Name == "note");
                    check(grid.Items.Count == 2 && ((TextBox)dialog.FindName("ColumnDetail")).Text.Contains("Column documentation", StringComparison.Ordinal), "column grid selection exposes default and comment in WPF");
                    TableEditChecks.Capture(dialog, "m34-columns");
                    dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                    TableEditChecks.Capture(dialog, "m34-columns-min");
                    ((TabControl)dialog.FindName("Details")).SelectedIndex = 2;
                    var dependents = (DataGrid)dialog.FindName("DependentsGrid");
                    dependents.SelectedItem = dialog.Snapshot!.Dependents.First(d => d.Description.Contains(table + "_view", StringComparison.Ordinal));
                    check(((TextBox)dialog.FindName("DependencyDetail")).Text.Contains(table + "_view", StringComparison.Ordinal), "dependency selection exposes full object description in WPF");
                    TableEditChecks.Capture(dialog, "m35-dependents-min");
                    dialog.Width = 820; dialog.Height = 650;
                    TableEditChecks.Capture(dialog, "m35-dependents");
                    ((TabControl)dialog.FindName("Details")).SelectedIndex = 3;
                    var indexes = (DataGrid)dialog.FindName("IndexesGrid"); indexes.SelectedIndex = 0;
                    check(indexes.Items.Count == 1 && ((TextBox)dialog.FindName("IndexDetail")).Text.Contains("lower(note)", StringComparison.Ordinal), "index selection shows actual full SQL definition in WPF");
                    TableEditChecks.Capture(dialog, "m43-indexes");
                    dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                    TableEditChecks.Capture(dialog, "m43-indexes-min");
                    ((TabControl)dialog.FindName("Details")).SelectedIndex = 4;
                    var constraints = (DataGrid)dialog.FindName("ConstraintsGrid"); constraints.SelectedIndex = 0;
                    check(constraints.Items.Count == 1 && ((TextBox)dialog.FindName("ConstraintDetail")).Text.Contains("NOT VALID", StringComparison.Ordinal), "constraint selection shows NOT VALID definition in WPF");
                    TableEditChecks.Capture(dialog, "m44-constraints-min");
                    dialog.Width = 820; dialog.Height = 650;
                    TableEditChecks.Capture(dialog, "m44-constraints");
                    ((TextBox)dialog.FindName("TableName")).Text = "missing_table";
                    check(constraints.Items.Count == 0 && ((TextBox)dialog.FindName("ConstraintDetail")).Text.Length == 0, "changing target clears constraints and detail");
                    check(indexes.Items.Count == 0 && ((TextBox)dialog.FindName("IndexDetail")).Text.Length == 0, "changing target clears index snapshot and detail");
                    check(dialog.Snapshot is null && ((TextBox)dialog.FindName("Summary")).Text.Length == 0, "changing table clears stale property snapshot");
                    check(dependents.Items.Count == 0, "changing table clears stale dependency rows");
                    check(grid.Items.Count == 0, "changing table clears inspected columns");
                    await dialog.InspectAsync();
                    check(dialog.Snapshot is null && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("not found", StringComparison.Ordinal) && ((Button)dialog.FindName("InspectButton")).IsEnabled, "missing table feedback persists and inspection can retry");
                }
                catch (Exception ex) { error = ex; }
                finally { dialog.Close(); }
            };
            dialog.ShowDialog();
            if (error is not null) throw error;
            var cancelDialog = new TablePropertiesDialog(connection, "public", table) { ShowInTaskbar = false };
            cancelDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    // Only the isolated test cluster: block the actual metadata read until canceled.
                    await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
                    await using var transaction = await blocker.BeginTransactionAsync();
                    await using (var command = new NpgsqlCommand("LOCK TABLE pg_catalog.pg_index IN ACCESS EXCLUSIVE MODE", blocker, transaction)) await command.ExecuteNonQueryAsync();
                    var pending = cancelDialog.InspectAsync();
                    await Task.Delay(100);
                    check(!pending.IsCompleted && ((Button)cancelDialog.FindName("CancelInspectionButton")).IsEnabled && !((Button)cancelDialog.FindName("TableOperationsButton")).IsEnabled, "busy property inspection offers cancellation without enabling edits");
                    TableEditChecks.Capture(cancelDialog, "m45-inspection-busy");
                    ((Button)cancelDialog.FindName("CancelInspectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    check(cancelDialog.Snapshot is null && ((TextBlock)cancelDialog.FindName("Feedback")).Text.Contains("canceled", StringComparison.Ordinal) && ((Button)cancelDialog.FindName("InspectButton")).IsEnabled, "canceling blocked catalog read restores retry without partial snapshot");
                    cancelDialog.Width = cancelDialog.MinWidth; cancelDialog.Height = cancelDialog.MinHeight;
                    TableEditChecks.Capture(cancelDialog, "m45-inspection-canceled-min");
                    pending = cancelDialog.InspectAsync(); await Task.Delay(100);
                    cancelDialog.Close(); await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    check(cancelDialog.IsVisible && cancelDialog.Snapshot is null, "closing busy inspector requests cancellation before allowing close");
                    await transaction.RollbackAsync();
                    await cancelDialog.InspectAsync();
                    check(cancelDialog.Snapshot?.Name == table && !((Button)cancelDialog.FindName("CancelInspectionButton")).IsEnabled, "inspection succeeds after cancellation and lock release");
                }
                catch (Exception ex) { error = ex; }
                finally { cancelDialog.Close(); }
            };
            cancelDialog.ShowDialog(); if (error is not null) throw error;
            string child = "public." + SqlIdentifier.Quote(table + "_child");
            await session.ExecuteAsync($"ALTER TABLE {target} ADD PRIMARY KEY(id); CREATE TABLE {child}(id int REFERENCES {target}(id) ON DELETE CASCADE);");
            var incomingDialog = new TablePropertiesDialog(connection, "public", table) { ShowInTaskbar = false };
            incomingDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    await incomingDialog.InspectAsync();
                    ((TabControl)incomingDialog.FindName("Details")).SelectedIndex = 5;
                    var incoming = (DataGrid)incomingDialog.FindName("IncomingGrid"); incoming.SelectedIndex = 0;
                    check(incoming.Items.Count == 1 && ((TextBox)incomingDialog.FindName("IncomingDetail")).Text.Contains(table + "_child", StringComparison.Ordinal) && ((TextBox)incomingDialog.FindName("IncomingDetail")).Text.Contains("ON DELETE CASCADE", StringComparison.Ordinal), "incoming FK WPF selection reveals exact source and definition");
                    TableEditChecks.Capture(incomingDialog, "m49-incoming-fks");
                    incomingDialog.Width = incomingDialog.MinWidth; incomingDialog.Height = incomingDialog.MinHeight;
                    TableEditChecks.Capture(incomingDialog, "m49-incoming-fks-min");
                    ((TextBox)incomingDialog.FindName("TableName")).Text = "missing";
                    check(incoming.Items.Count == 0 && ((TextBox)incomingDialog.FindName("IncomingDetail")).Text.Length == 0, "changing target clears incoming FK rows and detail");
                }
                catch (Exception ex) { error = ex; }
                finally { incomingDialog.Close(); }
            };
            incomingDialog.ShowDialog(); if (error is not null) throw error;
            string function = "public." + SqlIdentifier.Quote(table + "_fn");
            await session.ExecuteAsync($"CREATE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$; CREATE TRIGGER before_note BEFORE UPDATE OF note ON {target} FOR EACH ROW WHEN (NEW.note IS NOT NULL) EXECUTE FUNCTION {function}();");
            var triggerDialog = new TablePropertiesDialog(connection, "public", table) { ShowInTaskbar = false };
            triggerDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    await triggerDialog.InspectAsync();
                    ((TabControl)triggerDialog.FindName("Details")).SelectedIndex = 6;
                    var triggers = (DataGrid)triggerDialog.FindName("TriggersGrid");
                    triggers.SelectedItem = triggerDialog.Snapshot!.Triggers.Single(t => t.Name == "before_note");
                    check(((TextBox)triggerDialog.FindName("TriggerDetail")).Text.Contains("IS NOT NULL", StringComparison.Ordinal) && ((TextBox)triggerDialog.FindName("TriggerDetail")).Text.Contains(table + "_fn", StringComparison.Ordinal), "trigger WPF selection exposes condition and function identity");
                    TableEditChecks.Capture(triggerDialog, "m50-triggers");
                    triggerDialog.Width = triggerDialog.MinWidth; triggerDialog.Height = triggerDialog.MinHeight;
                    TableEditChecks.Capture(triggerDialog, "m50-triggers-min");
                    ((TextBox)triggerDialog.FindName("TableName")).Text = "missing";
                    check(triggers.Items.Count == 0 && ((TextBox)triggerDialog.FindName("TriggerDetail")).Text.Length == 0, "changing target clears trigger rows and definition");
                }
                catch (Exception ex) { error = ex; }
                finally { triggerDialog.Close(); }
            };
            triggerDialog.ShowDialog(); if (error is not null) throw error;
            // Subsequent inspection milestones extend this fixture.
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS public.{SqlIdentifier.Quote(table + "_child")} CASCADE; DROP TABLE IF EXISTS {target} CASCADE; DROP FUNCTION IF EXISTS public.{SqlIdentifier.Quote(table + "_fn")}();"); }
    }
}
