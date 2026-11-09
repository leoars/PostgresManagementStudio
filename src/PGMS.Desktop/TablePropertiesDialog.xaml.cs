using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public sealed record TableEditSelection(TableProperties Table, string? Column);

/// <summary>Where the column edits of Table Properties go when it is opened from a database diagram: Read gives the table as designed in the
/// diagram, Apply takes the edited columns (pedido do usuário, 08/11/2026). The diagram keeps them until its own Save.</summary>
public sealed record TableColumnEditTarget(Func<TableDesign> Read, Action<IReadOnlyList<DesignColumn>> Apply);

/// <summary>A column on the Columns tab of Table Properties: name, type, nulls and identity can be changed, as in the Table Designer.</summary>
public sealed class TablePropertiesColumnRow : INotifyPropertyChanged
{
    /// <summary>The Identity Specification choices of the Table Designer.</summary>
    public static IReadOnlyList<string> IdentityChoices { get; } = ["No", "Always", "By Default"];
    private string name, type;
    private bool nullable;
    private ColumnIdentity identity;
    public event PropertyChangedEventHandler? PropertyChanged;
    public DesignColumn Design { get; }
    public short? Position => Design.Position;
    public string OriginalName => Design.Name;
    public bool IsKey { get; }
    public bool Editable { get; }
    public string Identity
    {
        get => identity switch { ColumnIdentity.Always => "Always", ColumnIdentity.ByDefault => "By Default", _ => "No" };
        set
        {
            var chosen = value switch { "Always" => ColumnIdentity.Always, "By Default" => ColumnIdentity.ByDefault, _ => ColumnIdentity.None };
            if (chosen == identity) return;
            identity = chosen;
            // An identity column never accepts NULL, as the Table Designer does.
            if (chosen != ColumnIdentity.None) nullable = false;
            PropertyChanged?.Invoke(this, new(nameof(Identity))); PropertyChanged?.Invoke(this, new(nameof(Nullable))); PropertyChanged?.Invoke(this, new(nameof(CanAllowNulls)));
        }
    }
    public ColumnIdentity IdentityKind => identity;
    public string Generated => Design.Generated is null ? "" : "STORED";
    public string? DefaultExpression => Design.Generated ?? Design.Default;
    public string? Comment => Design.Comment;
    /// <summary>Primary key and identity columns never accept NULL, as in the Table Designer.</summary>
    public bool CanAllowNulls => Editable && !IsKey && identity == ColumnIdentity.None;
    /// <summary>A generated column cannot be an identity column.</summary>
    public bool CanSetIdentity => Editable && Design.Generated is null;

    public TablePropertiesColumnRow(DesignColumn design, bool isKey, bool editable)
    {
        Design = design; IsKey = isKey; Editable = editable;
        name = design.Name; type = design.Type; nullable = design.Nullable; identity = design.Identity;
    }
    public string Name { get => name; set => Set(ref name, value.Trim()); }
    public string Type { get => type; set => Set(ref type, value.Trim()); }
    public bool Nullable { get => nullable; set => Set(ref nullable, value); }
    /// <summary>PostgreSQL refuses a default on an identity column, so turning identity on drops the default (the dialog says so).</summary>
    public DesignColumn ToDesign() => Design with { Name = name, Type = type, Nullable = nullable, Identity = identity, Default = identity == ColumnIdentity.None ? Design.Default : null };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new(property));
    }
}

public partial class TablePropertiesDialog : Window
{
    private readonly string connection;
    private bool ready, busy, refreshing;
    private CancellationTokenSource? inspection;
    /// <summary>The table the column edits start from: from the diagram, or read from the server for Apply. Null when the columns cannot be edited.</summary>
    private TableDesign? design, serverDesign;
    private string? readOnlyReason;
    public TableEditSelection? EditSelection { get; private set; }
    public TableProperties? Snapshot { get; private set; }
    public ObservableCollection<TablePropertiesColumnRow> ColumnRows { get; } = [];
    public ObservableCollection<string> TypeChoices { get; } = new(TableDesignerView.BaseTypes);
    /// <summary>Set by a diagram: the columns are the diagram's and edits go to it instead of Apply.</summary>
    public TableColumnEditTarget? ColumnTarget { get; init; }
    internal Func<TableDesignChange, bool>? ReviewPrompt { get; set; }
    internal Func<string, bool>? ConfirmPrompt { get; set; }
    /// <summary>The columns edited and not applied yet (Object Explorer only).</summary>
    public bool HasPendingColumns => ColumnTarget is null && design is not null && serverDesign is not null && design.Fingerprint != serverDesign.Fingerprint;

    public TablePropertiesDialog(string connection, string? schema = null, string? table = null)
    {
        this.connection = connection; InitializeComponent(); WindowWorkArea.Attach(this);
        var target = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Table Properties\nServer: {target.Host}:{target.Port}   Database: {target.Database}   User: {target.Username}";
        SchemaName.Text = schema ?? "public"; TableName.Text = table ?? "";
        ColumnsGrid.ItemsSource = ColumnRows;
        ready = true; ClearSnapshot();
        Loaded += async (_, _) =>
        {
            if (ColumnTarget is not null)
            {
                // The table of the diagram: the target cannot change, and the operations that work on the server are left out.
                SchemaName.IsReadOnly = TableName.IsReadOnly = true;
                TableOperationsButton.Visibility = ColumnOperationsButton.Visibility = ApplyButton.Visibility = Visibility.Collapsed;
                ShowColumns(ColumnTarget.Read(), editable: ColumnTarget.Read().ReadOnlyReason is null, ColumnTarget.Read().ReadOnlyReason);
            }
            if (InspectOnOpen && InspectButton.IsEnabled && Snapshot is null) await InspectAsync();
        };
    }
    /// <summary>Set by Object Explorer ▸ Properties: read the table as soon as the window opens, as SSMS shows properties already loaded.</summary>
    public bool InspectOnOpen { get; init; }
    private void Changed(object sender, RoutedEventArgs e) { if (ready) ClearSnapshot(); }
    private void ClearSnapshot()
    {
        TableOperationsButton.IsEnabled = ColumnOperationsButton.IsEnabled = false;
        TriggersGrid.ItemsSource = null; TriggerDetail.Clear(); TriggerNotice.Text = "Inspect to read triggers.";
        IncomingGrid.ItemsSource = null; IncomingDetail.Clear(); IncomingNotice.Text = "Inspect to read incoming foreign keys.";
        ConstraintsGrid.ItemsSource = null; ConstraintDetail.Clear(); ConstraintNotice.Text = "Inspect to read constraints.";
        IndexesGrid.ItemsSource = null; IndexDetail.Clear(); IndexNotice.Text = "Inspect to read indexes.";
        Snapshot = null; Summary.Clear(); ColumnDetail.Clear();
        if (ColumnTarget is null) { design = serverDesign = null; ShowColumns(null, false, null); }
        DependentsGrid.ItemsSource = null; DependencyDetail.Clear(); DependencyNotice.Text = "Inspect to read direct dependency entries.";
        Feedback.Text = "Inspect to read current catalog metadata. No table rows are read.";
        try { ObjectSql.Relation(SchemaName.Text, TableName.Text); InspectButton.IsEnabled = !busy; }
        catch (ArgumentException ex) { InspectButton.IsEnabled = false; Feedback.Text = ex.Message; }
    }

    // ---- Columns ----
    /// <summary>Fills the Columns tab from a design; editable only for an ordinary table the designer can change.</summary>
    private void ShowColumns(TableDesign? shown, bool editable, string? reason)
    {
        refreshing = true;
        int selected = ColumnsGrid.SelectedIndex;
        foreach (var row in ColumnRows) row.PropertyChanged -= ColumnEdited;
        ColumnRows.Clear();
        design = shown; readOnlyReason = editable ? null : reason;
        if (shown is not null)
        {
            var key = shown.Indexes.Where(i => i.Kind == DesignIndexKind.PrimaryKey).SelectMany(i => i.Columns).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var column in shown.Columns)
            {
                var row = new TablePropertiesColumnRow(column, key.Contains(column.Name), editable);
                row.PropertyChanged += ColumnEdited; ColumnRows.Add(row);
                if (!TypeChoices.Contains(column.Type)) TypeChoices.Add(column.Type);
            }
        }
        ColumnsGrid.IsReadOnly = !editable;
        if (selected >= 0 && selected < ColumnRows.Count) ColumnsGrid.SelectedIndex = selected;
        ColumnNotice.Text = shown is null ? "Inspect to read columns."
            : !editable ? "Columns are read-only: " + (reason ?? "only ordinary tables can be changed here.")
            : ColumnTarget is not null ? "Name, Type, Nullable and Identity can be changed. The diagram shows the change now and Save writes it to the database, after the script."
            : "Name, Type, Nullable and Identity can be changed. Apply shows the script and runs it in one transaction.";
        refreshing = false;
        UpdateApply();
    }

    /// <summary>An edit is checked as the Table Designer checks it; one that PostgreSQL would refuse goes back, with the reason.</summary>
    private void ColumnEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (refreshing || design is null) return;
        // After the grid finishes its own commit.
        Dispatcher.BeginInvoke(CommitColumns, System.Windows.Threading.DispatcherPriority.Background);
    }
    internal void CommitColumns()
    {
        if (design is not { } before) return;
        var columns = ColumnRows.Select(r => r.ToDesign()).ToArray();
        if (columns.SequenceEqual(before.Columns)) return;
        TableDesign edited;
        try
        {
            edited = TableDesignColumns.WithColumns(before, columns);
            TableDesignScript.Build(before, edited);
        }
        catch (ArgumentException ex)
        {
            // The grid may still be editing the row: the edit is dropped before the rows are made again from the last accepted design.
            ColumnsGrid.CancelEdit(DataGridEditingUnit.Cell); ColumnsGrid.CancelEdit(DataGridEditingUnit.Row);
            ShowColumns(before, true, null); Feedback.Text = ex.Message; return;
        }
        var lostDefaults = before.Columns.Where(b => b.Default is not null && columns.FirstOrDefault(c => c.Position == b.Position) is { Default: null, Identity: not ColumnIdentity.None }).Select(b => b.Name).ToArray();
        string note = lostDefaults.Length == 0 ? "" : $" Identity replaces the default of {string.Join(", ", lostDefaults)}.";
        // The rows already show the accepted values and stay as they are, so a row the grid is still editing is not replaced under it.
        if (ColumnTarget is { } target)
        {
            target.Apply(columns);
            design = target.Read(); UpdateApply();
            Feedback.Text = "The diagram was updated. Save the diagram to write the change to the database." + note;
        }
        else
        {
            design = edited; UpdateApply();
            Feedback.Text = (HasPendingColumns ? "Column changes are not applied yet. Apply shows the script." : "No column changes to apply.") + note;
        }
    }

    private void UpdateApply() => ApplyButton.IsEnabled = !busy && HasPendingColumns;

    private async void ApplyColumnsClick(object sender, RoutedEventArgs e) => await ApplyColumnsAsync();
    /// <summary>Object Explorer: the script of the column changes, then one transaction that refuses a table changed since it was read.</summary>
    public async Task<bool> ApplyColumnsAsync()
    {
        if (busy || !HasPendingColumns || serverDesign is not { } before || design is not { } after) return false;
        TableDesignChange change;
        try { change = TableDesignScript.Build(before, after); }
        catch (ArgumentException ex) { Feedback.Text = ex.Message; return false; }
        if (!(ReviewPrompt ?? (c => new ChangeScriptDialog(c, true) { Owner = this }.ShowDialog() == true))(change)) return false;
        busy = true; Fields.IsEnabled = CloseButton.IsEnabled = false; UpdateApply();
        Feedback.Text = "Applying column changes…";
        try
        {
            await new AdministrativeEditService(connection).ApplyAsync(new TableDesignEdit(before, after));
            // What was written is now the table as read, so reading it again has nothing pending to ask about.
            serverDesign = after; busy = false;
            await InspectAsync();
            Feedback.Text = "Column changes applied.";
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            Feedback.Text = "The columns were not changed: " + (ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message);
            return false;
        }
        finally { busy = false; Fields.IsEnabled = CloseButton.IsEnabled = true; UpdateOperationButtons(); UpdateApply(); }
    }

    // ---- Inspection ----
    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();
    public async Task InspectAsync()
    {
        if (busy) return;
        if (HasPendingColumns && !Confirm("Inspect again discards the column changes that were not applied. Continue?")) return;
        ClearSnapshot(); busy = true; Fields.IsEnabled = CloseButton.IsEnabled = false;
        using var cancellation = new CancellationTokenSource();
        inspection = cancellation; CancelInspectionButton.IsEnabled = true;
        Feedback.Text = "Reading properties… Cancel Inspection stops this read.";
        try
        {
            var info = await new TablePropertiesService(connection).InspectAsync(SchemaName.Text, TableName.Text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            TableDesign? read = null;
            if (ColumnTarget is null)
            {
                read = info.Kind == "Ordinary table" ? await new TableDesignService(connection).InspectAsync(info.Schema, info.Name, cancellation.Token) : null;
                cancellation.Token.ThrowIfCancellationRequested();
            }
            TriggersGrid.ItemsSource = info.Triggers; TriggerNotice.Text = $"{info.Triggers.Count} triggers, including internal constraint triggers. Firing mode depends on session_replication_role. Function bodies and effects are not inspected.";
            IncomingGrid.ItemsSource = info.IncomingForeignKeys; IncomingNotice.Text = $"{info.IncomingForeignKeys.Count} foreign keys referencing this table, including self-references. Direct catalog entries only; not a complete DROP impact report.";
            ConstraintsGrid.ItemsSource = info.Constraints; ConstraintNotice.Text = $"{info.Constraints.Count} constraints owned by this table. References to this table are on Incoming FKs. Column NOT NULL is shown on Columns.";
            IndexesGrid.ItemsSource = info.Indexes; IndexNotice.Text = $"{info.Indexes.Count} indexes. Select an index to read its full definition. Valid and Ready are catalog flags, not a performance assessment.";
            Snapshot = info;
            if (ColumnTarget is { } target) { var shown = target.Read(); ShowColumns(shown, shown.ReadOnlyReason is null, shown.ReadOnlyReason); }
            else
            {
                serverDesign = read;
                // Tables the designer cannot change, and other relation kinds, show the catalog columns read-only.
                ShowColumns(read ?? new TableDesign(info.Oid, info.Schema, info.Name, info.Columns.Select(c => new DesignColumn(c.Position, c.Name, c.Type, c.Nullable) { Default = c.Generated.Length > 0 ? null : c.DefaultExpression, Generated = c.Generated.Length > 0 ? c.DefaultExpression : null, Comment = c.Comment }).ToArray()),
                    read is { ReadOnlyReason: null }, read?.ReadOnlyReason ?? $"{info.Kind} columns are shown as read.");
            }
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
        finally { inspection = null; CancelInspectionButton.IsEnabled = false; busy = false; Fields.IsEnabled = CloseButton.IsEnabled = true; UpdateOperationButtons(); UpdateApply(); }
    }
    private void ColumnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ready) UpdateOperationButtons();
        if (ColumnsGrid.SelectedItem is TablePropertiesColumnRow column)
            ColumnDetail.Text = $"Column: {column.Name}\n{(column.Generated.Length > 0 ? "Generation expression" : "Default expression")}: {column.DefaultExpression ?? "(none)"}\nComment: {column.Comment ?? "(none)"}";
    }
    private void TypeEditorLoaded(object sender, RoutedEventArgs e) => ((ComboBox)sender).Focus();
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
        ColumnOperationsButton.IsEnabled = TableOperationsButton.IsEnabled && ColumnsGrid.SelectedItem is TablePropertiesColumnRow;
    }
    private bool LeavePending() => !HasPendingColumns || Confirm("The column changes were not applied. Discard them?");
    private void OpenTableOperations(object sender, RoutedEventArgs e)
    {
        if (busy || Snapshot?.Kind != "Ordinary table" || !LeavePending()) return;
        EditSelection = new(Snapshot, null); DialogResult = true;
    }
    private void OpenColumnOperations(object sender, RoutedEventArgs e)
    {
        if (busy || Snapshot?.Kind != "Ordinary table" || ColumnsGrid.SelectedItem is not TablePropertiesColumnRow column || !LeavePending()) return;
        // The operations work on the table as it is in the database: a column renamed here and not applied is found by its old name.
        EditSelection = new(Snapshot, column.OriginalName); DialogResult = true;
    }
    private bool Confirm(string text) => ConfirmPrompt?.Invoke(text) ?? MessageBox.Show(this, text, "PGMS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
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
        if (busy) { e.Cancel = true; RequestCancellation(); return; }
        if (DialogResult != true && !LeavePending()) e.Cancel = true;
    }
}
