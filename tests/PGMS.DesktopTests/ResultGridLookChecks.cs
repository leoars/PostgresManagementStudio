using System.Data;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>Results grid drawn without per-cell bindings (M1132): the dotted rectangle follows the current cell, NULL cells are yellow, and cells and
/// row headers expose their text once to UI Automation. No database.</summary>
internal static class ResultGridLookChecks
{
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var table = new DataTable();
        table.Columns.Add(new DataColumn("c0", typeof(object)) { Caption = "id" });
        table.Columns.Add(new DataColumn("c1", typeof(object)) { Caption = "name" });
        for (int i = 1; i <= 300; i++) table.Rows.Add(i, i % 3 == 0 ? DBNull.Value : "row " + i);
        var query = new QueryView(Guid.NewGuid(), _ => { });
        var window = new Window { Content = query, Width = 700, Height = 600, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        window.Show();
        try
        {
            query.AddResultGridForTests(table);
            var sets = (TabControl)query.FindName("ResultSets");
            sets.SelectedIndex = 0;
            var grid = (DataGrid)((TabItem)sets.Items[0]).Content;
            async Task Settle() { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
            await Settle();
            DataGridCell[] Current() => Descendants<DataGridCell>(grid).Where(ResultCellChrome.GetIsCurrentCell).ToArray();
            DataGridCell CellAt(int row, int column) => Descendants<DataGridCell>(grid).Single(c => ReferenceEquals(c.DataContext, grid.Items[row]) && c.Column == grid.Columns[column]);
            check(Current().Length == 0, "No dotted rectangle before the grid has a current cell");
            grid.CurrentCell = new DataGridCellInfo(grid.Items[0], grid.Columns[0]);
            await Settle();
            check(Current().SequenceEqual([CellAt(0, 0)]), "The dotted rectangle marks the current cell only");
            check(Descendants<ResultCellChrome>(CellAt(0, 0)).Single().IsCurrent && !Descendants<ResultCellChrome>(CellAt(0, 1)).Single().IsCurrent, "The cell chrome draws the rectangle of the current cell");
            grid.CurrentCell = new DataGridCellInfo(grid.Items[1], grid.Columns[1]);
            await Settle();
            check(Current().SequenceEqual([CellAt(1, 1)]), "Moving the current cell moves the rectangle");
            grid.ScrollIntoView(grid.Items[299]); await Settle();
            check(Current().Length == 0, "Scrolled away, no realized cell shows the rectangle" + string.Concat(Current().Select(c => $" [{grid.Items.IndexOf(c.DataContext)},{c.Column?.DisplayIndex}]")));
            bool NullsMatch() => Descendants<DataGridCell>(grid).Where(c => c.Column == grid.Columns[1] && c.DataContext is DataRowView).All(c => (((DataRowView)c.DataContext).Row["c1"] is DBNull) == ReferenceEquals(c.Background, ResultGridStyle.NullBackground));
            check(NullsMatch(), "Rows reused for other items while scrolling keep the NULL color of their new values");
            grid.ScrollIntoView(grid.Items[0]); await Settle();
            check(NullsMatch(), "Scrolling back, the NULL color still matches every realized value");
            check(Current().SequenceEqual([CellAt(1, 1)]), "Scrolling back regenerates the current cell with its rectangle");
            check(CellAt(2, 1).Background == ResultGridStyle.NullBackground && CellAt(1, 1).Background is SolidColorBrush { Color.A: 0 }, "NULL cells are light yellow; others keep the transparent background");
            grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[2], grid.Columns[1])); await Settle();
            check(Descendants<ResultCellChrome>(CellAt(2, 1)).Single().Background == Application.Current.TryFindResource("Pgms.GridSelection"), "A selected cell shows the selection color, also over NULL");
            var peer = (DataGridAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(grid);
            peer.ResetChildrenCache();
            var row = peer.GetChildren().OfType<ItemAutomationPeer>().First(p => ReferenceEquals(p.Item, grid.Items[0]));
            var children = row.GetChildren();
            var header = children.OfType<DataGridRowHeaderAutomationPeer>().Single();
            check(header.GetName() == "1" && (header.GetChildren()?.Count ?? 0) == 0, "The row header reads its number once, without a text child");
            var cell = children.First(c => c is not DataGridRowHeaderAutomationPeer);
            check(cell.GetName() == "1" && (cell.GetChildren()?.Count ?? 0) == 0, "A cell reads its value as its name, without a separate text child");
        }
        finally { window.Close(); }
    }
}
