using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Results to Text and Results to File (M1044), laid out as measured in SSMS 22.11 (docs/MARCO-M1044.md). Text keeps the display budget
/// of the grid; File writes every row, each value cut at 256 characters, spooling a result to a temporary file until it ends.</summary>
public partial class QueryView
{
    private ResultsDestination resultsDestination = ResultsDestination.Grid;
    /// <summary>Destination of the next execution; changing it does not touch results already shown.</summary>
    public ResultsDestination ResultsDestination
    {
        get => resultsDestination;
        set { if (resultsDestination == value) return; resultsDestination = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }
    /// <summary>Asks where Results to File writes; null cancels the execution. Replaced by tests.</summary>
    internal Func<string?>? PickResultsFile { get; set; }
    /// <summary>Last file written by Results to File.</summary>
    public string? LastResultsFile { get; private set; }

    private string? AskResultsFile()
    {
        if (PickResultsFile is { } pick) return pick();
        var dialog = new SaveFileDialog
        {
            Title = "Save Results", Filter = "Report files (*.rpt)|*.rpt|All files (*.*)|*.*", DefaultExt = ".rpt", AddExtension = true, OverwritePrompt = true,
            InitialDirectory = LastResultsFile is { } last ? Path.GetDirectoryName(last) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }

    /// <summary>Shows the panes of a destination, as SSMS does when execution starts: Grid shows Results (grid) and Messages; Text only Results (text);
    /// File only Messages.</summary>
    private void ApplyOutputLayout(ResultsDestination mode)
    {
        ResultsTab.Visibility = mode == ResultsDestination.File ? Visibility.Collapsed : Visibility.Visible;
        MessagesTab.Visibility = mode == ResultsDestination.Text ? Visibility.Collapsed : Visibility.Visible;
        ResultsText.Visibility = mode == ResultsDestination.Text ? Visibility.Visible : Visibility.Collapsed;
        ResultsGridPanel.Visibility = mode == ResultsDestination.Text ? Visibility.Collapsed : Visibility.Visible;
        // Grid keeps the tab the user had selected (as before M1044), unless that tab was just hidden.
        if (mode != ResultsDestination.Grid) OutputTabs.SelectedItem = mode == ResultsDestination.File ? MessagesTab : ResultsTab;
        else if (OutputTabs.SelectedItem is UIElement { Visibility: not Visibility.Visible }) OutputTabs.SelectedItem = ResultsTab;
    }

    private static string NoticeText(QueryNotice notice) => $"{notice.Severity}: {notice.Message}";

    private async Task ExecuteAsTextAsync(string sql, int submittedStart, long submittedRevision, int timeout)
    {
        bool toFile = ResultsDestination == ResultsDestination.File;
        string? path = null;
        if (toFile && (path = AskResultsFile()) is null) { QueryStatus.Text = $"Not executed | {Database} | No results file chosen"; return; }
        var notices = new QueryNotices();
        ClearAnalysisReview();
        errorOffset = null; ErrorLocation.IsEnabled = false;
        var actualPlans = IncludeActualPlan ? new ActualPlanCapture() : null;
        var options = new QueryExecutionOptions(timeout, notices) { ReportStatements = true, TextCellLimit = toFile ? QueryOptions.TextMaxCharacters : null, ActualPlans = actualPlans, DiscardResults = QueryOptions.TextDiscardResults };
        QueryTimeout.IsEnabled = false;
        IsBusy = executingQuery = true;
        cancellation = new CancellationTokenSource();
        StateChanged?.Invoke(this, EventArgs.Empty);
        QueryStatus.Text = "Executing query…";
        Messages.Clear(); ClearResultPages(); ResultSets.Items.Clear(); ResultsText.Clear();
        ApplyOutputLayout(toFile ? ResultsDestination.File : ResultsDestination.Text);
        displayLimited = false; resultsIncomplete = true;
        UpdateExport();
        int displayedRows = 0, noticeIndex = 0, shown = 0;
        DateTime startedUtc = DateTime.UtcNow;
        Exception? failure = null; bool canceled = false; long? affectedRows = null;
        var timer = Stopwatch.StartNew();
        var screen = new StringWriter();
        StringWriter? messagesOut = toFile ? new StringWriter() : null;
        StreamWriter? file = null;
        TextResultsWriter? results = null, messages = null;
        void Each(Action<TextResultsWriter> act) { act(results!); if (messages is not null) act(messages); }
        void DrainNotices() { var items = notices.Items; for (; noticeIndex < items.Count; noticeIndex++) { var text = NoticeText(items[noticeIndex]); Each(w => w.Message(text)); } }
        void Process(QueryBatch batch)
        {
            DrainNotices();
            if (batch.Columns is not null) results!.StartResult(batch.Columns, spool: toFile, batch.ColumnTypes);
            if (batch.Rows.Count > 0) { results!.AddRows(batch.Rows); displayedRows += batch.Rows.Count; }
            if (batch.DisplayLimited) displayLimited = true;
            if (batch.StatementEnd) Each(w => w.Statement(batch.RowsAffected));
        }
        // Text mode shows the text as it grows; File mode shows Messages when it ends (the writers run off the UI thread).
        void Show()
        {
            var built = (messagesOut ?? screen).GetStringBuilder();
            var target = toFile ? Messages : ResultsText;
            if (built.Length > shown) { target.AppendText(built.ToString(shown, built.Length - shown)); shown = built.Length; }
        }
        bool succeeded = false;
        try
        {
            if (toFile)
            {
                file = new StreamWriter(path!, false, new UTF8Encoding(true)) { NewLine = "\r\n" };
                messages = new TextResultsWriter(messagesOut!, messagesOnly: true) { NoCount = QueryOptions.NoCount, ShowCompletionTime = QueryOptions.ShowCompletionTime };
            }
            results = new TextResultsWriter(toFile ? file! : screen, false, QueryOptions.TextMaxCharacters, QueryOptions.TextLayout) { NoCount = QueryOptions.NoCount, ShowCompletionTime = QueryOptions.ShowCompletionTime };
            if (await ApplySessionOptionsAsync(cancellation.Token) is { } sessionNote) Each(w => w.Message(sessionNote));
            if (QueryOptions.TextIncludeQuery) results.Message(sql.Trim());
            var lastStatus = Stopwatch.StartNew();
            int nextFlush = 100;
            var meter = StartClientTrial(sql);
            var completion = await session!.StreamAsync(sql, Measured(meter, async (batch, token) =>
            {
                if (toFile)
                {
                    Process(batch);
                    if (lastStatus.ElapsedMilliseconds >= 250)
                    {
                        lastStatus.Restart();
                        int rows = displayedRows;
                        await Dispatcher.InvokeAsync(() => QueryStatus.Text = $"Executing query | {Database} | {rows:N0} row(s) written to file", System.Windows.Threading.DispatcherPriority.Background, token);
                    }
                    return;
                }
                // Observed in SSMS: the text goes to the screen at row 100, every 500 rows and at the end; only then does the reading thread wait for it.
                // The clock is PGMS's, so a slow query still shows what it has. Show runs while this thread waits, so the writers are never used at the same time.
                Process(batch);
                if (!batch.StatementEnd && displayedRows < nextFlush && lastStatus.ElapsedMilliseconds < 500) return;
                if (displayedRows >= nextFlush) nextFlush = displayedRows < 100 ? 100 : (displayedRows / 500 + 1) * 500;
                lastStatus.Restart();
                await Dispatcher.InvokeAsync(() =>
                {
                    Show();
                    QueryStatus.Text = $"Executing query | {Database} | {displayedRows:N0} row(s)" + (displayLimited ? " | Display limited; reading remaining results" : "");
                }, System.Windows.Threading.DispatcherPriority.Background, token);
            }), options, cancellation.Token);
            RecordClientTrial(meter);
            affectedRows = completion.RecordsAffected >= 0 ? completion.RecordsAffected : null;
            succeeded = true;
            resultsIncomplete = false;
            DrainNotices();
            if (displayLimited) Each(w => w.Message($"Display limit reached: {QueryDisplayBudget.Description} Remaining rows were read but not shown. Results to File writes every row."));
            Each(w => w.Complete(DateTimeOffset.Now));
            if (toFile) LastResultsFile = path;
            QueryStatus.Text = $"Query completed | {Database} | {timer.Elapsed.TotalSeconds:F3} s | {displayedRows:N0} rows" + (toFile ? " | Saved to " + Path.GetFileName(path) : displayLimited ? " | Display limited" : "");
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            failure = ex; canceled = ex is OperationCanceledException || cancellation?.IsCancellationRequested == true;
            if (ex is PostgresException positioned) SetErrorPosition(sql, positioned.Position, submittedStart, submittedRevision);
            string error = ex is PostgresException pg ? QueryErrorFormatter.Format(pg) :
                ex is OperationCanceledException ? "Query canceled. An explicit transaction may require ROLLBACK before further commands." : ex.Message;
            if (results is not null)
            {
                try { DrainNotices(); Each(w => w.Message(error)); Each(w => w.Complete(DateTimeOffset.Now, succeeded: false)); }
                catch (IOException) { }
            }
            else Messages.Text = error;
            QueryStatus.Text = $"Query did not complete | {Database} | {displayedRows:N0} row(s) " + (toFile ? "written" : "received");
            if (results is null) ApplyOutputLayout(ResultsDestination.File); // the file could not be created: show the error in Messages
        }
        finally
        {
            results?.Dispose(); messages?.Dispose();
            try { file?.Dispose(); } catch (IOException ex) { Messages.AppendText("\n" + ex.Message); succeeded = false; }
            Show();
            if (!succeeded && toFile && file is not null) Messages.AppendText($"\nThe file {path} holds the results received before execution stopped.");
            if (actualPlans is not null) await ShowActualPlansAsync(actualPlans, submittedRevision);
            IsBusy = executingQuery = false;
            QueryTimeout.IsEnabled = true;
            if (notices.Omitted > 0) (toFile ? Messages : ResultsText).AppendText($"\n{notices.Omitted:N0} server notice(s) omitted: limit 100 notices, 4096 characters each, 65536 characters total.");
            cancellation.Dispose();
            cancellation = null;
            UpdateExport();
            StateChanged?.Invoke(this, EventArgs.Empty);
            RecordHistory(sql, startedUtc, timer.Elapsed, failure, canceled, displayedRows, affectedRows);
        }
    }
}
