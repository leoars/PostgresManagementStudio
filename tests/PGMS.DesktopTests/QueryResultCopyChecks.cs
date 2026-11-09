using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class QueryResultCopyChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var writes = new List<string>();
        Exception? destinationFailure = null;
        var query = new QueryView(Guid.NewGuid(), text => { if (destinationFailure is not null) throw destinationFailure; writes.Add(text); });
        var window = new Window { Content = query, Width = 900, Height = 650, ShowInTaskbar = false };
        window.Show();
        try
        {
            await query.ConnectAsync(connection);
            var editor = (TextEditor)query.FindName("Editor");
            var sets = (TabControl)query.FindName("ResultSets");
            editor.Text = "SELECT NULL::text AS same, ''::text AS same, E'á\\t😀' AS value; SELECT 1 WHERE false;";
            await query.ExecuteAsync();
            await query.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)((TabItem)sets.Items[0]).Content;
            check(!ApplicationCommands.Copy.CanExecute(null, grid) && !QueryView.CopyWithoutHeaders.CanExecute(null, grid), "Copy commands disabled without selection");
            ApplicationCommands.Copy.Execute(null, grid);
            check(writes.Count == 0, "Disabled routed Copy never calls the destination");
            grid.SelectAllCells();
            check(query.PrepareSelectedResultCopy(grid, true) == "\"same\"\t\"same\"\t\"value\"\r\n\\N\t\"\"\t\"á\t😀\"\r\n", "Query grid copy keeps duplicate labels NULL empty Unicode and tabs");
            check(query.PrepareSelectedResultCopy(grid, false) == "\\N\t\"\"\t\"á\t😀\"\r\n", "Query grid copy can omit headers");
            check(ApplicationCommands.Copy.CanExecute(null, grid) && QueryView.CopyWithoutHeaders.CanExecute(null, grid), "Both grid copy commands enabled after completion with selection");
            ApplicationCommands.Copy.Execute(null, grid);
            check(writes.SequenceEqual(new[] { "\"same\"\t\"same\"\t\"value\"\r\n\\N\t\"\"\t\"á\t😀\"\r\n" }), "Routed Copy writes one complete payload with headers");
            check(((TextBlock)query.FindName("QueryStatus")).Text == query.DescribeCopiedSelection(grid, true), "Routed Copy reports success after destination returns");
            QueryView.CopyWithoutHeaders.Execute(null, grid);
            check(writes.Count == 2 && writes[1] == "\\N\t\"\"\t\"á\t😀\"\r\n", "Headerless routed command writes values without labels");
            var otherWrites = new List<string>();
            await using (var other = new QueryView(Guid.NewGuid(), otherWrites.Add))
            {
                await other.ConnectAsync(connection);
                ((TextEditor)other.FindName("Editor")).Text = "SELECT 7 AS other_document;";
                await other.ExecuteAsync();
                var otherSets = (TabControl)other.FindName("ResultSets");
                var otherGrid = (DataGrid)((TabItem)otherSets.Items[0]).Content;
                otherGrid.SelectAllCells();
                QueryView.CopyWithoutHeaders.Execute(null, otherGrid);
                check(otherWrites.SequenceEqual(new[] { "\"7\"\r\n" }) && writes.Count == 2, "Copy destination belongs to one query document only");
            }
            bool nullDestination = false;
            try { _ = new QueryView(Guid.NewGuid(), null!); } catch (ArgumentNullException) { nullDestination = true; }
            check(nullDestination, "Query refuses a null copy destination before initializing controls");
            int beforeBusyClipboard = writes.Count;
            destinationFailure = new System.Runtime.InteropServices.COMException("Synthetic clipboard contention", unchecked((int)0x800401D0));
            QueryView.CopyWithoutHeaders.Execute(null, grid);
            check(writes.Count == beforeBusyClipboard && ((TextBlock)query.FindName("QueryStatus")).Text == "Copy failed: Clipboard is busy. Try Copy again.", "Busy clipboard feedback gives a retry action without reporting success");
            check(grid.SelectedCells.Count == 3 && !query.IsBusy && QueryView.CopyWithoutHeaders.CanExecute(null, grid), "Clipboard failure preserves selection and leaves retry enabled");
            destinationFailure = null;
            QueryView.CopyWithoutHeaders.Execute(null, grid);
            check(writes.Count == beforeBusyClipboard + 1 && ((TextBlock)query.FindName("QueryStatus")).Text == query.DescribeCopiedSelection(grid, false), "Retry after clipboard contention writes once and clears error feedback");
            foreach (Exception failure in new Exception[]
            {
                new System.Runtime.InteropServices.ExternalException("Synthetic external error", unchecked((int)0x80004005)),
                new InvalidOperationException("Synthetic destination unavailable"),
                new ArgumentException("Synthetic invalid clipboard argument")
            })
            {
                int beforeFailure = writes.Count;
                destinationFailure = failure;
                ApplicationCommands.Copy.Execute(null, grid);
                string failureStatus = ((TextBlock)query.FindName("QueryStatus")).Text;
                check(writes.Count == beforeFailure && failureStatus.StartsWith("Copy failed:") && !failureStatus.StartsWith("Copied"), "Destination failure cannot report copied success: " + failure.GetType().Name);
                check(grid.SelectedCells.Count == 3 && query.PrepareSelectedResultCopy(grid, false) == "\\N\t\"\"\t\"á\t😀\"\r\n", "Destination failure preserves original selected values: " + failure.GetType().Name);
                if (failure is System.Runtime.InteropServices.ExternalException)
                    check(failureStatus.Contains("Try Copy again or export CSV"), "Unknown Windows clipboard error offers retry and CSV recovery");
            }
            destinationFailure = null;
            var withHeaders = (MenuItem)grid.ContextMenu.Items[0];
            var withoutHeaders = (MenuItem)grid.ContextMenu.Items[1];
            check(withHeaders.Command == ApplicationCommands.Copy && withoutHeaders.Command == QueryView.CopyWithoutHeaders && withHeaders.CommandTarget == grid && withoutHeaders.CommandTarget == grid, "Context copy commands target their result grid");
            check(new MenuItemAutomationPeer(withHeaders).GetName().Contains("headers") && new MenuItemAutomationPeer(withoutHeaders).GetName().Contains("without"), "Copy menu exposes distinct automation names");
            check(grid.InputBindings.OfType<KeyBinding>().Any(k => k.Command == QueryView.CopyWithoutHeaders && k.Key == Key.C && k.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) && !editor.InputBindings.OfType<KeyBinding>().Any(k => k.Command == QueryView.CopyWithoutHeaders), "Headerless shortcut is confined to the result grid");
            check(query.DescribeCopiedSelection(grid, false).Contains("3 selected cell(s) without headers."), "Copy feedback identifies selection and header scope");
            await query.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            var status = (TextBlock)query.FindName("QueryStatus");
            check(Equals(status.ToolTip, status.Text), "Copy status exposes full feedback through its bound tooltip");
            if (Environment.GetEnvironmentVariable("PGMS_COPY_REVIEW") == "1")
            {
                destinationFailure = new System.Runtime.InteropServices.ExternalException("Synthetic review failure", unchecked((int)0x80004005));
                ApplicationCommands.Copy.Execute(null, grid);
                grid.Focus();
                await query.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                Capture(query,"m416-copy-error-normal");
                window.Width=600; window.Height=440;
                await query.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                Capture(query,"m416-copy-error-small");
                window.Width=900; window.Height=650;
                destinationFailure = null;
            }
            sets.SelectedIndex = 1;
            var empty = (DataGrid)((TabItem)sets.Items[1]).Content;
            check(!ApplicationCommands.Copy.CanExecute(null, empty), "Zero-row result does not offer a copy payload");
            int beforeEmpty = writes.Count;
            QueryView.CopyWithoutHeaders.Execute(null, empty);
            check(writes.Count == beforeEmpty, "Empty-result routed command cannot overwrite destination");
            sets.SelectedIndex=0;
            editor.Text = "SELECT * FROM (VALUES (1,'hidden'),(2,'á'),(3,'😀')) v(id,value);";
            await query.ExecuteAsync();
            grid = (DataGrid)((TabItem)sets.Items[0]).Content;
            var ordered = (DataView)grid.ItemsSource;
            ordered.RowFilter = "c0 > 1";
            ordered.Sort = "c0 DESC";
            grid.Columns[1].DisplayIndex = 0;
            grid.SelectedCells.Add(new DataGridCellInfo(ordered[0], grid.Columns[1]));
            grid.SelectedCells.Add(new DataGridCellInfo(ordered[1], grid.Columns[0]));
            QueryView.CopyWithoutHeaders.Execute(null, grid);
            check(writes[^1] == "\"😀\"\t\"\"\r\n\"\"\t\"2\"\r\n", "Routed sparse copy follows filtering sorting and display order without leaking gaps");
            check(((TextBlock)query.FindName("QueryStatus")).Text.Contains("2 selected cell(s)"), "Sparse copy feedback counts selected cells rather than rectangle positions");
            editor.Text = "SELECT repeat('x',65536) AS payload FROM generate_series(1,70);";
            await query.ExecuteAsync();
            grid = (DataGrid)((TabItem)sets.Items[0]).Content;
            grid.SelectAllCells();
            int beforeQuota = writes.Count;
            ApplicationCommands.Copy.Execute(null, grid);
            check(writes.Count == beforeQuota, "Oversized routed copy refuses the complete payload before destination write");
            check(((TextBlock)query.FindName("QueryStatus")).Text.StartsWith("Copy failed:") && !query.IsBusy, "Copy quota failure leaves query usable with failure feedback");
            grid.SelectedCells.Clear();
            grid.SelectedCells.Add(new DataGridCellInfo(((DataView)grid.ItemsSource)[0], grid.Columns[0]));
            QueryView.CopyWithoutHeaders.Execute(null, grid);
            check(writes.Count == beforeQuota + 1 && writes[^1] == "\"" + new string('x',65536) + "\"\r\n", "Smaller selection retries successfully after quota refusal");
            editor.Text = "SELECT i FROM generate_series(1,600)i;";
            await query.ExecuteAsync();
            await query.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            grid=(DataGrid)((TabItem)sets.Items[0]).Content;
            var last=((DataView)grid.ItemsSource)[599];
            check(grid.ItemContainerGenerator.ContainerFromItem(last) is null, "Virtualized copy fixture keeps last row unrealized");
            grid.SelectedCells.Add(new DataGridCellInfo(last,grid.Columns[0]));
            check(query.PrepareSelectedResultCopy(grid,false)=="\"600\"\r\n", "Copy reads selected unrealized row without scrolling or realizing it");
            QueryView.CopyWithoutHeaders.Execute(null, grid);
            check(writes[^1] == "\"600\"\r\n", "Routed copy writes an unrealized selected row");
            check(grid.ItemContainerGenerator.ContainerFromItem(last) is null, "Routed copy does not realize a virtualized row");
            // The third value is over the limit per cell, the one display limit a grid result still has.
            editor.Text = "SELECT CASE WHEN g = 3 THEN repeat('x', 70000) ELSE g::text END AS i FROM generate_series(1,3) g; SELECT pg_sleep(30);";
            var pending = query.ExecuteAsync();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline && !((TextBlock)query.FindName("ExportScope")).Text.Contains("Display limit reached")) await Task.Delay(10);
                check(query.IsBusy && sets.Items.Count > 0, "Copy state fixture reaches a limited running query");
                grid = (DataGrid)((TabItem)sets.Items[0]).Content;
                grid.SelectedCells.Add(new DataGridCellInfo(((DataView)grid.ItemsSource)[0],grid.Columns[0]));
                check(!ApplicationCommands.Copy.CanExecute(null, grid) && !QueryView.CopyWithoutHeaders.CanExecute(null, grid), "Copy remains disabled while result reader drains");
                int beforeRunningCopy = writes.Count;
                ApplicationCommands.Copy.Execute(null, grid);
                QueryView.CopyWithoutHeaders.Execute(null, grid);
                check(writes.Count == beforeRunningCopy, "Both routed commands avoid destination writes while query is running");
                bool busy = false;
                try { query.PrepareSelectedResultCopy(grid,false); } catch(InvalidOperationException) { busy=true; }
                check(busy, "Copy payload preparation also rejects a busy query");
                query.Cancel();
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                check(query.PrepareSelectedResultCopy(grid,false)=="\"1\"\r\n" && ApplicationCommands.Copy.CanExecute(null,grid), "Completed cancellation allows copying retained selected cells");
                string scope = query.DescribeCopiedSelection(grid,true);
                check(scope.Contains("Partial results") && scope.Contains("Display limit reached"), "Copy feedback retains both partial and display limit warnings");
                ApplicationCommands.Copy.Execute(null, grid);
                check(writes.Count == beforeRunningCopy + 1 && writes[^1] == "\"i\"\r\n\"1\"\r\n", "Routed copy after cancellation writes retained displayed selection only");
                check(((TextBlock)query.FindName("QueryStatus")).Text.Contains("Partial results") && ((TextBlock)query.FindName("QueryStatus")).Text.Contains("Display limit reached"), "Routed copy success preserves partial and display-limit warnings");
            }
            finally { query.Cancel(); await pending; }
            editor.Text = "SELECT 42 AS answer;";
            await query.ExecuteAsync();
            grid = (DataGrid)((TabItem)sets.Items[0]).Content;
            grid.SelectAllCells();
            check(!query.DescribeCopiedSelection(grid,true).Contains("Partial") && !query.DescribeCopiedSelection(grid,true).Contains("limit"), "Successful retry clears prior copy scope warnings");
            editor.Text = "SELECT n AS same, label AS same FROM (VALUES (2, 'b'), (1, 'a'), (3, 'c')) AS v(n,label);";
            await query.ExecuteAsync();
            grid = (DataGrid)((TabItem)sets.Items[0]).Content;
            await query.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            grid.UpdateLayout();
            var header = Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h => h.Column == grid.Columns[0]);
            var click = typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            click.Invoke(header, null);
            check(grid.Items.Cast<DataRowView>().Select(r => (string)r.Row[0]).SequenceEqual(new[] {"1","2","3"}), "Real column header click sorts displayed result values ascending without indexer-property crash");
            click.Invoke(header, null);
            check(grid.Items.Cast<DataRowView>().Select(r => (string)r.Row[0]).SequenceEqual(new[] {"3","2","1"}), "Second header click sorts displayed values descending with duplicate captions");
            grid.SelectAllCells();
            QueryView.CopyWithoutHeaders.Execute(null, grid);
            check(writes[^1] == "\"3\"\t\"c\"\r\n\"2\"\t\"b\"\r\n\"1\"\t\"a\"\r\n", "Copy after header sorting follows displayed row order and preserves paired values");
        }
        finally { query.Cancel(); await query.DisposeAsync(); window.Close(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i=0; i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);
            yield return child;
            foreach(var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Capture(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory("artifacts/review");
        using var output = System.IO.File.Create($"artifacts/review/{name}.png");
        encoder.Save(output);
    }
}
