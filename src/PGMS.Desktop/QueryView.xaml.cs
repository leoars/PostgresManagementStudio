using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;
using Npgsql;
using PGMS.PostgreSql;
using Microsoft.Win32;

namespace PGMS.Desktop;

public partial class QueryView : UserControl, IAsyncDisposable
{
    internal static readonly RoutedUICommand CopyWithoutHeaders = new("Copy without headers", nameof(CopyWithoutHeaders), typeof(QueryView));
    public Guid RecoveryId { get; }
    public long RecoveryRevision{get;private set;}
    public long EditRevision { get; private set; }
    public string? FilePath { get; internal set; }
    public SqlFileVersion? FileVersion { get; internal set; }
    public SqlFileFormat FileFormat { get; internal set; } = SqlFileFormat.Utf8;
    public bool IsDirty { get; set; }
    public bool IsBusy { get; private set; }
    public bool IsExporting => exporting;
    public bool IsConnected => session?.IsConnected == true;
    public bool HasSession => session is not null;
    public string Database => session?.Database ?? "Not connected";
    public event EventHandler? StateChanged;
    private QuerySession? session;
    private CancellationTokenSource? cancellation;
    private readonly SearchPanel search;
    private bool displayLimited;
    private bool resultsIncomplete;
    private bool exporting;
    private bool executingQuery;
    private readonly Action<string> writeResultClipboard;
    // Definitions are read-only after loading; each editor owns its colorizer and state.
    private static readonly Lazy<IHighlightingDefinition> sqlHighlighting = new(() =>
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/PGMS;component/Resources/PostgreSQL.xshd")).Stream;
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    });

    public QueryView() : this(Guid.NewGuid()) { }

    internal QueryView(Guid recoveryId) : this(recoveryId, text => Clipboard.SetText(text, TextDataFormat.UnicodeText)) { }

    internal static QueryView CreateInstrumented(Guid recoveryId, Action<string, double>? observer)
        => new(recoveryId, text => Clipboard.SetText(text, TextDataFormat.UnicodeText), observer);

    internal QueryView(Guid recoveryId, Action<string> writeResultClipboard, Action<string, double>? observer = null)
    {
        if (recoveryId == Guid.Empty) throw new ArgumentException("Missing recovery identity.", nameof(recoveryId));
        ArgumentNullException.ThrowIfNull(writeResultClipboard);
        this.writeResultClipboard = writeResultClipboard;
        RecoveryId = recoveryId;
        using (UiPhaseScope.Start(observer, "view-xaml")) InitializeComponent();
        ConfigureSqlLinks();
        ConfigureSqlEditing();
        QueryTimeout.TextChanged+=(_,_)=>{if(int.TryParse(QueryTimeout.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int value) && value is >=0 and <=86400)CommandTimeoutSeconds=value;};
        CommandBindings.Add(new CommandBinding(GoToLineCommand,(_,e)=>{GoToLine();e.Handled=true;},(_,e)=>{e.CanExecute=true;e.Handled=true;}));
        InputBindings.Add(new KeyBinding(GoToLineCommand,Key.G,ModifierKeys.Control));
        Editor.OptionChanged += (_, _) => ConfigureSqlLinks();
        using (UiPhaseScope.Start(observer, "highlight-definition")) Editor.SyntaxHighlighting = sqlHighlighting.Value;
        using (UiPhaseScope.Start(observer, "search-install")) search = SearchPanel.Install(Editor.TextArea);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Replace, (_, e) => { Replace(); e.Handled = true; }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Replace, Key.H, ModifierKeys.Control));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            { Find(); e.Handled = true; }
            else if (ReplacementBar.IsVisible && e.Key == Key.Escape)
            { ReplacementBar.Hide(); e.Handled = true; }
            else if (ReplacementBar.IsVisible && e.Key == Key.F3 && Keyboard.Modifiers == ModifierKeys.None)
            { ReplacementBar.FindNext(); e.Handled = true; }
        };
        Editor.TextChanged += (_, _) => { EditRevision++; RecoveryRevision++; IsDirty = true; ErrorLocation.IsEnabled=false;RefreshPlanRevision(); StateChanged?.Invoke(this, EventArgs.Empty); };
    }

    public void Find() { ClearAnalysisReview();HideSessionSettings();NavigationBar.Visibility=Visibility.Collapsed;ReplacementBar.Hide(); search.Open(); search.Reactivate(); }
    public void Replace() { ClearAnalysisReview();HideSessionSettings();NavigationBar.Visibility=Visibility.Collapsed;search.Close(); ReplacementBar.Open(Editor); }

    public async Task ConnectAsync(string connectionString)
    {
        if (IsBusy) return;
        if (session is not null) throw new InvalidOperationException("Open a new query to use a different connection.");
        IsBusy = true;
        cancellation = new CancellationTokenSource();
        StateChanged?.Invoke(this, EventArgs.Empty);
        var candidate = new QuerySession(connectionString);
        try
        {
            await candidate.OpenAsync(cancellation.Token);
            session = candidate;
            QueryStatus.Text = $"Connected | {Database}";
            Messages.Text = $"Connected to {Database}. Each query tab has its own session.";
        }
        catch { await candidate.DisposeAsync(); throw; }
        finally { IsBusy = false; cancellation.Dispose(); cancellation = null; UpdateExport(); StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public async Task ExecuteAsync()
    {
        if (IsBusy || session?.IsConnected != true) return;
        string sql = Editor.SelectionLength > 0 ? Editor.SelectedText : Editor.Text;
        int submittedStart=Editor.SelectionLength>0?Editor.SelectionStart:0;
        long submittedRevision=EditRevision;
        if (string.IsNullOrWhiteSpace(sql)) return;
        if (!int.TryParse(QueryTimeout.Text,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out int timeout) || timeout is < 0 or > 86400)
        {
            QueryStatus.Text="Enter a read timeout from 0 to 86400 seconds (0 = no timeout).";
            QueryTimeout.Focus(); QueryTimeout.SelectAll(); return;
        }
        if (QueryPlaceholders.Find(sql) is { } unbound)
        {
            // Sending a bare $n breaks the session (08P01 and Npgsql closes the connection), so refuse before anything is sent.
            MarkUnboundParameter(unbound, submittedStart, submittedRevision);
            Messages.Text = $"Not sent: the SQL contains parameter placeholder {unbound.Description}, which has no value. Placeholders like $1 come from prepared statements or pg_stat_statements; replace each one with a literal value before executing. Estimated Plan shows the generic plan without values.\nNothing was sent to the server and the session stays open. Go to Error selects the first placeholder.";
            QueryStatus.Text = $"Not sent | {Database} | Unbound parameter ${unbound.Number}";
            OutputTabs.SelectedIndex = 1;
            return;
        }
        var notices=new QueryNotices();
        ClearAnalysisReview();
        errorOffset=null;ErrorLocation.IsEnabled=false;
        var options=new QueryExecutionOptions(timeout,notices);
        QueryTimeout.IsEnabled=false;
        IsBusy = executingQuery = true;
        cancellation = new CancellationTokenSource();
        StateChanged?.Invoke(this, EventArgs.Empty);
        QueryStatus.Text = "Executing query…";
        Messages.Clear();
        ResultSets.Items.Clear();
        displayLimited = false;
        resultsIncomplete = true;
        UpdateExport();
        int displayedRows = 0;
        var timer = Stopwatch.StartNew();
        try
        {
            var tables = new List<DataTable>();
            var result = await session.StreamAsync(sql, async (batch, token) =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (batch.Columns is not null)
                    {
                        var table = new DataTable();
                        for (int i = 0; i < batch.Columns.Count; i++)
                            table.Columns.Add(new DataColumn($"c{i}", typeof(object)) { Caption = batch.Columns[i] });
                        tables.Add(table);
                        AddResultGrid(table);
                        if (ResultSets.Items.Count == 1) { ResultSets.SelectedIndex = 0; OutputTabs.SelectedIndex = 0; }
                    }
                    foreach (var values in batch.Rows) tables[batch.ResultIndex].Rows.Add(values);
                    displayedRows += batch.Rows.Count;
                    if (batch.DisplayLimited) { displayLimited = true; UpdateExport(); }
                    QueryStatus.Text = $"Executing query | {Database} | {displayedRows:N0} displayed row(s)" + (displayLimited ? " | Display limited; reading remaining results" : "");
                }, System.Windows.Threading.DispatcherPriority.Background, token);
            }, options, cancellation.Token);
            resultsIncomplete = false;
            displayLimited = result.Truncated;
            if (tables.Count == 0) OutputTabs.SelectedIndex = 1;
            var rows = result.DisplayedRows;
            Messages.Text = $"Query completed. {rows:N0} displayed row(s).\nRows affected: {result.RecordsAffected}.\nElapsed: {timer.Elapsed.TotalSeconds:F3} s.";
            if (result.Truncated) Messages.Text += $"\nDisplay limit reached ({result.LimitReasons}): {QueryDisplayBudget.Description} Remaining rows were read but not displayed. Values are never shortened; an oversized row and subsequent rows are omitted.";
            QueryStatus.Text = $"Query completed | {Database} | {timer.Elapsed.TotalSeconds:F3} s | {rows:N0} rows" + (result.Truncated ? " | Display limited" : "");
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or InvalidOperationException or IOException)
        {
            if(ex is PostgresException positioned)SetErrorPosition(sql,positioned.Position,submittedStart,submittedRevision);
            Messages.Text = ex is PostgresException pg ? QueryErrorFormatter.Format(pg) :
                ex is OperationCanceledException ? "Query canceled. An explicit transaction may require ROLLBACK before further commands." : ex.Message;
            Messages.Text += $"\nPartial results: {displayedRows:N0} displayed row(s) received before execution stopped. These results do not confirm successful completion or commit.\nAn explicit transaction may require ROLLBACK before further commands.";
            QueryStatus.Text = $"Query did not complete | {Database} | Partial results: {displayedRows:N0} rows";
            OutputTabs.SelectedIndex = 1;
        }
        finally
        {
            IsBusy = executingQuery = false;
            QueryTimeout.IsEnabled=true;
            foreach(var notice in notices.Items)
                Messages.AppendText($"\n{notice.Severity} {notice.SqlState}: {notice.Message}");
            if(notices.Omitted>0)
                Messages.AppendText($"\n{notices.Omitted:N0} server notice(s) omitted: limit 100 notices, 4096 characters each, 65536 characters total.");
            cancellation.Dispose();
            cancellation = null;
            UpdateExport();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AddResultGrid(DataTable table)
    {
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, RowHeight = 23,
            HeadersVisibility = DataGridHeadersVisibility.All, GridLinesVisibility = DataGridGridLinesVisibility.All,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 12,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader, ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader };
        foreach (DataColumn column in table.Columns)
            grid.Columns.Add(new DataGridTextColumn { Header = column.Caption, SortMemberPath = column.ColumnName,
                Binding = new Binding($"[{column.ColumnName}]") { Converter = new DatabaseValueConverter() } });
        grid.ItemsSource = table.DefaultView;
        DataGridRowNames.SetEnabled(grid, true);
        grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, e) => { CopyResultSelection(grid, true); e.Handled = true; },
            (_, e) => { e.CanExecute = !IsBusy && grid.SelectedCells.Count > 0; e.Handled = true; }));
        grid.CommandBindings.Add(new CommandBinding(CopyWithoutHeaders,
            (_, e) => { CopyResultSelection(grid, false); e.Handled = true; },
            (_, e) => { e.CanExecute = !IsBusy && grid.SelectedCells.Count > 0; e.Handled = true; }));
        grid.InputBindings.Add(new KeyBinding(CopyWithoutHeaders, Key.C, ModifierKeys.Control | ModifierKeys.Shift));
        grid.SelectedCellsChanged += (_, _) => CommandManager.InvalidateRequerySuggested();
        grid.ContextMenu = new ContextMenu();
        grid.ContextMenu.Items.Add(new MenuItem { Header="Copy with _headers", Command=ApplicationCommands.Copy, CommandTarget=grid, InputGestureText="Ctrl+C", ToolTip="Selected cells as quoted tab-separated text. NULL = unquoted \\N; unselected gaps are empty." });
        grid.ContextMenu.Items.Add(new MenuItem { Header="Copy _without headers", Command=CopyWithoutHeaders, CommandTarget=grid, InputGestureText="Ctrl+Shift+C", ToolTip="Selected cells without column labels. NULL = unquoted \\N; unselected gaps are empty." });
        System.Windows.Automation.AutomationProperties.SetName(grid.ContextMenu, "Result copy options");
        ResultSets.Items.Add(new TabItem { Header = $"Result {ResultSets.Items.Count + 1}", Content = grid });
    }

    internal string PrepareSelectedResultCopy(DataGrid grid, bool includeHeaders)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current operation before copying results.");
        return ResultGridCopy.Prepare(grid, includeHeaders);
    }
    internal string DescribeCopiedSelection(DataGrid grid, bool includeHeaders) =>
        $"Copied {grid.SelectedCells.Count:N0} selected cell(s) " + (includeHeaders ? "with headers." : "without headers.") +
        (resultsIncomplete ? " Partial results: query did not complete." : "") +
        (displayLimited ? " Display limit reached; displayed cells only." : "");

    private void CopyResultSelection(DataGrid grid, bool includeHeaders)
    {
        try
        {
            string text = PrepareSelectedResultCopy(grid, includeHeaders);
            writeResultClipboard(text);
            QueryStatus.Text = DescribeCopiedSelection(grid, includeHeaders);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            QueryStatus.Text = ex is System.Runtime.InteropServices.ExternalException
                ? ex.HResult == unchecked((int)0x800401D0)
                    ? "Copy failed: Clipboard is busy. Try Copy again."
                    : "Copy failed: Windows could not update the clipboard. Try Copy again or export CSV."
                : "Copy failed: " + ex.Message;
        }
    }

    private void CancelQuery(object sender, RoutedEventArgs e) => Cancel();
    public void Cancel()
    {
        if (cancellation is null) return;
        if (executingQuery)
        {
            CancelQueryButton.IsEnabled = false;PlanCancelButton.IsEnabled=false;
            QueryStatus.Text = $"Cancel requested | {Database} | Waiting for the server";
        }
        cancellation.Cancel();
    }
    private void ExportFormatChanged(object sender,SelectionChangedEventArgs e){if(CsvOptionsPanel is not null)UpdateExport();}
    private void ResultSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateExport();
    private void UpdateExport()
    {
        CommandManager.InvalidateRequerySuggested();
        if (ExportCsvButton is null || ExplainButton is null || PlanCancelButton is null || PlanExportButton is null) return;
        ExplainButton.IsEnabled=IsConnected && !IsBusy;
        if(AnalyzeButton is not null)AnalyzeButton.IsEnabled=IsConnected && !IsBusy;
        if(AnalysisRun is not null)AnalysisRun.IsEnabled=!IsBusy && analysisReview is not null;
        PlanExportButton.IsEnabled=currentPlan is not null && !IsBusy;
        if(SessionRefreshButton is not null)SessionRefreshButton.IsEnabled=IsConnected && !IsBusy;
        if(SessionEditButton is not null)SessionEditButton.IsEnabled=sessionSnapshot is not null && !IsBusy;
        if(SessionSettingPanel is not null)SessionSettingPanel.IsEnabled=!IsBusy;
        if(SessionCancelButton is not null){SessionCancelButton.Visibility=inspectingSession?Visibility.Visible:Visibility.Collapsed;SessionCancelButton.IsEnabled=inspectingSession && cancellation?.IsCancellationRequested==false;}
        PlanCancelButton.Visibility=IsBusy?Visibility.Visible:Visibility.Collapsed;
        PlanCancelButton.IsEnabled=IsBusy && cancellation?.IsCancellationRequested==false;
        bool json=ExportFormat.SelectedIndex==1;
        CsvOptionsPanel.Visibility=json?Visibility.Collapsed:Visibility.Visible;
        CsvOptionsPanel.IsEnabled=ExportRows.IsEnabled=ExportFormat.IsEnabled=!IsBusy;
        ExportCsvButton.Content=json?"Export JSON…":"Export CSV…";
        ExportCsvButton.ToolTip=json?"Save ordered columns and rows as JSON strings/null":"Save displayed results using the selected CSV options";
        ExportCsvButton.IsEnabled = !IsBusy && ResultSets.SelectedItem is TabItem { Content: DataGrid };
        CancelQueryButton.Visibility = executingQuery ? Visibility.Visible : Visibility.Collapsed;
        CancelQueryButton.IsEnabled = executingQuery && cancellation?.IsCancellationRequested == false;
        CancelExportButton.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        ExportScope.Text = executingQuery ? (displayLimited ? "Display limit reached - reading remaining results. Export available after execution." : "Receiving results - export available after execution.") : resultsIncomplete ? "Partial results - query did not complete. Displayed rows only." : displayLimited ? "Displayed rows only — display limit reached" : (json?"Displayed rows only · JSON strings/null; columns + rows":"Displayed rows only · NULL = unquoted \\N");
    }
    private async void ExportCsv(object sender, RoutedEventArgs e)
    {
        if (IsBusy || ResultSets.SelectedItem is not TabItem) return;
        bool json=ExportFormat.SelectedIndex==1;
        var picker = new SaveFileDialog { Filter = json?"JSON files (*.json)|*.json":"CSV files (*.csv)|*.csv", DefaultExt = json?".json":".csv", FileName = json?"results.json":"results.csv" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await ExportResultsAsync(picker.FileName,File.Exists(picker.FileName),default,json); }
        catch (OperationCanceledException) { QueryStatus.Text = "Result export canceled."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { QueryStatus.Text = "Result export failed: " + ex.Message; }
    }
    private void CancelExport(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    public Task ExportSelectedCsvAsync(string path,bool overwrite=false,CancellationToken token=default)=>ExportResultsAsync(path,overwrite,token,false);
    public Task ExportSelectedJsonAsync(string path,bool overwrite=false,CancellationToken token=default)=>ExportResultsAsync(path,overwrite,token,true);
    private async Task ExportResultsAsync(string path,bool overwrite,CancellationToken token,bool json)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current operation before exporting.");
        if (ResultSets.SelectedItem is not TabItem { Content: DataGrid { ItemsSource: DataView } grid }) throw new InvalidOperationException("Select a result set to export.");
        bool selectedRowsOnly=ExportRows.SelectedIndex==1;
        var options=new CsvExportOptions(CsvDelimiter.SelectedIndex switch{1=>';',2=>'\t',_=>','},CsvHeaders.IsChecked==true,CsvBom.IsChecked==true);
        IsBusy = exporting = true;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        UpdateExport(); StateChanged?.Invoke(this, EventArgs.Empty);
        bool gridEnabled=grid.IsEnabled;grid.IsEnabled=false;
        QueryStatus.Text = "Preparing displayed results…";
        try
        {
            // Snapshot the selected view on the UI thread; disk work runs off-thread.
            using var snapshot = await ResultExportSnapshot.CreateAsync(grid,selectedRowsOnly,cancellation.Token);
            QueryStatus.Text="Exporting displayed results…";
            await Task.Run(() => json?JsonResults.SaveAsync(snapshot,path,overwrite,cancellation.Token):CsvResults.SaveAsync(snapshot, path, options, overwrite, cancellation.Token));
            QueryStatus.Text = $"Exported {snapshot.Rows.Count:N0} {(selectedRowsOnly?"selected":"displayed")} row(s) to {(json?"JSON":"CSV")}." + (resultsIncomplete ? " Partial results: query did not complete." : displayLimited ? " Query display limit reached; displayed rows only." : "");
        }
        finally
        {
            grid.IsEnabled=gridEnabled;
            cancellation.Dispose(); cancellation = null; IsBusy = exporting = false;
            UpdateExport(); StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public async ValueTask DisposeAsync() { if (session is not null) await session.DisposeAsync(); }
}

public sealed class DatabaseValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => ResultCopyText.DisplayValue(value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
