using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PGMS.Desktop;

internal static class ResultExportUiChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var query=new QueryView();var window=new Window{Content=query,Width=900,Height=650,ShowInTaskbar=false};window.Show();
        try
        {
            using var table=new DataTable();table.Columns.Add("first");table.Columns.Add("second");table.Rows.Add("á😀",DBNull.Value);table.Rows.Add("","\\N");
            var grid=new DataGrid{ItemsSource=table.DefaultView,IsReadOnly=true,AutoGenerateColumns=true};
            var sets=(TabControl)query.FindName("ResultSets");sets.Items.Add(new TabItem{Header="Result 1",Content=grid});sets.SelectedIndex=0;
            await query.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var delimiter=(ComboBox)query.FindName("CsvDelimiter");var headers=(CheckBox)query.FindName("CsvHeaders");var bom=(CheckBox)query.FindName("CsvBom");
            check(delimiter.SelectedIndex==0 && headers.IsChecked==true && bom.IsChecked==false,"CSV UI defaults preserve comma headers and no BOM");
            delimiter.SelectedIndex=1;headers.IsChecked=false;bom.IsChecked=true;
            string root=Path.Combine("artifacts","export-ui",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);string path=Path.Combine(root,"result.csv");
            var export=query.ExportSelectedCsvAsync(path);check(!delimiter.IsEnabled && !headers.IsEnabled && !bom.IsEnabled,"CSV options freeze during export");await export;
            check((await File.ReadAllBytesAsync(path)).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()),"CSV UI sends explicit BOM option to saved file");
            check(await File.ReadAllTextAsync(path)=="\"á😀\";\\N\r\n\"\";\"\\N\"\r\n","CSV UI sends delimiter and header choice without changing values");
            check(delimiter.IsEnabled && !query.IsBusy,"CSV UI releases options after save");
            foreach(string name in new[]{"CsvDelimiter","CsvHeaders","CsvBom"})check(!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName((DependencyObject)query.FindName(name))),"Export option accessible name: "+name);
            await Capture(window,query,"m883-export-normal",900,650,check);await Capture(window,query,"m883-export-small",600,440,check);
            headers.IsChecked=true;delimiter.SelectedIndex=0;bom.IsChecked=false;grid.Columns[1].DisplayIndex=0;
            table.Columns[0].Caption=table.Columns[1].Caption="same";table.DefaultView.Sort="first ASC";
            await query.ExportSelectedCsvAsync(path,true);
            check(await File.ReadAllTextAsync(path)=="\"same\",\"same\"\r\n\"\\N\",\"\"\r\n\\N,\"á😀\"\r\n","CSV follows visual column and row order while preserving duplicate captions");
            grid.Columns[0].Visibility=Visibility.Collapsed;await query.ExportSelectedCsvAsync(path,true);
            check(await File.ReadAllTextAsync(path)=="\"same\"\r\n\"\\N\"\r\n\\N\r\n","CSV excludes hidden columns from displayed snapshot");
            check(table.Columns.Count==2 && table.Rows.Count==2 && grid.Columns[1].DisplayIndex==0,"Export snapshot leaves live table and column order unchanged");
            grid.Columns[0].Visibility=Visibility.Visible;
            var scope=(ComboBox)query.FindName("ExportRows");scope.SelectedIndex=1;grid.UnselectAll();grid.UnselectAllCells();
            bool refused=false;try{await query.ExportSelectedCsvAsync(path,true);}catch(InvalidOperationException){refused=true;}
            check(refused && !query.IsBusy && (await File.ReadAllTextAsync(path)).StartsWith("\"same\"\r\n"),"Empty selected-row scope refuses export and preserves existing file");
            grid.SelectionUnit=DataGridSelectionUnit.Cell;grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[1],grid.Columns[0]));
            await query.ExportSelectedCsvAsync(path,true);
            check(await File.ReadAllTextAsync(path)=="\"same\",\"same\"\r\n\\N,\"á😀\"\r\n","Selected-row export includes every visible column of selected row");
            grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[1],grid.Columns[1]));await query.ExportSelectedCsvAsync(path,true);
            check((await File.ReadAllTextAsync(path)).Split("\r\n").Length==3,"Multiple selected cells do not duplicate exported row");
            scope.SelectedIndex=0;await query.ExportSelectedCsvAsync(path,true);check((await File.ReadAllTextAsync(path)).Split("\r\n").Length==4,"All displayed rows ignores partial selection");
            await Capture(window,query,"m885-selection-normal",900,650,check);await Capture(window,query,"m885-selection-small",600,440,check);
            table.Clear();for(int i=0;i<1000;i++)table.Rows.Add(i.ToString(),"payload");
            string before=await File.ReadAllTextAsync(path);bool sawPreparing=false;
            var cancelButton=(Button)query.FindName("CancelExportButton");
            _=query.Dispatcher.BeginInvoke(new Action(()=>{sawPreparing=!grid.IsEnabled && query.IsBusy && ((TextBlock)query.FindName("QueryStatus")).Text.StartsWith("Preparing");cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}),DispatcherPriority.Normal);
            bool canceled=false;try{await query.ExportSelectedCsvAsync(path,true);}catch(OperationCanceledException){canceled=true;}
            check(canceled && sawPreparing,"Cancel Export is processed while preparing snapshot with grid frozen");
            check(await File.ReadAllTextAsync(path)==before && !Directory.EnumerateFiles(root,"*.partial").Any(),"Canceled preparation preserves destination and creates no partial file");
            check(!query.IsBusy && grid.IsEnabled && delimiter.IsEnabled,"Canceled preparation restores grid and export controls");
            using var preCanceled=new CancellationTokenSource();preCanceled.Cancel();canceled=false;
            try{await query.ExportSelectedCsvAsync(path,true,preCanceled.Token);}catch(OperationCanceledException){canceled=true;}
            check(canceled && !query.IsBusy,"Already canceled export refuses preparation");
            await query.ExportSelectedCsvAsync(path,true);check((await File.ReadAllTextAsync(path)).Split("\r\n").Length==1002,"Fresh export succeeds after preparation cancellation");
            var format=(ComboBox)query.FindName("ExportFormat");format.SelectedIndex=1;
            check(((Button)query.FindName("ExportCsvButton")).Content.ToString()=="Export JSON…" && !((FrameworkElement)query.FindName("CsvOptionsPanel")).IsVisible,"JSON choice changes export action and hides CSV-only options");
            scope.SelectedIndex=1;grid.SelectedCells.Clear();grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[2],grid.Columns[0]));
            string jsonPath=Path.Combine(root,"selected.json");await query.ExportSelectedJsonAsync(jsonPath);
            using var json=JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
            check(json.RootElement.GetProperty("rows").GetArrayLength()==1 && json.RootElement.GetProperty("rows")[0][0].GetString()=="payload","JSON UI shares selected rows and visual column order");
            check(((TextBlock)query.FindName("QueryStatus")).Text.Contains("selected row(s) to JSON"),"JSON status identifies scope and format");
            await Capture(window,query,"m888-json-normal",900,650,check);await Capture(window,query,"m888-json-small",600,440,check);
            format.SelectedIndex=0;check(headers.IsChecked==true && delimiter.SelectedIndex==0 && ((FrameworkElement)query.FindName("CsvOptionsPanel")).IsVisible,"Returning to CSV retains its options");
        }
        finally{await query.DisposeAsync();window.Close();}
    }
    internal static async Task Capture(Window window,QueryView query,string name,double width,double height,Action<bool,string> check)
    {
        window.Width=width;window.Height=height;await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);window.UpdateLayout();
        foreach(string control in new[]{"ExportCsvButton","CsvOptionsPanel"})
        {
            var element=(FrameworkElement)query.FindName(control);if(!element.IsVisible)continue;var bounds=element.TransformToAncestor(query).TransformBounds(new Rect(element.RenderSize));
            check(bounds.Left>=0 && bounds.Right<=query.ActualWidth+1 && bounds.Bottom<=query.ActualHeight,"Export controls fit "+name+"/"+control);
        }
        var bitmap=new RenderTargetBitmap((int)query.ActualWidth,(int)query.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(query);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using var stream=File.Create("artifacts/review/"+name+".png");encoder.Save(stream);
    }
}
