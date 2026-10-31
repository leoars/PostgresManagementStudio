using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public sealed record TableEditSelection(TableProperties Table, string? Column);

public partial class TablePropertiesDialog : Window
{
    private readonly string connection;
    private bool ready, busy;
    private CancellationTokenSource? inspection;
    public TableEditSelection? EditSelection { get; private set; }
    public TableProperties? Snapshot { get; private set; }
    public TablePropertiesDialog(string connection, string? schema = null, string? table = null)
    {
        this.connection = connection; InitializeComponent(); WindowWorkArea.Attach(this);
        var target = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Table Properties\nServer: {target.Host}:{target.Port}   Database: {target.Database}   User: {target.Username}";
        SchemaName.Text = schema ?? "public"; TableName.Text = table ?? "";
        ready = true; ClearSnapshot();
    }
    private void Changed(object sender, RoutedEventArgs e) { if (ready) ClearSnapshot(); }
    private void ClearSnapshot()
    {
        TableOperationsButton.IsEnabled = ColumnOperationsButton.IsEnabled = false;
        TriggersGrid.ItemsSource = null; TriggerDetail.Clear(); TriggerNotice.Text = "Inspect to read triggers.";
        IncomingGrid.ItemsSource = null; IncomingDetail.Clear(); IncomingNotice.Text = "Inspect to read incoming foreign keys.";
        ConstraintsGrid.ItemsSource = null; ConstraintDetail.Clear(); ConstraintNotice.Text = "Inspect to read constraints.";
        IndexesGrid.ItemsSource = null; IndexDetail.Clear(); IndexNotice.Text = "Inspect to read indexes.";
        Snapshot = null; Summary.Clear(); ColumnsGrid.ItemsSource = null; ColumnDetail.Clear();
        DependentsGrid.ItemsSource = null; DependencyDetail.Clear(); DependencyNotice.Text = "Inspect to read direct dependency entries.";
        Feedback.Text = "Inspect to read current catalog metadata. No table rows are read.";
        try { ObjectSql.Relation(SchemaName.Text, TableName.Text); InspectButton.IsEnabled = !busy; }
        catch (ArgumentException ex) { InspectButton.IsEnabled = false; Feedback.Text = ex.Message; }
    }
    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();
    public async Task InspectAsync()
    {
        if (busy) return;
        ClearSnapshot(); busy = true; Fields.IsEnabled = CloseButton.IsEnabled = false;
        using var cancellation = new CancellationTokenSource();
        inspection = cancellation; CancelInspectionButton.IsEnabled = true;
        Feedback.Text = "Reading properties… Cancel Inspection stops this read.";
        try
        {
            var info = await new TablePropertiesService(connection).InspectAsync(SchemaName.Text, TableName.Text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            TriggersGrid.ItemsSource = info.Triggers; TriggerNotice.Text = $"{info.Triggers.Count} triggers, including internal constraint triggers. Firing mode depends on session_replication_role. Function bodies and effects are not inspected.";
            IncomingGrid.ItemsSource = info.IncomingForeignKeys; IncomingNotice.Text = $"{info.IncomingForeignKeys.Count} foreign keys referencing this table, including self-references. Direct catalog entries only; not a complete DROP impact report.";
            ConstraintsGrid.ItemsSource = info.Constraints; ConstraintNotice.Text = $"{info.Constraints.Count} constraints owned by this table. References to this table are on Incoming FKs. Column NOT NULL is shown on Columns.";
            IndexesGrid.ItemsSource = info.Indexes; IndexNotice.Text = $"{info.Indexes.Count} indexes. Select an index to read its full definition. Valid and Ready are catalog flags, not a performance assessment.";
            Snapshot = info; ColumnsGrid.ItemsSource = info.Columns;
            DependentsGrid.ItemsSource = info.Dependents;
            DependencyNotice.Text = (info.DependentsTruncated ? "Showing first 1000 entries; more exist. " : $"{info.Dependents.Count} direct entries. ") + "Includes internal objects and column references; not a recursive graph or complete DROP impact report. External SQL is not tracked.";
            DependencyDetail.Text = "Select an entry to read its full description.";
            ColumnDetail.Text = "Select a column to read its default or generation expression and comment.";
            Summary.Text = $"Schema: {info.Schema}\nTable: {info.Name}\nOID: {info.Oid}\nOwner: {info.Owner}\nKind: {info.Kind}\nPersistence: {info.Persistence}\nEstimated rows: {(info.EstimatedRows < 0 ? "Unknown (not analyzed)" : info.EstimatedRows.ToString(CultureInfo.InvariantCulture))}\nRow security enabled: {info.RowSecurity}\nRow security forced: {info.ForceRowSecurity}\n\nComment:\n{info.Comment ?? "(none)"}";
            Feedback.Text = "Catalog snapshot loaded. Estimated rows may be stale; this is not a row count. Inspect again to refresh.";
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or TimeoutException)
        { Feedback.Text = "Inspection canceled. No snapshot loaded. Inspect again to retry."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException)
        { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { inspection = null; CancelInspectionButton.IsEnabled = false; busy = false; Fields.IsEnabled = CloseButton.IsEnabled = true; UpdateOperationButtons(); }
    }
    private void ColumnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ready) UpdateOperationButtons();
        if (ColumnsGrid.SelectedItem is TableColumnInfo column)
            ColumnDetail.Text = $"Column: {column.Name}\n{(column.Generated.Length > 0 ? "Generation expression" : "Default expression")}: {column.DefaultExpression ?? "(none)"}\nComment: {column.Comment ?? "(none)"}";
    }
    private void TriggerSelected(object sender, SelectionChangedEventArgs e)
    {
        TriggerDetail.Text = TriggersGrid.SelectedItem is InspectedTableTrigger trigger ? trigger.Definition : "";
    }
    private void IncomingSelected(object sender, SelectionChangedEventArgs e)
    {
        IncomingDetail.Text = IncomingGrid.SelectedItem is IncomingForeignKeyInfo key
            ? $"Source: {SqlIdentifier.Quote(key.Schema)}.{SqlIdentifier.Quote(key.Table)}\nConstraint: {SqlIdentifier.Quote(key.Name)}\n{key.Definition}" : "";
    }
    private void ConstraintSelected(object sender, SelectionChangedEventArgs e)
    {
        ConstraintDetail.Text = ConstraintsGrid.SelectedItem is InspectedTableConstraint constraint ? constraint.Definition : "";
    }
    private void IndexSelected(object sender, SelectionChangedEventArgs e)
    {
        IndexDetail.Text = IndexesGrid.SelectedItem is TableIndexInfo index ? index.Definition : "";
    }
    private void DependentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DependentsGrid.SelectedItem is TableDependencyInfo dependency)
            DependencyDetail.Text = $"{dependency.Description}\nReference: {dependency.Reference}\nDependency kind: {dependency.Kind}";
    }
    private void UpdateOperationButtons()
    {
        TableOperationsButton.IsEnabled = !busy && Snapshot?.Kind == "Ordinary table";
        ColumnOperationsButton.IsEnabled = TableOperationsButton.IsEnabled && ColumnsGrid.SelectedItem is TableColumnInfo;
    }
    private void OpenTableOperations(object sender, RoutedEventArgs e)
    {
        if (busy || Snapshot?.Kind != "Ordinary table") return;
        EditSelection = new(Snapshot, null); DialogResult = true;
    }
    private void OpenColumnOperations(object sender, RoutedEventArgs e)
    {
        if (busy || Snapshot?.Kind != "Ordinary table" || ColumnsGrid.SelectedItem is not TableColumnInfo column) return;
        EditSelection = new(Snapshot, column.Name); DialogResult = true;
    }
    private void CancelInspection(object sender, RoutedEventArgs e) => RequestCancellation();
    private void RequestCancellation()
    {
        if (!busy || inspection is null) return;
        CancelInspectionButton.IsEnabled = false;
        Feedback.Text = "Canceling inspection…";
        inspection.Cancel();
    }
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (busy && e.Key == System.Windows.Input.Key.Escape) { RequestCancellation(); e.Handled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; RequestCancellation(); }
    }
}
