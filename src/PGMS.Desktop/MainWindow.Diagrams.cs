using System.IO;
using System.Windows;
using AvalonDock.Layout;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

// Database Diagrams (PGMS-028, docs/MARCO-M1043.md): the folder menus of Object Explorer and the diagram document tabs.
public partial class MainWindow
{
    private readonly Dictionary<DatabaseDiagramView, LayoutDocument> diagrams = [];
    private readonly HashSet<DatabaseDiagramView> acceptedDiagramCloses = [];
    private DatabaseDiagramView? ActiveDiagram => DockManager.ActiveContent as DatabaseDiagramView;
    internal IReadOnlyCollection<DatabaseDiagramView> DatabaseDiagrams => diagrams.Keys;

    // Replaceable in tests, so the suite answers the prompts without message boxes.
    internal Func<string, bool>? DiagramConfirmPrompt { get; set; }
    internal Func<string, string, string?>? DiagramNamePrompt { get; set; }
    internal Func<DatabaseDiagramView, MessageBoxResult>? DiagramClosePrompt { get; set; }
    /// <summary>Replaces Install Diagram Support (question, script and the optional function of PGMS-045); without it, DiagramConfirmPrompt answers the question alone.</summary>
    internal Func<InstallDiagramSupportDialog, bool>? DiagramInstallPrompt { get; set; }
    private bool ConfirmDiagram(string text) => DiagramConfirmPrompt?.Invoke(text) ?? MessageBox.Show(this, text, "PGMS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private string? AskDiagramName(string title, string suggested) => DiagramNamePrompt is { } prompt ? prompt(title, suggested) : NamePrompt.Show(this, title, "Enter a name for the diagram:", suggested);

    private const string InstallQuestion = "This database does not have one or more of the support objects required to use database diagramming. Do you wish to create them?\n\nPGMS creates the schema pgms and the table pgms.diagrams, which keeps the tables shown in each diagram and their positions, never table data.";

    /// <summary>Install Diagram Support with the SSMS question. True when support is (now) installed.</summary>
    internal async Task<bool> EnsureDiagramSupportAsync(string connection, bool askEvenIfInstalled = false)
    {
        var service = new DiagramService(connection);
        try
        {
            if (await service.IsInstalledAsync())
            {
                if (askEvenIfInstalled) Status.Text = "Diagram support is already installed in this database.";
                return true;
            }
            bool function = false;
            if (DiagramInstallPrompt is null && DiagramConfirmPrompt is not null) { if (!ConfirmDiagram(InstallQuestion)) return false; }
            else
            {
                var dialog = new InstallDiagramSupportDialog(InstallQuestion) { Owner = this };
                if (!(DiagramInstallPrompt ?? (d => d.ShowDialog() == true))(dialog)) return false;
                function = dialog.FunctionBox.IsChecked == true;
            }
            await service.InstallAsync(function);
            Status.Text = function ? "Diagram support installed (schema pgms, table pgms.diagrams, function pgms.diagram_markdown)." : "Diagram support installed (schema pgms, table pgms.diagrams).";
            return true;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or TimeoutException or InvalidOperationException)
        {
            Status.Text = "Diagram support could not be installed: " + (ex is Npgsql.PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message);
            return false;
        }
    }

    private async void InstallDiagramSupport(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) return;
        if (await EnsureDiagramSupportAsync(connection, askEvenIfInstalled: true)) await RefreshDiagramFoldersAsync(new Npgsql.NpgsqlConnectionStringBuilder(connection).Database);
    }

    private async void NewDiagramFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before creating a diagram."; return; }
        await NewDiagramAsync(connection);
    }

    /// <summary>New Database Diagram: asks to install support when missing, then opens an empty diagram named Diagram_n and the Add Table dialog, as SSMS does.</summary>
    internal async Task<DatabaseDiagramView?> NewDiagramAsync(string connection, bool showAddTable = true)
    {
        if (!await EnsureDiagramSupportAsync(connection)) return null;
        await RefreshDiagramFoldersAsync(new Npgsql.NpgsqlConnectionStringBuilder(connection).Database);
        IReadOnlyList<DiagramInfo> existing;
        try { existing = await new DiagramService(connection).ListAsync(); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or TimeoutException) { Status.Text = "Diagrams could not be read: " + ex.Message; return null; }
        var used = existing.Select(d => d.Name).Concat(diagrams.Keys.Where(d => d.IsNew).Select(d => d.DiagramName)).ToHashSet(StringComparer.Ordinal);
        int n = 0; while (used.Contains("Diagram_" + n)) n++;
        var view = OpenDiagramDocument(connection, null, "Diagram_" + n);
        if (showAddTable) _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => { if (diagrams.ContainsKey(view)) view.ShowAddTable(); });
        return view;
    }

    private void ModifyDiagramFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || (ExplorerTree.SelectedItem as CatalogNode)?.Entry is not { IsDiagram: true } entry) return;
        OpenDiagram(connection, (int)entry.Oid, entry.Name);
    }

    /// <summary>Opens a saved diagram; one already open is activated instead.</summary>
    internal DatabaseDiagramView OpenDiagram(string connection, int id, string name)
    {
        if (diagrams.FirstOrDefault(d => d.Key.Targets(connection, id)) is { Key: { } open, Value: { } tab }) { tab.IsActive = true; return open; }
        return OpenDiagramDocument(connection, id, name);
    }

    private DatabaseDiagramView OpenDiagramDocument(string connection, int? id, string name)
    {
        var view = new DatabaseDiagramView(connection, id, name) { ConfirmPrompt = DiagramConfirmPrompt };
        var document = new LayoutDocument { Title = view.Title, ContentId = Guid.NewGuid().ToString(), Content = view, ToolTip = view.TabToolTip };
        diagrams.Add(view, document);
        view.TitleChanged += () => { document.Title = view.Title; RefreshCommands(); };
        view.Saved += changedTables => _ = AfterDiagramSavedAsync(view, changedTables);
        view.DesignRequested += table => OpenTableDesigner(connection, table.Schema, table.Name);
        view.UnindexedForeignKeysRequested += table => _ = ShowUnindexedForeignKeysAsync(connection, table is null ? null : (table.Schema, table.Name));
        document.Closing += (_, e) => { if (acceptedDiagramCloses.Remove(view)) return; e.Cancel = true; _ = CloseDiagramAsync(view); };
        document.Closed += (_, _) => { diagrams.Remove(view); view.Shutdown(); RefreshCommands(); };
        AddDocument(document);
        document.IsActive = true;
        RefreshCommands();
        return view;
    }

    private async Task AfterDiagramSavedAsync(DatabaseDiagramView view, bool changedTables)
    {
        await RefreshDiagramFoldersAsync(view.Database);
        if (changedTables) await RefreshExplorerFoldersAsync(n => n.Entry.Kind == CatalogKind.Tables && n.Entry.Database == view.Database);
    }
    private Task RefreshDiagramFoldersAsync(string? database) => RefreshExplorerFoldersAsync(n => n.Entry.Kind == CatalogKind.DatabaseDiagrams && n.Entry.Database == database);

    /// <summary>Reloads only the matching loaded folders and their open branches, so the rest of the tree stays as the user left it.</summary>
    private async Task RefreshExplorerFoldersAsync(Func<CatalogNode, bool> match)
    {
        static IEnumerable<CatalogNode> Walk(CatalogNode node) => node.Children.SelectMany(Walk).Prepend(node);
        async Task Reload(CatalogNode node)
        {
            await node.LoadAsync(catalogCancellation.Token, refresh: true);
            foreach (var child in node.Children.Where(c => c.IsExpanded).ToList()) await Reload(child);
        }
        foreach (var folder in ExplorerTree.Items.OfType<CatalogNode>().SelectMany(Walk).Where(match).ToList()) await Reload(folder);
    }

    internal async Task<bool> CloseDiagramAsync(DatabaseDiagramView view)
    {
        if (!diagrams.TryGetValue(view, out var document) || !await ConfirmDiagramCloseAsync(view)) return false;
        acceptedDiagramCloses.Add(view);
        document.Close();
        return true;
    }
    /// <summary>Save changes to the following items? Save / Don't Save / Cancel, as SSMS asks when a changed diagram closes.</summary>
    private async Task<bool> ConfirmDiagramCloseAsync(DatabaseDiagramView view)
    {
        if (view.IsBusy) { MessageBox.Show(this, "Cancel the running operation before closing this diagram.", "PGMS"); return false; }
        if (!view.IsDirty) return true;
        var answer = DiagramClosePrompt?.Invoke(view) ?? MessageBox.Show(this, "Save changes to the following items?\n\n" + view.DiagramName, "PGMS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && await view.SaveAsync() && !view.IsDirty);
    }

    // As in SSMS, Rename edits the label in the tree.
    private void RenameDiagramFromExplorer(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is CatalogNode node) BeginExplorerRename(node);
    }

    /// <summary>Starts editing the label of a saved diagram in place. False when the node cannot be renamed now.</summary>
    internal bool BeginExplorerRename(CatalogNode node)
    {
        CancelPendingExplorerRename();
        if (!node.Entry.IsDiagram || node.IsRenaming || catalogService?.ConnectionFor(node.Entry.Database) is not { } connection) return false;
        if (diagrams.Keys.FirstOrDefault(d => d.Targets(connection, (int)node.Entry.Oid)) is { IsBusy: true }) { Status.Text = "Wait for the diagram to finish its operation before renaming it."; return false; }
        node.RenameText = node.Entry.Name;
        node.IsRenaming = true;
        return true;
    }

    /// <summary>Enter or leaving the box keeps the typed name; Escape (accept: false) brings the old one back.</summary>
    internal async Task<bool> EndExplorerRenameAsync(CatalogNode node, bool accept)
    {
        if (!node.IsRenaming) return false;
        node.IsRenaming = false;
        string typed = node.RenameText.Trim();
        bool renamed = accept && typed.Length > 0 && typed != node.Entry.Name && catalogService?.ConnectionFor(node.Entry.Database) is { } connection
            && await RenameDiagramAsync(connection, (int)node.Entry.Oid, node.Entry.Name, typed);
        // The renamed diagram is a new node after the refresh; keep it selected, as SSMS does.
        var current = ExplorerTree.Items.OfType<CatalogNode>().SelectMany(WalkExplorer).FirstOrDefault(n => n.Entry.IsDiagram && n.Entry.Oid == node.Entry.Oid && n.Entry.Database == node.Entry.Database) ?? node;
        current.IsSelected = true;
        ExplorerTree.Focus();
        return renamed;
    }
    private static IEnumerable<CatalogNode> WalkExplorer(CatalogNode node) => node.Children.SelectMany(WalkExplorer).Prepend(node);

    internal async Task<bool> RenameDiagramAsync(string connection, int id, string current, string? newName = null)
    {
        // An open diagram renames too (pedido do usuário, 07/11/2026): with the version its tab read, and the tab takes the new name and version.
        var open = diagrams.Keys.FirstOrDefault(d => d.Targets(connection, id));
        if (open is { IsBusy: true }) { Status.Text = "Wait for the diagram to finish its operation before renaming it."; return false; }
        if ((newName ?? AskDiagramName("Rename Diagram", current)) is not { } typed || typed.Trim() == current) return false;
        if (open is { IsBusy: true }) { Status.Text = "Wait for the diagram to finish its operation before renaming it."; return false; }
        try
        {
            var renamed = await new DiagramService(connection).RenameAsync(id, typed, open?.Info?.Version);
            open?.ApplyRename(renamed);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException) { Status.Text = "The diagram was not renamed: " + ex.Message; return false; }
        await RefreshDiagramFoldersAsync(new Npgsql.NpgsqlConnectionStringBuilder(connection).Database);
        Status.Text = "Diagram renamed.";
        return true;
    }

    // A second, slow click on the label of the selected diagram starts the rename; a double click opens it instead.
    private System.Windows.Threading.DispatcherTimer? pendingExplorerRename;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    private void CancelPendingExplorerRename() { pendingExplorerRename?.Stop(); pendingExplorerRename = null; }

    private void ExplorerLeftClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (InExplorerRenameBox(e.OriginalSource)) return;
        CancelPendingExplorerRename();
        if (e.ClickCount != 1 || !ExplorerTree.IsKeyboardFocusWithin || e.OriginalSource is not System.Windows.Controls.TextBlock { Parent: System.Windows.Controls.Border }
            || System.Windows.Controls.ItemsControl.ContainerFromElement(null, (DependencyObject)e.OriginalSource) is not System.Windows.Controls.TreeViewItem { DataContext: CatalogNode { IsSelected: true, Entry.IsDiagram: true } node }) return;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        timer.Tick += (_, _) => { timer.Stop(); if (pendingExplorerRename == timer) { pendingExplorerRename = null; if (node.IsSelected) BeginExplorerRename(node); } };
        pendingExplorerRename = timer;
        timer.Start();
    }

    private void ExplorerKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.F2 || InExplorerRenameBox(e.OriginalSource) || ExplorerTree.SelectedItem is not CatalogNode node) return;
        e.Handled = BeginExplorerRename(node);
    }

    /// <summary>Clicks and keys inside the rename box belong to the edit, not to the tree.</summary>
    internal static bool InExplorerRenameBox(object? source)
    {
        for (var element = source as DependencyObject; element is not null; element = element is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is System.Windows.Controls.TextBox) return true;
        return false;
    }

    private void ExplorerRenameShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { IsVisible: true } box) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { box.Focus(); box.SelectAll(); });
    }

    private void ExplorerRenameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { DataContext: CatalogNode node } || e.Key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Escape)) return;
        e.Handled = true;
        _ = EndExplorerRenameAsync(node, accept: e.Key == System.Windows.Input.Key.Enter);
    }

    private void ExplorerRenameLostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox { DataContext: CatalogNode { IsRenaming: true } node }) _ = EndExplorerRenameAsync(node, accept: true);
    }

    private async void DeleteDiagramFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || (ExplorerTree.SelectedItem as CatalogNode)?.Entry is not { IsDiagram: true } entry) return;
        await DeleteDiagramAsync(connection, (int)entry.Oid, entry.Name);
    }
    /// <summary>Deletes the diagram only; its tables stay.</summary>
    internal async Task<bool> DeleteDiagramAsync(string connection, int id, string name)
    {
        if (diagrams.Keys.Any(d => d.Targets(connection, id))) { Status.Text = "Close the diagram before deleting it."; return false; }
        if (!ConfirmDiagram($"Delete the diagram {name}?\n\nOnly the diagram is deleted; its tables stay in the database.")) return false;
        try { await new DiagramService(connection).DeleteAsync(id); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or TimeoutException or InvalidOperationException) { Status.Text = "The diagram was not deleted: " + ex.Message; return false; }
        await RefreshDiagramFoldersAsync(new Npgsql.NpgsqlConnectionStringBuilder(connection).Database);
        Status.Text = "Diagram deleted.";
        return true;
    }
}
