using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-050 (M1110): Results/Messages on top of the grid and no export strip until Save Results As….</summary>
internal static class ResultsAreaChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            var query = window.NewQueryTab("SELECT 1;");
            await Idle();
            var tabs = (TabControl)query.FindName("OutputTabs");
            check(tabs.TabStripPlacement == Dock.Top, "Results, Messages and the other output tabs sit on top, as in SSMS");
            var options = (FrameworkElement)query.FindName("ExportOptions");
            var export = (Button)query.FindName("ExportCsvButton");
            check(!query.ExportOptionsVisible && options.Visibility == Visibility.Collapsed && VisualAncestor(export, options), "No export strip over the results at first; Export CSV… is among the hidden options");
            var table = new DataTable(); table.Columns.Add("one", typeof(int)); table.Columns.Add("two", typeof(string)); table.Rows.Add(1, "a,b");
            query.AddResultGridForTests(table);
            await Idle();
            var grid = (DataGrid)((TabItem)((TabControl)query.FindName("ResultSets")).Items[^1]).Content;
            var exportOptions = grid.ContextMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Export _Options…");
            tabs.SelectedItem = query.FindName("MessagesTab");
            exportOptions.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Idle();
            check(query.ExportOptionsVisible && tabs.SelectedItem == query.FindName("ResultsTab"), "Export Options… on the grid shows the export options on the Results tab");

            // M1114: Save Results As… is the Save Grid Results dialog: .csv comma-separated, .txt tab-separated, with headers.
            var saveAs = grid.ContextMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Save Results _As…");
            check(QueryView.SaveGridResultsFilter.StartsWith("CSV (Comma delimited) (*.csv)|*.csv|Text (Tab delimited) (*.txt)|*.txt", StringComparison.Ordinal), "Save Grid Results offers CSV (Comma delimited) and Text (Tab delimited)");
            string folder = Path.Combine(Path.GetTempPath(), "pgms-save-grid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var (name, expected) in new[] { ("Results.csv", "\"one\",\"two\"\r\n\"1\",\"a,b\"\r\n"), ("Results.txt", "\"one\"\t\"two\"\r\n\"1\"\t\"a,b\"\r\n") })
                {
                    string file = Path.Combine(folder, name);
                    query.SaveGridResultsPath = () => file;
                    saveAs.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    for (int i = 0; i < 100 && (!File.Exists(file) || query.IsBusy); i++) await Task.Delay(50);
                    string written = File.Exists(file) ? File.ReadAllText(file) : "";
                    check(written == expected, $"Save Results As… writes {name} with headers: {written.Replace("\r\n", "⏎", StringComparison.Ordinal).Replace("\t", "→", StringComparison.Ordinal)}");
                }
                query.SaveGridResultsPath = () => null;
                saveAs.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Idle();
                check(Directory.GetFiles(folder).Length == 2 && !query.IsBusy, "Canceling Save Grid Results writes nothing");
            }
            finally { query.SaveGridResultsPath = null; Directory.Delete(folder, true); }
            ((Button)query.FindName("HideExportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!query.ExportOptionsVisible, "✕ hides them again");
        }
        finally { foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false; window.Close(); }
    }

    private static bool VisualAncestor(DependencyObject child, DependencyObject ancestor)
    {
        for (var node = LogicalTreeHelper.GetParent(child); node is not null; node = LogicalTreeHelper.GetParent(node)) if (node == ancestor) return true;
        return false;
    }
}
