using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class SequenceDetachChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string name = "pgms_detach_ui_" + Guid.NewGuid().ToString("N"), target = ObjectSql.Relation("public", name);
        string table = ObjectSql.Relation("public", name + "_table");
        await using var session = new QuerySession(connection); await session.OpenAsync();
        var service = new SequenceService(connection);
        try
        {
            await session.ExecuteAsync($"CREATE SEQUENCE {target}; CREATE TABLE {table}(id bigint, other bigint); ALTER SEQUENCE {target} OWNED BY {table}.id;");
            var before = await service.InspectAsync("public", name);
            SequenceRestartChecks.Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.DetachOwnedBy;
                check(!((Button)w.FindName("ScriptButton")).IsEnabled && !((Button)w.FindName("ApplyButton")).IsEnabled, "Detach requires inspection for both actions");
                check(((FrameworkElement)w.FindName("ParameterFields")).Visibility == Visibility.Collapsed, "Detach hides irrelevant numeric fields");
                await w.InspectAsync();
                check(((TextBox)w.FindName("Summary")).Text.Contains(before.OwnedBy!), "Detach shows the inspected association");
                ((Button)w.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(w.ScriptSql?.Contains("OWNED BY NONE") == true && !w.WasApplied, "Detach Script returns SQL without execution");
            });
            check(await service.InspectAsync("public", name) == before, "Detach Script preserves association");
            SequenceRestartChecks.Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.DetachOwnedBy;
                await w.InspectAsync(); await SequenceRestartChecks.Capture(w, "sequence-detach", check);
                await SequenceConfirmationChecks.RespondAsync(w, false, check);
                check(!w.WasApplied && await service.InspectAsync("public", name) == before, "Detach No preserves association and permits retry");
                await session.ExecuteAsync($"ALTER SEQUENCE {target} OWNED BY {table}.other;");
                await SequenceConfirmationChecks.RespondAsync(w, true, check);
                check(!w.WasApplied && ((TextBlock)w.FindName("Feedback")).Text.Contains("association changed"), "Detach Yes refuses concurrent reassociation");
                await w.InspectAsync();
                await SequenceConfirmationChecks.RespondAsync(w, true, check);
                check(w.WasApplied, "Detach Yes applies after reinspection");
            });
            check((await service.InspectAsync("public", name)).Dependency is null, "WPF detach removes actual dependency");
            SequenceRestartChecks.Show(new SequenceDialog(connection, "public", name) { ShowInTaskbar = false }, async w =>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem = SequenceOperation.DetachOwnedBy;
                await w.InspectAsync();
                check(!((Button)w.FindName("ApplyButton")).IsEnabled && ((TextBlock)w.FindName("Feedback")).Text.Contains("no inspected OWNED BY"), "Already detached state disables Apply with explanation");
            });
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {table}; DROP SEQUENCE IF EXISTS {target};"); }
    }
}
