using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ConstraintIdentityChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_constraint_ui_" + Guid.NewGuid().ToString("N");
        string target = "public." + SqlIdentifier.Quote(table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {target}(id int CONSTRAINT inspected PRIMARY KEY);");
            var dialog = new TableConstraintDialog(connection, "public", table) { ShowInTaskbar = false };
            Exception? error = null;
            dialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)dialog.FindName("Operation")).SelectedValue = TableConstraintOperation.Drop;
                    await dialog.InspectAsync();
                    ((ListBox)dialog.FindName("ExistingConstraints")).SelectedIndex = 0;
                    var request = (TableConstraintEdit)typeof(TableConstraintDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                    check(request.ExpectedIdentity is not null, "constraint row selection carries identity into WPF request");
                    TableEditChecks.Capture(dialog, "m53-inspected-constraint");
                    await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};");
                    await dialog.InspectAsync();
                    request = (TableConstraintEdit)typeof(TableConstraintDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                    check(request.ExpectedIdentity is not null, "failed constraint inspection retains prior identity protection");
                    dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                    TableEditChecks.Capture(dialog, "m53-inspection-error-min");
                    ((TextBox)dialog.FindName("TableName")).Text = "different_table";
                    request = (TableConstraintEdit)typeof(TableConstraintDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                    check(request.ExpectedIdentity is null, "changing table clears constraint identity");
                }
                catch (Exception ex) { error = ex; }
                finally { dialog.Close(); }
            };
            dialog.ShowDialog(); if (error is not null) throw error;

            await session.ExecuteAsync($"CREATE TABLE {target}(id int UNIQUE, other int); ALTER TABLE {target} ADD CONSTRAINT inspected CHECK(id > 0);");
            var dialog54 = new CheckConstraintDialog(connection, "public", table) { ShowInTaskbar = false };
            error = null;
            dialog54.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)dialog54.FindName("Operation")).SelectedValue = CheckConstraintOperation.Drop;
                    await dialog54.InspectAsync();
                    ((ListBox)dialog54.FindName("ExistingConstraints")).SelectedIndex = 0;
                    var request = (CheckConstraintEdit)typeof(CheckConstraintDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog54)!;
                    check(request.ExpectedIdentity is not null, "CHECK row selection carries identity into WPF request");
                    TableEditChecks.Capture(dialog54, "m54-inspected-constraint");
                    ((TextBox)dialog54.FindName("ObjectName")).Text = "manual_name";
                    request = (CheckConstraintEdit)typeof(CheckConstraintDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog54)!;
                    check(request.ExpectedIdentity is null, "CHECK unmatched manual name does not reuse another identity");
                    ((TextBox)dialog54.FindName("ObjectName")).Text = "inspected";
                    dialog54.Width = dialog54.MinWidth; dialog54.Height = dialog54.MinHeight;
                    TableEditChecks.Capture(dialog54, "m54-inspected-constraint-min");
                    await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};");
                    await dialog54.InspectAsync();
                    request = (CheckConstraintEdit)typeof(CheckConstraintDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog54)!;
                    check(request.ExpectedIdentity is not null, "CheckConstraint missing table does not erase prior identity snapshot");
                    check(((TextBlock)dialog54.FindName("Feedback")).Text.Contains("not found"), "CheckConstraint inspection error remains visible after busy state ends");
                    TableEditChecks.Capture(dialog54, "m56-checkconstraint-missing-min");
                    ((TextBox)dialog54.FindName("TableName")).Text = "other_table";
                    request = (CheckConstraintEdit)typeof(CheckConstraintDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog54)!;
                    check(request.ExpectedIdentity is null, "CHECK target change clears snapshot identity");
                }
                catch (Exception ex) { error = ex; }
                finally { dialog54.Close(); }
            };
            dialog54.ShowDialog(); if (error is not null) throw error;
            await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};");

            await session.ExecuteAsync($"CREATE TABLE {target}(id int UNIQUE, other int); ALTER TABLE {target} ADD CONSTRAINT inspected FOREIGN KEY(other) REFERENCES {target}(id);");
            var dialog55 = new ForeignKeyDialog(connection, "public", table) { ShowInTaskbar = false };
            error = null;
            dialog55.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)dialog55.FindName("Operation")).SelectedValue = ForeignKeyOperation.Drop;
                    await dialog55.InspectAsync();
                    ((ListBox)dialog55.FindName("ExistingConstraints")).SelectedIndex = 0;
                    var request = (ForeignKeyEdit)typeof(ForeignKeyDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog55)!;
                    check(request.ExpectedIdentity is not null, "FOREIGN KEY row selection carries identity into WPF request");
                    TableEditChecks.Capture(dialog55, "m55-inspected-constraint");
                    ((TextBox)dialog55.FindName("ObjectName")).Text = "manual_name";
                    request = (ForeignKeyEdit)typeof(ForeignKeyDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog55)!;
                    check(request.ExpectedIdentity is null, "FOREIGN KEY unmatched manual name does not reuse another identity");
                    ((TextBox)dialog55.FindName("ObjectName")).Text = "inspected";
                    dialog55.Width = dialog55.MinWidth; dialog55.Height = dialog55.MinHeight;
                    TableEditChecks.Capture(dialog55, "m55-inspected-constraint-min");
                    await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};");
                    await dialog55.InspectAsync();
                    request = (ForeignKeyEdit)typeof(ForeignKeyDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog55)!;
                    check(request.ExpectedIdentity is not null, "ForeignKey missing table does not erase prior identity snapshot");
                    check(((TextBlock)dialog55.FindName("Feedback")).Text.Contains("not found"), "ForeignKey inspection error remains visible after busy state ends");
                    TableEditChecks.Capture(dialog55, "m56-foreignkey-missing-min");
                    ((TextBox)dialog55.FindName("TableName")).Text = "other_table";
                    request = (ForeignKeyEdit)typeof(ForeignKeyDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog55)!;
                    check(request.ExpectedIdentity is null, "FOREIGN KEY target change clears snapshot identity");
                }
                catch (Exception ex) { error = ex; }
                finally { dialog55.Close(); }
            };
            dialog55.ShowDialog(); if (error is not null) throw error;
            await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};");

            await session.ExecuteAsync($"CREATE TABLE {target}(id int UNIQUE, other int);");
            foreach (bool isCheck in new[] { true, false })
            {
                Window feedbackDialog = isCheck ? new CheckConstraintDialog(connection, "public", table) : new ForeignKeyDialog(connection, "public", table);
                feedbackDialog.ShowInTaskbar = false;
                Func<Task> inspect = isCheck ? ((CheckConstraintDialog)feedbackDialog).InspectAsync : ((ForeignKeyDialog)feedbackDialog).InspectAsync;
                Func<Task> apply = isCheck ? ((CheckConstraintDialog)feedbackDialog).ApplyAsync : ((ForeignKeyDialog)feedbackDialog).ApplyAsync;
                string label = isCheck ? "check" : "fk";
                error = null;
                feedbackDialog.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        await inspect();
                        check(((TextBlock)feedbackDialog.FindName("Feedback")).Text.StartsWith("No "), label + " empty inspection result stays visible");
                        ((TextBox)feedbackDialog.FindName("ObjectName")).Text = "feedback_" + label;
                        if (isCheck) ((TextBox)feedbackDialog.FindName("Expression")).Text = "missing_column > 0";
                        else
                        {
                            ((TextBox)feedbackDialog.FindName("Columns")).Text = "missing_column";
                            ((TextBox)feedbackDialog.FindName("ReferencedTable")).Text = table;
                            ((TextBox)feedbackDialog.FindName("ReferencedColumns")).Text = "id";
                        }
                        await apply();
                        check(((TextBlock)feedbackDialog.FindName("Feedback")).Text.StartsWith("42703:"), label + " PostgreSQL SQLSTATE remains visible after failed Apply");
                        check(((Button)feedbackDialog.FindName("ApplyButton")).IsEnabled && ((Button)feedbackDialog.FindName("CloseButton")).IsEnabled, label + " failed Apply restores retry and close controls");
                        TableEditChecks.Capture(feedbackDialog, "m57-" + label + "-apply-error");
                        feedbackDialog.Width = feedbackDialog.MinWidth; feedbackDialog.Height = feedbackDialog.MinHeight;
                        feedbackDialog.UpdateLayout();
                        check(((TextBox)feedbackDialog.FindName("Preview")).ActualHeight >= 64, label + " SQL preview remains readable at minimum size");
                        TableEditChecks.Capture(feedbackDialog, "m57-" + label + "-apply-error-min");
                        if (isCheck) ((TextBox)feedbackDialog.FindName("Expression")).Text = "id > 0";
                        else ((TextBox)feedbackDialog.FindName("Columns")).Text = "other";
                        await apply();
                    }
                    catch (Exception ex) { error = ex; feedbackDialog.Close(); }
                };
                feedbackDialog.ShowDialog(); if (error is not null) throw error;
                check(isCheck ? ((CheckConstraintDialog)feedbackDialog).WasApplied : ((ForeignKeyDialog)feedbackDialog).WasApplied, label + " corrected input successfully retries real Apply");
                int count = isCheck ? (await new CheckConstraintService(connection).InspectAsync("public", table)).Count : (await new ForeignKeyService(connection).InspectAsync("public", table)).Count;
                check(count == 1, label + " successful retry created the real constraint");
            }
            // Additional constraint dialogs.
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target} CASCADE;"); }
    }
}
