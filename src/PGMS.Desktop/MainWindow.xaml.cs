using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using Microsoft.Win32;
using PGMS.PostgreSql;
using PGMS.Desktop.ViewModels;

namespace PGMS.Desktop;

public partial class MainWindow : Window
{
    private readonly Dictionary<QueryView, LayoutDocument> documents = [];
    private readonly Dictionary<ActivityMonitorView, LayoutDocument> monitors = [];
    private int sequence;
    private string? explorerConnection;
    private CatalogService? catalogService;
    private CancellationTokenSource catalogCancellation = new();
    private bool refreshing;
    private bool shuttingDown;
    private bool closingWindow;
    private Task closeTask = Task.CompletedTask;
    public Task CloseCompletion => closeTask;
    private readonly string layoutFile = Path.Combine(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGMS"), "layout.xml");
    private QueryView? ActiveQuery => DockManager.ActiveContent as QueryView;
    private ActivityMonitorView? ActiveMonitor => DockManager.ActiveContent as ActivityMonitorView;

    public MainWindow()
    {
        InitializeComponent();
        WindowWorkArea.Attach(this);
        // Keep the caption and status reachable on scaled or small work areas.
        var workArea = SystemParameters.WorkArea;
        if (!workArea.IsEmpty && workArea.Width > 0 && workArea.Height > 0)
        {
            double availableWidth = Math.Max(1, workArea.Width - 16);
            double availableHeight = Math.Max(1, workArea.Height - 16);
            MinWidth = Math.Min(MinWidth, availableWidth);
            MinHeight = Math.Min(MinHeight, availableHeight);
            Width = Math.Min(Width, availableWidth);
            Height = Math.Min(Height, availableHeight);
        }
        DockManager.ActiveContentChanged += (_, _) => documentSelectionRevision++;
        Closed += (_, _) => { recoveryClosed = true; recoveryTimer.Stop(); DiscardSpareQuery(); recoveryIo.Run(() => recoverySession?.Dispose()); };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.New, (_, _) => NewQuery()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, async (_, e) =>
        {
            e.Handled = true;
            if (ActiveQuery is { } query) await CloseQueryAsync(query);
            else if (ActiveMonitor is { } monitor && monitors.TryGetValue(monitor, out var tab)) tab.Close();
        }, (_, e) =>
        {
            e.CanExecute = (ActiveQuery is { } query ? !closingQueries.Contains(query) && !pendingTabCloses.ContainsKey(query) : ActiveMonitor is not null)
                && !closeRequested && !closingWindow && !shuttingDown;
            e.Handled = true;
        }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Close, Key.F4, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, Open, (_, e) => e.CanExecute = !closeRequested && !closingWindow && !shuttingDown));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Replace, (_, _) => ActiveQuery?.Replace(), (_, e) => e.CanExecute = ActiveQuery is not null));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Replace, Key.H, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(QueryView.GoToLineCommand,(_,_)=>ActiveQuery?.GoToLine(),(_,e)=>e.CanExecute=ActiveQuery is not null));
        InputBindings.Add(new KeyBinding(QueryView.GoToLineCommand,Key.G,ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.Comment,(_,_)=>ActiveQuery?.CommentSelection(false),(_,e)=>e.CanExecute=ActiveQuery is not null && !closingWindow && !closeRequested));
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.Uncomment,(_,_)=>ActiveQuery?.CommentSelection(true),(_,e)=>e.CanExecute=ActiveQuery is not null && !closingWindow && !closeRequested));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, async (_, _) => { if (ActiveQuery is { } query) await SaveAsync(query); }, (_, e) => e.CanExecute = ActiveQuery is { } active && !IsSaving(active) && !closingWindow && !closingQueries.Contains(active)));
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                if (ExplorerContent.IsKeyboardFocusWithin) await RefreshSelectedExplorerAsync();
                else if (ActiveQuery is { } query) await query.ExecuteAsync();
                else if (ActiveMonitor is { } monitor) await monitor.ReloadAsync();
            }
            if (e.Key == Key.Escape && ExplorerContent.IsKeyboardFocusWithin && (ExplorerTree.SelectedItem as CatalogNode)?.LoadTarget is { IsBusy: true } loading)
            {
                loading.CancelLoad(); e.Handled = true;
            }
            if (e.Key == Key.F8) { ShowExplorer(this, e); e.Handled = true; }
            // SSMS shortcut for Activity Monitor; with Alt held WPF reports the letter in SystemKey.
            if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) { ShowActivity(this, e); e.Handled = true; }
        };
    }

    internal Action<string, double>? UiPhaseMeasured { get; set; }

    private QueryView NewQuery(string? text = null, string? path = null, Guid? recoveryId = null, bool activate = true, PreparedQueryDocument? prepared = null)
    {
        QueryView view;
        using (UiPhaseScope.Start(UiPhaseMeasured, "view-total"))
        {
            view = TakeSpareOrCreate(recoveryId);
            view.FilePath = path;
        }
        using (UiPhaseScope.Start(UiPhaseMeasured, "document-attach"))
        {
            if (prepared is null) view.Editor.Text = text ?? "";
            else view.Editor.Document = prepared.TakeOwnership();
        }
        using var dockPhase = UiPhaseScope.Start(UiPhaseMeasured, "dock-and-commands");
        view.IsDirty = false;
        var document = new LayoutDocument { Title = path is null ? $"SQLQuery{++sequence}.sql" : Path.GetFileName(path), ContentId = Guid.NewGuid().ToString(), Content = view };
        documents.Add(view, document);
        view.StateChanged += (_, _) =>
        {
            document.Title = document.Title.TrimEnd('*') + (view.IsDirty ? "*" : "");
            RefreshCommands();
        };
        document.Closing += (_, e) =>
        {
            if (acceptedTabCloses.Remove(view)) return;
            e.Cancel = true;
            _ = CloseQueryAsync(view);
        };
        document.Closed += async (_, _) => { documents.Remove(view); await view.DisposeAsync(); RefreshCommands(); };
        AddDocument(document);
        if (activate) document.IsActive = true;
        // The editor is not yet in the visual tree when the document is inserted.
        // Defer focus until layout has created it, so Ctrl+N accepts typing immediately.
        if (activate) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (ActiveQuery != view || !documents.ContainsKey(view) || closingWindow || closingQueries.Contains(view)) return;
            view.Editor.Focus();
            Keyboard.Focus(view.Editor.TextArea);
        });
        RefreshCommands();
        return view;
    }

    private void AddDocument(LayoutDocument document)
    {
        var pane = DockManager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (pane is null)
        {
            pane = new LayoutDocumentPane();
            DockManager.Layout.RootPanel.Children.Add(pane);
        }
        pane.Children.Add(document);
    }

    private async void Open(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            if (closeRequested || shuttingDown) break;
            try { await OpenQueryFileAsync(file); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { ShowError(ex); }
        }
    }

    public bool TryOpenQueryFile(string path, out Exception? error)
    {
        try { OpenQueryFile(path); error = null; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { error = ex; return false; }
    }

    public QueryView OpenQueryFile(string path)
    {
        Dispatcher.VerifyAccess();
        if (closeRequested || closingWindow || recoveryClosed || shuttingDown) throw new IOException("The window is closing.");
        path = Path.GetFullPath(path);
        if (pendingOpens.ContainsKey(path)) throw new IOException("This file is being opened. Wait before using it.");
        if (pendingSavePaths.Contains(path)) throw new IOException("This file is being saved. Wait before opening it.");
        var existing = documents.Keys.FirstOrDefault(q => string.Equals(q.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (closingQueries.Contains(existing)) throw new IOException("The query is closing. Wait before reopening it.");
            documents[existing].IsActive = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (ActiveQuery != existing || !documents.ContainsKey(existing) || closingWindow || closingQueries.Contains(existing)) return;
                existing.Editor.Focus(); Keyboard.Focus(existing.Editor.TextArea);
            });
            Status.Text = "File already open; existing document retained";
            return existing;
        }
        var file = SqlFileStore.Read(path);
        var query = NewQuery(file.Text, file.Path);
        query.FileFormat = file.Format;
        query.FileVersion = file.Version;
        return query;
    }

    public bool TrySaveQueryFile(QueryView query, string path, out Exception? error, bool overwrite = false)
    {
        try { SaveQueryFile(query, path, overwrite); error = null; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { error = ex; return false; }
    }

    public void SaveQueryFile(QueryView query, string path, bool overwrite = false)
    {
        if (!documents.ContainsKey(query)) throw new ArgumentException("The query does not belong to this window.", nameof(query));
        if (closingWindow || closingQueries.Contains(query) || recoveryClosed || shuttingDown) throw new IOException("The query is closing.");
        if (IsSaving(query)) throw new IOException("This query is already being saved.");
        path = Path.GetFullPath(path);
        if (pendingOpens.ContainsKey(path)) throw new IOException("This file is being opened. Wait before using it.");
        if (pendingSavePaths.Contains(path)) throw new IOException("This file is being saved by another query.");
        if (documents.Keys.Any(q => q != query && string.Equals(q.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("This file is already open in another query tab. Switch to that tab or use a different filename.");
        SqlFileVersion? expected = null;
        if (overwrite)
        {
            if (string.Equals(path, query.FilePath, StringComparison.OrdinalIgnoreCase)) expected = query.FileVersion;
            else if (File.Exists(path)) expected = SqlFileStore.CaptureVersion(path);
        }
        var saved = SqlFileStore.Write(path, query.Editor.Text, query.FileFormat, expected);
        query.FilePath = saved.Path;
        query.FileVersion = saved.Version;
        query.IsDirty = false;
        documents[query].Title = Path.GetFileName(saved.Path);
        Status.Text = "Item saved";
        ForgetRecovery(query);
    }

    private async void Connect(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var query = ActiveQuery;
        // Preserve any existing session and its transaction; connect in a new tab instead.
        if (query is null || query.HasSession || query.IsBusy) query = NewQuery();
        Status.Text = "Connecting…";
        try
        {
            await query.ConnectAsync(dialog.ConnectionString);
            await ConnectExplorerAsync(dialog.ConnectionString);
            Status.Text = "Connected";
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
        RefreshCommands();
    }

    public async Task ConnectExplorerAsync(string connection)
    {
        explorerConnection = connection;
        await LoadExplorerAsync();
    }

    private async Task LoadExplorerAsync()
    {
        if (explorerConnection is null || refreshing) return;
        refreshing = true;
        Status.Text = "Refreshing Object Explorer…";
        try
        {
            catalogCancellation.Cancel(); catalogCancellation.Dispose();
            catalogCancellation = new CancellationTokenSource();
            catalogService = new CatalogService(explorerConnection);
            var root = new CatalogNode(new(CatalogKind.Server, catalogService.DisplayName), catalogService);
            await root.LoadAsync(catalogCancellation.Token);
            ExplorerTree.Items.Clear();
            ExplorerTree.Items.Add(root);
            Status.Text = "Object Explorer refreshed. Expand a database to load its objects.";
            NewDatabaseMenu.IsEnabled = NewSchemaMenu.IsEnabled = NewRoleMenu.IsEnabled = BackupMenu.IsEnabled = RestoreMenu.IsEnabled = true;
            BackupGlobalsMenu.IsEnabled=RestoreGlobalsMenu.IsEnabled=true;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException) { ShowError(ex); }
        finally { refreshing = false; }
    }

    private async void ExplorerExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: CatalogNode node })
        {
            await node.LoadAsync(catalogCancellation.Token);
            if (node.Error is { } error) Status.Text = error;
        }
    }
    private void ExplorerSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var node = ExplorerTree.SelectedItem as CatalogNode;
        ExplorerFilterPanel.IsEnabled = node?.CanFilter == true;
        ExplorerFilter.Text = node?.Filter ?? "";

    }
    private async void ApplyExplorerFilter(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not CatalogNode { CanFilter: true } node) return;
        node.SetFilter(ExplorerFilter.Text);
        await node.LoadAsync(catalogCancellation.Token);
        Status.Text = node.Error ?? node.FilterSummary;
    }
    private void ClearExplorerFilter(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not CatalogNode { CanFilter: true } node) return;
        node.SetFilter(""); ExplorerFilter.Text = "";
    }
    private void ExplorerFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; ApplyExplorerFilter(sender, e); }
    }
    private void CancelExplorerLoad(object sender, RoutedEventArgs e)
    {
        if ((ExplorerTree.SelectedItem as CatalogNode)?.LoadTarget is { } node) node.CancelLoad();
    }
    private void ExplorerRightClick(object sender, MouseButtonEventArgs e)
    {
        // A nested node belongs to its parent TreeViewItem, not directly to ExplorerTree.
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(null, source) is TreeViewItem item)
            item.IsSelected = true;
    }
    private string? SelectedConnection => catalogService?.ConnectionFor((ExplorerTree.SelectedItem as CatalogNode)?.Entry.Database);
    private async void QueryFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) return;
        var query = NewQuery();
        try { await query.ConnectAsync(connection); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or OperationCanceledException) { ShowError(ex); }
    }
    private void NewDatabase(object sender, RoutedEventArgs e) => CreateObject(CreateObjectKind.Database);
    private void NewSchema(object sender, RoutedEventArgs e) => CreateObject(CreateObjectKind.Schema);
    private async void TableProperties(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before inspecting tables."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var properties = new TablePropertiesDialog(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null) { Owner = this };
        if (properties.ShowDialog() == true && properties.EditSelection is { } selection)
            await ShowTableOperationsAsync(connection, selection.Table.Schema, selection.Table.Name, selection.Table, selection.Column);
    }
    private void ObjectProperties(object sender, RoutedEventArgs e)
    {
        if (catalogService is null || ExplorerTree.SelectedItem is not CatalogNode { Entry.CanInspectObjectProperties: true } node) return;
        var connection = catalogService.ConnectionFor(node.Entry.Kind == CatalogKind.Database ? null : node.Entry.Database);
        new ObjectPropertiesDialog(connection, node.Entry) { Owner = this }.ShowDialog();
    }
    private async void TableOperationsFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || ExplorerTree.SelectedItem is not CatalogNode { Entry.CanEditTable: true } node) return;
        var entry = node.Entry;
        try
        {
            var snapshot = await new TablePropertiesService(connection).InspectAsync(entry.Schema!, entry.Name);
            if (snapshot.Oid != entry.Oid) { Status.Text = "Table changed since Explorer loaded. Refresh Object Explorer before editing."; return; }
            await ShowTableOperationsAsync(connection, snapshot.Schema, snapshot.Name, snapshot);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or ArgumentException or TimeoutException)
        { Status.Text = ex is Npgsql.PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
    }
    private async void NewTypeFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text="Connect before creating types.";return; }
        var dialog=new CreateTypeDialog(connection,(ExplorerTree.SelectedItem as CatalogNode)?.Entry.Schema){Owner=this};dialog.ShowDialog();
        if(dialog.ScriptSql is {} sql)await OpenAdministrativeScriptAsync(connection,sql);
        if(dialog.RequiresParentRefresh)await LoadExplorerAsync();
    }
    private async void DomainFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || ExplorerTree.SelectedItem is not CatalogNode { Entry.CanManageDomain: true } node) return;
        var dialog = new DomainDialog(connection,node.Entry.Schema!,node.Entry.Name,node.Entry.Oid) { Owner = this };
        dialog.ShowDialog();
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection,sql);
        if (dialog.RequiresParentRefresh) await LoadExplorerAsync();
    }
    private async void EnumFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || ExplorerTree.SelectedItem is not CatalogNode { Entry.CanManageEnum: true } node) return;
        var dialog = new EnumDialog(connection,node.Entry.Schema!,node.Entry.Name,node.Entry.Oid) { Owner = this };
        dialog.ShowDialog();
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection,sql);
        if (dialog.RequiresParentRefresh) await LoadExplorerAsync();
    }
    private async void PartitionsFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || ExplorerTree.SelectedItem is not CatalogNode { Entry.CanManagePartitions: true } node) return;
        var dialog = new PartitionDialog(connection,node.Entry.Schema!,node.Entry.Name,node.Entry.Oid) { Owner=this };
        dialog.ShowDialog();
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection,sql);
        if (dialog.RequiresParentRefresh) await LoadExplorerAsync();
    }
    private void PendingIndexFromExplorer(object sender,RoutedEventArgs e)
    {
        if(SelectedConnection is not { } connection){Status.Text="Connect to inspect a pending index operation.";return;}
        new ConcurrentIndexDialog(connection){Owner=this}.ShowDialog();
    }
    private async void ExpressionIndexFromExplorer(object sender, RoutedEventArgs e)
    {
        if(SelectedConnection is not {} connection || ExplorerTree.SelectedItem is not CatalogNode {Entry.CanEditTable:true} node)return;
        var dialog=new ExpressionIndexDialog(connection,node.Entry.Schema!,node.Entry.Name,node.Entry.Oid){Owner=this};dialog.ShowDialog();
        if(dialog.ScriptSql is {} sql)await OpenAdministrativeScriptAsync(connection,sql);
        if(dialog.RequiresParentRefresh)await LoadExplorerAsync();
    }
    private async void RangeExclusionFromExplorer(object sender, RoutedEventArgs e)
    {
        if(SelectedConnection is not {} connection || ExplorerTree.SelectedItem is not CatalogNode {Entry.CanEditTable:true} node)return;
        var dialog=new RangeExclusionDialog(connection,node.Entry.Schema!,node.Entry.Name,node.Entry.Oid){Owner=this};dialog.ShowDialog();
        if(dialog.ScriptSql is {} sql)await OpenAdministrativeScriptAsync(connection,sql);
        if(dialog.RequiresParentRefresh)await LoadExplorerAsync();
    }
    private async void TableOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing tables."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        await ShowTableOperationsAsync(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null);
    }
    private async Task ShowTableOperationsAsync(string connection, string? schema, string? table, PGMS.PostgreSql.TableProperties? snapshot = null, string? column = null)
    {
        var dialog = new TableEditDialog(connection, schema, table, snapshot, column) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) await LoadExplorerAsync();
    }
    private async void SequenceOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing sequences."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var dialog = new SequenceDialog(connection, entry?.Schema, entry?.CanInspectSequence == true ? entry.Name : null, entry?.CanInspectSequence == true ? entry.Oid : null) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Sequence operation completed. Object Explorer refreshed."; }
    }
    private async void IndexOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing indexes."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var dialog = new RelationEditDialog(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Index operation completed. Object Explorer refreshed."; }
    }
    private async void TableConstraintOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing table constraints."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var dialog = new TableConstraintDialog(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Constraint operation completed. Object Explorer refreshed."; }
    }
    private async void CheckConstraintOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing CHECK constraints."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var dialog = new CheckConstraintDialog(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "CHECK constraint operation completed. Object Explorer refreshed."; }
    }
    private async void ForeignKeyOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing foreign keys."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var dialog = new ForeignKeyDialog(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Foreign key operation completed. Object Explorer refreshed."; }
    }
    private async void DatabaseOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing databases."; return; }
        string? database = (ExplorerTree.SelectedItem as CatalogNode)?.Entry is { Kind: CatalogKind.Database } entry ? entry.Name : null;
        var dialog = new DatabaseEditDialog(connection, database) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql)
        {
            var maintenanceConnection = new Npgsql.NpgsqlConnectionStringBuilder(connection) { Database = "postgres" };
            await OpenAdministrativeScriptAsync(maintenanceConnection.ConnectionString, sql);
        }
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Database operation completed. Object Explorer refreshed."; }
    }
    private async void NewTable(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before creating tables."; return; }
        string? schema = (ExplorerTree.SelectedItem as CatalogNode)?.Entry.Schema;
        var dialog = new CreateTableDialog(connection, schema) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Table created. Object Explorer refreshed."; }
    }
    private async void SchemaOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing schemas."; return; }
        string? schema = (ExplorerTree.SelectedItem as CatalogNode)?.Entry is { Kind: CatalogKind.Schema } entry ? entry.Name : null;
        var dialog = new SchemaEditDialog(connection, schema) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Schema operation completed. Object Explorer refreshed."; }
    }
    private async void RoleProperties(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing roles."; return; }
        var dialog = new RoleAttributeDialog(connection) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
    }
    private async void RenameRole(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before renaming a role."; return; }
        var role = (ExplorerTree.SelectedItem as CatalogNode)?.Entry is { Kind: CatalogKind.Role } entry ? entry.Name : null;
        var dialog = new RoleRenameDialog(connection, role) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Role renamed. Object Explorer refreshed."; }
    }
    private async void DropRole(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before dropping a role."; return; }
        var role = (ExplorerTree.SelectedItem as CatalogNode)?.Entry is { Kind: CatalogKind.Role } entry ? entry.Name : null;
        var dialog = new RoleDropDialog(connection, role) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Role dropped. Object Explorer refreshed."; }
    }
    private async void SetRolePassword(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before changing a role password."; return; }
        var role = (ExplorerTree.SelectedItem as CatalogNode)?.Entry is { Kind: CatalogKind.Role } entry ? entry.Name : null;
        var dialog = new RolePasswordDialog(connection, role) { Owner = this };
        dialog.ShowDialog();
        if (dialog.WasApplied) Status.Text = "Role password changed.";
        await Task.CompletedTask;
    }
    private async Task OpenAdministrativeScriptAsync(string connection, string sql)
    {
        var query = NewQuery(sql); query.IsDirty = true; documents[query].Title += "*";
        try { await query.ConnectAsync(connection); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or OperationCanceledException) { ShowError(ex); }
    }
    private async void RoleMemberships(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing memberships."; return; }
        var dialog = new RoleMembershipDialog(connection) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
    }
    private void ShowActivity(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before opening Activity Monitor."; return; }
        OpenActivityMonitor(connection);
    }
    /// <summary>Activity Monitor as a document tab, like SSMS; a tab already monitoring the same server and user is activated instead.</summary>
    internal ActivityMonitorView OpenActivityMonitor(string connection)
    {
        if (monitors.FirstOrDefault(m => m.Key.Targets(connection)) is { Key: { } existing, Value: { } tab }) { tab.IsActive = true; return existing; }
        var view = new ActivityMonitorView(connection) { OpenQueryText = OpenQueryTextAsync, OpenQueryPlan = OpenEstimatedPlanAsync };
        var document = new LayoutDocument { Title = view.Title, ContentId = Guid.NewGuid().ToString(), Content = view };
        monitors.Add(view, document);
        document.Closing += (_, e) => { if (!view.RequestClose()) e.Cancel = true; };
        document.Closed += (_, _) => { monitors.Remove(view); view.Shutdown(); RefreshCommands(); };
        AddDocument(document);
        document.IsActive = true;
        RefreshCommands();
        return view;
    }
    internal IReadOnlyCollection<ActivityMonitorView> ActivityMonitors => monitors.Keys;
    private void ShowQueryStatistics(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before opening Query Statistics."; return; }
        new QueryStatisticsWindow(connection, OpenGenericPlanAsync) { Owner = this }.Show();
    }
    // Query Statistics → Show Plan: a new tab connected to the statement's database with its normalised SQL and generic plan.
    internal async Task<QueryView> OpenGenericPlanAsync(string connection, string sql)
    {
        var query = NewQuery(sql);
        try { await query.ConnectAsync(connection); await query.ExplainGenericPlanAsync(); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or OperationCanceledException) { ShowError(ex); }
        return query;
    }
    // Activity Monitor → Active Expensive Queries: the running statement in a new tab connected to its database, not executed.
    internal async Task<QueryView> OpenQueryTextAsync(string connection, string sql)
    {
        var query = NewQuery(sql);
        try { await query.ConnectAsync(connection); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or OperationCanceledException) { ShowError(ex); }
        return query;
    }
    // Estimated plan only (EXPLAIN without ANALYZE); text with $n placeholders switches to the generic plan.
    internal async Task<QueryView> OpenEstimatedPlanAsync(string connection, string sql)
    {
        var query = await OpenQueryTextAsync(connection, sql);
        if (query.IsConnected) await query.ExplainPlanAsync();
        return query;
    }
    private async void RoutinePermissionsFromExplorer(object sender, RoutedEventArgs e)
    {
        if (catalogService is null || ExplorerTree.SelectedItem is not CatalogNode { Entry.CanManageRoutinePermissions: true } node) return;
        string connection = catalogService.ConnectionFor(node.Entry.Database);
        var dialog = new PrivilegeDialog(connection, node.Entry) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
    }
    private async void DefaultPermissions(object sender, RoutedEventArgs e)
    {
        if(SelectedConnection is not { } connection){Status.Text="Connect before managing default permissions.";return;}
        var dialog=new DefaultPrivilegeDialog(connection){Owner=this};
        if(dialog.ShowDialog()==true && dialog.ScriptSql is { } sql)await OpenAdministrativeScriptAsync(connection,sql);
    }
    private async void ObjectPermissions(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing object permissions."; return; }
        var dialog = new PrivilegeDialog(connection) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ScriptSql is not { } sql) return;
        var query = NewQuery(sql); query.IsDirty = true; documents[query].Title += "*";
        try { await query.ConnectAsync(connection); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or OperationCanceledException) { ShowError(ex); }
    }
    private async void NewRole(object sender, RoutedEventArgs e)
    {
        if (explorerConnection is not { } connection) return;
        var dialog = new CreateRoleDialog(connection) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql)
        {
            var query = NewQuery(sql);
            query.IsDirty = true;
            documents[query].Title += "*";
            try { await query.ConnectAsync(connection); }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or OperationCanceledException) { ShowError(ex); }
        }
        if (dialog.WasCreated)
        {
            await LoadExplorerAsync();
            Status.Text = "Role and memberships created. Refresh completed.";
        }
    }
    private async void CreateObject(CreateObjectKind kind)
    {
        if (SelectedConnection is not { } connection) return;
        var dialog = new CreateObjectDialog(kind, connection) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql)
        {
            var query = NewQuery(sql);
            query.IsDirty = true;
            documents[query].Title += "*";
            try { await query.ConnectAsync(connection); }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or OperationCanceledException) { ShowError(ex); }
        }
        if (dialog.WasCreated)
        {
            await LoadExplorerAsync();
            Status.Text = kind == CreateObjectKind.Database ? "Database created. Refresh completed." : "Schema created. Refresh completed.";
        }
    }

    private void BackupDatabase(object sender, RoutedEventArgs e) => ShowBackup(false);
    private void RestoreDatabase(object sender, RoutedEventArgs e) => ShowBackup(true);
    private void BackupGlobals(object sender,RoutedEventArgs e)=>ShowBackup(false,true);
    private void RestoreGlobals(object sender,RoutedEventArgs e)=>ShowBackup(true,true);
    private async void ShowBackup(bool restore,bool globals=false)
    {
        if (SelectedConnection is not { } connection) return;
        var dialog = new BackupDialog(restore, connection,globals) { Owner = this };
        dialog.ShowDialog();
        if (dialog.WasCompleted && restore) await LoadExplorerAsync();
    }

    private void RefreshCommands()
    {
        if (ExecuteButton is null) return;
        var query = ActiveQuery;
        ExecuteButton.IsEnabled = ExecuteMenu.IsEnabled = query is { IsConnected: true, IsBusy: false };
        CancelButton.IsEnabled = CancelMenu.IsEnabled = query is { IsBusy: true, IsExporting: false };
        DatabaseName.Text = query?.Database ?? "No active query";
        DatabaseName.ToolTip = query is null
            ? "Activate a query tab to see its database and session status. Object Explorer uses a separate connection."
            : "Database of the active query session";
        CommandManager.InvalidateRequerySuggested();
    }
    private void ActiveChanged(object? sender, EventArgs e) => RefreshCommands();
    private async void Execute(object sender, RoutedEventArgs e) { if (ActiveQuery is { } query) await query.ExecuteAsync(); }
    private void CancelQuery(object sender, RoutedEventArgs e) => ActiveQuery?.Cancel();
    private async void RefreshExplorer(object sender, RoutedEventArgs e) => await RefreshSelectedExplorerAsync();
    public async Task RefreshSelectedExplorerAsync()
    {
        var selected = ExplorerTree.SelectedItem as CatalogNode;
        var target = selected?.LoadTarget;
        if (target is CatalogNode node)
        {
            if (node.IsBusy) { Status.Text = "Folder is loading. Use Cancel Load to interrupt."; return; }
            await node.LoadAsync(catalogCancellation.Token, refresh: true);
            Status.Text = node.Error ?? (node.WasCanceled ? "Folder load canceled. Refresh to retry." : "Selected folder refreshed");
        }
        else await LoadExplorerAsync();
    }
    private async void SaveAs(object sender, RoutedEventArgs e) { if (ActiveQuery is { } query) await SaveAsync(query, true); }
    private void ViewMenuOpened(object sender,RoutedEventArgs e){WordWrapMenu.IsEnabled=ActiveQuery is not null;WordWrapMenu.IsChecked=ActiveQuery?.WordWrap==true;WhiteSpaceMenu.IsEnabled=ActiveQuery is not null;WhiteSpaceMenu.IsChecked=ActiveQuery?.ShowWhiteSpace==true;}
    private void ToggleWordWrap(object sender,RoutedEventArgs e){if(ActiveQuery is { } query)query.WordWrap=WordWrapMenu.IsChecked;}
    private void ToggleWhiteSpace(object sender,RoutedEventArgs e){if(ActiveQuery is { } query)query.ShowWhiteSpace=WhiteSpaceMenu.IsChecked;}
    private void Find(object sender, RoutedEventArgs e) => ActiveQuery?.Find();
    private void Exit(object sender, RoutedEventArgs e) => Close();
    private void About(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
    private void ShowError(Exception error) { Status.Text = "Operation failed"; MessageBox.Show(this, error.Message, "PGMS", MessageBoxButton.OK, MessageBoxImage.Error); }

    private void ShowExplorer(object sender, RoutedEventArgs e)
    {
        var explorer = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == "explorer");
        if (explorer is not null) { explorer.Show(); explorer.IsActive = true; }
    }

    private void ResetLayout(object sender, RoutedEventArgs e)
    {
        var root = new LayoutRoot();
        var panel = new LayoutPanel { Orientation = Orientation.Horizontal };
        var tools = new LayoutAnchorablePane { DockWidth = new GridLength(300) };
        var existing = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == "explorer");
        if (existing is not null) { existing.Content = null; }
        tools.Children.Add(new LayoutAnchorable { Title = "Object Explorer", ContentId = "explorer", Content = ExplorerContent, CanClose = false });
        var pane = new LayoutDocumentPane();
        foreach (var document in documents.Values.Concat(monitors.Values)) pane.Children.Add(document);
        panel.Children.Add(tools); panel.Children.Add(pane); root.RootPanel = panel; DockManager.Layout = root;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        RestoreSavedLayout();
        StartRecovery();
    }

    private void RestoreSavedLayout()
    {
        if (!File.Exists(layoutFile)) return;
        try
        {
            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                if (args.Model.ContentId == "explorer") args.Content = ExplorerContent;
                else args.Cancel = true; // Never restore an empty tab pretending its text was saved.
            };
            serializer.Deserialize(layoutFile);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Xml.XmlException or UnauthorizedAccessException)
        { Status.Text = "Saved layout could not be restored. Using the default layout."; ResetLayout(this, new RoutedEventArgs()); }
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (shuttingDown) return;
        e.Cancel = true;
        if (!closeTask.IsCompleted) return;
        closeTask = CloseWindowAsync();
    }

    private async Task CloseWindowAsync()
    {
        closeRequested = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            CancelPendingOpens();
            try { await OpenCompletion; }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidDataException) { }
            await CloseWindowCoreAsync();
        }
        finally { closeRequested = false; CommandManager.InvalidateRequerySuggested(); }
    }

    private async Task CloseWindowCoreAsync()
    {
        if (IsRecovering)
        {
            CancelRecovery(); Status.Text = "Canceling recovery before closing...";
            await RecoveryCompletion;
        }
        try { await SaveCompletion; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        { Status.Text = "Save failed; window remains open: " + ex.Message; return; }
        await TabCloseCompletion;
        var accepted = new Dictionary<QueryView, long>();
        foreach (var query in documents.Keys.ToArray())
        {
            if (!await ConfirmCloseAsync(query)) return;
            accepted[query] = query.EditRevision;
        }
        if (documents.Count != accepted.Count || documents.Keys.Any(q => !accepted.TryGetValue(q, out long revision) || revision != q.EditRevision))
        { Status.Text = "Documents changed during close. Review them before closing again."; return; }
        closingWindow = true; IsEnabled = false; recoveryTimer.Stop();
        try
        {
            if (!await ClearRecoveryOnCloseAsync()) return;
            catalogCancellation.Cancel();
            foreach (var monitor in monitors.Keys) monitor.Shutdown();
            try
            {
                using var writer = new StringWriter();
                new XmlLayoutSerializer(DockManager).Serialize(writer);
                string xml = writer.ToString();
                await fileIo.RunAsync(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(layoutFile)!);
                    string temporary = layoutFile + ".tmp";
                    File.WriteAllText(temporary, xml, new System.Text.UTF8Encoding(false));
                    File.Move(temporary, layoutFile, true);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { ShowError(ex); }
            foreach (var query in documents.Keys.ToArray()) await query.DisposeAsync();
            await recoveryIo.RunAsync(() => recoverySession?.Dispose());
            shuttingDown = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        finally
        {
            closingWindow = false;
            if (!shuttingDown) { IsEnabled = true; if (recoveryStarted && !recoveryClosed) recoveryTimer.Start(); }
        }
    }
}
