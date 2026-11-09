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
    private readonly Dictionary<ReportView, LayoutDocument> reports = [];
    private readonly Dictionary<TableDesignerView, LayoutDocument> designers = [];
    private readonly HashSet<TableDesignerView> acceptedDesignerCloses = [];
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
    private ReportView? ActiveReport => DockManager.ActiveContent as ReportView;
    private TableDesignerView? ActiveDesigner => DockManager.ActiveContent as TableDesignerView;

    public MainWindow()
    {
        InitializeComponent();
        InitializeExplorerToolbar();
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
        RestoreWindowPlacement();
        ShowActiveDatabase(null);
        DockManager.ActiveContentChanged += (_, _) => documentSelectionRevision++;
        Closed += (_, _) => { recoveryClosed = true; recoveryTimer.Stop(); DiscardSpareQuery(); recoveryIo.Run(() => recoverySession?.Dispose()); };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.New, async (_, _) => await NewQueryInContextAsync()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, async (_, e) =>
        {
            e.Handled = true;
            if (ActiveQuery is { } query) await CloseQueryAsync(query);
            else if (ActiveMonitor is { } monitor && monitors.TryGetValue(monitor, out var tab)) tab.Close();
            else if (ActiveReport is { } report && reports.TryGetValue(report, out var reportTab)) reportTab.Close();
            else if (ActiveDesigner is { } designer && designers.TryGetValue(designer, out var designerTab)) designerTab.Close();
            else if (ActiveDiagram is { } diagram && diagrams.TryGetValue(diagram, out var diagramTab)) diagramTab.Close();
            else if (ActiveViewDesigner is { } viewDesigner && viewDesigners.TryGetValue(viewDesigner, out var viewDesignerTab)) viewDesignerTab.Close();
            else if (ActiveEditor is { } editor && editors.TryGetValue(editor, out var editorTab)) editorTab.Close();
        }, (_, e) =>
        {
            e.CanExecute = (ActiveQuery is { } query ? !closingQueries.Contains(query) && !pendingTabCloses.ContainsKey(query) : ActiveMonitor is not null || ActiveReport is not null || ActiveDesigner is not null || ActiveDiagram is not null || ActiveViewDesigner is not null || ActiveEditor is not null)
                && !closeRequested && !closingWindow && !shuttingDown;
            e.Handled = true;
        }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Close, Key.F4, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, Open, (_, e) => e.CanExecute = !closeRequested && !closingWindow && !shuttingDown));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Replace, (_, _) => ActiveQuery?.Replace(), (_, e) => e.CanExecute = ActiveQuery is not null));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Replace, Key.H, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(GenerateSqlWithAi, (_, _) => OpenAiQuery(), (_, e) => e.CanExecute = ActiveQuery is not null && !closingWindow && !closeRequested));
        InputBindings.Add(new KeyBinding(GenerateSqlWithAi, Key.I, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(ShowQueryOptions, (_, _) => OpenQueryOptions(), (_, e) => e.CanExecute = ActiveQuery is { IsBusy: false } && !closingWindow && !closeRequested));
        CommandBindings.Add(new CommandBinding(DesignQueryInEditor, (_, _) => _ = OpenQueryDesignerAsync(), (_, e) => e.CanExecute = ActiveQuery is { IsConnected: true, IsBusy: false } && !closingWindow && !closeRequested));
        InputBindings.Add(new KeyBinding(DesignQueryInEditor, Key.Q, ModifierKeys.Control | ModifierKeys.Shift));
        CommandBindings.Add(new CommandBinding(QueryView.GoToLineCommand,(_,_)=>ActiveQuery?.GoToLine(),(_,e)=>e.CanExecute=ActiveQuery is not null));
        InputBindings.Add(new KeyBinding(QueryView.GoToLineCommand,Key.G,ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.Comment,(_,_)=>ActiveQuery?.CommentSelection(false),(_,e)=>e.CanExecute=ActiveQuery is not null && !closingWindow && !closeRequested));
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.Uncomment,(_,_)=>ActiveQuery?.CommentSelection(true),(_,e)=>e.CanExecute=ActiveQuery is not null && !closingWindow && !closeRequested));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, async (_, _) => { if (ActiveQuery is { } query) await SaveAsync(query); else if (ActiveDesigner is { } designer) await designer.SaveAsync(); else if (ActiveDiagram is { } diagram) await diagram.SaveAsync(); else if (ActiveViewDesigner is { } viewDesigner) await viewDesigner.SaveAsync(); }, (_, e) => e.CanExecute = ActiveDesigner is { CanEdit: true, IsDirty: true } || ActiveDiagram is { IsBusy: false, IsDirty: true } || ActiveViewDesigner is { IsBusy: false, IsDirty: true } || ActiveQuery is { } active && !IsSaving(active) && !closingWindow && !closingQueries.Contains(active)));
        PreviewKeyDown += async (_, e) =>
        {
            if (HandleQueryMenuShortcut(e)) return;
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                if (ExplorerContent.IsKeyboardFocusWithin) await RefreshSelectedExplorerAsync();
                else if (ActiveQuery is { } query) await query.ExecuteAsync();
                else if (ActiveMonitor is { } monitor) await monitor.ReloadAsync();
                else if (ActiveReport is { } report) await report.LoadAsync();
                else if (ActiveEditor is { } editor) await editor.LoadAsync();
            }
            if (e.Key == Key.Escape && ExplorerContent.IsKeyboardFocusWithin && (ExplorerTree.SelectedItem as CatalogNode)?.LoadTarget is { IsBusy: true } loading)
            {
                loading.CancelLoad(); e.Handled = true;
            }
            if (e.Key == Key.F8) { ShowExplorer(this, e); e.Handled = true; }
            if (HandleResultsShortcut(e)) return;
            // SSMS shortcut for Activity Monitor; with Alt held WPF reports the letter in SystemKey.
            if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) { ShowActivity(this, e); e.Handled = true; }
            if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.G && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) { ShowRegisteredServers(); e.Handled = true; }
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
        // Tools ▸ Options ▸ Query Results ▸ General ▸ Default destination for results.
        var environment = EnvironmentSettings.Load();
        view.ResultsDestination = environment.DefaultResults;
        view.ApplyQueryOptions(environment.QueryOptionDefaults);
        view.JsonLinkRequested += OpenJsonDocument;
        view.PlanTabRequested += statements => OpenPlanDocument(statements, "Execution plan");
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
        bool accepted = dialog.ShowDialog() == true;
        registeredServers?.Reload(); // The dialog may have saved or deleted profiles.
        if (!accepted) return;
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
        if (explorerConnection is not { } connection || refreshing) return;
        refreshing = true;
        Status.Text = "Refreshing Object Explorer…";
        try
        {
            catalogCancellation.Cancel(); catalogCancellation.Dispose();
            catalogCancellation = new CancellationTokenSource();
            var token = catalogCancellation.Token;
            catalogService = new CatalogService(connection);
            var root = new CatalogNode(new(CatalogKind.Server, catalogService.DisplayName), catalogService);
            await root.LoadAsync(token);
            if (token.IsCancellationRequested || explorerConnection != connection) return; // Disconnected while loading.
            ExplorerTree.Items.Clear();
            ExplorerTree.Items.Add(root);
            Status.Text = "Object Explorer refreshed. Expand a database to load its objects.";
            NewDatabaseMenu.IsEnabled = NewSchemaMenu.IsEnabled = NewRoleMenu.IsEnabled = BackupMenu.IsEnabled = RestoreMenu.IsEnabled = true;
            BackupGlobalsMenu.IsEnabled=RestoreGlobalsMenu.IsEnabled=true;
            DisconnectExplorerButton.IsEnabled = true;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException) { ShowError(ex); }
        catch (OperationCanceledException) when (explorerConnection != connection) { }
        finally { refreshing = false; }
    }

    private void DisconnectExplorer(object sender, RoutedEventArgs e) => DisconnectExplorer();
    /// <summary>As in SSMS: closes the Object Explorer connection only. Query windows keep their own sessions and transactions.</summary>
    internal void DisconnectExplorer()
    {
        if (explorerConnection is null && catalogService is null) return;
        catalogCancellation.Cancel(); catalogCancellation.Dispose(); catalogCancellation = new CancellationTokenSource();
        explorerConnection = null; catalogService = null;
        ExplorerTree.Items.Clear(); ExplorerFilterButton.IsEnabled = false;
        NewDatabaseMenu.IsEnabled = NewSchemaMenu.IsEnabled = NewRoleMenu.IsEnabled = BackupMenu.IsEnabled = RestoreMenu.IsEnabled = false;
        BackupGlobalsMenu.IsEnabled = RestoreGlobalsMenu.IsEnabled = DisconnectExplorerButton.IsEnabled = false;
        Status.Text = "Object Explorer disconnected. Open query windows keep their own connections.";
    }

    private async void ExplorerExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: CatalogNode node })
        {
            await node.LoadAsync(catalogCancellation.Token);
            if (node.Error is { } error) Status.Text = error;
        }
    }
    private void ExplorerSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        ExplorerFilterButton.IsEnabled = (ExplorerTree.SelectedItem as CatalogNode)?.CanFilter == true;
    private void OpenExplorerFilter(object sender, RoutedEventArgs e) => _ = FilterSelectedFolderAsync();
    /// <summary>Replaces the Filter Settings window in tests; returns the new filter, or null for Cancel.</summary>
    internal Func<CatalogNode, CatalogFilter?>? ExplorerFilterPrompt { get; set; }
    private CatalogFilter? PromptExplorerFilter(CatalogNode node)
    {
        if (ExplorerFilterPrompt is { } prompt) return prompt(node);
        var root = node; while (root.Parent is { } parent) root = parent;
        string server = root.Entry.Name.Split(" (")[0];
        var dialog = new FilterSettingsDialog(server, node.Entry.Kind == CatalogKind.Roles ? null : node.Entry.Database, node.Filter, node.CanFilterByOwner) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
    internal async Task FilterSelectedFolderAsync()
    {
        if (ExplorerTree.SelectedItem is not CatalogNode { CanFilter: true } node || PromptExplorerFilter(node) is not { } filter) return;
        await ApplyExplorerFilterAsync(node, filter);
    }
    private async Task ApplyExplorerFilterAsync(CatalogNode node, CatalogFilter filter)
    {
        node.SetFilter(filter);
        node.IsExpanded = true;
        await node.LoadAsync(catalogCancellation.Token);
        Status.Text = node.Error ?? node.FilterSummary;
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
    // As in SSMS, Properties is the default action of a login/role node.
    private void ExplorerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CancelPendingExplorerRename();
        if (e.ChangedButton != MouseButton.Left || InExplorerRenameBox(e.OriginalSource) || e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(null, source) is not TreeViewItem { DataContext: CatalogNode node } item) return;
        // SSMS opens a diagram (Modify) on double click.
        if (node.Entry.IsDiagram) { item.IsSelected = true; e.Handled = true; ModifyDiagramFromExplorer(sender, e); return; }
        if (!node.Entry.CanManageRole) return;
        item.IsSelected = true; e.Handled = true;
        RoleProperties(sender, e);
    }
    private string? SelectedConnection => catalogService?.ConnectionFor((ExplorerTree.SelectedItem as CatalogNode)?.Entry.Database);
    private async void QueryFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) return;
        var query = NewQuery();
        try { await query.ConnectAsync(connection); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or OperationCanceledException) { ShowError(ex); }
    }
    /// <summary>
    /// New Query (Ctrl+N and toolbar): the active query's connection or, with the Object Explorer or another
    /// document active, the database selected in the tree. Stays disconnected only when neither exists.
    /// </summary>
    private async Task NewQueryInContextAsync()
    {
        string? connection = ActiveQuery?.ConnectionString ?? SelectedConnection;
        var query = NewQuery();
        if (connection is null) return;
        try { await query.ConnectAsync(connection); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
    }
    private void NewDatabase(object sender, RoutedEventArgs e) => CreateObject(CreateObjectKind.Database);
    private void NewSchema(object sender, RoutedEventArgs e) => CreateObject(CreateObjectKind.Schema);
    private async void TableProperties(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before inspecting tables."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var properties = new TablePropertiesDialog(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null) { Owner = this, InspectOnOpen = entry?.Kind == CatalogKind.Relation };
        if (properties.ShowDialog() == true && properties.EditSelection is { } selection)
            await ShowTableOperationsAsync(connection, selection.Table.Schema, selection.Table.Name, selection.Table, selection.Column);
    }
    private void ObjectProperties(object sender, RoutedEventArgs e)
    {
        if (catalogService is null || ExplorerTree.SelectedItem is not CatalogNode { Entry.CanInspectObjectProperties: true } node) return;
        var connection = catalogService.ConnectionFor(node.Entry.Kind == CatalogKind.Database ? null : node.Entry.Database);
        new ObjectPropertiesDialog(connection, node.Entry) { Owner = this, InspectOnOpen = true }.ShowDialog();
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
        var dialog = new SequenceDialog(connection, entry?.Schema, entry?.CanInspectSequence == true ? entry.Name : null, entry?.CanInspectSequence == true ? entry.Oid : null) { Owner = this, InspectOnOpen = entry?.CanInspectSequence == true };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Sequence operation completed. Object Explorer refreshed."; }
    }
    private async void IndexOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing indexes."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var dialog = new RelationEditDialog(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null) { Owner = this, InspectOnOpen = entry?.Kind == CatalogKind.Relation };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Index operation completed. Object Explorer refreshed."; }
    }
    private async void TableConstraintOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing table constraints."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var dialog = new TableConstraintDialog(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null) { Owner = this, InspectOnOpen = entry?.Kind == CatalogKind.Relation };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Constraint operation completed. Object Explorer refreshed."; }
    }
    private async void CheckConstraintOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing CHECK constraints."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var dialog = new CheckConstraintDialog(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null) { Owner = this, InspectOnOpen = entry?.Kind == CatalogKind.Relation };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "CHECK constraint operation completed. Object Explorer refreshed."; }
    }
    private async void ForeignKeyOperations(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before managing foreign keys."; return; }
        var entry = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        var dialog = new ForeignKeyDialog(connection, entry?.Schema, entry?.Kind == CatalogKind.Relation ? entry.Name : null) { Owner = this, InspectOnOpen = entry?.Kind == CatalogKind.Relation };
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
        var dialog = new RoleAttributeDialog(connection, SelectedRoleName) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.PasswordWasApplied) Status.Text = "Role password changed.";
    }
    private void AiAssistantSettings(object sender, RoutedEventArgs e) => new AiSettingsDialog { Owner = this }.ShowDialog();
    public static readonly RoutedUICommand GenerateSqlWithAi = new("Generate SQL with AI", nameof(GenerateSqlWithAi), typeof(MainWindow));
    public static readonly RoutedUICommand DesignQueryInEditor = new("Design Query in Editor", nameof(DesignQueryInEditor), typeof(MainWindow));
    public static readonly RoutedUICommand ShowQueryOptions = new("Query Options", nameof(ShowQueryOptions), typeof(MainWindow));
    // Opens the assistant for the active query tab; the generated SQL only reaches the editor through Insert.
    internal void OpenAiQuery(string? settingsPath = null)
    {
        if (ActiveQuery is not { } query) return;
        if (!query.IsConnected || query.ConnectionString is not { } connection) { Status.Text = "Connect this query window before generating SQL with AI."; return; }
        var store = new AiSettingsStore(settingsPath ?? AiSettingsStore.DefaultPath);
        AiProviderSettings? provider = null;
        for (int attempt = 0; attempt < 2 && provider is null; attempt++)
        {
            try
            {
                var settings = store.Load(); var active = settings.For(settings.ActiveProvider);
                if (active.Model.Length > 0 && (!active.NeedsKey || active.HasKey)) provider = active;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            { Status.Text = "AI settings could not be read: " + ex.Message; return; }
            if (provider is null && attempt == 0)
            {
                if (MessageBox.Show(this, "The AI assistant needs a provider, a model and, for Anthropic or OpenAI, an API key.\nOpen AI Assistant Settings now?", "Generate SQL with AI", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
                new AiSettingsDialog(store.FilePath) { Owner = this }.ShowDialog();
            }
        }
        if (provider is null) { Status.Text = "The AI assistant is not configured."; return; }
        var dialog = new AiQueryDialog(provider, connection, query.SelectedSql, query.InsertGeneratedSql) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.WasInserted) Status.Text = "AI-generated SQL inserted into the editor. Review it before running.";
    }
    private void ConfigureServerTls(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before configuring server TLS."; return; }
        var dialog = new ServerTlsDialog(connection) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Result is not null) Status.Text = "Server TLS enabled. Reconnect to use it in this window.";
    }
    // Explorer labels login roles as "name (login)"; dialogs need the exact rolname.
    private string? SelectedRoleName => (ExplorerTree.SelectedItem as CatalogNode)?.Entry is { CanManageRole: true } entry ? entry.RoleName : null;
    private async void RenameRole(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before renaming a role."; return; }
        var role = SelectedRoleName;
        var dialog = new RoleRenameDialog(connection, role) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Role renamed. Object Explorer refreshed."; }
    }
    private async void DropRole(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before dropping a role."; return; }
        var role = SelectedRoleName;
        var dialog = new RoleDropDialog(connection, role) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptSql is { } sql) await OpenAdministrativeScriptAsync(connection, sql);
        if (dialog.WasApplied) { await LoadExplorerAsync(); Status.Text = "Role dropped. Object Explorer refreshed."; }
    }
    private async void SetRolePassword(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before changing a role password."; return; }
        var role = SelectedRoleName;
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
        var dialog = new RoleMembershipDialog(connection, (ExplorerTree.SelectedItem as CatalogNode)?.Entry is { Kind: CatalogKind.Role } role ? role.RoleName : null) { Owner = this };
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
    /// <summary>Standard report as a document tab; the same report of the same target is activated and read again instead of opening a second tab.</summary>
    internal ReportView OpenReport(string connection, ServerReportKind kind)
    {
        if (reports.FirstOrDefault(r => r.Key.Targets(connection, kind)) is { Key: { } existing, Value: { } tab }) { tab.IsActive = true; _ = existing.LoadAsync(); return existing; }
        var view = new ReportView(connection, kind);
        var document = new LayoutDocument { Title = view.Title, ContentId = Guid.NewGuid().ToString(), Content = view };
        reports.Add(view, document);
        document.Closed += (_, _) => { reports.Remove(view); view.Shutdown(); RefreshCommands(); };
        AddDocument(document);
        document.IsActive = true;
        RefreshCommands();
        return view;
    }
    internal IReadOnlyCollection<ReportView> Reports => reports.Keys;
    /// <summary>Table Designer as a document tab; a table already open is activated instead of opening a second tab. A null table starts a new one.</summary>
    internal TableDesignerView OpenTableDesigner(string connection, string schema, string? table)
    {
        if (designers.FirstOrDefault(d => d.Key.Targets(connection, schema, table)) is { Key: { } existing, Value: { } tab }) { tab.IsActive = true; return existing; }
        var view = new TableDesignerView(connection, schema, table);
        var document = new LayoutDocument { Title = view.Title, ContentId = Guid.NewGuid().ToString(), Content = view, ToolTip = view.ToolTip };
        designers.Add(view, document);
        view.TitleChanged += () => { document.Title = view.Title; RefreshCommands(); };
        view.Saved += () =>
        {
            _ = RefreshDesignedTableAsync(view);
            // Diagrams open on the same database show the saved table as it is now.
            foreach (var diagram in diagrams.Keys.Where(d => d.OnDatabase(connection)).ToList()) _ = diagram.RefreshTablesAsync();
        };
        document.Closing += (_, e) => { if (acceptedDesignerCloses.Remove(view)) return; e.Cancel = true; _ = CloseDesignerAsync(view); };
        document.Closed += (_, _) => { designers.Remove(view); view.Shutdown(); RefreshCommands(); };
        AddDocument(document);
        document.IsActive = true;
        RefreshCommands();
        return view;
    }
    internal IReadOnlyCollection<TableDesignerView> TableDesigners => designers.Keys;
    /// <summary>Reloads only the Tables folder of the saved table and its open branches, so the rest of the tree stays as the user left it.</summary>
    private async Task RefreshDesignedTableAsync(TableDesignerView view)
    {
        static IEnumerable<CatalogNode> Walk(CatalogNode node) => node.Children.SelectMany(Walk).Prepend(node);
        async Task Reload(CatalogNode node)
        {
            await node.LoadAsync(catalogCancellation.Token, refresh: true);
            foreach (var child in node.Children.Where(c => c.IsExpanded).ToList()) await Reload(child);
        }
        foreach (var folder in ExplorerTree.Items.OfType<CatalogNode>().SelectMany(Walk).Where(n => n.Entry.Kind == CatalogKind.Tables && n.Entry.Schema == view.Schema && n.Entry.Database == view.Database).ToList())
            await Reload(folder);
    }
    internal Func<TableDesignerView, MessageBoxResult>? DesignerClosePrompt { get; set; }
    internal async Task<bool> CloseDesignerAsync(TableDesignerView view)
    {
        if (!designers.TryGetValue(view, out var document) || !await ConfirmDesignerCloseAsync(view)) return false;
        acceptedDesignerCloses.Add(view);
        document.Close();
        return true;
    }
    private async Task<bool> ConfirmDesignerCloseAsync(TableDesignerView view)
    {
        if (view.IsBusy) { MessageBox.Show(this, "Cancel the running operation before closing this table.", "PGMS"); return false; }
        if (!view.IsDirty) return true;
        var answer = DesignerClosePrompt?.Invoke(view) ?? MessageBox.Show(this, "Save changes to the following items?\n\n" + view.Title.TrimEnd('*'), "PGMS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && await view.SaveAsync() && !view.IsDirty);
    }
    private void DesignFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || (ExplorerTree.SelectedItem as CatalogNode)?.Entry is not { Schema: { } schema } entry) { Status.Text = "Connect before designing tables."; return; }
        OpenTableDesigner(connection, schema, entry.Name);
    }
    private void NewTableInDesigner(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before creating tables."; return; }
        OpenTableDesigner(connection, (ExplorerTree.SelectedItem as CatalogNode)?.Entry.Schema ?? "public", null);
    }
    private void TableDesignerMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ActiveDesigner is not { } designer) return;
        DesignerPrimaryKeyMenu.Header = designer.PrimaryKeyHeader;
        DesignerPrimaryKeyMenu.IsEnabled = DesignerInsertMenu.IsEnabled = DesignerDeleteMenu.IsEnabled = designer.CanEdit;
        DesignerScriptMenu.IsEnabled = designer.IsDirty;
    }
    private void DesignerPrimaryKey(object sender, RoutedEventArgs e) => ActiveDesigner?.TogglePrimaryKey();
    private void DesignerInsertColumn(object sender, RoutedEventArgs e) => ActiveDesigner?.InsertColumn();
    private void DesignerDeleteColumn(object sender, RoutedEventArgs e) => ActiveDesigner?.DeleteColumn();
    private void DesignerRelationships(object sender, RoutedEventArgs e) => ActiveDesigner?.ShowRelationships();
    private void DesignerIndexes(object sender, RoutedEventArgs e) => ActiveDesigner?.ShowIndexes();
    private void DesignerChecks(object sender, RoutedEventArgs e) => ActiveDesigner?.ShowChecks();
    private void DesignerScript(object sender, RoutedEventArgs e) => ActiveDesigner?.ShowChangeScript();
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
        TableDesignerMenu.Visibility = ActiveDesigner is null ? Visibility.Collapsed : Visibility.Visible;
        ExecuteButton.IsEnabled = ExecuteMenu.IsEnabled = query is { IsConnected: true, IsBusy: false };
        CancelButton.IsEnabled = CancelMenu.IsEnabled = query is { IsBusy: true, IsExporting: false };
        RefreshResultsButtons();
        ShowActiveDatabase(query);
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
    /// <summary>Replaces the error box in tests, which would block the run.</summary>
    internal Action<Exception>? ShowErrorOverride { get; set; }
    private void ShowError(Exception error) { Status.Text = "Operation failed"; if (ShowErrorOverride is { } shown) { shown(error); return; } MessageBox.Show(this, error.Message, "PGMS", MessageBoxButton.OK, MessageBoxImage.Error); }

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
        // Registered Servers is left out of the default layout; View ▸ Registered Servers adds it again.
        if (DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == RegisteredServersId) is { } registered) registered.Content = null;
        tools.Children.Add(new LayoutAnchorable { Title = "Object Explorer", ContentId = "explorer", Content = ExplorerContent, CanClose = false });
        var pane = new LayoutDocumentPane();
        foreach (var document in documents.Values.Concat(monitors.Values).Concat(reports.Values)) pane.Children.Add(document);
        panel.Children.Add(tools); panel.Children.Add(pane); root.RootPanel = panel; DockManager.Layout = root;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        RestoreSavedLayout();
        StartRecovery();
        // As SSMS does, the Connect dialog opens at startup and connects Object Explorer (pedido do usuário, 08/11/2026). Only for the real
        // application: test hosts use a plain Application and must not get a modal dialog.
        if (Application.Current is App)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, async () => await ConnectAtStartupAsync());
    }

    /// <summary>Connect to Server at startup: connects only Object Explorer, like SSMS; skipped when a dialog (for example draft recovery) is already open.</summary>
    internal async Task ConnectAtStartupAsync(Func<ConnectionDialog, bool?>? show = null)
    {
        if (explorerConnection is not null || closeRequested || closingWindow || OwnedWindows.OfType<Window>().Any(w => w.IsVisible)) return;
        var dialog = new ConnectionDialog { Owner = this };
        bool accepted = (show ?? (d => d.ShowDialog()))(dialog) == true;
        registeredServers?.Reload();
        if (!accepted) return;
        Status.Text = "Connecting…";
        try { await ConnectExplorerAsync(dialog.ConnectionString); Status.Text = "Connected"; }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
        RefreshCommands();
    }

    // ---- Window size and position (Tools ▸ Options ▸ Environment ▸ General, pedido do usuário, 08/11/2026) ----
    internal EnvironmentOptions EnvironmentSettings { get; } = new(EnvironmentOptions.DefaultPath);

    /// <summary>Opens where the window was closed, when the option is on and that place is still on a screen; otherwise centered as before.
    /// The work area fitting (WindowWorkArea) still keeps the window inside the monitor.</summary>
    internal void RestoreWindowPlacement()
    {
        var document = EnvironmentSettings.Load();
        if (!document.RestoreWindowPlacement || document.Window is not { } saved || saved.Width < 200 || saved.Height < 150) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(screen, new Rect(saved.Left, saved.Top, saved.Width, saved.Height));
        if (visible.IsEmpty || visible.Width < 100 || visible.Height < 50) return; // the monitor it was on is gone
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left; Top = saved.Top; Width = saved.Width; Height = saved.Height;
        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Keeps the normal bounds and whether the window is maximized, when the option is on. A minimized window keeps its normal bounds.</summary>
    internal void SaveWindowPlacement()
    {
        if (!EnvironmentSettings.Load().RestoreWindowPlacement) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || double.IsNaN(bounds.Left)) return;
        bool maximized = WindowState == WindowState.Maximized;
        EnvironmentSettings.Update(d => d with { Window = new(Math.Round(bounds.Left), Math.Round(bounds.Top), Math.Round(bounds.Width), Math.Round(bounds.Height), maximized) });
    }

    private void ShowOptions(object sender, RoutedEventArgs e)
    {
        var dialog = new OptionsDialog(EnvironmentSettings) { Owner = this };
        dialog.ShowDialog();
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
                else if (args.Model.ContentId == RegisteredServersId) args.Content = RegisteredServers;
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
        foreach (var designer in designers.Keys.ToArray()) if (!await ConfirmDesignerCloseAsync(designer)) return;
        foreach (var viewDesigner in viewDesigners.Keys.ToArray()) if (!await ConfirmViewDesignerCloseAsync(viewDesigner)) return;
        foreach (var diagram in diagrams.Keys.ToArray()) if (!await ConfirmDiagramCloseAsync(diagram)) return;
        closingWindow = true; IsEnabled = false; recoveryTimer.Stop();
        try
        {
            if (!await ClearRecoveryOnCloseAsync()) return;
            catalogCancellation.Cancel();
            foreach (var monitor in monitors.Keys) monitor.Shutdown();
            foreach (var report in reports.Keys) report.Shutdown();
            foreach (var designer in designers.Keys) designer.Shutdown();
            foreach (var viewDesigner in viewDesigners.Keys) viewDesigner.Shutdown();
            foreach (var diagram in diagrams.Keys) diagram.Shutdown();
            SaveWindowPlacement();
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
