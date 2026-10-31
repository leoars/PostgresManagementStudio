using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class RelationEditDialog : Window
{
    private readonly string connection;
    private bool ready, busy;
    private readonly DialogFocus focus = new();
    private CancellationTokenSource? inspection;
    private readonly ObservableCollection<InspectedIndex> indexes = [];
    public string? ScriptSql { get; private set; }
    public bool WasApplied { get; private set; }
    public RelationEditDialog(string connection, string? schema = null, string? table = null)
    {
        this.connection = connection; InitializeComponent();
        Loaded += (_, _) => Operation.Focus();
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Index Operations\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}";
        SortOrder.ItemsSource = Enum.GetValues<IndexSortOrder>(); SortOrder.SelectedItem = IndexSortOrder.Default;
        NullPlacement.ItemsSource = Enum.GetValues<IndexNullPlacement>(); NullPlacement.SelectedItem = IndexNullPlacement.Default;
        Method.ItemsSource = Enum.GetValues<IndexMethod>(); Method.SelectedItem = IndexMethod.BTree;
        ExistingIndexes.ItemsSource = indexes;
        Operation.ItemsSource = new[]
        {
            new { Value = RelationOperation.CreateIndex, Label = "Create Index" },
            new { Value = RelationOperation.DropIndex, Label = "Drop Index" }
        };
        Operation.DisplayMemberPath = "Label";
        Operation.SelectedValuePath = "Value";
        Operation.SelectedValue = RelationOperation.CreateIndex;
        SchemaName.Text = schema ?? "public"; TableName.Text = table ?? ""; ready = true; Update();
    }
    private RelationEdit Request => new((RelationOperation)Operation.SelectedValue, SchemaName.Text, TableName.Text, ObjectName.Text, RelationEdit.ParseColumns(Columns.Text), Unique.IsChecked == true) { ExpectedIdentity = SelectedIdentity, IncludedColumns = RelationEdit.ParseColumns(IncludedColumns.Text), NullsNotDistinct = NullsNotDistinct.IsChecked == true, SortOrder = (IndexSortOrder)SortOrder.SelectedItem, NullPlacement = (IndexNullPlacement)NullPlacement.SelectedItem, Method = (IndexMethod)Method.SelectedItem, FillFactor = Operation.SelectedValue is RelationOperation.DropIndex ? null : ReadFillFactor() };
    private int? ReadFillFactor()
    {
        if (string.IsNullOrWhiteSpace(FillFactor.Text)) return null;
        if (int.TryParse(FillFactor.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)) return value;
        throw new ArgumentException("Fillfactor must be an integer from 10 to 100, or blank for the server default.");
    }
    private IndexIdentity? SelectedIdentity => indexes.FirstOrDefault(i => i.Name == ObjectName.Text) is { } row ? new(row.TableOid, row.Index.Oid) : null;
    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        if (sender == SchemaName || sender == TableName) { indexes.Clear(); IndexDefinition.Clear(); }
        if (sender == Unique && Unique.IsChecked != true) NullsNotDistinct.IsChecked = false;
        Update();
    }
    private void Update()
    {
        bool drop = Operation.SelectedValue is RelationOperation.DropIndex;
        Columns.IsEnabled = Unique.IsEnabled = !drop;
        NullsNotDistinct.IsEnabled = !drop && Unique.IsChecked == true;
        CreateFields.Visibility = drop ? Visibility.Collapsed : Visibility.Visible;
        try { Preview.Text = Request.ToSql(); Feedback.Text = drop ? "Drop uses RESTRICT. Matching inspected names are checked for identity before commit; Script and unmatched manual names have no identity guard. Inspect again after conflicts." : "Standard creation can block writes; timeout 30s. " + (Method.SelectedItem is IndexMethod.Hash ? "Hash supports equality searches on one key; no UNIQUE or INCLUDE." : "Key priority follows the lines above; direction/NULL position apply to all keys. INCLUDE does not affect uniqueness. Unique indexes keep NULLs distinct unless Treat NULLs as equal is selected."); ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy; }
        catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = ex.Message; ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; }
        UpdateActions();
    }
    private bool CanInspect() { try { ObjectSql.Relation(SchemaName.Text, TableName.Text); return true; } catch (ArgumentException) { return false; } }
    private void UpdateActions()
    {
        ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy && Preview.Text.Length > 0;
        InspectButton.IsEnabled = !busy && CanInspect(); ExistingIndexes.IsEnabled = !busy && indexes.Count > 0;
    }
    private void SetBusy(bool value) { if (value) focus.Capture(); busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; UpdateActions(); if (!value) focus.Restore(this, CancelInspectionButton); }
    private void IndexSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ready || busy) return;
        IndexDefinition.Text = (ExistingIndexes.SelectedItem as InspectedIndex)?.Index.Definition ?? "";
        if (Operation.SelectedValue is RelationOperation.DropIndex && ExistingIndexes.SelectedItem is InspectedIndex selected) ObjectName.Text = selected.Name;
    }
    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();
    public async Task InspectAsync()
    {
        if (busy || !CanInspect()) return;
        using var cancellation = new CancellationTokenSource(); inspection = cancellation; CancelInspectionButton.IsEnabled = true;
        SetBusy(true); CancelInspectionButton.Focus(); Feedback.Text = "Inspecting indexes...";
        try
        {
            var inspected = await new IndexInspectionService(connection).InspectAsync(SchemaName.Text, TableName.Text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            indexes.Clear(); IndexDefinition.Clear(); foreach (var index in inspected) indexes.Add(index);
            Feedback.Text = indexes.Count == 0 ? "No indexes found for this table." : $"Found {indexes.Count} index(es). Select one to review its definition; in Drop, selection copies its exact name.";
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or TimeoutException)
        { Feedback.Text = "Inspection canceled. Previous results retained; inspect again to refresh."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException)
        { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { inspection = null; CancelInspectionButton.IsEnabled = false; SetBusy(false); }
    }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy) return;
        var request = Request;
        if (request.Operation == RelationOperation.DropIndex && MessageBox.Show(this, Target.Text + "\n\n" + request.ToSql() + "\n\nDrop this index?", "Confirm index deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true); Feedback.Text = "Applying operation…";
        try { await new AdministrativeEditService(connection).ApplyAsync(request); WasApplied = true; busy = false; DialogResult = true; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private void CancelInspection(object sender, RoutedEventArgs e) => RequestInspectionCancellation();
    private void RequestInspectionCancellation()
    {
        if (inspection is null || inspection.IsCancellationRequested) return;
        CancelInspectionButton.IsEnabled = false; Feedback.Text = "Canceling read..."; inspection.Cancel();
    }
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (inspection is not null && e.Key == System.Windows.Input.Key.Escape)
        { RequestInspectionCancellation(); e.Handled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; RequestInspectionCancellation(); } }
}
