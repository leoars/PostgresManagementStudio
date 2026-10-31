using Npgsql;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ConstraintCancellationChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_cancel_" + Guid.NewGuid().ToString("N");
        string target = "public." + SqlIdentifier.Quote(table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {target}(id int CONSTRAINT pk PRIMARY KEY, other int CONSTRAINT ck CHECK(other > 0), CONSTRAINT fk FOREIGN KEY(other) REFERENCES {target}(id));");
            var dialog58 = new TableConstraintDialog(connection, "public", table);
            await ExerciseAsync(dialog58, dialog58.InspectAsync, connection, "m58", check);
            var dialog59 = new CheckConstraintDialog(connection, "public", table);
            await ExerciseAsync(dialog59, dialog59.InspectAsync, connection, "m59", check);
            var dialog60 = new ForeignKeyDialog(connection, "public", table);
            await ExerciseAsync(dialog60, dialog60.InspectAsync, connection, "m60", check);
            // Dialog cases.
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};"); }
    }

    private static async Task ExerciseAsync(Window dialog, Func<Task> inspect, string connection, string milestone, Action<bool, string> check)
    {
        dialog.ShowInTaskbar = false;
        Exception? error = null;
        dialog.ContentRendered += async (_, _) =>
        {
            try
            {
                var operation = (ComboBox)dialog.FindName("Operation");
                operation.SelectedItem = operation.Items.Cast<object>().Single(item => ((string)item.GetType().GetProperty("Label")!.GetValue(item)!).StartsWith("Drop"));
                await inspect();
                var list = (ListBox)dialog.FindName("ExistingConstraints");
                list.SelectedIndex = 0;
                check(((Button)dialog.FindName("ApplyButton")).IsEnabled, milestone + " inspected Drop is valid before blocking inspection");
                object original = list.Items[0];
                await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
                await using var transaction = await blocker.BeginTransactionAsync();
                await using (var command = new NpgsqlCommand("LOCK TABLE pg_catalog.pg_constraint IN ACCESS EXCLUSIVE MODE", blocker, transaction)) await command.ExecuteNonQueryAsync();
                foreach (string action in new[] { "button", "escape", "close" })
                {
                    Task pending = inspect(); await Task.Delay(150);
                    check(!pending.IsCompleted && !((Button)dialog.FindName("ApplyButton")).IsEnabled, milestone + " blocked inspection keeps Apply disabled: " + action);
                    if (action == "button")
                    {
                        TableEditChecks.Capture(dialog, milestone + "-inspection-busy");
                        ((Button)dialog.FindName("CancelInspectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (action == "escape")
                        dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    else dialog.Close();
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    check(dialog.IsVisible && ReferenceEquals(original, list.Items[0]) && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("canceled"), milestone + " cancellation retains snapshot and dialog: " + action);
                }
                dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                TableEditChecks.Capture(dialog, milestone + "-inspection-canceled-min");
                await transaction.RollbackAsync();
                await inspect();
                check(list.Items.Count > 0 && !ReferenceEquals(original, list.Items[0]) && !((Button)dialog.FindName("CancelInspectionButton")).IsEnabled, milestone + " inspection retries after lock release");
            }
            catch (Exception ex) { error = ex; }
            finally { dialog.Close(); }
        };
        dialog.ShowDialog(); if (error is not null) throw error;
    }
}
