using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;

/// <summary>PGMS-067 (M1101): Query ▸ Include Live Query Statistics on a connected query tab.</summary>
internal static class LiveStatisticsChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static async Task Until(Func<bool> done) { for (int i = 0; i < 600 && !done(); i++) { await Task.Delay(50); await Idle(); } }
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        var errors = new List<string>(); window.ShowErrorOverride = ex => errors.Add(ex.Message);
        window.OpenTransactionPrompt = _ => MessageBoxResult.No;
        try
        {
            await Idle();
            var menu = (MenuItem)window.FindName("LiveStatisticsMenu");
            var client = (MenuItem)window.FindName("ClientStatisticsMenu");
            var parent = (ItemsControl)menu.Parent;
            check(parent.Items.IndexOf(menu) == parent.Items.IndexOf(client) - 1 && (string)menu.Header == "Include Li_ve Query Statistics" && !menu.IsEnabled, "Query ▸ Include Live Query Statistics comes before Include Client Statistics, off without a query");
            var query = await window.OpenQueryTextAsync(connection, "-- live");
            await Idle();
            var tab = (TabItem)query.FindName("LiveStatisticsTab");
            check(menu.IsEnabled && !menu.IsChecked && tab.Visibility == Visibility.Collapsed, "With a query the command is available and off, and the tab is hidden");
            window.ToggleLiveStatistics(); await Idle();
            check(query.IncludeLiveStatistics && menu.IsChecked && tab.Visibility == Visibility.Visible, "Turning it on checks the command and shows the tab");
            query.LiveInterval = TimeSpan.FromMilliseconds(150);
            await query.ExecuteShortcutAsync("SELECT pg_sleep(1.2)");
            await Until(() => !query.IsBusy);
            var text = ((TextBox)query.FindName("LiveStatisticsText")).Text;
            check(query.LiveSamples >= 2 && text.Contains("State: active", StringComparison.Ordinal) && text.Contains("PgSleep", StringComparison.Ordinal) && text.Contains("Execution finished after", StringComparison.Ordinal),
                "While the query runs the tab shows the backend state and wait, then the end: " + query.LiveSamples + " " + text.Replace("\n", " | "));
            check(query.LastExecution is { Outcome: "Succeeded" }, "The query itself is not disturbed");
            window.ToggleLiveStatistics(); await Idle();
            int before = query.LiveSamples;
            await query.ExecuteShortcutAsync("SELECT pg_sleep(0.5)");
            await Until(() => !query.IsBusy);
            check(!query.IncludeLiveStatistics && !menu.IsChecked && query.LiveSamples == before && tab.Visibility == Visibility.Visible, "Off, later executions are not sampled; the last statistics stay visible");
            check(errors.Count == 0, "No error was shown: " + string.Join(" | ", errors));
        }
        finally
        {
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
            NpgsqlConnection.ClearAllPools();
        }
    }
}
