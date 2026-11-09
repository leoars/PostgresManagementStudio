using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;

/// <summary>M1053: grid results in pages, and the option with the page size.</summary>
internal static class ResultPagingChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static T Named<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name);
        var window = new MainWindow { ShowInTaskbar = false, Width = 1200, Height = 850 }; window.Show();
        try
        {
            await Idle();
            check(new QueryOptions().GridPageRows == 100000, "A page has 100,000 rows by default");
            check(new QueryOptions(GridPageRows: 1).Normalized().GridPageRows == 100000 && new QueryOptions(GridPageRows: 5000).Normalized().GridPageRows == 5000, "A page size out of range goes back to the default");

            var query = await window.OpenQueryTextAsync(connection, "SELECT g, 'v' || g AS texto, NULL::text AS nada FROM generate_series(1, 400) g"); await Idle();
            query.ApplyQueryOptions(new QueryOptions(GridPageRows: 150));
            var pager = Named<FrameworkElement>(query, "ResultPager"); var status = Named<TextBlock>(query, "PagerStatus"); var scope = Named<TextBlock>(query, "ExportScope");
            var first = Named<Button>(query, "PagerFirst"); var previous = Named<Button>(query, "PagerPrevious"); var next = Named<Button>(query, "PagerNext"); var last = Named<Button>(query, "PagerLast");
            DataGrid Grid() => (DataGrid)((TabItem)Named<TabControl>(query, "ResultSets").Items[0]).Content;
            DataTable Page() => ((DataView)Grid().ItemsSource).Table!;
            string Cell(int row, int column) => (string)Page().Rows[row][column];
            async Task Click(Button button) { string before = status.Text; button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); for (int i = 0; i < 300 && status.Text == before; i++) await Task.Delay(10); await Idle(); }

            await query.ExecuteAsync(); await Idle();
            check(Page().Rows.Count == 150 && Cell(0, 0) == "1" && Cell(149, 0) == "150", "The grid holds the first page");
            check(Named<TextBlock>(query, "QueryStatus").Text.StartsWith("Query completed", StringComparison.Ordinal), "The status keeps saying the query completed");
            check(pager.Visibility == Visibility.Visible && status.Text == "Rows 1-150 of 400 | Page 1 of 3", "The bar says which rows are on screen");
            check(!first.IsEnabled && !previous.IsEnabled && next.IsEnabled && last.IsEnabled, "On the first page only forward is possible");
            check(Named<TextBox>(query, "Messages").Text.Contains("400 displayed row(s).", StringComparison.Ordinal) && !Named<TextBox>(query, "Messages").Text.Contains("Display limit", StringComparison.Ordinal), "Messages counts every row and reports no limit");
            check(scope.Text.StartsWith("Current page only", StringComparison.Ordinal), "Export says it takes the current page");

            await Click(next);
            check(Page().Rows.Count == 150 && Cell(0, 0) == "151" && Cell(149, 1) == "v300" && Page().Rows[0][2] == DBNull.Value, "Next brings the following rows, with text and NULL");
            check(status.Text == "Rows 151-300 of 400 | Page 2 of 3" && first.IsEnabled && previous.IsEnabled && next.IsEnabled && last.IsEnabled, "The bar follows");
            Grid().UpdateLayout(); await Idle();
            var row = (DataGridRow)Grid().ItemContainerGenerator.ContainerFromIndex(0);
            check(row is not null && (string)row.Header == 151.ToString(CultureInfo.CurrentCulture), "Row numbers continue from the previous page");
            await Click(last);
            check(Page().Rows.Count == 100 && Cell(99, 0) == "400" && !next.IsEnabled && !last.IsEnabled && status.Text == "Rows 301-400 of 400 | Page 3 of 3", "Last goes to the end");
            await Click(previous);
            check(Cell(0, 0) == "151", "Previous goes back");
            await Click(first);
            check(Page().Rows.Count == 150 && Cell(0, 0) == "1" && status.Text == "Rows 1-150 of 400 | Page 1 of 3", "First returns to the start");

            await Click(next);
            string csv = System.IO.Path.GetTempFileName();
            try
            {
                await query.ExportSelectedCsvAsync(csv, overwrite: true);
                var lines = System.IO.File.ReadAllLines(csv);
                check(lines.Length == 151 && lines[1].StartsWith("151", StringComparison.Ordinal) || lines.Length == 151 && lines[1].StartsWith("\"151\"", StringComparison.Ordinal), "Export writes the page on screen");
            }
            finally { System.IO.File.Delete(csv); }

            query.Editor.Text = "SELECT g FROM generate_series(1, 150) g";
            await query.ExecuteAsync(); await Idle();
            check(pager.Visibility == Visibility.Collapsed && Page().Rows.Count == 150 && !scope.Text.StartsWith("Current page", StringComparison.Ordinal), "A result that fits one page has no bar");

            query.Editor.Text = "SELECT g FROM generate_series(1, 300) g; SELECT 1 AS one";
            await query.ExecuteAsync(); await Idle();
            var sets = Named<TabControl>(query, "ResultSets");
            check(pager.Visibility == Visibility.Visible && status.Text == "Rows 1-150 of 300 | Page 1 of 2", "A result with pages shows the bar");
            sets.SelectedIndex = 1; await Idle();
            check(pager.Visibility == Visibility.Collapsed, "Another result of the same query has its own state");
            sets.SelectedIndex = 0; await Idle();
            check(pager.Visibility == Visibility.Visible, "Coming back shows the bar again");

            query.ApplyQueryOptions(new QueryOptions(GridDiscardResults: true));
            await query.ExecuteAsync(); await Idle();
            check(sets.Items.Count == 0 && pager.Visibility == Visibility.Collapsed, "Discarded results have no pages");

            var dialog = new QueryOptionsDialog(new QueryOptions(GridPageRows: 2500)) { ShowInTaskbar = false };
            var pages = Named<QueryOptionsPages>(dialog, "OptionPages");
            dialog.Show(); await Idle();
            var box = Named<TextBox>(pages, "PageRowsBox");
            check(box.Text == "2500" && pages.TryRead()?.GridPageRows == 2500, "Query Options shows and keeps the page size");
            box.Text = "12"; pages.ShowPage(QueryOptionsPages.ExecutionGeneral);
            check(pages.TryRead() is null && pages.RejectedPage == QueryOptionsPages.ResultsGrid, "A page size out of range is refused on its page");
            pages.ShowPage(QueryOptionsPages.ResultsGrid); pages.ResetCurrentPage();
            check(box.Text == "100000", "Reset to Default restores 100,000");
            dialog.Close();
        }
        finally
        {
            foreach (var view in window.QueryDocuments.ToArray()) view.IsDirty = false;
            window.Close();
        }
    }
}
