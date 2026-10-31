using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class SequenceParameterChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await SequenceApplyCancellationChecks.RunAsync(connection, check);
        await SequenceAttachChecks.RunAsync(connection, check);
        await SequenceRestartChecks.RunAsync(connection, check);
        await SequenceDetachChecks.RunAsync(connection, check);
        string name = "pgms_param_ui_" + Guid.NewGuid().ToString("N"), target = ObjectSql.Relation("public", name);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        try
        {
            await session.ExecuteAsync($"CREATE SEQUENCE {target} START 10 MAXVALUE 100;");
            var dialog = new SequenceDialog(connection, "public", name) { ShowInTaskbar = false };
            Show(dialog, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.SetIncrement;
                var value = (TextBox)w.FindName("ParameterValue"); var apply = (Button)w.FindName("ApplyButton");
                value.Text = "0"; check(!apply.IsEnabled, "parameter UI rejects zero increment");
                value.Text = "3"; check(!apply.IsEnabled && ((Button)w.FindName("ScriptButton")).IsEnabled, "parameter SQL review available before inspection, Apply requires snapshot");
                await w.InspectAsync(); ((Button)w.FindName("LoadParameterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(value.Text == "1", "parameter UI explicitly loads inspected increment");
                value.Text = "3"; await session.ExecuteAsync($"ALTER SEQUENCE {target} CACHE 2;");
                await w.ApplyAsync();
                check(!w.WasApplied && ((TextBlock)w.FindName("Feedback")).Text.Contains("configuration or identity changed"), "parameter conflict remains visible and dialog permits retry");
                await w.InspectAsync(); check(value.Text == "3", "parameter reinspection preserves proposal");
                await Capture(w, "sequence-increment", check);
                await w.ApplyAsync(); check(w.WasApplied, "increment Apply succeeds after reviewed reinspection");
            });
            check((await new SequenceService(connection).InspectAsync("public", name)).Increment == 3, "WPF increment persists in PostgreSQL");
            Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.SetCache;
                await w.InspectAsync(); ((Button)w.FindName("LoadParameterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var value = (TextBox)w.FindName("ParameterValue");
                check(value.Text == "2" && ((Label)w.FindName("ParameterLabel")).Content.ToString() == "New cache size:", "cache UI loads inspected value with correct label");
                value.Text = "-1"; check(!((Button)w.FindName("ApplyButton")).IsEnabled, "cache UI rejects negative proposal");
                value.Text = "7"; await Capture(w, "sequence-cache", check); await w.ApplyAsync();
                check(w.WasApplied, "cache Apply succeeds from WPF");
            });
            check((await new SequenceService(connection).InspectAsync("public", name)).Cache == 7, "WPF cache persists");
            foreach (bool enabled in new[] { true, false })
            {
                Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
                {
                    ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.SetCycle;
                    check(((TextBox)w.FindName("ParameterValue")).Visibility == Visibility.Collapsed, "cycle operation hides numeric input");
                    await w.InspectAsync(); ((Button)w.FindName("LoadParameterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(((CheckBox)w.FindName("ParameterCycle")).IsChecked == !enabled, "cycle UI loads inspected toggle");
                    ((CheckBox)w.FindName("ParameterCycle")).IsChecked = enabled;
                    if (enabled) await Capture(w, "sequence-cycle", check);
                    await w.ApplyAsync(); check(w.WasApplied, "cycle toggle Apply succeeds");
                });
                check((await new SequenceService(connection).InspectAsync("public", name)).Cycle == enabled, "WPF cycle toggle persists");
            }

            Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.SetStart;
                var value = (TextBox)w.FindName("ParameterValue"); value.Text = "101";
                await w.InspectAsync();
                check(!((Button)w.FindName("ApplyButton")).IsEnabled, "inspection reevaluates START against loaded bounds");
                ((Button)w.FindName("LoadParameterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(value.Text == "10", "START loads configured value explicitly");
                value.Text = "20"; await w.InspectAsync(); check(value.Text == "20", "START reinspection preserves proposal");
                await Capture(w, "sequence-start", check); await w.ApplyAsync();
                check(w.WasApplied, "START Apply succeeds from WPF");
            });
            check((await new SequenceService(connection).InspectAsync("public", name)).Start == 20, "WPF configured START persists");
            Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.SetBounds;
                await w.InspectAsync(); ((Button)w.FindName("LoadParameterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var minimum = (TextBox)w.FindName("ParameterValue"); var maximum = (TextBox)w.FindName("ParameterMaximum");
                check(minimum.Text == "1" && maximum.Text == "100", "bounds UI loads both inspected limits");
                minimum.Text = "40"; maximum.Text = "30";
                check(!((Button)w.FindName("ApplyButton")).IsEnabled, "bounds UI rejects inverted range");
                minimum.Text = "1"; maximum.Text = "15"; await w.InspectAsync();
                check(!((Button)w.FindName("ApplyButton")).IsEnabled && ((TextBlock)w.FindName("Feedback")).Text.Contains("include configured START"), "invalid bounds feedback survives inspection");
                maximum.Text = "200"; await Capture(w, "sequence-bounds", check);
                await w.ApplyAsync(); check(w.WasApplied, "bounds Apply succeeds from WPF");
            });
            check((await new SequenceService(connection).InspectAsync("public", name)).Maximum == 200, "WPF bounds persist");
            await session.ExecuteAsync($"SELECT setval('{target}',150,true);");
            Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.SetBounds;
                await w.InspectAsync();
                ((TextBox)w.FindName("ParameterValue")).Text = "1"; ((TextBox)w.FindName("ParameterMaximum")).Text = "100";
                await w.ApplyAsync();
                check(!w.WasApplied && ((TextBlock)w.FindName("Feedback")).Text.Contains("22023") && ((Button)w.FindName("ApplyButton")).IsEnabled, "bounds server failure preserves SQLSTATE and permits correction");
                await Capture(w, "sequence-bounds-error", check);
                ((TextBox)w.FindName("ParameterMaximum")).Text = "250"; await w.ApplyAsync();
                check(w.WasApplied, "bounds Apply succeeds after correcting server error");
            });
            var beforeType = await new SequenceService(connection).InspectAsync("public", name);
            Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.SetDataType;
                var picker = (ComboBox)w.FindName("ParameterType");
                check(picker.IsVisible && !((TextBox)w.FindName("ParameterValue")).IsVisible, "Type operation exposes selector without irrelevant numeric input");
                check(!((Button)w.FindName("ScriptButton")).IsEnabled && !((Button)w.FindName("ApplyButton")).IsEnabled, "Type review and Apply require inspection to preserve limits");
                check(System.Windows.Automation.AutomationProperties.GetName(picker) == "New sequence data type", "Type picker has accessible name");
                await w.InspectAsync(); ((Button)w.FindName("LoadParameterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check((SequenceDataType)picker.SelectedItem == SequenceDataType.BigInt && picker.IsKeyboardFocusWithin, "Load inspected type selects current type and restores picker focus");
                picker.SelectedItem = SequenceDataType.SmallInt;
                check(((TextBox)w.FindName("Preview")).Text.Contains("AS smallint MINVALUE 1 MAXVALUE 250"), "Type preview exposes preserved bounds");
                await session.ExecuteAsync($"ALTER SEQUENCE {target} CACHE 2;");
                await w.ApplyAsync();
                check(!w.WasApplied && ((TextBlock)w.FindName("Feedback")).Text.Contains("configuration or identity changed"), "Type UI preserves concurrent change and explains reinspection");
                await w.InspectAsync();
                check((SequenceDataType)picker.SelectedItem == SequenceDataType.SmallInt, "Type reinspection preserves proposed selection");
                await Capture(w, "sequence-type", check);
                await w.ApplyAsync(); check(w.WasApplied, "Type Apply executes through WPF after reviewed reinspection");
            });
            check(await new SequenceService(connection).InspectAsync("public", name) == beforeType with { DataType = "smallint", Cache = 2 }, "WPF type change preserves other configuration");
            var typeScriptSnapshot = await new SequenceService(connection).InspectAsync("public", name);
            Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.SetDataType;
                await w.InspectAsync(); ((ComboBox)w.FindName("ParameterType")).SelectedItem = SequenceDataType.Integer;
                ((Button)w.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(w.ScriptSql?.Contains("AS integer MINVALUE 1 MAXVALUE 250") == true && !w.WasApplied, "Type Script returns SQL without applying it");
            });
            check(await new SequenceService(connection).InspectAsync("public", name) == typeScriptSnapshot, "Type Script leaves inspected database state unchanged");
            var beforeScript = await new SequenceService(connection).InspectAsync("public", name);
            Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.SetBounds;
                ((TextBox)w.FindName("ParameterValue")).Text = "-5"; ((TextBox)w.FindName("ParameterMaximum")).Text = "500";
                ((Button)w.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(w.ScriptSql?.Contains("MINVALUE -5 MAXVALUE 500") == true && !w.WasApplied, "parameter Script returns reviewed SQL without Apply");
                return Task.CompletedTask;
            });
            check(await new SequenceService(connection).InspectAsync("public", name) == beforeScript, "parameter Script preserves database configuration");
        }
        finally { await session.ExecuteAsync($"DROP SEQUENCE IF EXISTS {target};"); }
    }
    private static async Task Capture(SequenceDialog w, string name, Action<bool, string> check)
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(w, name);
        w.Width = w.MinWidth; w.Height = w.MinHeight;
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(w, name + "-min");
        var root = (FrameworkElement)w.Content; var close = (Button)w.FindName("CloseButton");
        check(close.TranslatePoint(new Point(0, close.ActualHeight), root).Y <= root.ActualHeight + .5, name + " actions fit minimum size");
    }
    private static void Show(SequenceDialog dialog, Func<SequenceDialog, Task> action)
    {
        Exception? error = null;
        dialog.ContentRendered += async (_, _) => { try { await action(dialog); } catch (Exception ex) { error = ex; } finally { if (dialog.IsVisible) dialog.Close(); } };
        dialog.ShowDialog(); if (error is not null) throw error;
    }
}
