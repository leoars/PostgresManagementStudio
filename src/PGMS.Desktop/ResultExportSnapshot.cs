using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PGMS.Desktop;

internal static class ResultExportSnapshot
{
    internal static async Task<DataTable> CreateAsync(DataGrid grid,bool selectedRowsOnly=false,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(grid.ItemsSource is not DataView view || view.Table is null)throw new InvalidOperationException("Select a result set to export.");
        var columns=grid.Columns.Where(c=>c.Visibility==Visibility.Visible).OrderBy(c=>c.DisplayIndex).Select(c=>grid.Columns.IndexOf(c)).ToArray();
        if(columns.Length==0 || columns.Any(i=>i>=view.Table.Columns.Count))throw new InvalidOperationException("No exportable result columns.");
        var selected=grid.SelectedCells.Where(c=>c.IsValid).Select(c=>c.Item).Concat(grid.SelectedItems.Cast<object>()).OfType<DataRowView>().ToHashSet();
        if(selectedRowsOnly && selected.Count==0)throw new InvalidOperationException("Select result cells or rows before exporting selected rows.");
        var snapshot=new DataTable();
        foreach(int ordinal in columns){var source=view.Table.Columns[ordinal];snapshot.Columns.Add(new DataColumn(source.ColumnName,source.DataType){Caption=source.Caption});}
        try
        {
            var rows=grid.Items.OfType<DataRowView>().Where(row=>!selectedRowsOnly || selected.Contains(row)).ToArray();
            if(rows.Length>10000 || (long)rows.Length*columns.Length>250000)throw new InvalidOperationException("Export exceeds displayed result limits.");
            for(int index=0;index<rows.Length;index++)
            {
                token.ThrowIfCancellationRequested();snapshot.Rows.Add(columns.Select(i=>rows[index].Row[i]).ToArray());
                if((index+1)%128==0)await Dispatcher.Yield(DispatcherPriority.Background);
            }
            token.ThrowIfCancellationRequested();return snapshot;
        }
        catch{snapshot.Dispose();throw;}
    }
}
