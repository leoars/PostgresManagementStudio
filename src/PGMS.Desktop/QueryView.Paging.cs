using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Grid results (M1053). As SSMS 22.11 does, the thread reading the server never waits for the screen: the rows are handed over and the grid is told at row
/// counts (docs/MARCO-M1053.md). Rows beyond a page go to a temporary file and the pager brings them back.</summary>
public partial class QueryView
{
    private sealed class ResultGridState(DataGrid grid, DataTable first, ResultPages pages)
    {
        public DataGrid Grid { get; } = grid;
        public DataTable First { get; } = first;
        public ResultPages Pages { get; } = pages;
        public int Page, Request;
        public long Offset;
    }

    private readonly List<ResultGridState> gridStates = [];

    private ResultGridState? SelectedGridState => ResultSets.SelectedItem is TabItem { Content: DataGrid grid } ? gridStates.FirstOrDefault(s => ReferenceEquals(s.Grid, grid)) : null;

    /// <summary>Results of the previous execution and their temporary files.</summary>
    private void ClearResultPages()
    {
        foreach (var state in gridStates) state.Pages.Dispose();
        gridStates.Clear();
        if (ResultPager is not null) ResultPager.Visibility = Visibility.Collapsed;
    }

    /// <summary>Takes the batches of the reading thread to the screen. Accept runs on that thread and only queues; Drain runs on the UI thread.</summary>
    private sealed class GridResultLoader
    {
        private readonly QueryView view;
        private readonly int pageRows;
        private readonly DispatcherTimer idleDrain;

        /// <summary>Created on the UI thread. The timer is PGMS's: the counts alone would leave the last rows hidden while a later statement of the script runs.</summary>
        internal GridResultLoader(QueryView view, int pageRows)
        {
            this.view = view; this.pageRows = pageRows;
            idleDrain = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => { if (!queue.IsEmpty) Drain(); }, view.Dispatcher);
        }

        private readonly record struct Pending(QueryBatch Batch, ResultPages? Pages, IReadOnlyList<object[]> Rows);
        private const int SliceMilliseconds = 30;
        private readonly ConcurrentQueue<Pending> queue = new();
        private ResultPages? current;
        private int scheduled;
        private bool finished;
        private long received, nextMilestone = 30;

        internal List<DataTable> Tables { get; } = [];
        internal long Received => Interlocked.Read(ref received);

        internal void Accept(QueryBatch batch)
        {
            ResultPages? started = null;
            if (batch.Columns is not null) current = started = new ResultPages(pageRows, batch.Columns.Count);
            IReadOnlyList<object[]> firstPage = [];
            if (batch.Rows.Count > 0)
            {
                firstPage = batch.Rows.Where(current!.Add).ToArray();
                Interlocked.Add(ref received, batch.Rows.Count);
            }
            if (started is not null || firstPage.Count > 0 || batch.DisplayLimited) queue.Enqueue(new(batch, started, firstPage));
            // Observed in SSMS: rows 30 and 100, then every 10,000, and the end.
            long total = Received;
            bool first = total > 0 && total == batch.Rows.Count;
            if (started is null && !first && !batch.DisplayLimited && total < nextMilestone) return;
            if (total >= nextMilestone) nextMilestone = total < 100 ? 100 : (total / 10000 + 1) * 10000;
            Schedule();
        }

        private void Schedule()
        {
            if (Interlocked.Exchange(ref scheduled, 1) == 0) _ = view.Dispatcher.BeginInvoke(() => Drain(), DispatcherPriority.Background);
        }

        /// <summary>Applies what is queued, a time slice at a time so the window keeps answering; everything when the reader has finished.</summary>
        internal void Drain(bool everything = false)
        {
            // A drain already queued must not bring back "Executing query" after the end.
            if (finished) return;
            finished = everything;
            if (everything) idleDrain.Stop();
            Volatile.Write(ref scheduled, 0);
            var slice = Stopwatch.StartNew();
            while ((everything || slice.ElapsedMilliseconds < SliceMilliseconds) && queue.TryDequeue(out var item)) Apply(item);
            if (!queue.IsEmpty) Schedule();
            view.QueryStatus.Text = $"Executing query | {view.Database} | {Received:N0} displayed row(s)" + (view.displayLimited ? " | Display limited; reading remaining results" : "");
            view.UpdatePager();
        }

        private void Apply(Pending item)
        {
            var batch = item.Batch;
            if (batch.Columns is not null)
            {
                var table = new DataTable();
                for (int i = 0; i < batch.Columns.Count; i++) table.Columns.Add(new DataColumn($"c{i}", typeof(object)) { Caption = batch.Columns[i] });
                Tables.Add(table);
                view.AddResultGrid(table, batch.ColumnTypes);
                view.TrackResultPages(table, item.Pages!);
                if (view.ResultSets.Items.Count == 1) { view.ResultSets.SelectedIndex = 0; view.OutputTabs.SelectedIndex = 0; }
            }
            foreach (var values in item.Rows) Tables[batch.ResultIndex].Rows.Add(values);
            // SSMS marks the first cell of the first result when it arrives (dotted rectangle). It also selects it; PGMS does not, so Copy
            // stays disabled until the user selects cells (rule kept from earlier milestones).
            if (batch.ResultIndex == 0 && item.Rows.Count > 0 && view.ResultSets.Items[0] is TabItem { Content: DataGrid first } && first.CurrentColumn is null && first.Columns.Count > 0 && first.Items.Count > 0)
                first.CurrentCell = new DataGridCellInfo(first.Items[0], first.Columns[0]);
            if (batch.DisplayLimited) { view.displayLimited = true; view.UpdateExport(); }
        }
    }

    private void TrackResultPages(DataTable table, ResultPages pages)
    {
        var grid = (DataGrid)((TabItem)ResultSets.Items[^1]).Content;
        var state = new ResultGridState(grid, table, pages);
        gridStates.Add(state);
        // Runs after the numbering of ResultGridStyle, which counts from the top of the page.
        grid.LoadingRow += (_, e) => { if (state.Offset > 0) e.Row.Header = (state.Offset + e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture); };
    }

    /// <summary>The bar appears only for a result with more than one page.</summary>
    private void UpdatePager()
    {
        if (ResultPager is null || PagerStatus is null) return;
        if (SelectedGridState is not { } state || state.Pages.PageCount < 2) { ResultPager.Visibility = Visibility.Collapsed; return; }
        ResultPager.Visibility = Visibility.Visible;
        int pages = state.Pages.PageCount;
        long first = state.Offset + 1, last = state.Offset + state.Pages.RowsOf(state.Page);
        string more = executingQuery ? "+" : "";
        PagerStatus.Text = $"Rows {first:N0}-{last:N0} of {state.Pages.TotalRows:N0}{more} | Page {state.Page + 1:N0} of {pages:N0}{more}";
        PagerFirst.IsEnabled = PagerPrevious.IsEnabled = state.Page > 0;
        PagerNext.IsEnabled = PagerLast.IsEnabled = state.Page < pages - 1;
    }

    private async void PageFirst(object sender, RoutedEventArgs e) => await ShowResultPageAsync(0);
    private async void PagePrevious(object sender, RoutedEventArgs e) { if (SelectedGridState is { } state) await ShowResultPageAsync(state.Page - 1); }
    private async void PageNext(object sender, RoutedEventArgs e) { if (SelectedGridState is { } state) await ShowResultPageAsync(state.Page + 1); }
    private async void PageLast(object sender, RoutedEventArgs e) { if (SelectedGridState is { } state) await ShowResultPageAsync(state.Pages.PageCount - 1); }

    /// <summary>Zero-based. The file is read off the UI thread; a click made meanwhile wins over this one.</summary>
    internal async Task ShowResultPageAsync(int index)
    {
        if (SelectedGridState is not { } state || index < 0 || index >= state.Pages.PageCount || index == state.Page) return;
        int request = ++state.Request;
        try
        {
            DataTable table = state.First;
            if (index > 0)
            {
                var empty = state.First.Clone();
                table = await Task.Run(() =>
                {
                    var rows = state.Pages.ReadPage(index);
                    empty.BeginLoadData();
                    foreach (var row in rows) empty.Rows.Add(row);
                    empty.EndLoadData();
                    return empty;
                });
            }
            if (request != state.Request) return;
            var previous = state.Grid.ItemsSource as DataView;
            state.Page = index;
            state.Offset = state.Pages.FirstRowOf(index);
            ResultGridStyle.FitRowHeaders(state.Grid, checked((int)Math.Min(int.MaxValue, state.Offset + table.Rows.Count)));
            state.Grid.ItemsSource = table.DefaultView;
            if (previous?.Table is { } old && !ReferenceEquals(old, state.First)) old.Dispose();
            if (state.Grid.Items.Count > 0 && state.Grid.Columns.Count > 0)
            {
                state.Grid.CurrentCell = new DataGridCellInfo(state.Grid.Items[0], state.Grid.Columns[0]);
                state.Grid.ScrollIntoView(state.Grid.Items[0]);
            }
            UpdateExport();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            QueryStatus.Text = "Could not read this page: " + ex.Message;
        }
    }
}
