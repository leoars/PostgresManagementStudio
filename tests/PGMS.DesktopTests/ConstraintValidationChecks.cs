using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ConstraintValidationChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_val_ui_" + Guid.NewGuid().ToString("N");
        string target = "public." + SqlIdentifier.Quote(table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        try
        {

            await session.ExecuteAsync($"CREATE TABLE {target}(id int PRIMARY KEY, other int); INSERT INTO {target} VALUES (1,-1); ALTER TABLE {target} ADD CONSTRAINT ck CHECK(other > 0) NOT VALID;");
            var dialog61 = new CheckConstraintDialog(connection, "public", table) { ShowInTaskbar = false };
            Exception? error61 = null;
            dialog61.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)dialog61.FindName("Operation")).SelectedValue = CheckConstraintOperation.Validate;
                    await dialog61.InspectAsync();
                    ((ListBox)dialog61.FindName("ExistingConstraints")).SelectedIndex = 0;
                    check(((TextBox)dialog61.FindName("Preview")).Text.Contains("VALIDATE CONSTRAINT") && ((TextBox)dialog61.FindName("ObjectName")).Text == "ck", "CHECK WPF selects existing constraint for validation");
                    check(((FrameworkElement)dialog61.FindName("ExpressionEntry")).Visibility == Visibility.Collapsed, "CHECK Validate hides creation-only fields");
                    await dialog61.ApplyAsync();
                    check(!dialog61.WasApplied && ((TextBlock)dialog61.FindName("Feedback")).Text.StartsWith("23514:"), "CHECK validation reports invalid existing data in WPF");
                    TableEditChecks.Capture(dialog61, "m61-validation-error");
                    await session.ExecuteAsync($"UPDATE {target} SET other=1;");
                    await dialog61.InspectAsync();
                    ((ListBox)dialog61.FindName("ExistingConstraints")).SelectedIndex = 0;
                    dialog61.Width = dialog61.MinWidth; dialog61.Height = dialog61.MinHeight;
                    TableEditChecks.Capture(dialog61, "m61-validation-review-min");
                    await dialog61.ApplyAsync();
                }
                catch (Exception ex) { error61 = ex; dialog61.Close(); }
            };
            dialog61.ShowDialog(); if (error61 is not null) throw error61;
            check(dialog61.WasApplied && (await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "ck").Validated, "CHECK WPF retry validates actual constraint");
            var script61 = new CheckConstraintDialog(connection, "public", table) { ShowInTaskbar = false };
            script61.ContentRendered += (_, _) =>
            {
                ((ComboBox)script61.FindName("Operation")).SelectedValue = CheckConstraintOperation.Validate;
                ((TextBox)script61.FindName("ObjectName")).Text = "ck";
                ((Button)script61.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            script61.ShowDialog();
            check(script61.ScriptSql?.Contains("VALIDATE CONSTRAINT") == true && !script61.WasApplied, "CHECK Script returns validation SQL without Apply");
            await session.ExecuteAsync($"DROP TABLE {target};");

            await session.ExecuteAsync($"CREATE TABLE {target}(id int PRIMARY KEY, other int); INSERT INTO {target} VALUES (1,2); ALTER TABLE {target} ADD CONSTRAINT fk FOREIGN KEY(other) REFERENCES {target}(id) NOT VALID;");
            var dialog62 = new ForeignKeyDialog(connection, "public", table) { ShowInTaskbar = false };
            Exception? error62 = null;
            dialog62.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)dialog62.FindName("Operation")).SelectedValue = ForeignKeyOperation.Validate;
                    await dialog62.InspectAsync();
                    ((ListBox)dialog62.FindName("ExistingConstraints")).SelectedIndex = 0;
                    check(((TextBox)dialog62.FindName("Preview")).Text.Contains("VALIDATE CONSTRAINT") && ((TextBox)dialog62.FindName("ObjectName")).Text == "fk", "FOREIGN KEY WPF selects existing constraint for validation");
                    check(((FrameworkElement)dialog62.FindName("CreateFields")).Visibility == Visibility.Collapsed, "FOREIGN KEY Validate hides creation-only fields");
                    await dialog62.ApplyAsync();
                    check(!dialog62.WasApplied && ((TextBlock)dialog62.FindName("Feedback")).Text.StartsWith("23503:"), "FOREIGN KEY validation reports invalid existing data in WPF");
                    TableEditChecks.Capture(dialog62, "m62-validation-error");
                    await session.ExecuteAsync($"UPDATE {target} SET other=1;");
                    await dialog62.InspectAsync();
                    ((ListBox)dialog62.FindName("ExistingConstraints")).SelectedIndex = 0;
                    dialog62.Width = dialog62.MinWidth; dialog62.Height = dialog62.MinHeight;
                    TableEditChecks.Capture(dialog62, "m62-validation-review-min");
                    await dialog62.ApplyAsync();
                }
                catch (Exception ex) { error62 = ex; dialog62.Close(); }
            };
            dialog62.ShowDialog(); if (error62 is not null) throw error62;
            check(dialog62.WasApplied && (await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "fk").Validated, "FOREIGN KEY WPF retry validates actual constraint");
            var script62 = new ForeignKeyDialog(connection, "public", table) { ShowInTaskbar = false };
            script62.ContentRendered += (_, _) =>
            {
                ((ComboBox)script62.FindName("Operation")).SelectedValue = ForeignKeyOperation.Validate;
                ((TextBox)script62.FindName("ObjectName")).Text = "fk";
                ((Button)script62.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            script62.ShowDialog();
            check(script62.ScriptSql?.Contains("VALIDATE CONSTRAINT") == true && !script62.WasApplied, "FOREIGN KEY Script returns validation SQL without Apply");
            await session.ExecuteAsync($"DROP TABLE {target};");
            await session.ExecuteAsync($"CREATE TABLE {target}(other int); INSERT INTO {target} VALUES (-1);");
            var create63 = new CheckConstraintDialog(connection, "public", table) { ShowInTaskbar = false };
            Exception? error63 = null;
            create63.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((TextBox)create63.FindName("ObjectName")).Text = "ck";
                    ((TextBox)create63.FindName("Expression")).Text = "other > 0";
                    var option = (CheckBox)create63.FindName("NotValid");
                    check(option.IsChecked != true, "CHECK creation validates rows by default");
                    option.IsChecked = true;
                    check(((TextBox)create63.FindName("Preview")).Text.Contains("NOT VALID"), "CHECK NOT VALID option updates preview");
                    TableEditChecks.Capture(create63, "m63-not-valid");
                    create63.Width = create63.MinWidth; create63.Height = create63.MinHeight;
                    TableEditChecks.Capture(create63, "m63-not-valid-min");
                    ((ComboBox)create63.FindName("Operation")).SelectedValue = CheckConstraintOperation.Validate;
                    check(option.Visibility == Visibility.Collapsed && !((TextBox)create63.FindName("Preview")).Text.Contains("NOT VALID"), "CHECK Validate ignores creation option");
                    ((ComboBox)create63.FindName("Operation")).SelectedValue = CheckConstraintOperation.Add;
                    await create63.ApplyAsync();
                }
                catch (Exception ex) { error63 = ex; create63.Close(); }
            };
            create63.ShowDialog(); if (error63 is not null) throw error63;
            check(create63.WasApplied && !(await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single().Validated, "CHECK WPF creates actual NOT VALID constraint");
            await session.ExecuteAsync($"DROP TABLE {target};");
            await session.ExecuteAsync($"CREATE TABLE {target}(id int PRIMARY KEY, other int); INSERT INTO {target} VALUES (1,2);");
            var create64 = new ForeignKeyDialog(connection, "public", table) { ShowInTaskbar = false };
            Exception? error64 = null;
            create64.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((TextBox)create64.FindName("ObjectName")).Text = "fk";
                    ((TextBox)create64.FindName("Columns")).Text = "other";
                    ((TextBox)create64.FindName("ReferencedTable")).Text = table;
                    ((TextBox)create64.FindName("ReferencedColumns")).Text = "id";
                    var option = (CheckBox)create64.FindName("NotValid");
                    check(option.IsChecked != true, "FOREIGN KEY creation validates rows by default");
                    option.IsChecked = true;
                    check(((TextBox)create64.FindName("Preview")).Text.Contains("NOT VALID"), "FOREIGN KEY NOT VALID option updates preview");
                    TableEditChecks.Capture(create64, "m64-not-valid");
                    create64.Width = create64.MinWidth; create64.Height = create64.MinHeight;
                    TableEditChecks.Capture(create64, "m64-not-valid-min");
                    var close64 = (Button)create64.FindName("CloseButton");
                    var root64 = (FrameworkElement)create64.Content;
                    check(close64.TransformToAncestor(root64).Transform(new Point(0, close64.ActualHeight)).Y <= root64.ActualHeight, "FK NOT VALID actions fit minimum height");
                    ((ComboBox)create64.FindName("Operation")).SelectedValue = ForeignKeyOperation.Validate;
                    check(option.Visibility == Visibility.Collapsed && !((TextBox)create64.FindName("Preview")).Text.Contains("NOT VALID"), "FOREIGN KEY Validate ignores creation option");
                    ((ComboBox)create64.FindName("Operation")).SelectedValue = ForeignKeyOperation.Add;
                    await create64.ApplyAsync();
                }
                catch (Exception ex) { error64 = ex; create64.Close(); }
            };
            create64.ShowDialog(); if (error64 is not null) throw error64;
            check(create64.WasApplied && !(await new TablePropertiesService(connection).InspectAsync("public", table)).Constraints.Single(c => c.Name == "fk").Validated, "FOREIGN KEY WPF creates actual NOT VALID constraint");
            await session.ExecuteAsync($"DROP TABLE {target};");
            // Validation dialogs.
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};"); }
    }
}
