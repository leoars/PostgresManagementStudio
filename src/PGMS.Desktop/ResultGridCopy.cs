using System.Data;
using System.Windows.Controls;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

internal static class ResultGridCopy
{
    internal static string Prepare(DataGrid grid, bool includeHeaders, bool retainLineBreaks = true)
    {
        if (grid.ItemsSource is not DataView) throw new InvalidOperationException("Select a result grid first.");
        var selected = grid.SelectedCells.Where(c => c.IsValid && c.Item is DataRowView).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select result cells to copy.");
        var rowIndices = new Dictionary<object,int>();
        for (int i = 0; i < grid.Items.Count; i++) rowIndices[grid.Items[i]] = i;
        var rows = selected.Select(c => c.Item).Distinct().OrderBy(item => rowIndices[item]).ToArray();
        var columns = selected.Select(c => c.Column).Distinct().OrderBy(c => c.DisplayIndex).ToArray();
        if ((long)rows.Length * columns.Length > 250000) throw new InvalidOperationException("Copy selection spans too many cells. Select fewer cells or export CSV.");
        // Sparse selections become a rectangle of selected rows/columns; never copy unselected values.
        var cells = selected.Select(c => (c.Item, c.Column)).ToHashSet();
        IEnumerable<IReadOnlyList<object?>> Values()
        {
            foreach (DataRowView row in rows)
                yield return columns.Select(column => cells.Contains(((object)row,column)) ? row.Row[grid.Columns.IndexOf(column)] : (object?)"").ToArray();
        }
        return ResultCopyText.Format(columns.Select(c => Convert.ToString(c.Header) ?? "").ToArray(), Values(), includeHeaders, retainLineBreaks: retainLineBreaks);
    }
}
