using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class SequenceChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await SequenceConfirmationChecks.RunAsync(connection, check);
        await SequenceAccessibilityChecks.RunAsync(connection, check);
        await SequenceParameterChecks.RunAsync(connection, check);
        string name = "pgms_seq_ui_" + Guid.NewGuid().ToString("N");
        string target = ObjectSql.Relation("public", name);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        try
        {
            await session.ExecuteAsync($"CREATE SEQUENCE {target} START 42 CACHE 3;");
            var dialog = new SequenceDialog(connection, "public", name) { ShowInTaskbar = false };
            Exception? error = null;
            dialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((Button)dialog.FindName("InspectButton")).Focus();
                    await dialog.InspectAsync();
                    check(((Button)dialog.FindName("InspectButton")).IsKeyboardFocused, "inspection restores initiating focus after success");
                    var snapshot = dialog.Snapshot;
                    check(snapshot?.Start == 42 && ((TextBox)dialog.FindName("Summary")).Text.Contains("Cache: 3"), "sequence WPF inspection displays configuration");
                    await session.ExecuteAsync($"ALTER SEQUENCE {target} RENAME TO {SqlIdentifier.Quote(name + "_away")};");
                    await dialog.InspectAsync();
                    check(ReferenceEquals(snapshot, dialog.Snapshot) && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("not found"), "failed sequence reinspection retains snapshot and useful error");
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(dialog, "sequence-inspection-error");
                    await session.ExecuteAsync($"ALTER SEQUENCE {ObjectSql.Relation("public", name + "_away")} RENAME TO {SqlIdentifier.Quote(name)};");
                    await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
                    await using var tx = await blocker.BeginTransactionAsync();
                    await using (var command = new NpgsqlCommand("LOCK TABLE pg_catalog.pg_sequence IN ACCESS EXCLUSIVE MODE", blocker, tx)) await command.ExecuteNonQueryAsync();
                    ((Button)dialog.FindName("InspectButton")).Focus();
                    var pending = dialog.InspectAsync(); await Task.Delay(150);
                    check(((Button)dialog.FindName("CancelInspectionButton")).IsKeyboardFocused, "pending inspection focuses cancellation");
                    check(!pending.IsCompleted && ((Button)dialog.FindName("CancelInspectionButton")).IsEnabled, "blocked sequence read exposes cancellation");
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(dialog, "sequence-inspection-busy");
                    ((Button)dialog.FindName("CancelInspectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    check(((Button)dialog.FindName("InspectButton")).IsKeyboardFocused, "canceled inspection restores initiating focus");
                    check(ReferenceEquals(snapshot, dialog.Snapshot) && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("canceled"), "sequence cancellation retains prior snapshot");
                    await tx.RollbackAsync(); await dialog.InspectAsync();
                    check(dialog.Snapshot is not null && !ReferenceEquals(snapshot, dialog.Snapshot), "sequence inspection retries after cancellation");
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(dialog, "sequence-inspect");
                    dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(dialog, "sequence-inspect-min");
                    ((TextBox)dialog.FindName("SequenceName")).Text = name + "_new";
                    check(dialog.Snapshot is null && ((TextBox)dialog.FindName("Summary")).Text == "", "changing sequence target clears snapshot");
                    dialog.Close();
                }
                catch (Exception ex) { error = ex; dialog.Close(); }
            };
            dialog.ShowDialog(); if (error is not null) throw error;
            var stale = new SequenceDialog(connection, "public", name, uint.MaxValue) { ShowInTaskbar = false };
            stale.ContentRendered += async (_, _) =>
            {
                try { await stale.InspectAsync(); check(stale.Snapshot is null && ((TextBlock)stale.FindName("Feedback")).Text.Contains("Explorer"), "sequence Explorer handoff refuses stale OID"); }
                catch (Exception ex) { error = ex; }
                finally { stale.Close(); }
            };
            stale.ShowDialog(); if (error is not null) throw error;
            var script = new SequenceDialog(connection, "public", name + "_script") { ShowInTaskbar = false };
            Show(script, async window =>
            {
                ((ComboBox)window.FindName("Operation")).SelectedItem = SequenceOperation.Create;
                ((Button)window.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(window.ScriptSql?.StartsWith("CREATE SEQUENCE") == true && !window.WasApplied, "sequence Script returns create SQL without Apply");
                await Task.CompletedTask;
            });
            bool scriptAbsent = false; try { await new SequenceService(connection).InspectAsync("public", name + "_script"); } catch (InvalidOperationException) { scriptAbsent = true; }
            check(scriptAbsent, "sequence Script does not create a server object");
            var createDialog = new SequenceDialog(connection, "public", name) { ShowInTaskbar = false };
            Show(createDialog, async window =>
            {
                TextBox Field(string field) => (TextBox)window.FindName(field);
                ((ComboBox)window.FindName("Operation")).SelectedItem = SequenceOperation.Create;
                Field("Increment").Text = "0";
                check(!((Button)window.FindName("ApplyButton")).IsEnabled, "zero sequence increment disables Apply");
                Field("Increment").Text = "2"; Field("StartValue").Text = "1"; Field("Minimum").Text = "1"; Field("Maximum").Text = "5"; Field("Cache").Text = "2";
                ((ComboBox)window.FindName("DataType")).SelectedItem = SequenceDataType.SmallInt; ((CheckBox)window.FindName("Cycle")).IsChecked = true;
                await window.ApplyAsync();
                check(!window.WasApplied && ((TextBlock)window.FindName("Feedback")).Text.Contains("42P07") && ((Button)window.FindName("ApplyButton")).IsEnabled, "duplicate sequence Apply retains SQLSTATE and permits retry");
                Field("SequenceName").Text = name + "_created";
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(window, "sequence-create");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(window, "sequence-create-min");
                var root = (FrameworkElement)window.Content; var close = (Button)window.FindName("CloseButton");
                check(close.TranslatePoint(new Point(0, close.ActualHeight), root).Y <= root.ActualHeight + 0.5, "sequence Create actions fit minimum size");
                await window.ApplyAsync(); check(window.WasApplied, "sequence Create Apply succeeds from WPF");
            });
            var created = await new SequenceService(connection).InspectAsync("public", name + "_created");
            check(created.DataType == "smallint" && created.Increment == 2 && created.Start == 1 && created.Minimum == 1 && created.Maximum == 5 && created.Cache == 2 && created.Cycle, "WPF sequence options persist in PostgreSQL");
            var renameDialog = new SequenceDialog(connection, "public", name) { ShowInTaskbar = false };
            uint renameOid = 0;
            Show(renameDialog, async window =>
            {
                ((ComboBox)window.FindName("Operation")).SelectedItem = SequenceOperation.Rename;
                ((TextBox)window.FindName("NewName")).Text = name + "_renamed";
                check(!((Button)window.FindName("ApplyButton")).IsEnabled && ((Button)window.FindName("ScriptButton")).IsEnabled, "sequence Rename requires inspection for Apply but permits Script");
                await window.InspectAsync();
                await session.ExecuteAsync($"DROP SEQUENCE {target}; CREATE SEQUENCE {target};");
                await window.ApplyAsync();
                check(!window.WasApplied && ((TextBlock)window.FindName("Feedback")).Text.Contains("identity changed"), "WPF rename rejects a replaced sequence");
                await window.InspectAsync(); renameOid = window.Snapshot!.Oid;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(window, "sequence-rename");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(window, "sequence-rename-min");
                await window.ApplyAsync(); check(window.WasApplied, "sequence Rename succeeds after reinspection");
            });
            check((await new SequenceService(connection).InspectAsync("public", name + "_renamed")).Oid == renameOid, "WPF rename preserves inspected sequence OID");
            await session.ExecuteAsync($"ALTER SEQUENCE {ObjectSql.Relation("public", name + "_renamed")} RENAME TO {SqlIdentifier.Quote(name)};");
            await session.ExecuteAsync($"COMMENT ON SEQUENCE {target} IS 'server comment';");
            var commentDialog = new SequenceDialog(connection, "public", name) { ShowInTaskbar = false };
            Show(commentDialog, async window =>
            {
                ((ComboBox)window.FindName("Operation")).SelectedItem = SequenceOperation.Comment;
                check(!((Button)window.FindName("ApplyButton")).IsEnabled, "empty sequence comment disables Apply");
                await window.InspectAsync(); ((Button)window.FindName("LoadCommentButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var text = (TextBox)window.FindName("CommentText");
                check(text.IsKeyboardFocused && text.SelectedText == "server comment", "loading comment focuses and selects editable text");
                check(text.Text == "server comment", "sequence loads inspected comment only on explicit action");
                text.Text = "reviewed ' comment"; await window.InspectAsync();
                check(text.Text == "reviewed ' comment", "sequence reinspection preserves proposed comment");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(window, "sequence-comment");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(window, "sequence-comment-min");
                await window.ApplyAsync(); check(window.WasApplied, "sequence comment Apply succeeds from WPF");
            });
            check((await new SequenceService(connection).InspectAsync("public", name)).Comment == "reviewed ' comment", "WPF sequence comment persists exactly");
            var removeScript = new SequenceDialog(connection, "public", name) { ShowInTaskbar = false };
            Show(removeScript, window =>
            {
                ((ComboBox)window.FindName("Operation")).SelectedItem = SequenceOperation.Comment;
                ((CheckBox)window.FindName("RemoveComment")).IsChecked = true;
                ((Button)window.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(window.ScriptSql?.EndsWith("IS NULL;") == true, "sequence removal Script uses SQL NULL");
                return Task.CompletedTask;
            });
            check((await new SequenceService(connection).InspectAsync("public", name)).Comment is not null, "sequence removal Script preserves actual comment");
            var remove = new SequenceDialog(connection, "public", name) { ShowInTaskbar = false };
            Show(remove, async window =>
            {
                ((ComboBox)window.FindName("Operation")).SelectedItem = SequenceOperation.Comment;
                ((CheckBox)window.FindName("RemoveComment")).IsChecked = true;
                await window.InspectAsync(); await window.ApplyAsync();
                check(window.WasApplied, "sequence comment removal Apply succeeds");
            });
            check((await new SequenceService(connection).InspectAsync("public", name)).Comment is null, "sequence comment removed in PostgreSQL from WPF");
            var dropScript = new SequenceDialog(connection, "public", name) { ShowInTaskbar = false };
            Show(dropScript, async window =>
            {
                ((ComboBox)window.FindName("Operation")).SelectedItem = SequenceOperation.Drop;
                check(!((Button)window.FindName("ApplyButton")).IsEnabled && ((StackPanel)window.FindName("CreateFields")).Visibility == Visibility.Collapsed && ((StackPanel)window.FindName("CommentFields")).Visibility == Visibility.Collapsed, "sequence Drop hides unrelated fields and requires inspection");
                await window.InspectAsync();
                check(((Button)window.FindName("ApplyButton")).IsEnabled && ((TextBox)window.FindName("Preview")).Text.EndsWith(" RESTRICT;"), "inspected sequence Drop enables restrictive SQL");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(window, "sequence-drop");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(window, "sequence-drop-min");
                ((Button)window.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(window.ScriptSql?.StartsWith("DROP SEQUENCE") == true && !window.WasApplied, "sequence Drop Script returns SQL without Apply");
            });
            check((await new SequenceService(connection).InspectAsync("public", name)).Oid > 0, "sequence Drop Script preserves actual sequence");
        }
        finally { await session.ExecuteAsync($"DROP SEQUENCE IF EXISTS {target}; DROP SEQUENCE IF EXISTS {ObjectSql.Relation("public", name + "_away")}; DROP SEQUENCE IF EXISTS {ObjectSql.Relation("public", name + "_created")}; DROP SEQUENCE IF EXISTS {ObjectSql.Relation("public", name + "_renamed")};"); }
    }
    private static void Show(SequenceDialog dialog, Func<SequenceDialog, Task> action)
    {
        Exception? error = null;
        dialog.ContentRendered += async (_, _) =>
        {
            try { await action(dialog); }
            catch (Exception ex) { error = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        };
        dialog.ShowDialog();
        if (error is not null) throw error;
    }
}
