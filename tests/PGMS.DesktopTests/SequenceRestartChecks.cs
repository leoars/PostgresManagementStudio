using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class SequenceRestartChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string name = "pgms_restart_ui_" + Guid.NewGuid().ToString("N"), target = ObjectSql.Relation("public", name);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        try
        {
            await session.ExecuteAsync($"CREATE SEQUENCE {target} START 10 MAXVALUE 100;");
            Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.Restart;
                var value = (TextBox)w.FindName("ParameterValue"); var apply = (Button)w.FindName("ApplyButton");
                value.Text = "50";
                check(!apply.IsEnabled && !((Button)w.FindName("ScriptButton")).IsEnabled, "Restart UI requires inspection before Script and Apply");
                await w.InspectAsync(); ((Button)w.FindName("LoadParameterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(value.Text == "10" && value.IsKeyboardFocusWithin, "Restart Load uses configured START and focuses value");
                value.Text = "101"; check(!apply.IsEnabled, "Restart UI refuses outside bounds");
                value.Text = "50";
                await Capture(w, "sequence-restart", check);
                await SequenceConfirmationChecks.RespondAsync(w, false, check);
                check(!w.WasApplied && w.IsVisible, "Restart No retains dialog without applying");
                await session.ExecuteAsync($"ALTER SEQUENCE {target} CACHE 2;");
                await SequenceConfirmationChecks.RespondAsync(w, true, check);
                check(!w.WasApplied && ((TextBlock)w.FindName("Feedback")).Text.Contains("configuration or identity changed"), "Restart Yes still checks concurrent configuration");
                await w.InspectAsync(); check(value.Text == "50", "Restart reinspection preserves proposed value");
                await SequenceConfirmationChecks.RespondAsync(w, true, check);
                check(w.WasApplied, "Restart Yes applies after reinspection");
            });
            await using var db = new Npgsql.NpgsqlConnection(connection); await db.OpenAsync();
            async Task<string?> State() { await using var c = new Npgsql.NpgsqlCommand($"SELECT last_value::text||':'||is_called::text FROM {target}", db); return (string?)await c.ExecuteScalarAsync(); }
            check(await State() == "50:false", "WPF restart writes exact unconsumed value");
            Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.Restart;
                await w.InspectAsync(); ((TextBox)w.FindName("ParameterValue")).Text = "70";
                ((Button)w.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(w.ScriptSql?.Contains("RESTART WITH 70") == true && !w.WasApplied, "Restart Script returns proposal without confirmation or execution");
            });
            check(await State() == "50:false", "Restart Script leaves current state unchanged");
        }
        finally { await session.ExecuteAsync($"DROP SEQUENCE IF EXISTS {target};"); }
    }
    internal static async Task Capture(SequenceDialog w, string name, Action<bool, string> check)
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(w, name);
        w.Width = w.MinWidth; w.Height = w.MinHeight;
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); TableEditChecks.Capture(w, name + "-min");
        var root = (FrameworkElement)w.Content; var close = (Button)w.FindName("CloseButton");
        check(close.TranslatePoint(new Point(0, close.ActualHeight), root).Y <= root.ActualHeight + .5, name + " actions fit minimum size");
    }
    internal static void Show(SequenceDialog dialog, Func<SequenceDialog, Task> action)
    {
        Exception? error = null;
        dialog.ContentRendered += async (_, _) => { try { await action(dialog); } catch (Exception ex) { error = ex; } finally { if (dialog.IsVisible) dialog.Close(); } };
        dialog.ShowDialog(); if (error is not null) throw error;
    }
}
