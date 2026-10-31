using System.Data;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation.Peers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class QueryStreamingChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        QueryErrorChecks.Run(check);
        var query = new QueryView();
        var window = new Window { Content = query, Width = 900, Height = 650, ShowInTaskbar = false };
        window.Show();
        try
        {
            await query.ConnectAsync(connection);
            var editor = (TextEditor)query.FindName("Editor");
            var sets = (TabControl)query.FindName("ResultSets");
            var timeout=(TextBox)query.FindName("QueryTimeout");
            editor.Text="SELECT 1";
            foreach(string invalid in new[]{"-1","86401","text",""})
            {
                timeout.Text=invalid;await query.ExecuteAsync();
                check(!query.IsBusy && sets.Items.Count==0 && ((TextBlock)query.FindName("QueryStatus")).Text.Contains("86400"),"Invalid UI timeout prevents execution: "+invalid);
            }
            timeout.Text="1";editor.Text="SELECT pg_sleep(5)";var timed=query.ExecuteAsync();
            check(!timeout.IsEnabled,"Read timeout cannot change while query runs");await timed;
            check(timeout.IsEnabled && ((TextBlock)query.FindName("QueryStatus")).Text.Contains("did not complete"),"UI applies timeout and restores control after failure");
            timeout.Text="0";
            editor.Text="-- prefix\nSELECT '😀', missing_column";editor.Select(10,editor.Document.TextLength-10);await query.ExecuteAsync();
            var goError=(Button)query.FindName("ErrorLocation");
            check(goError.IsEnabled,"Unchanged selected SQL enables explicit error navigation");
            goError.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(editor.CaretOffset==editor.Text.IndexOf("missing_column",StringComparison.Ordinal),"Server character position maps Unicode and selection prefix to UTF16 caret");
            editor.Text="SELECT 1";goError.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!goError.IsEnabled,"Stale error position never navigates edited document");
            editor.Text="SELECT 1; SELECT missing_column";await query.ExecuteAsync();
            check(!goError.IsEnabled,"Semicolon SQL conservatively disables ambiguous error navigation");
            editor.Text="DO $$ BEGIN RAISE NOTICE 'UI notice 😀'; END $$";await query.ExecuteAsync();
            check(((TextBox)query.FindName("Messages")).Text.Contains("NOTICE 00000: UI notice 😀"),"Messages presents Unicode notice with severity and SQLSTATE");
            editor.Text="DO $$ BEGIN RAISE NOTICE 'before error'; RAISE EXCEPTION 'test error'; END $$";await query.ExecuteAsync();
            check(((TextBox)query.FindName("Messages")).Text.Contains("before error") && ((TextBox)query.FindName("Messages")).Text.Contains("P0001"),"Notices remain visible alongside execution error");
            editor.Text="DO $$ BEGIN FOR i IN 1..105 LOOP RAISE NOTICE '%', i; END LOOP; END $$";await query.ExecuteAsync();
            check(((TextBox)query.FindName("Messages")).Text.Contains("5 server notice(s) omitted"),"Messages explains omitted notices");
            editor.Text="SELECT 1";await query.ExecuteAsync();
            check(!((TextBox)query.FindName("Messages")).Text.Contains("NOTICE") && !((TextBox)query.FindName("Messages")).Text.Contains("omitted"),"New execution clears previous notices and omission count");
            editor.Text = "SELECT i, repeat('x', 256) AS text FROM generate_series(1,600)i; SELECT pg_sleep(2); SELECT 'last' AS value;";
            var executionWatch = System.Diagnostics.Stopwatch.StartNew();
            var pending = query.ExecuteAsync();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && (sets.Items.Count == 0 || ((DataGrid)((TabItem)sets.Items[0]).Content).Items.Count == 0)) await Task.Delay(10);
            var firstDisplayMilliseconds = executionWatch.ElapsedMilliseconds;
            check(!pending.IsCompleted && sets.Items.Count > 0 && ((DataGrid)((TabItem)sets.Items[0]).Content).Items.Count > 0, "M99 WPF shows rows while script is still running");
            var cancelButton = (Button)query.FindName("CancelQueryButton");
            check(cancelButton.IsVisible && cancelButton.IsEnabled && new ButtonAutomationPeer(cancelButton).GetName() == "Cancel running query", "M102 Results exposes a named cancellation action during streaming");
            await query.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(query, "m102-streaming");
            check(!((Button)query.FindName("ExportCsvButton")).IsEnabled, "M99 export stays disabled during streaming");
            bool responsive = false; await query.Dispatcher.InvokeAsync(() => responsive = true, DispatcherPriority.Input);
            check(responsive && query.IsBusy, "M99 dispatcher processes input during streaming");
            var outputTabs = (TabControl)query.FindName("OutputTabs");
            outputTabs.SelectedIndex = 1;
            editor.Focus();
            await pending;
            Console.WriteLine($"Streaming timing: first displayed rows at {firstDisplayMilliseconds} ms; completion at {executionWatch.ElapsedMilliseconds} ms (synthetic script with pg_sleep(2)).");
            check(outputTabs.SelectedIndex == 1 && editor.IsKeyboardFocusWithin, "M102 later batches preserve chosen tab and editor focus");
            check(!cancelButton.IsVisible, "M102 cancellation action hides after completion");
            check(sets.Items.Count == 3 && ((DataGrid)((TabItem)sets.Items[0]).Content).Items.Count == 600, "M99 completion retains all streamed rows and result tabs");
            check(((Button)query.FindName("ExportCsvButton")).IsEnabled, "M99 export enabled only after reader completes");
            editor.Text = "SELECT 1 AS same, 2 AS same, NULL::text, ''::text; SELECT 1 WHERE false;";
            await query.ExecuteAsync();
            var grid = (DataGrid)((TabItem)sets.Items[0]).Content;
            var table = ((DataView)grid.ItemsSource).Table!;
            check(table.Columns[0].Caption == table.Columns[1].Caption && table.Rows[0][2] is DBNull && (string)table.Rows[0][3] == "", "M99 grid preserves duplicate labels NULL and empty text");
            check(sets.Items.Count == 2 && ((DataGrid)((TabItem)sets.Items[1]).Content).Items.Count == 0, "M99 empty result has its own grid");
            editor.Text = "SELECT i, repeat('x',256) FROM generate_series(1,600)i; SELECT pg_sleep(30);";
            pending = query.ExecuteAsync();
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && (sets.Items.Count == 0 || ((DataGrid)((TabItem)sets.Items[0]).Content).Items.Count == 0)) await Task.Delay(10);
            cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!cancelButton.IsEnabled, "M102 cancel click disables duplicate requests");
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            check(sets.Items.Count > 0 && ((DataGrid)((TabItem)sets.Items[0]).Content).Items.Count > 0, "M100 cancellation retains rows already displayed");
            check(!query.IsBusy && ((TextBlock)query.FindName("ExportScope")).Text.Contains("Partial results") && ((TextBox)query.FindName("Messages")).Text.Contains("ROLLBACK"), "M100 cancellation labels partial data and transaction recovery");
            outputTabs.SelectedIndex = 0;
            await query.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(query, "m102-partial");
            sets.SelectedIndex = 0;
            string path = Path.GetFullPath("artifacts/review/m100-partial.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await query.ExportSelectedCsvAsync(path, overwrite: true);
            check(((TextBlock)query.FindName("QueryStatus")).Text.Contains("Partial results") && File.ReadAllLines(path).Length == ((DataGrid)((TabItem)sets.Items[0]).Content).Items.Count + 1, "M100 partial CSV exports only displayed rows and retains warning");
            editor.Text = "SELECT i, repeat('x',256) FROM generate_series(1,600)i; SELECT 1/0;";
            await query.ExecuteAsync();
            check(((TextBox)query.FindName("Messages")).Text.Contains("22012") && ((TextBlock)query.FindName("ExportScope")).Text.Contains("Partial results"), "M100 later SQL error preserves SQLSTATE and partial warning");
            check(((DataGrid)((TabItem)sets.Items[0]).Content).Items.Count > 0, "M100 rows before server error remain inspectable");
            editor.Text = "SELECT 'retry';"; await query.ExecuteAsync();
            check(sets.Items.Count == 1 && ((DataGrid)((TabItem)sets.Items[0]).Content).Items.Count == 1 && !((TextBlock)query.FindName("ExportScope")).Text.Contains("Partial results"), "M100 successful retry replaces prior rows and clears partial state");
            editor.Text = "SELECT 'safe' AS value; SELECT repeat('x',2097152); SELECT 'omitted';";
            await query.ExecuteAsync();
            check(((TextBox)query.FindName("Messages")).Text.Contains("16 MiB") && ((TextBlock)query.FindName("ExportScope")).Text.Contains("display limit reached"), "M101 WPF reports text budget and limited export scope");
            check(((DataGrid)((TabItem)sets.Items[0]).Content).Items.Count == 1 && ((DataGrid)((TabItem)sets.Items[1]).Content).Items.Count == 0 && !((TextBlock)query.FindName("ExportScope")).Text.Contains("Partial results"), "M101 completed limited query preserves only whole values");
            sets.SelectedIndex = 0; await query.ExportSelectedCsvAsync(path, overwrite: true);
            check(File.ReadAllText(path) == "\"value\"\r\n\"safe\"\r\n", "M101 CSV keeps exact retained value without shortened cell");
            outputTabs.SelectedIndex = 0;
            await query.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(query, "m102-limited");
            window.Width = 600; window.Height = 440;
            await query.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(query, "m102-limited-small");
            var export = (Button)query.FindName("ExportCsvButton");
            var point = export.TranslatePoint(new Point(), query);
            check(point.X >= 0 && point.X + export.ActualWidth <= query.ActualWidth && point.Y + export.ActualHeight <= query.ActualHeight, "M102 export remains in bounds in a narrow query viewport");
            editor.Text = "SELECT i, repeat('x',128) FROM generate_series(1,12000)i; SELECT pg_sleep(30);";
            pending = query.ExecuteAsync();
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !((TextBlock)query.FindName("ExportScope")).Text.Contains("Display limit reached")) await Task.Delay(10);
            check(query.IsBusy && ((TextBlock)query.FindName("ExportScope")).Text.Contains("Display limit reached"), "M102 row cap is announced while remaining query still runs");
            cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            check(!query.IsBusy && ((TextBlock)query.FindName("ExportScope")).Text.Contains("Partial results"), "M102 capped reader remains cancelable during drain");
        }
        finally { query.Cancel(); await query.DisposeAsync(); window.Close(); }
        await QueryLoadChecks.RunAsync(connection, check);
    }
    private static void Capture(QueryView query, string name)
    {
        query.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(query.ActualWidth), (int)Math.Ceiling(query.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(query);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/review");
        using var file = File.Create($"artifacts/review/{name}.png"); encoder.Save(file);
    }
}
