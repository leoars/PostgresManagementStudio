using System.Globalization;
using PGMS.PostgreSql;

internal static class ResultCopyChecks
{
    public static async Task RunAsync(string connection, Action<bool,string> check)
    {
        ClipboardGuardChecks.Run(check);
        string Copy(string[] headers, object?[][] rows, bool include=false,int max=ResultCopyText.MaximumCharacters)=>ResultCopyText.Format(headers,rows,include,max);
        check(Copy(new[]{"v"},new[]{new object?[]{DBNull.Value},new object?[]{""},new object?[]{"\\N"}})=="\\N\r\n\"\"\r\n\"\\N\"\r\n","Copy distinguishes NULL empty and literal marker");
        check(Copy(new[]{"v"},new[]{new object?[]{"a\tb\r\n\"c"}})=="\"a\tb\r\n\"\"c\"\r\n","Copy preserves tabs newlines and quotes");
        check(Copy(new[]{"same","same"},Array.Empty<object?[]>(),true)=="\"same\"\t\"same\"\r\n","Copy keeps duplicate headers");
        check(Copy(new[]{"v"},Array.Empty<object?[]>())=="","Copy without rows or headers is empty");
        var prior=CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("pt-BR");check(Copy(new[]{"v"},new[]{new object?[]{1.25m}})=="\"1.25\"\r\n","Copy uses invariant numeric formatting"); } finally {CultureInfo.CurrentCulture=prior;}
        check(Copy(new[]{"v"},new[]{new object?[]{"a"}},max:5)=="\"a\"\r\n","Copy exact character boundary is accepted");
        bool over=false;try{Copy(new[]{"v"},new[]{new object?[]{"😀"}},max:5);}catch(InvalidOperationException){over=true;}
        check(over,"Copy rejects whole oversized Unicode payload");
        bool width=false;try{Copy(new[]{"v"},new[]{new object?[]{1,2}});}catch(ArgumentException){width=true;}
        check(width,"Copy refuses inconsistent row width");
        check(Copy(new[]{"bytes"},new[]{new object?[]{new byte[]{0,127,255}}})=="\"\\x007FFF\"\r\n", "Copy keeps bytea hexadecimal representation");
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("pt-BR");
            check(Copy(new[]{"array"},new[]{new object?[]{new decimal[]{1.25m,2.5m}}})=="\"{1.25,2.5}\"\r\n", "Copy array display uses invariant element formatting");
            var value=new object?[]{1,null,"text"};
            check(new PGMS.Desktop.DatabaseValueConverter().Convert(value,typeof(string),null!,CultureInfo.CurrentCulture).Equals(ResultCopyText.DisplayValue(value)), "Grid and copy share display representation for arrays with NULL");
        }
        finally { CultureInfo.CurrentCulture=prior; }
        int quota = ResultCopyText.MaximumCharacters;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        check(Copy(new[]{"v"},new[]{new object?[]{new string('x',quota-4)}}).Length == quota, "Copy accepts the complete 4 Mi character boundary");
        bool fullOver=false;
        try { Copy(new[]{"v"},new[]{new object?[]{new string('x',quota-3)}}); } catch(InvalidOperationException) { fullOver=true; }
        check(fullOver, "Copy refuses a payload one character beyond the real quota");
        bool escapedOver=false;
        try { Copy(new[]{"v"},new[]{new object?[]{"\""}},max:5); } catch(InvalidOperationException) { escapedOver=true; }
        check(escapedOver, "Copy counts duplicated quote escaping against quota");
        bool headerOver=false;
        try { Copy(new[]{"long"},Array.Empty<object?[]>(),true,max:7); } catch(InvalidOperationException) { headerOver=true; }
        check(headerOver, "Copy counts headers against quota");
        Console.WriteLine($"Copy full quota acceptance and rejection: {clock.ElapsedMilliseconds} ms (local synthetic sample).");
        var table = new System.Data.DataTable();table.Columns.Add("id",typeof(int));table.Columns.Add("value",typeof(string));
        table.Rows.Add(1,"secret");table.Rows.Add(2,"b");table.Rows.Add(3,"c");
        var grid = new System.Windows.Controls.DataGrid { AutoGenerateColumns=false, ItemsSource=table.DefaultView, SelectionUnit=System.Windows.Controls.DataGridSelectionUnit.Cell };
        grid.Columns.Add(new System.Windows.Controls.DataGridTextColumn {Header="same"});grid.Columns.Add(new System.Windows.Controls.DataGridTextColumn {Header="same"});
        var window = new System.Windows.Window { Content=grid, Width=600, Height=350, ShowInTaskbar=false };
        window.Show();
        try {
        await grid.Dispatcher.InvokeAsync(() => {}, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        grid.SelectedCells.Add(new System.Windows.Controls.DataGridCellInfo(table.DefaultView[0],grid.Columns[0]));
        grid.SelectedCells.Add(new System.Windows.Controls.DataGridCellInfo(table.DefaultView[1],grid.Columns[1]));
        string sparse=PGMS.Desktop.ResultGridCopy.Prepare(grid,false);
        check(sparse=="\"1\"\t\"\"\r\n\"\"\t\"b\"\r\n" && !sparse.Contains("secret"),"Sparse copy never includes an unselected secret value");
        grid.Columns[1].DisplayIndex=0;
        check(PGMS.Desktop.ResultGridCopy.Prepare(grid,false).StartsWith("\"\"\t\"1\""),"Copy follows reordered display columns");
        grid.SelectedCells.Clear();table.DefaultView.Sort="id DESC";table.DefaultView.RowFilter="id>1";
        grid.SelectedCells.Add(new System.Windows.Controls.DataGridCellInfo(table.DefaultView[0],grid.Columns[0]));
        check(PGMS.Desktop.ResultGridCopy.Prepare(grid,true)=="\"same\"\r\n\"3\"\r\n","Copy follows sorted filtered DataView");
        grid.SelectedCells.Clear();bool empty=false;try{PGMS.Desktop.ResultGridCopy.Prepare(grid,false);}catch(InvalidOperationException){empty=true;}
        check(empty,"Empty grid selection is refused");
        } finally { window.Close(); }
        using var wide = new System.Data.DataTable();
        for (int i=0;i<501;i++) wide.Columns.Add($"c{i}",typeof(int));
        for (int i=0;i<501;i++) { var row=wide.NewRow(); row[i]=7; wide.Rows.Add(row); }
        var wideGrid = new System.Windows.Controls.DataGrid { AutoGenerateColumns=false, ItemsSource=wide.DefaultView, IsReadOnly=true, CanUserAddRows=false, SelectionUnit=System.Windows.Controls.DataGridSelectionUnit.Cell, EnableRowVirtualization=true, EnableColumnVirtualization=true };
        for (int i=0;i<501;i++) wideGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header=$"c{i}", Width=60 });
        var wideWindow = new System.Windows.Window { Content=wideGrid, Width=600, Height=350, ShowInTaskbar=false };
        wideWindow.Show();
        try
        {
            await wideGrid.Dispatcher.InvokeAsync(() => {}, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            for (int i=0;i<500;i++) wideGrid.SelectedCells.Add(new System.Windows.Controls.DataGridCellInfo(wide.DefaultView[i],wideGrid.Columns[i]));
            string rectangle=PGMS.Desktop.ResultGridCopy.Prepare(wideGrid,false);
            check(rectangle.Count(c=>c=='7')==500 && !rectangle.Contains("\\N"), "Sparse 250000-position boundary copies selected diagonal and empty gaps");
            wideGrid.SelectedCells.Add(new System.Windows.Controls.DataGridCellInfo(wide.DefaultView[500],wideGrid.Columns[500]));
            bool area=false;
            try { PGMS.Desktop.ResultGridCopy.Prepare(wideGrid,false); } catch(InvalidOperationException) { area=true; }
            check(area,"Sparse selection rejects rectangle above 250000 positions");
        }
        finally { wideWindow.Close(); }
    }
}
