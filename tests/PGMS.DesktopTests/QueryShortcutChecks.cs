using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;

/// <summary>PGMS-072 (M1097): Tools ▸ Options ▸ Environment ▸ Query Shortcuts and running them in the active query's session.</summary>
internal static class QueryShortcutChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static async Task Until(Func<bool> done) { for (int i = 0; i < 600 && !done(); i++) { await Task.Delay(50); await Idle(); } }
        check(MainWindow.QueryShortcutName(Key.System, Key.F1, ModifierKeys.Alt) == "Alt+F1" && MainWindow.QueryShortcutName(Key.D3, Key.None, ModifierKeys.Control) == "Ctrl+3" && MainWindow.QueryShortcutName(Key.D0, Key.None, ModifierKeys.Control) == "Ctrl+0",
            "Alt+F1 and Ctrl+digit name the query shortcuts");
        check(MainWindow.QueryShortcutName(Key.F1, Key.None, ModifierKeys.Control) is null && MainWindow.QueryShortcutName(Key.D3, Key.None, ModifierKeys.Control | ModifierKeys.Shift) is null && MainWindow.QueryShortcutName(Key.D3, Key.None, ModifierKeys.None) is null,
            "Ctrl+F1 (View Help), Ctrl+Shift+digit and a plain digit are not query shortcuts");
        check(MainWindow.QueryShortcutSql("TABLE", "public.t") == "TABLE public.t" && MainWindow.QueryShortcutSql("SELECT 1", null) == "SELECT 1", "The selection is added after a space");

        string directory = Path.Combine(Path.GetTempPath(), "pgms-shortcuts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var store = new EnvironmentOptions(Path.Combine(directory, "environment.json"));
        var dialog = new OptionsDialog(store) { ShowInTaskbar = false }; dialog.Show(); await Idle();
        ((TreeViewItem)dialog.FindName("QueryShortcutsPage")).IsSelected = true; await Idle();
        var rows = dialog.QueryShortcutRows;
        check(((FrameworkElement)dialog.FindName("QueryShortcutsPanel")).Visibility == Visibility.Visible && rows.Count == 11 && rows[0].Shortcut == "Alt+F1" && rows[1].Shortcut == "Ctrl+1" && rows[^1].Shortcut == "Ctrl+0" && rows.All(r => r.Text == ""),
            "Environment ▸ Query Shortcuts lists Alt+F1 and Ctrl+1 to Ctrl+0, all empty");
        rows.Single(r => r.Shortcut == "Ctrl+3").Text = "  SELECT 42 AS answer ";
        dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
        check(store.Load().QueryShortcut("Ctrl+3") == "SELECT 42 AS answer" && store.Load().QueryShortcut("Ctrl+4") is null && store.Load().QueryShortcuts?.Count == 1, "OK saves only the shortcuts with text, trimmed");
        try { Directory.Delete(directory, true); } catch (IOException) { }

        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        var errors = new List<string>(); window.ShowErrorOverride = ex => errors.Add(ex.Message);
        window.OpenTransactionPrompt = _ => MessageBoxResult.No;
        var before = window.EnvironmentSettings.Load();
        try
        {
            await Idle();
            window.EnvironmentSettings.Update(d => d with { QueryShortcuts = new Dictionary<string, string> { ["Ctrl+3"] = "SELECT 42 AS answer", ["Ctrl+4"] = "SELECT" } });
            check(!await window.RunQueryShortcutAsync("Ctrl+3"), "Without a query tab a shortcut does nothing");
            var query = await window.OpenQueryTextAsync(connection, "-- editor text stays");
            await Idle();
            check(await window.RunQueryShortcutAsync("Ctrl+3"), "Ctrl+3 runs its text in the query's session");
            await Until(() => query.LastExecution is not null && !query.IsBusy);
            check(query.LastExecution is { Rows: 1, Outcome: "Succeeded" } && query.EditorText == "-- editor text stays", "The shortcut's result comes back like Execute and the editor text is untouched");
            query.Editor.Text = "7 AS seven"; query.Editor.SelectAll();
            check(await window.RunQueryShortcutAsync("Ctrl+4"), "Ctrl+4 runs with the selection");
            await Until(() => !query.IsBusy);
            check(query.LastExecution is { Rows: 1, Outcome: "Succeeded" }, "The selection is passed after the text (SELECT 7 AS seven)");
            check(!await window.RunQueryShortcutAsync("Ctrl+5"), "A shortcut without text does nothing");
            check(errors.Count == 0, "No error was shown: " + string.Join(" | ", errors));
        }
        finally
        {
            window.EnvironmentSettings.Update(_ => before);
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
            NpgsqlConnection.ClearAllPools();
        }
    }
}
