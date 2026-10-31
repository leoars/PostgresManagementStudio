using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class TableEditDialog : Window
{
    private readonly string connection;
    private bool ready, busy;
    private ColumnIdentity previousIdentity;
    private bool nullableBeforeIdentity=true;
    private readonly DialogFocus focus = new();
    private CancellationTokenSource? inspection;
    public TableProperties? InspectedTable { get; private set; }
    public string? ScriptSql { get; private set; }
    public bool WasApplied { get; private set; }
    public TableEditDialog(string connection, string? schema = null, string? table = null, TableProperties? inspectedTable = null, string? column = null)
    {
        this.connection = connection; InitializeComponent();
        Loaded += (_, _) => Operation.Focus();
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Table Operations\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}";
        Operation.ItemsSource = Enum.GetValues<TableOperation>(); Operation.SelectedIndex = 0;
        ColumnTypeSelector.ItemsSource = Enum.GetValues<ColumnType>(); ColumnTypeSelector.SelectedItem = ColumnType.Text;
        IdentitySelector.ItemsSource=ColumnIdentityChoices.All;IdentitySelector.DisplayMemberPath="Label";IdentitySelector.SelectedIndex=0;
        SchemaName.Text = inspectedTable?.Schema ?? schema ?? "public";
        TableName.Text = inspectedTable?.Name ?? table ?? "";
        InspectedTable = inspectedTable;
        if (column is not null) { ColumnName.Text = column; Operation.SelectedItem = TableOperation.RenameColumn; }
        ready = true; Update();
        if (column is not null) ColumnPicker.SelectedItem = InspectedTable?.Columns.SingleOrDefault(c => c.Name == column);
    }
    private TableEdit Request
    {
        get
        {
            var operation = (TableOperation)Operation.SelectedItem;
            bool existingColumn = operation is TableOperation.RenameColumn or TableOperation.DropColumn or TableOperation.SetNotNull or TableOperation.DropNotNull or TableOperation.SetDefault or TableOperation.DropDefault or TableOperation.CommentColumn;
            var column = existingColumn ? InspectedTable?.Columns.SingleOrDefault(c => c.Name == ColumnName.Text) : null;
            if (existingColumn && InspectedTable is not null && column is null)
                throw new ArgumentException("Choose a column from the inspected table, or inspect again to refresh.");
            return new(SchemaName.Text, TableName.Text, operation, NewName.Text, ColumnName.Text, (ColumnType)ColumnTypeSelector.SelectedItem, AllowNull.IsChecked == true, DefaultValue.Text, CommentText.Text, RemoveComment.IsChecked == true)
            { Identity=operation==TableOperation.AddColumn?IdentityMode:ColumnIdentity.None,
                ExpectedTableOid = InspectedTable?.Oid, ExpectedColumnPosition = column?.Position,
                ExpectedColumnType = column?.TypeIdentity,
                ExpectedNullable = column is not null && operation is TableOperation.SetNotNull or TableOperation.DropNotNull ? column.Nullable : null,
                CheckDefault = column is not null && operation is TableOperation.SetDefault or TableOperation.DropDefault,
                ExpectedDefaultExpression = column?.DefaultExpression,
                CheckComment = InspectedTable is not null && operation is TableOperation.CommentTable or TableOperation.CommentColumn,
                ExpectedComment = operation == TableOperation.CommentTable ? InspectedTable?.Comment : column?.Comment };
        }
    }
    private void PickColumn(object sender, SelectionChangedEventArgs e)
    {
        if (ready && ColumnPicker.SelectedItem is TableColumnInfo column) ColumnName.Text = column.Name;
    }
    private void Changed(object sender, RoutedEventArgs e) { if (ready) Update(); }
    private ColumnIdentity IdentityMode=>IdentitySelector.SelectedItem is ColumnIdentityChoice choice?choice.Value:ColumnIdentity.None;
    private void UpdateColumnIdentity()
    {
        var mode=IdentityMode;var prior=previousIdentity;previousIdentity=mode;
        if(mode!=ColumnIdentity.None && prior==ColumnIdentity.None){nullableBeforeIdentity=AllowNull.IsChecked==true;AllowNull.IsChecked=false;}
        if(mode==ColumnIdentity.None && prior!=ColumnIdentity.None)AllowNull.IsChecked=nullableBeforeIdentity;
        AllowNull.IsEnabled=mode==ColumnIdentity.None;
        IdentityHelp.Text=mode==ColumnIdentity.None?"No automatic value generation."
            :"Identity fills existing rows from an implicit sequence and requires NOT NULL. It does not create a primary key.";
    }
    private void Update()
    {
        UpdateColumnIdentity();
        if (InspectedTable is { } inspected && (inspected.Schema != SchemaName.Text || inspected.Name != TableName.Text)) InspectedTable = null;
        SnapshotStatus.Text = InspectedTable is { } snapshot ? $"Inspected OID {snapshot.Oid}. Apply checks table identity; Script does not." : "Not inspected. Apply resolves the typed table name at execution.";
        try { ObjectSql.Relation(SchemaName.Text, TableName.Text); InspectTargetButton.IsEnabled = !busy; }
        catch (ArgumentException) { InspectTargetButton.IsEnabled = false; }
        bool comment = Operation.SelectedItem is TableOperation.CommentTable or TableOperation.CommentColumn;
        CommentFields.Visibility = comment ? Visibility.Visible : Visibility.Collapsed;
        LoadCommentButton.IsEnabled = !busy && InspectedTable is not null && (Operation.SelectedItem is TableOperation.CommentTable || InspectedTable.Columns.Any(c => c.Name == ColumnName.Text));
        CommentText.IsEnabled = RemoveComment.IsChecked != true;
        bool move = Operation.SelectedItem is TableOperation.SetSchema;
        DestinationSchemasFields.Visibility = move ? Visibility.Visible : Visibility.Collapsed;
        if (DestinationSchemaPicker.SelectedItem is string selectedSchema && selectedSchema != NewName.Text) DestinationSchemaPicker.SelectedItem = null;
        NewNameLabel.Content = move ? "Destination schema:" : "New name:";
        bool drop = Operation.SelectedItem is TableOperation.Drop;
        bool add = Operation.SelectedItem is TableOperation.AddColumn;
        bool renameColumn = Operation.SelectedItem is TableOperation.RenameColumn;
        bool dropColumn = Operation.SelectedItem is TableOperation.DropColumn;
        bool nullability = Operation.SelectedItem is TableOperation.SetNotNull or TableOperation.DropNotNull;
        bool setDefault = Operation.SelectedItem is TableOperation.SetDefault;
        bool dropDefault = Operation.SelectedItem is TableOperation.DropDefault;
        var inspectedColumn = InspectedTable?.Columns.SingleOrDefault(c => c.Name == ColumnName.Text);
        IdentityOperationsButton.Visibility=!add && inspectedColumn?.TypeIdentity?.Oid is 20 or 21 or 23?Visibility.Visible:Visibility.Collapsed;
        IdentityOperationsButton.IsEnabled=!busy && IdentityOperationsButton.Visibility==Visibility.Visible;
        TypeOperationsButton.Visibility=!add && inspectedColumn is not null?Visibility.Visible:Visibility.Collapsed;
        TypeOperationsButton.IsEnabled=!busy && TypeOperationsButton.Visibility==Visibility.Visible;
        AddCatalogColumnButton.IsEnabled=!busy && InspectedTable is not null && IdentityMode==ColumnIdentity.None;
        InspectedColumnType.Visibility = inspectedColumn is not null && !add ? Visibility.Visible : Visibility.Collapsed;
        InspectedColumnType.Text = $"Inspected type: {inspectedColumn?.Type}. Apply checks type identity; Script does not.";
        InspectedColumnStateFields.Visibility = (setDefault || dropDefault || nullability) && inspectedColumn is not null ? Visibility.Visible : Visibility.Collapsed;
        InspectedColumnStateLabel.Content = nullability ? "Inspected nullability:" : inspectedColumn?.Generated.Length > 0 ? "Inspected generation expression:" : "Inspected default expression:";
        InspectedColumnState.Text = nullability ? (inspectedColumn?.Nullable == true ? "NULL allowed" : "NOT NULL") : inspectedColumn?.DefaultExpression ?? "(none)";
        DefaultFields.Visibility = setDefault ? Visibility.Visible : Visibility.Collapsed;
        NewNameFields.Visibility = comment || drop || add || dropColumn || nullability || setDefault || dropDefault ? Visibility.Collapsed : Visibility.Visible;
        ColumnFields.Visibility = Operation.SelectedItem is TableOperation.CommentColumn || add || renameColumn || dropColumn || nullability || setDefault || dropDefault ? Visibility.Visible : Visibility.Collapsed;
        ColumnPicker.ItemsSource = InspectedTable?.Columns;
        ColumnPicker.Visibility = InspectedTable is not null && !add ? Visibility.Visible : Visibility.Collapsed;
        if (ColumnPicker.SelectedItem is TableColumnInfo picked && picked.Name != ColumnName.Text) ColumnPicker.SelectedItem = null;
        TypeFields.Visibility = add ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            Preview.Text = Request.ToSql();
            Feedback.Text = (comment ? "Replaces the catalog comment. Inspect Target then Load Inspected Comment to edit existing text. Inspected comments are checked for concurrent changes on Apply. Remove clears it; rows are unchanged. " : move ? "Move to an existing schema in this database, including associated indexes, constraints and owned sequences. SQL using the old schema must be updated. CREATE permission on the destination is required. " : dropDefault ? "Remove the default for future writes. Existing rows and NOT NULL constraints are unchanged. " : setDefault ? "Literal value only, without SQL quotes or expressions. Empty means empty text; NULL means the text NULL. PostgreSQL converts it to the column type. Existing rows are unchanged. " : Operation.SelectedItem is TableOperation.DropNotNull ? "Allow NULL values. Primary keys, identity and other constraints may prevent or still restrict NULLs. " : nullability ? "Reject NULL values. Existing NULLs prevent this change; a table scan may be required. " : dropColumn ? "Drop permanently removes this column's values and local indexes/constraints using it. RESTRICT refuses external dependencies. " : renameColumn ? "Rename preserves column values. SQL using the old column name must be updated. " : add ? IdentityMode==ColumnIdentity.None?"Add one typed column without a default. Existing rows receive NULL; NOT NULL requires an empty table. ":"Add an identity column; PostgreSQL generates values for existing rows. No key is added automatically. " : drop ? "Drop permanently removes all rows, indexes, triggers and constraints of this table. RESTRICT refuses external dependencies. " : "Rename preserves data. SQL using the old name must be updated. ") + "Apply supports ordinary tables only; it takes an exclusive lock (30s timeout). Script does not include the type guard.";
            if (nullability && inspectedColumn is not null) Feedback.Text = "Apply checks the inspected nullability for concurrent changes. " + Feedback.Text;
            if ((setDefault || dropDefault) && inspectedColumn is not null) Feedback.Text = "Apply checks the inspected default for concurrent changes. " + Feedback.Text;
            ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy;
        }
        catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = ex.Message; ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; }
    }
    private void SetBusy(bool value)
    {
        if (value) focus.Capture();
        busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value;
        ScriptButton.IsEnabled = ApplyButton.IsEnabled = !value && Preview.Text.Length > 0;
        if (!value)
        {
            // Refresh local enabled states before restoring focus, retaining operation feedback.
            var feedback = Feedback.Text; Update(); Feedback.Text = feedback;
            focus.Restore(this, CancelInspectionButton);
        }
    }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy) return;
        var request = Request;
        if (request.Operation == TableOperation.Drop && MessageBox.Show(this, Target.Text + "\n\n" + request.ToSql() + "\n\nPermanently delete this table and all its rows, indexes, triggers and constraints? External dependencies prevent deletion.", "Drop Table", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (request.Operation == TableOperation.DropColumn && MessageBox.Show(this, Target.Text + "\n\n" + request.ToSql() + "\n\nPermanently delete this column's values and local indexes/constraints using it? External dependencies prevent deletion.", "Drop Column", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true); Feedback.Text = "Applying…";
        try { await new AdministrativeEditService(connection).ApplyAsync(request); WasApplied = true; busy = false; DialogResult = true; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private void LoadComment(object sender, RoutedEventArgs e)
    {
        if (busy || InspectedTable is null) return;
        string? current;
        if (Operation.SelectedItem is TableOperation.CommentTable) current = InspectedTable.Comment;
        else if (Operation.SelectedItem is TableOperation.CommentColumn && InspectedTable.Columns.SingleOrDefault(c => c.Name == ColumnName.Text) is { } column) current = column.Comment;
        else return;
        CommentText.Text = current ?? "";
        RemoveComment.IsChecked = false;
        Update();
        CommentText.Focus(); CommentText.SelectAll();
    }
    private async void InspectTarget(object sender, RoutedEventArgs e) => await InspectTargetAsync();
    private async void OpenIdentityOperations(object sender,RoutedEventArgs e)=>await OpenIdentityOperationsAsync();
    private async void OpenTypeOperations(object sender,RoutedEventArgs e)=>await OpenTypeOperationsAsync();
    private async void AddCatalogColumn(object sender,RoutedEventArgs e)=>await OpenCatalogAdditionAsync();
    public async Task OpenCatalogAdditionAsync()
    {
        if(busy || !AddCatalogColumnButton.IsEnabled || InspectedTable is null)return;
        var dialog=new ColumnTypeDialog(connection,InspectedTable,null,ColumnName.Text){Owner=this,ShowInTaskbar=false};dialog.ShowDialog();
        WasApplied|=dialog.WasApplied;
        if(dialog.ScriptSql is {} sql){ScriptSql=sql;DialogResult=true;return;}
        if(dialog.RequiresParentRefresh)await InspectTargetAsync();
    }
    public async Task OpenTypeOperationsAsync()
    {
        if(busy || !TypeOperationsButton.IsEnabled || InspectedTable?.Columns.SingleOrDefault(c=>c.Name==ColumnName.Text) is not {} column)return;
        var dialog=new ColumnTypeDialog(connection,InspectedTable,column){Owner=this,ShowInTaskbar=false};dialog.ShowDialog();
        WasApplied|=dialog.WasApplied;
        if(dialog.ScriptSql is {} sql){ScriptSql=sql;DialogResult=true;return;}
        if(dialog.RequiresParentRefresh)await InspectTargetAsync();
    }
    public async Task OpenIdentityOperationsAsync()
    {
        if(busy || !IdentityOperationsButton.IsEnabled || InspectedTable?.Columns.SingleOrDefault(c=>c.Name==ColumnName.Text) is not { } column)return;
        var dialog=new IdentityColumnDialog(connection,InspectedTable,column){Owner=this,ShowInTaskbar=false};dialog.ShowDialog();
        WasApplied|=dialog.WasApplied;
        if(dialog.ScriptSql is{ } sql){ScriptSql=sql;DialogResult=true;return;}
        if(dialog.RequiresParentRefresh)await InspectTargetAsync();
    }
    public async Task InspectTargetAsync()
    {
        if (busy) return;
        SetBusy(true); Feedback.Text = "Inspecting target…";
        using var cancellation = new CancellationTokenSource();
        inspection = cancellation; CancelInspectionButton.IsEnabled = true; CancelInspectionButton.Focus();
        try
        {
            var snapshot = await new TablePropertiesService(connection).InspectAsync(SchemaName.Text, TableName.Text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            InspectedTable = snapshot;
            Update();
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or TimeoutException)
        { Feedback.Text = "Inspection canceled. Previous snapshot, if any, is preserved. Inspect Target to retry."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException)
        { Update(); Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { inspection = null; CancelInspectionButton.IsEnabled = false; SetBusy(false); }
    }
    private void PickDestinationSchema(object sender, SelectionChangedEventArgs e)
    {
        if (ready && DestinationSchemaPicker.SelectedItem is string schema) NewName.Text = schema;
    }
    private async void LoadSchemas(object sender, RoutedEventArgs e) => await LoadDestinationSchemasAsync();
    public async Task LoadDestinationSchemasAsync()
    {
        if (busy || Operation.SelectedItem is not TableOperation.SetSchema) return;
        SetBusy(true); DestinationSchemaPicker.ItemsSource = null;
        SchemaLoadStatus.Text = "Loading schemas…"; Feedback.Text = "Reading available schemas…";
        using var cancellation = new CancellationTokenSource();
        inspection = cancellation; CancelInspectionButton.Content = "Cancel Schema Load (_P)"; CancelInspectionButton.IsEnabled = true; CancelInspectionButton.Focus();
        try
        {
            var catalog = new CatalogService(connection);
            var schemas = await catalog.ChildrenAsync(new(CatalogKind.Schemas, "Schemas", catalog.DefaultDatabase), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            DestinationSchemaPicker.ItemsSource = schemas.Select(s => s.Name).ToArray();
            SchemaLoadStatus.Text = $"{schemas.Count} non-system schemas. Select a destination or enter an exact name. Permissions are checked on Apply.";
            Feedback.Text = "Schema list loaded. Choose a different destination schema.";
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or TimeoutException)
        { SchemaLoadStatus.Text = "Schema loading canceled. Load Schemas to retry."; Feedback.Text = SchemaLoadStatus.Text; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException)
        { SchemaLoadStatus.Text = "Schema list unavailable. Retry Load Schemas or enter an exact name."; Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally
        {
            inspection = null; CancelInspectionButton.IsEnabled = false; CancelInspectionButton.Content = "Cancel Ins_pection";
            SetBusy(false);
        }
    }
    private void CancelInspection(object sender, RoutedEventArgs e) => RequestInspectionCancellation();
    private void RequestInspectionCancellation()
    {
        if (inspection is null) return;
        CancelInspectionButton.IsEnabled = false; Feedback.Text = "Canceling read…"; inspection.Cancel();
    }
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (inspection is not null && e.Key == System.Windows.Input.Key.Escape) { RequestInspectionCancellation(); e.Handled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; RequestInspectionCancellation(); }
    }
}
