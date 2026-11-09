using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Query menu items of PGMS-034: Disconnect, Parse and Client Statistics.</summary>
public partial class QueryView
{
    /// <summary>Closes this tab's session and keeps its text and results; Connect can open another one.</summary>
    internal async Task DisconnectAsync()
    {
        if (IsBusy || session is not { } closing) return;
        session = null; ConnectionString = null;
        ForgetSessionState(); HideSessionSettings(); ClearAnalysisReview();
        sessionSnapshot = null; SessionProperties.ItemsSource = null;
        QueryStatus.Text = "Not connected";
        UpdateExport(); StateChanged?.Invoke(this, EventArgs.Empty);
        await closing.DisposeAsync();
    }

    /// <summary>Query ▸ Parse: the server checks the SQL without running it. PostgreSQL's parse also resolves names, so unlike SET PARSEONLY a statement that
    /// depends on an object created earlier in the same script reports that object as missing.</summary>
    public async Task ParseAsync()
    {
        if (IsBusy || session?.IsConnected != true) return;
        string sql = Editor.SelectionLength > 0 ? Editor.SelectedText : Editor.Text;
        int submittedStart = Editor.SelectionLength > 0 ? Editor.SelectionStart : 0;
        long submittedRevision = EditRevision;
        if (string.IsNullOrWhiteSpace(sql)) return;
        if (MessagesTab.Visibility != Visibility.Visible) ApplyOutputLayout(ResultsDestination.Grid);
        if (QueryPlaceholders.Find(sql) is { } unbound)
        {
            MarkUnboundParameter(unbound, submittedStart, submittedRevision);
            Messages.Text = $"Not parsed: the SQL contains parameter placeholder {unbound.Description}, which has no value.";
            QueryStatus.Text = $"Not parsed | {Database} | Unbound parameter ${unbound.Number}";
            OutputTabs.SelectedItem = MessagesTab;
            return;
        }
        ClearAnalysisReview();
        errorOffset = null; ErrorLocation.IsEnabled = false;
        IsBusy = executingQuery = true;
        cancellation = new CancellationTokenSource();
        Messages.Clear();
        UpdateExport(); StateChanged?.Invoke(this, EventArgs.Empty);
        QueryStatus.Text = "Parsing…";
        try
        {
            bool insideTransaction = false;
            if (mayHaveOpenTransaction)
            {
                var risk = await session.InspectLossRiskAsync(cancellation.Token);
                if (risk.Aborted) { Messages.Text = "Not parsed: the current transaction has failed. Run ROLLBACK first."; QueryStatus.Text = $"Not parsed | {Database}"; return; }
                insideTransaction = risk.OpenTransaction;
                if (!insideTransaction) mayHaveOpenTransaction = false;
            }
            await session.ParseAsync(sql, insideTransaction, cancellation.Token);
            Messages.Text = "Commands completed successfully.";
            QueryStatus.Text = $"Parse succeeded | {Database}";
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or InvalidOperationException or IOException)
        {
            if (ex is PostgresException positioned) SetErrorPosition(sql, positioned.Position, submittedStart, submittedRevision);
            Messages.Text = ex is PostgresException pg ? QueryErrorFormatter.Format(pg) : ex is OperationCanceledException ? "Parse canceled." : ex.Message;
            QueryStatus.Text = $"Parse failed | {Database}";
        }
        finally
        {
            OutputTabs.SelectedItem = MessagesTab;
            IsBusy = executingQuery = false;
            cancellation.Dispose(); cancellation = null;
            UpdateExport(); StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool includeClientStatistics;
    private readonly ClientStatisticsLog clientStatistics = new();
    internal ClientStatisticsLog ClientStatistics => clientStatistics;

    /// <summary>Query ▸ Include Client Statistics: each later execution adds a trial to the Client Statistics tab of this query.</summary>
    public bool IncludeClientStatistics
    {
        get => includeClientStatistics;
        set
        {
            includeClientStatistics = value;
            ClientStatisticsTab.Visibility = value || clientStatistics.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ResetClientStatistics()
    {
        clientStatistics.Reset();
        ShowClientStatistics();
        ClientStatisticsTab.Visibility = includeClientStatistics ? Visibility.Visible : Visibility.Collapsed;
    }

    private ClientTrialMeter? StartClientTrial(string sql) => includeClientStatistics ? new ClientTrialMeter(sql) : null;

    private static Func<QueryBatch, CancellationToken, ValueTask> Measured(ClientTrialMeter? meter, Func<QueryBatch, CancellationToken, ValueTask> consume) => meter?.Wrap(consume) ?? consume;

    private void RecordClientTrial(ClientTrialMeter? meter)
    {
        if (meter is null) return;
        clientStatistics.Add(meter.Finish(DateTime.Now));
        ShowClientStatistics();
    }

    private void ShowClientStatistics()
    {
        var (headers, rows) = clientStatistics.Table();
        ClientStatisticsGrid.Columns.Clear();
        for (int i = 0; i < headers.Count; i++)
            ClientStatisticsGrid.Columns.Add(new DataGridTextColumn { Header = headers[i], Binding = new Binding($"[{i}]"), Width = i == 0 ? new DataGridLength(360) : DataGridLength.Auto });
        ClientStatisticsGrid.ItemsSource = rows;
    }
}
