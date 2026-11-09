using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PGMS.Desktop;

internal static class ResultExportSnapshot
{
    internal static async Task<DataTable> CreateAsync(DataGrid grid,bool selectedRowsOnly=false,CancellationToken token=default,bool retainLineBreaks=true)
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
            var slice=System.Diagnostics.Stopwatch.StartNew();
            for(int index=0;index<rows.Length;index++)
            {
                token.ThrowIfCancellationRequested();snapshot.Rows.Add(columns.Select(i=>rows[index].Row[i] is string text && !retainLineBreaks?PGMS.PostgreSql.ResultCopyText.FlattenLineBreaks(text):rows[index].Row[i]).ToArray());
                // Early on so even a short export can be canceled; then by time, as waiting for the screen every few rows made long ones slow.
                if(index==127 || slice.ElapsedMilliseconds>=30){await Dispatcher.Yield(DispatcherPriority.Background);slice.Restart();}
            }
            token.ThrowIfCancellationRequested();return snapshot;
        }
        catch{snapshot.Dispose();throw;}
    }
}
