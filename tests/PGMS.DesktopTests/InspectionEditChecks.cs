using Npgsql;
using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class InspectionEditChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_inspected_ui_" + Guid.NewGuid().ToString("N");
        string destinationSchema = table + "_dst" + (char)34;
        string removedSchema = table + "_gone";
        string target = "public." + SqlIdentifier.Quote(table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync($"CREATE TABLE {target}(id int, note text);");
        try
        {
            var dialog = new TableEditDialog(connection, "public", table) { ShowInTaskbar = false };
            Exception? error = null;
            dialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((TextBox)dialog.FindName("NewName")).Text = table + "_renamed";
                    await dialog.InspectTargetAsync();
                    check(dialog.InspectedTable?.Name == table, "Table Operations inspects exact target in WPF");
                    TableEditChecks.Capture(dialog, "m38-inspected-table");
                    uint inspectedOid = dialog.InspectedTable!.Oid;
                    await session.ExecuteAsync($"DROP TABLE {target};");
                    await dialog.InspectTargetAsync();
                    check(dialog.InspectedTable?.Oid == inspectedOid && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("not found", StringComparison.Ordinal), "failed reinspection preserves existing identity protection");
                    await session.ExecuteAsync($"CREATE TABLE {target}(id int, note text);");
                    await dialog.ApplyAsync();
                    check(!dialog.WasApplied && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("identity", StringComparison.Ordinal), "WPF Apply refuses stale table identity");
                    ((TextBox)dialog.FindName("TableName")).Text = "other_table";
                    check(dialog.InspectedTable is null, "editing target clears inspected identity");
                }
                catch (Exception ex) { error = ex; }
                finally { dialog.Close(); }
            };
            dialog.ShowDialog(); if (error is not null) throw error;
            var columnDialog = new TableEditDialog(connection, "public", table) { ShowInTaskbar = false };
            columnDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)columnDialog.FindName("Operation")).SelectedItem = TableOperation.RenameColumn;
                    await columnDialog.InspectTargetAsync();
                    ((ComboBox)columnDialog.FindName("ColumnPicker")).SelectedItem = columnDialog.InspectedTable!.Columns.Single(c => c.Name == "note");
                    ((TextBox)columnDialog.FindName("NewName")).Text = "new_note";
                    check(((TextBox)columnDialog.FindName("ColumnName")).Text == "note", "inspected column picker transfers exact name");
                    TableEditChecks.Capture(columnDialog, "m39-column-picker");
                    await session.ExecuteAsync($"ALTER TABLE {target} DROP COLUMN note; ALTER TABLE {target} ADD COLUMN note text;");
                    await columnDialog.ApplyAsync();
                    check(!columnDialog.WasApplied && ((TextBlock)columnDialog.FindName("Feedback")).Text.Contains("Column identity", StringComparison.Ordinal), "WPF rejects replacement column after inspection");
                    ((TextBox)columnDialog.FindName("ColumnName")).Text = "absent";
                    check(!((Button)columnDialog.FindName("ApplyButton")).IsEnabled, "inspected operation rejects column absent from snapshot");
                }
                catch (Exception ex) { error = ex; }
                finally { columnDialog.Close(); }
            };
            columnDialog.ShowDialog(); if (error is not null) throw error;
            await session.ExecuteAsync($"COMMENT ON COLUMN {target}.note IS 'Original documentation';");
            var commentDialog = new TableEditDialog(connection, "public", table) { ShowInTaskbar = false };
            commentDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)commentDialog.FindName("Operation")).SelectedItem = TableOperation.CommentColumn;
                    ((TextBox)commentDialog.FindName("ColumnName")).Text = "note";
                    await commentDialog.InspectTargetAsync();
                    ((Button)commentDialog.FindName("LoadCommentButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(((TextBox)commentDialog.FindName("CommentText")).Text == "Original documentation", "load inspected comment transfers actual catalog text");
                    TableEditChecks.Capture(commentDialog, "m40-load-comment");
                    commentDialog.Width = commentDialog.MinWidth; commentDialog.Height = commentDialog.MinHeight;
                    TableEditChecks.Capture(commentDialog, "m40-load-comment-min");
                    ((TextBox)commentDialog.FindName("CommentText")).Text = "my edit";
                    await session.ExecuteAsync($"COMMENT ON COLUMN {target}.note IS 'Concurrent writer';");
                    await commentDialog.ApplyAsync();
                    check(!commentDialog.WasApplied && ((TextBlock)commentDialog.FindName("Feedback")).Text.Contains("Comment changed", StringComparison.Ordinal), "WPF Apply preserves concurrent comment and shows conflict");
                    await commentDialog.InspectTargetAsync();
                    ((Button)commentDialog.FindName("LoadCommentButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(((TextBox)commentDialog.FindName("CommentText")).Text == "Concurrent writer", "reinspection loads latest comment after conflict");
                }
                catch (Exception ex) { error = ex; }
                finally { commentDialog.Close(); }
            };
            commentDialog.ShowDialog(); if (error is not null) throw error;
            foreach (bool selectedColumn in new[] { false, true })
            {
                var properties = new TablePropertiesDialog(connection, "public", table) { ShowInTaskbar = false };
                properties.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        check(!((Button)properties.FindName("TableOperationsButton")).IsEnabled, "property operations require inspection first");
                        await properties.InspectAsync();
                        if (selectedColumn)
                        {
                            ((TabControl)properties.FindName("Details")).SelectedIndex = 1;
                            ((DataGrid)properties.FindName("ColumnsGrid")).SelectedItem = properties.Snapshot!.Columns.Single(c => c.Name == "note");
                            TableEditChecks.Capture(properties, "m41-properties-actions");
                        }
                        ((Button)properties.FindName(selectedColumn ? "ColumnOperationsButton" : "TableOperationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { error = ex; properties.Close(); }
                };
                properties.ShowDialog(); if (error is not null) throw error;
                var selection = properties.EditSelection!;
                var routed = new TableEditDialog(connection, inspectedTable: selection.Table, column: selection.Column) { ShowInTaskbar = false };
                routed.ContentRendered += (_, _) =>
                {
                    try
                    {
                        check(routed.InspectedTable?.Oid == selection.Table.Oid && ((TextBox)routed.FindName("TableName")).Text == table && ((TextBox)routed.FindName("ColumnName")).Text == (selectedColumn ? "note" : ""), "property handoff preserves inspected table and selected column");
                        if (selectedColumn) TableEditChecks.Capture(routed, "m41-routed-column");
                    }
                    catch (Exception ex) { error = ex; }
                    finally { routed.Close(); }
                };
                routed.ShowDialog(); if (error is not null) throw error;
            }
            var defaultDialog = new TableEditDialog(connection, "public", table) { ShowInTaskbar = false };
            defaultDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)defaultDialog.FindName("Operation")).SelectedItem = TableOperation.SetDefault;
                    ((TextBox)defaultDialog.FindName("ColumnName")).Text = "note";
                    await defaultDialog.InspectTargetAsync();
                    check(((TextBox)defaultDialog.FindName("InspectedColumnState")).Text == "(none)", "default editor exposes inspected absence without inventing literal");
                    ((TextBox)defaultDialog.FindName("DefaultValue")).Text = "my default";
                    await session.ExecuteAsync($"ALTER TABLE {target} ALTER COLUMN note SET DEFAULT 'other writer';");
                    await defaultDialog.ApplyAsync();
                    check(!defaultDialog.WasApplied && ((TextBlock)defaultDialog.FindName("Feedback")).Text.Contains("Default changed", StringComparison.Ordinal), "WPF default Apply refuses concurrent edit");
                    await defaultDialog.InspectTargetAsync();
                    check(((TextBox)defaultDialog.FindName("InspectedColumnState")).Text == "'other writer'::text", "reinspection exposes latest default expression for review");
                    TableEditChecks.Capture(defaultDialog, "m46-default-review");
                    defaultDialog.Width = defaultDialog.MinWidth; defaultDialog.Height = defaultDialog.MinHeight;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(defaultDialog, "m46-default-review-min");
                    await defaultDialog.ApplyAsync();
                }
                catch (Exception ex) { error = ex; }
                finally { defaultDialog.Close(); }
            };
            defaultDialog.ShowDialog(); if (error is not null) throw error;
            check(defaultDialog.WasApplied && (await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").DefaultExpression == "'my default'::text", "reviewed default applies through actual WPF flow");
            var nullableDialog = new TableEditDialog(connection, "public", table) { ShowInTaskbar = false };
            nullableDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)nullableDialog.FindName("Operation")).SelectedItem = TableOperation.DropNotNull;
                    ((TextBox)nullableDialog.FindName("ColumnName")).Text = "note";
                    await nullableDialog.InspectTargetAsync();
                    check(((TextBox)nullableDialog.FindName("InspectedColumnState")).Text == "NULL allowed", "nullability editor exposes actual inspected state");
                    await session.ExecuteAsync($"ALTER TABLE {target} ALTER COLUMN note SET NOT NULL;");
                    await nullableDialog.ApplyAsync();
                    check(!nullableDialog.WasApplied && ((TextBlock)nullableDialog.FindName("Feedback")).Text.Contains("Nullability changed", StringComparison.Ordinal), "WPF nullability Apply preserves concurrent restriction");
                    TableEditChecks.Capture(nullableDialog, "m47-nullability-conflict");
                    await nullableDialog.InspectTargetAsync();
                    check(((TextBox)nullableDialog.FindName("InspectedColumnState")).Text == "NOT NULL", "nullability reinspection shows concurrent state before retry");
                    nullableDialog.Width = nullableDialog.MinWidth; nullableDialog.Height = nullableDialog.MinHeight;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(nullableDialog, "m47-nullability-review-min");
                    await nullableDialog.ApplyAsync();
                }
                catch (Exception ex) { error = ex; }
                finally { nullableDialog.Close(); }
            };
            nullableDialog.ShowDialog(); if (error is not null) throw error;
            check(nullableDialog.WasApplied && (await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").Nullable, "reviewed nullability applies through actual WPF flow");
            var cancelDialog = new TableEditDialog(connection, "public", table) { ShowInTaskbar = false };
            cancelDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((TextBox)cancelDialog.FindName("NewName")).Text = "renamed_table";
                    await cancelDialog.InspectTargetAsync(); var original = cancelDialog.InspectedTable;
                    await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
                    await using var transaction = await blocker.BeginTransactionAsync();
                    await using (var command = new NpgsqlCommand("LOCK TABLE pg_catalog.pg_index IN ACCESS EXCLUSIVE MODE", blocker, transaction)) await command.ExecuteNonQueryAsync();
                    var pending = cancelDialog.InspectTargetAsync(); await Task.Delay(100);
                    check(!pending.IsCompleted && ((Button)cancelDialog.FindName("CancelInspectionButton")).IsEnabled && !((Button)cancelDialog.FindName("ApplyButton")).IsEnabled, "Operations offers cancel while inspection blocks and disables Apply");
                    TableEditChecks.Capture(cancelDialog, "m48-inspection-busy");
                    ((Button)cancelDialog.FindName("CancelInspectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    check(ReferenceEquals(original, cancelDialog.InspectedTable) && ((TextBlock)cancelDialog.FindName("Feedback")).Text.Contains("canceled", StringComparison.Ordinal), "canceled Operations inspection preserves previous identity snapshot");
                    cancelDialog.Width = cancelDialog.MinWidth; cancelDialog.Height = cancelDialog.MinHeight;
                    TableEditChecks.Capture(cancelDialog, "m48-inspection-canceled-min");
                    pending = cancelDialog.InspectTargetAsync(); await Task.Delay(100); cancelDialog.Close();
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    check(cancelDialog.IsVisible && ReferenceEquals(original, cancelDialog.InspectedTable), "closing Operations while inspecting cancels and retains snapshot");
                    await transaction.RollbackAsync(); await cancelDialog.InspectTargetAsync();
                    check(!ReferenceEquals(original, cancelDialog.InspectedTable) && ((Button)cancelDialog.FindName("ApplyButton")).IsEnabled, "Operations retries inspection and restores Apply after cancellation");
                }
                catch (Exception ex) { error = ex; }
                finally { cancelDialog.Close(); }
            };
            cancelDialog.ShowDialog(); if (error is not null) throw error;
            var typeDialog = new TableEditDialog(connection, "public", table) { ShowInTaskbar = false };
            typeDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)typeDialog.FindName("Operation")).SelectedItem = TableOperation.CommentColumn;
                    ((TextBox)typeDialog.FindName("ColumnName")).Text = "note";
                    ((TextBox)typeDialog.FindName("CommentText")).Text = "reviewed type comment";
                    await typeDialog.InspectTargetAsync();
                    check(((TextBlock)typeDialog.FindName("InspectedColumnType")).Text.Contains("text", StringComparison.Ordinal), "column editor displays inspected type");
                    await session.ExecuteAsync($"ALTER TABLE {target} ALTER COLUMN note TYPE varchar(80);");
                    await typeDialog.ApplyAsync();
                    check(!typeDialog.WasApplied && ((TextBlock)typeDialog.FindName("Feedback")).Text.Contains("Column type changed", StringComparison.Ordinal), "WPF column edit refuses changed type");
                    TableEditChecks.Capture(typeDialog, "m51-type-conflict");
                    await typeDialog.InspectTargetAsync();
                    check(((TextBlock)typeDialog.FindName("InspectedColumnType")).Text.Contains("character varying(80)", StringComparison.Ordinal), "column type reinspection shows new modifier");
                    typeDialog.Width = typeDialog.MinWidth; typeDialog.Height = typeDialog.MinHeight;
                    TableEditChecks.Capture(typeDialog, "m51-type-review-min");
                    await typeDialog.ApplyAsync();
                }
                catch (Exception ex) { error = ex; }
                finally { typeDialog.Close(); }
            };
            typeDialog.ShowDialog(); if (error is not null) throw error;
            check(typeDialog.WasApplied && (await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").Comment == "reviewed type comment", "reviewed type applies through WPF without losing proposed comment");
            await session.ExecuteAsync($"CREATE SCHEMA {SqlIdentifier.Quote(destinationSchema)}; CREATE SCHEMA {SqlIdentifier.Quote(removedSchema)};");
            var moveDialog = new TableEditDialog(connection, "public", table) { ShowInTaskbar = false };
            moveDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)moveDialog.FindName("Operation")).SelectedItem = TableOperation.SetSchema;
                    await moveDialog.LoadDestinationSchemasAsync();
                    var picker = (ComboBox)moveDialog.FindName("DestinationSchemaPicker");
                    check(picker.Items.Contains(destinationSchema) && !picker.Items.Contains("pg_catalog") && !picker.Items.Contains("information_schema"), "schema picker loads actual non-system schemas with exact quoted names");
                    picker.SelectedItem = destinationSchema;
                    check(((TextBox)moveDialog.FindName("NewName")).Text == destinationSchema && ((TextBox)moveDialog.FindName("Preview")).Text.Contains(SqlIdentifier.Quote(destinationSchema), StringComparison.Ordinal), "schema selection preserves exact name and quoted preview");
                    ((TextBox)moveDialog.FindName("NewName")).Text = removedSchema;
                    check(picker.SelectedItem is null, "manual destination edit clears mismatched picker selection");
                    picker.SelectedItem = destinationSchema;
                    await session.ExecuteAsync($"DROP SCHEMA {SqlIdentifier.Quote(removedSchema)};");
                    await moveDialog.LoadDestinationSchemasAsync();
                    check(!picker.Items.Contains(removedSchema) && ((TextBox)moveDialog.FindName("NewName")).Text == destinationSchema, "schema refresh removes dropped schema without replacing typed destination");
                    picker.SelectedItem = destinationSchema;
                    await moveDialog.InspectTargetAsync();
                    TableEditChecks.Capture(moveDialog, "m52-schema-picker");
                    moveDialog.Width = moveDialog.MinWidth; moveDialog.Height = moveDialog.MinHeight;
                    TableEditChecks.Capture(moveDialog, "m52-schema-picker-min");
                    await moveDialog.ApplyAsync();
                }
                catch (Exception ex) { error = ex; }
                finally { moveDialog.Close(); }
            };
            moveDialog.ShowDialog(); if (error is not null) throw error;
            check(moveDialog.WasApplied && (await new TablePropertiesService(connection).InspectAsync(destinationSchema, table)).Schema == destinationSchema, "selected schema move executes on real table through WPF");
            // Further inspected editing checks.
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target} CASCADE; DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(destinationSchema)} CASCADE; DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(removedSchema)} CASCADE;"); }
    }
}
