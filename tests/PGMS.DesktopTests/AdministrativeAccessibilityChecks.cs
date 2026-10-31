using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class AdministrativeAccessibilityChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_access_" + Guid.NewGuid().ToString("N");
        string target = ObjectSql.Relation("public", table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {target}(id int PRIMARY KEY, other int CHECK(other > 0), FOREIGN KEY(other) REFERENCES {target}(id));");
            await session.ExecuteAsync($"COMMENT ON TABLE {target} IS 'catalog comment';");
            // A catalog lock can block backend startup before cancellation is available.
            // Reuse the connection warmed by the first inspection to test query cancellation.
            string uiConnection = new NpgsqlConnectionStringBuilder(connection) { Pooling = true, ApplicationName = "PGMS.Accessibility." + Guid.NewGuid().ToString("N") }.ConnectionString;
            var indexes = new RelationEditDialog(uiConnection, "public", table);
            await ExerciseAsync(indexes, indexes.InspectAsync, uiConnection, "pg_index", "m88", check);
            var keys = new TableConstraintDialog(uiConnection, "public", table);
            await ExerciseAsync(keys, keys.InspectAsync, uiConnection, "pg_constraint", "m89", check);
            var checks = new CheckConstraintDialog(uiConnection, "public", table);
            await ExerciseAsync(checks, checks.InspectAsync, uiConnection, "pg_constraint", "m90", check);
            var foreignKeys = new ForeignKeyDialog(uiConnection, "public", table);
            await ExerciseAsync(foreignKeys, foreignKeys.InspectAsync, uiConnection, "pg_constraint", "m91", check);
            var tableEdits = new TableEditDialog(uiConnection, "public", table);
            await ExerciseAsync(tableEdits, tableEdits.InspectTargetAsync, uiConnection, "pg_index", "m92", check);
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};"); }
    }

    private static async Task ExerciseAsync(Window dialog, Func<Task> inspect, string connection, string catalog, string milestone, Action<bool, string> check)
    {
        dialog.ShowInTaskbar = false;
        Exception? error = null;
        dialog.ContentRendered += async (_, _) =>
        {
            try
            {
                var operation = (ComboBox)dialog.FindName("Operation");
                var schema = (TextBox)dialog.FindName("SchemaName");
                var table = (TextBox)dialog.FindName("TableName");
                var inspectButton = (Button)(dialog.FindName("InspectButton") ?? dialog.FindName("InspectTargetButton"));
                var cancel = (Button)dialog.FindName("CancelInspectionButton");
                var preview = (TextBox)dialog.FindName("Preview");
                var feedback = (TextBlock)dialog.FindName("Feedback");
                void Assert(bool ok, string text) => check(ok, milestone + " " + text);
                Assert(operation.IsKeyboardFocused, "initial focus is Operation");
                operation.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Assert(schema.IsKeyboardFocused, "Tab reaches schema");
                schema.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Assert(table.IsKeyboardFocused, "Tab reaches table");
                table.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                Assert(schema.IsKeyboardFocused, "Shift Tab returns to schema");
                AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), "t", false);
                Assert(table.IsKeyboardFocused, "table mnemonic focuses its target");
                Assert(!((Button)dialog.FindName("ApplyButton")).IsDefault, "Enter does not default to Apply");

                foreach (var field in new[] { ("ObjectName", "reviewed_name"), ("Columns", "id"), ("Expression", "other > 0"), ("ReferencedTable", table.Text), ("ReferencedColumns", "id"), ("NewName", "reviewed_name"), ("ColumnName", "id"), ("DefaultValue", "1"), ("CommentText", "reviewed comment") })
                    if (dialog.FindName(field.Item1) is TextBox input) input.Text = field.Item2;
                for (int i = 0; i < operation.Items.Count; i++)
                {
                    operation.SelectedIndex = i;
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var fields = Descendants(dialog).OfType<Control>().Where(c => c is TextBox or ComboBox or ListBox && c.IsVisible && c.Name.Length > 0 && ReferenceEquals(dialog.FindName(c.Name), c)).ToArray();
                    var unnamed = fields.Where(c => string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(c)?.GetName())).Select(c => c.Name).ToArray();
                    Assert(fields.Length >= 4 && unnamed.Length == 0, $"operation {i} exposes named fields/list/preview ({string.Join(",", unnamed)})");
                    Assert(new TextBlockAutomationPeer(feedback).GetName() == feedback.Text, "feedback exposes actual message");
                }
                operation.SelectedIndex = 0;
                inspectButton.Focus(); await inspect();
                Assert(inspectButton.IsKeyboardFocused, "successful inspection restores initiating focus");
                await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString); await blocker.OpenAsync();
                await using (var transaction = await blocker.BeginTransactionAsync())
                {
                    await using (var command = new NpgsqlCommand($"LOCK TABLE pg_catalog.{catalog} IN ACCESS EXCLUSIVE MODE", blocker, transaction)) await command.ExecuteNonQueryAsync();
                    foreach (bool moveFocus in new[] { false, true })
                    {
                        // Seed an observation before inspection starts: polling must refresh
                        // PostgreSQL's transaction-local activity snapshot to see the new wait.
                        await using (var seed = new NpgsqlCommand("SELECT pg_catalog.pg_stat_clear_snapshot(); SELECT count(*) FROM pg_catalog.pg_stat_activity", blocker, transaction))
                            await seed.ExecuteScalarAsync();
                        inspectButton.Focus();
                        Task pending = inspect();
                        try
                        {
                        string? initialFocus = (Keyboard.FocusedElement as FrameworkElement)?.Name;
                        await WaitForCatalogLockAsync(blocker, transaction, new NpgsqlConnectionStringBuilder(connection).ApplicationName ?? throw new InvalidOperationException("Missing test application name."), catalog, pending);
                        Assert(!pending.IsCompleted && cancel.IsKeyboardFocused,  $"blocked read focuses Cancel Inspection (completed={pending.IsCompleted}, focus={(Keyboard.FocusedElement as FrameworkElement)?.Name}, feedback={feedback.Text}, initialFocus={initialFocus}, active={dialog.IsActive})");
                        if (moveFocus)
                        {
                            preview.Focus();
                            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        else AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), "p", false);
                        await pending.WaitAsync(TimeSpan.FromSeconds(10));
                        Assert(feedback.Text.Contains("canceled") && (moveFocus ? preview.IsKeyboardFocused : inspectButton.IsKeyboardFocused), moveFocus ? "completion preserves user-selected preview focus" : "cancel mnemonic restores initiating focus");
                        }
                        finally
                        {
                            if (!pending.IsCompleted)
                            {
                                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                await pending.WaitAsync(TimeSpan.FromSeconds(10));
                            }
                        }
                    }
                    await transaction.RollbackAsync();
                }
                inspectButton.Focus(); await inspect();
                Assert(inspectButton.IsKeyboardFocused && !cancel.IsEnabled, "retry restores focus and actions");
                if (dialog is TableEditDialog edits)
                {
                    operation.SelectedItem = TableOperation.SetSchema;
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var namePeer = new TextBoxAutomationPeer((TextBox)dialog.FindName("NewName"));
                    Assert(namePeer.GetName().Contains("Destination schema"), "name field announces destination schema in Move");
                    var load = (Button)dialog.FindName("LoadSchemasButton"); load.Focus();
                    await edits.LoadDestinationSchemasAsync();
                    Assert(load.IsKeyboardFocused && new ButtonAutomationPeer(cancel).GetAccessKey().EndsWith("P", StringComparison.OrdinalIgnoreCase), "schema loading restores focus and inspection cancel mnemonic");
                    operation.SelectedItem = TableOperation.CommentTable;
                    ((Button)dialog.FindName("LoadCommentButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var comment = (TextBox)dialog.FindName("CommentText");
                    Assert(comment.IsKeyboardFocused && comment.SelectedText == "catalog comment", "loading comment focuses and selects catalog text");
                    operation.SelectedItem = TableOperation.Rename;
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert(namePeer.GetName().Contains("New name"), "name field returns to rename label");
                }
                TableEditChecks.Capture(dialog, milestone + "-accessible");
                dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var root = (FrameworkElement)dialog.Content;
                var close = (Button)dialog.FindName("CloseButton");
                Point bottom = close.TranslatePoint(new Point(close.ActualWidth, close.ActualHeight), root);
                Assert(bottom.X <= root.ActualWidth + .5 && bottom.Y <= root.ActualHeight + .5, "actions fit existing minimum size");
                if ((dialog.FindName("ExistingConstraints") ?? dialog.FindName("ExistingIndexes")) is ListBox list && list.Items.Count > 0)
                {
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    var viewport = Descendants(list).OfType<ScrollContentPresenter>().First();
                    Assert(viewport.ActualHeight >= row.ActualHeight - .5, $"minimum list viewport fits a complete row above its scrollbar ({viewport.ActualHeight:F1}/{row.ActualHeight:F1})");
                }
                TableEditChecks.Capture(dialog, milestone + "-accessible-min");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                for (int retry = 0; retry < 100 && dialog.IsVisible; retry++) { dialog.Close(); if (dialog.IsVisible) await Task.Delay(50); }
                if (dialog.IsVisible) { Console.Error.WriteLine("Test dialog did not close after cancellation."); Environment.Exit(1); }
            }
        };
        dialog.ShowDialog();
        if (error is not null) throw error;
        await Task.CompletedTask;
    }

    private static async Task WaitForCatalogLockAsync(NpgsqlConnection observer, NpgsqlTransaction transaction, string applicationName, string catalog, Task pending)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && !pending.IsCompleted)
        {
            await using (var refresh = new NpgsqlCommand("SELECT pg_catalog.pg_stat_clear_snapshot()", observer, transaction))
                await refresh.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE pid<>pg_backend_pid() AND wait_event_type='Lock' AND application_name=@application AND position(@catalog in query)>0)", observer, transaction);
            command.Parameters.AddWithValue("catalog", catalog);
            command.Parameters.AddWithValue("application", applicationName);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(20);
        }
        throw new Exception($"Inspection never reached catalog lock wait; completed={pending.IsCompleted}.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
