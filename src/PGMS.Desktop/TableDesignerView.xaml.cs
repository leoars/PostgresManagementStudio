using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Table Designer as a document tab, like Design in the SSMS 22.11 table menu (docs/MARCO-M1042.md). Nothing reaches the server until Save, which shows the script first.</summary>
public partial class TableDesignerView : UserControl
{
    internal static readonly string[] BaseTypes = ["bigint", "boolean", "bytea", "character varying(50)", "character(10)", "date", "double precision", "integer", "interval", "json", "jsonb", "numeric(18,2)", "real", "smallint", "text", "time without time zone", "timestamp with time zone", "timestamp without time zone", "uuid"];
    private static readonly string[] YesNo = ["Yes", "No"];

    private readonly string connection;
    private readonly NpgsqlConnectionStringBuilder target;
    private readonly List<DesignerIndexRow> indexes = [];
    private readonly List<DesignerForeignKeyRow> foreignKeys = [];
    private readonly List<DesignerCheckRow> checks = [];
    private TableDesign? original;
    private string baseline = "";
    private string schema, tableName, tableDescription = "";
    private DesignerColumnRow? shownColumn, lastRow, menuRow;
    private CancellationTokenSource? operation;
    private bool loadedOnce, updating, existing;

    public ObservableCollection<DesignerColumnRow> Columns { get; } = [];
    public ObservableCollection<string> TypeChoices { get; } = new(BaseTypes);
    public bool IsNew => !existing;
    public bool IsBusy => operation is not null;
    public bool IsReadOnly => original?.ReadOnlyReason is not null;
    public bool IsDirty { get; private set; }
    public TableDesign? Original => original;
    public string Schema => schema;
    public string? Database => target.Database;
    public string Title => $"{schema}.{tableName}{(IsDirty ? "*" : "")}";
    public IReadOnlyList<DesignerIndexRow> Indexes => indexes;
    public IReadOnlyList<DesignerForeignKeyRow> ForeignKeys => foreignKeys;
    public IReadOnlyList<DesignerCheckRow> Checks => checks;
    public string FeedbackText => Feedback.Text;

    public event Action? TitleChanged;
    public event Action? Saved;
    internal Func<string, string?>? NamePrompt { get; set; }
    internal Func<TableDesignChange, bool>? ReviewPrompt { get; set; }

    public TableDesignerView(string connection, string schema, string? table)
    {
        InitializeComponent();
        this.connection = connection; target = new(connection); this.schema = schema; tableName = table ?? "Table_1"; existing = table is not null;
        ColumnGrid.ItemsSource = Columns;
        Columns.CollectionChanged += ColumnsChanged;
        ColumnProperties.ValueChanged += Touch; TableProperties.ValueChanged += Touch;
        AutomationProperties.SetName(this, "Table Designer");
        // With Alt held WPF reports the key in SystemKey.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.System && e.SystemKey == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt) { ShowTableProperties(); e.Handled = true; } };
        ToolTip = $"{target.Host}:{target.Port} {target.Database}";
        ShowColumn(null); ShowTable();
        if (!existing) Fill(null);
    }

    /// <summary>Same server, port, user, database and table; new tables never match.</summary>
    public bool Targets(string other, string otherSchema, string? table)
    {
        var candidate = new NpgsqlConnectionStringBuilder(other);
        return table is not null && !IsNew && table == tableName && otherSchema == schema && string.Equals(candidate.Host, target.Host, StringComparison.OrdinalIgnoreCase) && candidate.Port == target.Port && candidate.Username == target.Username && candidate.Database == target.Database;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loadedOnce) return;
        loadedOnce = true; ColumnGrid.Focus();
        await LoadAsync();
    }

    /// <summary>Reads the table again and discards pending changes; the owner asks first when there are any.</summary>
    public async Task LoadAsync()
    {
        if (IsBusy) return;
        using var cancellation = operation = new CancellationTokenSource();
        try
        {
            Say("Reading table…", cancellable: true);
            await LoadTypesAsync(cancellation.Token);
            // A new table was set up by the constructor: what the user typed while the types loaded stays.
            if (existing) Fill(await new TableDesignService(connection).InspectAsync(schema, tableName, cancellation.Token));
            Say("");
        }
        catch (OperationCanceledException) { Say("Reading canceled."); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException) { Say("The table could not be read: " + Message(ex)); }
        finally { operation = null; RefreshState(); }
    }

    private async Task LoadTypesAsync(CancellationToken token)
    {
        var inspected = await new DataTypeCatalog(connection).InspectAsync(token);
        foreach (var type in inspected.Where(t => t.Schema != "pg_catalog" && t.Kind is "e" or "d").OrderBy(t => t.Schema).ThenBy(t => t.Name))
        {
            string name = ObjectSql.Relation(type.Schema, type.Name);
            if (!TypeChoices.Contains(name)) TypeChoices.Add(name);
        }
    }

    private void Fill(TableDesign? design)
    {
        updating = true;
        original = design;
        foreach (var row in Columns) Detach(row);
        Columns.Clear(); indexes.Clear(); foreignKeys.Clear(); checks.Clear();
        if (design is not null)
        {
            foreach (var column in design.Columns) Columns.Add(new(column));
            indexes.AddRange(design.Indexes.Select(i => new DesignerIndexRow(i)));
            foreignKeys.AddRange(design.ForeignKeys.Select(f => new DesignerForeignKeyRow(f)));
            checks.AddRange(design.Checks.Select(c => new DesignerCheckRow(c)));
            schema = design.Schema; tableName = design.Name; tableDescription = design.Comment ?? "";
        }
        SyncPrimaryKey();
        updating = false;
        bool readOnly = IsReadOnly;
        ColumnGrid.IsReadOnly = readOnly; ColumnGrid.CanUserAddRows = !readOnly; ColumnProperties.IsReadOnly = TableProperties.IsReadOnly = readOnly;
        ReadOnlyBanner.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed; ReadOnlyText.Text = design?.ReadOnlyReason ?? "";
        baseline = Fingerprint();
        ShowColumn(Columns.FirstOrDefault()); ShowTable();
        Touch();
    }

    private void ColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DesignerColumnRow row in e.NewItems ?? Array.Empty<DesignerColumnRow>()) { row.PropertyChanged += RowChanged; row.Renamed += RowRenamed; }
        foreach (DesignerColumnRow row in e.OldItems ?? Array.Empty<DesignerColumnRow>()) Detach(row);
        if (!updating) Touch();
    }
    private void Detach(DesignerColumnRow row) { row.PropertyChanged -= RowChanged; row.Renamed -= RowRenamed; }
    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesignerColumnRow.IsCurrent)) return;
        if (!updating) { ColumnProperties.RefreshValues(); Touch(); }
    }
    // A key or index stays attached to a renamed column, as it does on the server.
    private void RowRenamed(DesignerColumnRow row, string before, string after)
    {
        if (before.Length == 0) return;
        foreach (var index in indexes) index.Columns = index.Columns.Select(c => c.Name == before ? c with { Name = after } : c).ToList();
        foreach (var key in foreignKeys)
        {
            key.Columns = key.Columns.Select(c => c == before ? after : c).ToList();
            if (key.ReferencedSchema == schema && key.ReferencedTable == tableName) key.ReferencedColumns = key.ReferencedColumns.Select(c => c == before ? after : c).ToList();
        }
    }

    private TableDesign Current()
    {
        var columns = Columns.Where(c => c.Position is not null || !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.ToDesign()).ToArray();
        var design = original ?? new TableDesign(0, schema, tableName, columns);
        return design with
        {
            Schema = schema, Name = tableName, Columns = columns, Comment = DesignerColumnRow.Optional(tableDescription), Indexes = indexes.Select(i => i.ToDesign()).ToArray(),
            ForeignKeys = foreignKeys.Select(f => f.ToDesign()).ToArray(), Checks = checks.Select(c => c.ToDesign()).ToArray()
        };
    }
    private string Fingerprint() => Current().Fingerprint;

    private void Touch()
    {
        bool dirty = Fingerprint() != baseline;
        IsDirty = dirty; TitleChanged?.Invoke(); CommandManager.InvalidateRequerySuggested();
    }
    private void RefreshState() { CommandManager.InvalidateRequerySuggested(); TitleChanged?.Invoke(); }

    private void Say(string text, bool cancellable = false)
    {
        Feedback.Text = text;
        FeedbackBar.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CancelSaveButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string Message(Exception ex) => ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
    private void CancelOperation(object sender, RoutedEventArgs e) => operation?.Cancel();
    /// <summary>Stops a read or save in progress when the tab closes.</summary>
    public void Shutdown() => operation?.Cancel();

    // ---- Grid ----
    // DataGrid clears its current cell when focus leaves the grid; the column stays current while the user works in the property panes.
    private DesignerColumnRow? CurrentRow => ColumnGrid.CurrentItem switch
    {
        DesignerColumnRow row => row,
        null when lastRow is not null && Columns.Contains(lastRow) => lastRow,
        _ => null
    };
    private IReadOnlyList<DesignerColumnRow> SelectedRows()
    {
        var rows = ColumnGrid.SelectedCells.Select(c => c.Item).OfType<DesignerColumnRow>().Distinct().ToList();
        if (rows.Count == 0 && CurrentRow is { } row) rows.Add(row);
        return rows;
    }
    private void CurrentCellChanged(object? sender, EventArgs e)
    {
        if (ColumnGrid.CurrentItem is DesignerColumnRow current) lastRow = current;
        foreach (var row in Columns) row.IsCurrent = ReferenceEquals(row, CurrentRow);
        // Moving between cells of one row keeps the panel, and with it the keyboard focus inside the panel.
        if (!ReferenceEquals(shownColumn, CurrentRow)) ShowColumn(CurrentRow);
    }
    private void RowRightClick(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is DataGridRow { Item: DesignerColumnRow row } gridRow)
            {
                if (!gridRow.IsSelected) { ColumnGrid.CurrentCell = new(row, ColumnGrid.Columns[0]); menuRow = row; }
                return;
            }
    }
    private void GridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ColumnGrid.SelectedCells.Count > 0 && ColumnGrid.SelectedCells.Count == SelectedRows().Count * ColumnGrid.Columns.Count) { DeleteColumn(); e.Handled = true; }
    }
    private void TypeEditorLoaded(object sender, RoutedEventArgs e) => ((ComboBox)sender).Focus();
    private void Commit() { ColumnGrid.CommitEdit(DataGridEditingUnit.Cell, true); ColumnGrid.CommitEdit(DataGridEditingUnit.Row, true); }

    // ---- Commands shared by the row menu and the Table Designer menu ----
    public string PrimaryKeyHeader => SelectedRows().Any() && SelectedRows().All(r => r.IsPrimaryKey) ? "Remove Primary Key" : "Set Primary Key";
    public bool CanEdit => !IsReadOnly && !IsBusy;
    private void RowMenuOpened(object sender, RoutedEventArgs e)
    {
        // The right button selects only the cell after the preview event; the row turns blue here, as in SSMS.
        if (menuRow is { } row) { ColumnGrid.UnselectAllCells(); ColumnGrid.SelectedItem = row; menuRow = null; }
        PrimaryKeyItem.Header = PrimaryKeyHeader; PrimaryKeyItem.IsEnabled = InsertItem.IsEnabled = DeleteItem.IsEnabled = CanEdit; ScriptItem.IsEnabled = IsDirty;
    }
    public void TogglePrimaryKey(object? sender = null, RoutedEventArgs? e = null)
    {
        if (!CanEdit) return;
        Commit();
        var rows = SelectedRows().Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToList();
        if (rows.Count == 0) return;
        var key = indexes.FirstOrDefault(i => i.Kind == DesignIndexKind.PrimaryKey);
        if (rows.All(r => r.IsPrimaryKey)) { if (key is not null) indexes.Remove(key); }
        else
        {
            if (key is null) indexes.Insert(0, key = new DesignerIndexRow { Name = UniqueName(tableName + "_pkey"), Kind = DesignIndexKind.PrimaryKey, Unique = true });
            key.Columns = Columns.Where(rows.Contains).Select(r => new DesignIndexColumn(r.Name)).ToList();
        }
        SyncPrimaryKey(); ShowColumn(CurrentRow); Touch();
    }
    private void SyncPrimaryKey()
    {
        var names = indexes.FirstOrDefault(i => i.Kind == DesignIndexKind.PrimaryKey)?.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var row in Columns) row.IsPrimaryKey = names.Contains(row.Name);
    }
    public void InsertColumn()
    {
        if (!CanEdit) return;
        Commit();
        int at = CurrentRow is { } row ? Columns.IndexOf(row) : Columns.Count;
        var added = new DesignerColumnRow();
        Columns.Insert(at, added);
        ColumnGrid.CurrentCell = new(added, ColumnGrid.Columns[0]);
        ColumnGrid.BeginEdit();
        Say("PostgreSQL adds new columns at the end of the table; the row order here does not move existing columns.");
    }
    public void DeleteColumn()
    {
        if (!CanEdit) return;
        Commit();
        foreach (var row in SelectedRows().ToArray())
        {
            Columns.Remove(row);
            // PostgreSQL drops the keys and indexes of a dropped column with it; the script says so explicitly.
            indexes.RemoveAll(i => i.Columns.Any(c => c.Name == row.Name));
            foreignKeys.RemoveAll(f => f.Columns.Contains(row.Name));
        }
        SyncPrimaryKey(); ShowColumn(CurrentRow); Touch();
    }
    private void InsertColumnClick(object sender, RoutedEventArgs e) => InsertColumn();
    private void DeleteColumnClick(object sender, RoutedEventArgs e) => DeleteColumn();
    private void RelationshipsClick(object sender, RoutedEventArgs e) => ShowRelationships();
    private void IndexesClick(object sender, RoutedEventArgs e) => ShowIndexes();
    private void ChecksClick(object sender, RoutedEventArgs e) => ShowChecks();
    private void ScriptClick(object sender, RoutedEventArgs e) => ShowChangeScript();
    private void PropertiesClick(object sender, RoutedEventArgs e) => ShowTableProperties();
    public void ShowTableProperties() => PropertyTabs.SelectedItem = TableTab;

    // ---- Property panes ----
    private void ShowColumn(DesignerColumnRow? row)
    {
        PropertyTabs.SelectedItem ??= ColumnTab;
        shownColumn = row;
        if (row is null) { ColumnProperties.Show([]); return; }
        const string general = "(General)", designer = "Table Designer";
        ColumnProperties.Show([
            new(general, "(Name)", "Indicates the name of the column.", () => row.Name, v => row.Name = v),
            new(general, "Allow Nulls", "Indicates whether the column accepts NULL.", () => row.AllowNulls ? "Yes" : "No", v => row.AllowNulls = v == "Yes", YesNo) { IsEditable = () => row.CanAllowNulls },
            new(general, "Data Type", "Indicates the data type of the column.", () => row.DataType, v => row.DataType = v),
            new(general, "Default Value or Binding", "Indicates the SQL expression used when an insert gives no value.", () => row.DefaultValue, v => row.DefaultValue = v) { IsEditable = () => row.Identity == ColumnIdentity.None },
            new(general, "Length", "Indicates the maximum number of characters or bits.", () => DesignerTypeText.Length(row.DataType), v => row.DataType = DesignerTypeText.WithLength(row.DataType, v)) { IsVisible = () => DesignerTypeText.HasLength(row.DataType) },
            new(general, "Precision", "Indicates the total number of digits.", () => DesignerTypeText.Length(row.DataType), v => row.DataType = DesignerTypeText.WithLength(row.DataType, v)) { IsVisible = () => DesignerTypeText.HasPrecision(row.DataType) },
            new(general, "Scale", "Indicates the number of digits after the decimal point.", () => DesignerTypeText.Scale(row.DataType), v => row.DataType = DesignerTypeText.WithScale(row.DataType, v)) { IsVisible = () => DesignerTypeText.HasPrecision(row.DataType) },
            new(designer, "Collation", "Indicates the collation; empty uses the default of the type.", () => row.Collation, v => row.Collation = v),
            new(designer, "Computed Column Specification", "Indicates the expression of a stored generated column. Can only be set on a new column; clearing it keeps the stored values.", () => row.Generated, v => row.Generated = v) { IsEditable = () => row.CanSetGenerated || row.Generated.Length > 0 },
            new(designer, "Condensed Data Type", "Shows the data type in the form PostgreSQL reports.", () => row.DataType),
            new(designer, "Description", "Indicates the comment stored on the column.", () => row.Description, v => row.Description = v),
            new(designer, "Identity Specification", "Indicates whether PostgreSQL generates the value. Requires smallint, integer or bigint.", () => IdentityLabel(row.Identity), v => row.Identity = IdentityValue(v), ["No", "Always", "By Default"]),
        ]);
    }
    private static string IdentityLabel(ColumnIdentity identity) => identity switch { ColumnIdentity.Always => "Always", ColumnIdentity.ByDefault => "By Default", _ => "No" };
    private static ColumnIdentity IdentityValue(string text) => text switch { "Always" => ColumnIdentity.Always, "By Default" => ColumnIdentity.ByDefault, _ => ColumnIdentity.None };

    private void ShowTable()
    {
        const string identity = "(Identity)", designer = "Table Designer";
        TableProperties.Show([
            new(identity, "(Name)", "Indicates the name of the table.", () => tableName, v => RenameTable(v.Trim())),
            new(identity, "Schema", "Indicates the schema of the table. Can only be chosen for a new table.", () => schema, v => schema = v.Trim()) { IsEditable = () => IsNew },
            new(identity, "Database Name", "Shows the database of the table.", () => target.Database ?? ""),
            new(identity, "Description", "Indicates the comment stored on the table.", () => tableDescription, v => tableDescription = v),
            new(designer, "Owner", "Shows the role that owns the table.", () => original?.Owner ?? ""),
            new(designer, "Estimated Rows", "Shows the row estimate kept by the server.", () => original is null ? "" : Math.Max(0, original.EstimatedRows).ToString("N0", System.Globalization.CultureInfo.CurrentCulture)),
        ]);
    }

    // ---- Dialogs ----
    private Window? OwnerWindow => Window.GetWindow(this);
    private string UniqueName(string name) => DesignerObjectKinds.UniqueName(name, indexes, foreignKeys, checks);
    /// <summary>On a new table the generated key, index and check names and self-references follow the name, so "Table_1" does not outlive the Choose Name prompt.</summary>
    private void RenameTable(string name)
    {
        string from = tableName; tableName = name;
        if (!IsNew || from.Length == 0 || name.Length == 0) return;
        string Follow(string current) => current.StartsWith(from + "_pkey", StringComparison.Ordinal) || new[] { "fk_", "ix_", "ck_" }.Any(p => current.StartsWith(p + from, StringComparison.Ordinal)) ? current.Replace(from, name, StringComparison.Ordinal) : current;
        foreach (var index in indexes) index.Name = Follow(index.Name);
        foreach (var check in checks) check.Name = Follow(check.Name);
        foreach (var key in foreignKeys)
        {
            key.Name = Follow(key.Name);
            if (key.ReferencedSchema == schema && key.ReferencedTable == from) key.ReferencedTable = name;
        }
    }
    internal DesignerObjectKind RelationshipsKind() => DesignerObjectKinds.Relationships(foreignKeys, schema, () => UniqueName("fk_" + tableName), Touch);
    internal DesignerObjectKind IndexesKind() => DesignerObjectKinds.Indexes(indexes, () => UniqueName("ix_" + tableName), () => (CurrentRow ?? Columns.FirstOrDefault())?.Name, Touch, () => { SyncPrimaryKey(); ShowColumn(CurrentRow); });
    internal DesignerObjectKind ChecksKind() => DesignerObjectKinds.Checks(checks, () => UniqueName("ck_" + tableName), Touch);
    private void ShowObjects(DesignerObjectKind kind)
    {
        Commit();
        new DesignerObjectsDialog(kind, !CanEdit) { Owner = OwnerWindow }.ShowDialog();
        Touch();
    }
    public void ShowRelationships() => ShowObjects(RelationshipsKind());
    public void ShowIndexes() => ShowObjects(IndexesKind());
    public void ShowChecks() => ShowObjects(ChecksKind());

    // ---- Script and save ----
    /// <summary>The script of the pending changes, or null with the reason in the feedback line.</summary>
    public TableDesignChange? BuildChange()
    {
        Commit();
        try { var change = TableDesignScript.Build(original, Current()); Say(""); return change; }
        catch (ArgumentException ex) { Say(ex.Message); return null; }
    }
    public void ShowChangeScript()
    {
        if (BuildChange() is not { } change) return;
        new ChangeScriptDialog(change, false) { Owner = OwnerWindow }.ShowDialog();
    }

    /// <summary>Asks for a name on a new table, shows the script and applies it. False leaves the designer as it was.</summary>
    public async Task<bool> SaveAsync()
    {
        if (!CanEdit) return false;
        Commit();
        string unnamed = tableName;
        if (IsNew)
        {
            string? name = (NamePrompt ?? PromptName)(tableName);
            if (name is null) return false;
            RenameTable(name.Trim());
        }
        bool Undone() { RenameTable(unnamed); Touch(); return false; }
        if (BuildChange() is not { } change) return Undone();
        if (change.Statements.Count == 0) { Touch(); return true; }
        if (!(ReviewPrompt ?? Review)(change)) return Undone();

        var before = original; var designed = Current();
        using var cancellation = operation = new CancellationTokenSource();
        Say("Saving…", cancellable: true); RefreshState();
        try
        {
            await new AdministrativeEditService(connection).ApplyAsync(new TableDesignEdit(before, designed), cancellation.Token);
            var saved = await new TableDesignService(connection).InspectAsync(designed.Schema, designed.Name, CancellationToken.None);
            existing = true; Fill(saved);
            Say("Table saved.");
            Saved?.Invoke();
            return true;
        }
        catch (OperationCanceledException) { Say("Save canceled. The transaction was rolled back."); return IsNew ? Undone() : false; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException) { Say("The table was not saved: " + Message(ex)); return IsNew ? Undone() : false; }
        finally { operation = null; RefreshState(); }
    }
    private bool Review(TableDesignChange change) => new ChangeScriptDialog(change, true) { Owner = OwnerWindow }.ShowDialog() == true;

    private string? PromptName(string suggested)
    {
        var name = new TextBox { Text = suggested, Margin = new Thickness(0, 6, 0, 12) };
        AutomationProperties.SetName(name, "Table name");
        var ok = new Button { Content = "OK", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 76, IsCancel = true };
        var dialog = new Window
        {
            Title = "Choose Name", Width = 340, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Owner = OwnerWindow,
            Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Enter a name for the table:" }, name, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } } }
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        return dialog.ShowDialog() == true ? name.Text : null;
    }
}
