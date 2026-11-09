using System.Windows;
using System.Windows.Input;
using PGMS.Desktop.ViewModels;

namespace PGMS.Desktop;

/// <summary>Query ▸ Results To and the SQL Editor toolbar buttons (M1044): Results to Text (Ctrl+T), Grid (Ctrl+D) and File (Ctrl+Shift+F) set the
/// destination of the active query tab, used from its next execution on, as in SSMS 22.11.</summary>
public partial class MainWindow
{
    private void ResultsToText(object sender, RoutedEventArgs e) => SetResultsDestination(ResultsDestination.Text);
    private void ResultsToGrid(object sender, RoutedEventArgs e) => SetResultsDestination(ResultsDestination.Grid);
    private void ResultsToFile(object sender, RoutedEventArgs e) => SetResultsDestination(ResultsDestination.File);

    internal void SetResultsDestination(ResultsDestination destination)
    {
        if (ActiveQuery is { } query) query.ResultsDestination = destination;
        RefreshResultsButtons();
    }

    private void ResultsToMenuOpened(object sender, RoutedEventArgs e)
    {
        var mode = ActiveQuery?.ResultsDestination;
        ResultsToTextMenu.IsEnabled = ResultsToGridMenu.IsEnabled = ResultsToFileMenu.IsEnabled = mode is not null;
        ResultsToTextMenu.IsChecked = mode == ResultsDestination.Text;
        ResultsToGridMenu.IsChecked = mode == ResultsDestination.Grid;
        ResultsToFileMenu.IsChecked = mode == ResultsDestination.File;
    }

    private void RefreshResultsButtons()
    {
        if (ResultsToTextButton is null) return;
        var mode = ActiveQuery?.ResultsDestination;
        ResultsToTextButton.IsEnabled = ResultsToGridButton.IsEnabled = ResultsToFileButton.IsEnabled = mode is not null;
        ResultsToTextButton.IsChecked = mode == ResultsDestination.Text;
        ResultsToGridButton.IsChecked = mode == ResultsDestination.Grid;
        ResultsToFileButton.IsChecked = mode == ResultsDestination.File;
        var query = ActiveQuery;
        EstimatedPlanButton.IsEnabled = EstimatedPlanMenu.IsEnabled = ParseMenu.IsEnabled = query is { IsConnected: true, IsBusy: false };
        ClientStatisticsMenu.IsEnabled = ResetClientStatisticsMenu.IsEnabled = OpenServerMenu.IsEnabled = query is not null;
        ClientStatisticsMenu.IsChecked = query?.IncludeClientStatistics == true;
        IntelliSenseMenu.IsEnabled = ListMembersMenu.IsEnabled = CompleteWordMenu.IsEnabled = RefreshCacheMenu.IsEnabled = query is not null;
        IntelliSenseMenu.IsChecked = query?.IntelliSenseEnabled == true;
        ActualPlanButton.IsEnabled = ActualPlanMenu.IsEnabled = query is not null;
        ActualPlanButton.IsChecked = ActualPlanMenu.IsChecked = query?.IncludeActualPlan == true;
    }

    private async void DisplayEstimatedPlan(object sender, RoutedEventArgs e) { if (ActiveQuery is { } query) await query.ExplainPlanAsync(); }
    private void ToggleActualPlan(object sender, RoutedEventArgs e) => ToggleActualPlan();
    internal void ToggleActualPlan()
    {
        if (ActiveQuery is { } query) query.IncludeActualPlan = !query.IncludeActualPlan;
        RefreshResultsButtons();
    }

    /// <summary>Query ▸ Query Options: changes only the active query tab.</summary>
    private void OpenQueryOptions()
    {
        if (ActiveQuery is not { } query) return;
        var dialog = new QueryOptionsDialog(query.CurrentQueryOptions) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } options) query.ApplyQueryOptions(options);
    }

    internal IReadOnlyCollection<QueryView> QueryDocuments => documents.Keys;
    private int jsonSequence;
    /// <summary>A JSON link opens as text in its own tab, not connected and without SQL colors.</summary>
    private void OpenJsonDocument(string json)
    {
        var view = NewQuery(json);
        view.Editor.SyntaxHighlighting = null;
        documents[view].Title = $"JSON{++jsonSequence}.json";
    }

    // ---- Select Top n Rows / Edit Top n Rows (Object Explorer table and view menus) ----
    private readonly Dictionary<EditRowsView, AvalonDock.Layout.LayoutDocument> editors = [];
    private EditRowsView? ActiveEditor => DockManager.ActiveContent as EditRowsView;
    internal IReadOnlyCollection<EditRowsView> RowEditors => editors.Keys;

    private async void SelectTopFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || (ExplorerTree.SelectedItem as CatalogNode)?.Entry is not { Schema: { } schema } entry) { Status.Text = "Connect before selecting rows."; return; }
        await SelectTopAsync(connection, schema, entry.Name);
    }

    /// <summary>As in SSMS: a new query tab (not marked as changed) with the generated SELECT, connected to the database and executed at once.</summary>
    internal async Task<QueryView?> SelectTopAsync(string connection, string schema, string table)
    {
        try
        {
            var (_, columns) = await new PGMS.PostgreSql.TableRowsService(connection).ReadColumnsAsync(schema, table);
            var query = NewQuery(PGMS.PostgreSql.TableRowsService.SelectTopSql(schema, table, columns.Select(c => c.Name).ToList(), EnvironmentSettings.Load().SelectTop));
            await query.ConnectAsync(connection);
            await query.ExecuteAsync();
            return query;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or PGMS.PostgreSql.TableRowsException or System.IO.IOException or OperationCanceledException or TimeoutException)
        { Status.Text = "Select Top Rows failed: " + (ex is Npgsql.PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message); return null; }
    }

    private async void EditTopFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || (ExplorerTree.SelectedItem as CatalogNode)?.Entry is not { Schema: { } schema } entry) { Status.Text = "Connect before editing rows."; return; }
        await OpenEditRowsAsync(connection, schema, entry.Name);
    }

    /// <summary>Edit Top n Rows in a document tab "host.database - schema.table"; the same table already open is activated and read again.</summary>
    internal async Task<EditRowsView> OpenEditRowsAsync(string connection, string schema, string table)
    {
        int top = EnvironmentSettings.Load().EditTop;
        if (editors.FirstOrDefault(d => d.Key.Targets(connection, schema, table)) is { Key: { } existing, Value: { } tab })
        { tab.IsActive = true; await existing.LoadAsync(top); return existing; }
        var view = new EditRowsView(connection, schema, table, top);
        var document = new AvalonDock.Layout.LayoutDocument { Title = view.Title, ContentId = Guid.NewGuid().ToString(), Content = view, ToolTip = view.Title };
        editors.Add(view, document);
        view.StateChanged += RefreshCommands;
        document.Closed += (_, _) => { editors.Remove(view); RefreshCommands(); };
        AddDocument(document);
        document.IsActive = true;
        RefreshCommands();
        await view.LoadAsync();
        return view;
    }

    /// <summary>Ctrl+T, Ctrl+D and Ctrl+Shift+F while the keyboard is in a query tab.</summary>
    private bool HandleResultsShortcut(KeyEventArgs e)
    {
        if (ActiveQuery is not { IsKeyboardFocusWithin: true } active) return false;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.M) { ToggleActualPlan(); e.Handled = true; return true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L) { e.Handled = true; _ = active.ExplainPlanAsync(); return true; }
        ResultsDestination? mode = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.T, ModifierKeys.Control) => ResultsDestination.Text,
            (Key.D, ModifierKeys.Control) => ResultsDestination.Grid,
            (Key.F, ModifierKeys.Control | ModifierKeys.Shift) => ResultsDestination.File,
            _ => null
        };
        if (mode is null) return false;
        SetResultsDestination(mode.Value);
        e.Handled = true;
        return true;
    }
}
