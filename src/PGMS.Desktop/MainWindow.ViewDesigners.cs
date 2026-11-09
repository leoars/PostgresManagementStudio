using System.Windows;
using AvalonDock.Layout;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

// View Designer (PGMS-031, docs/MARCO-M1046.md): the document tabs and the Object Explorer commands.
public partial class MainWindow
{
    private readonly Dictionary<ViewDesignerView, LayoutDocument> viewDesigners = [];
    private readonly HashSet<ViewDesignerView> acceptedViewDesignerCloses = [];
    private ViewDesignerView? ActiveViewDesigner => DockManager.ActiveContent as ViewDesignerView;
    internal IReadOnlyCollection<ViewDesignerView> ViewDesigners => viewDesigners.Keys;
    internal Func<ViewDesignerView, MessageBoxResult>? ViewDesignerClosePrompt { get; set; }

    /// <summary>A view already open is activated instead of opening a second tab. A null view starts a new one.</summary>
    internal ViewDesignerView OpenViewDesigner(string connection, string schema, string? view, bool addTableOnOpen = true)
    {
        if (viewDesigners.FirstOrDefault(d => d.Key.Targets(connection, schema, view)) is { Key: { } existing, Value: { } tab }) { tab.IsActive = true; return existing; }
        var designer = new ViewDesignerView(connection, schema, view) { AddTableOnOpen = addTableOnOpen };
        var document = new LayoutDocument { Title = designer.Title, ContentId = Guid.NewGuid().ToString(), Content = designer, ToolTip = designer.ToolTip };
        viewDesigners.Add(designer, document);
        designer.TitleChanged += () => { document.Title = designer.Title; RefreshCommands(); };
        designer.Saved += () => _ = RefreshSavedViewAsync(designer);
        document.Closing += (_, e) => { if (acceptedViewDesignerCloses.Remove(designer)) return; e.Cancel = true; _ = CloseViewDesignerAsync(designer); };
        document.Closed += (_, _) => { viewDesigners.Remove(designer); designer.Shutdown(); RefreshCommands(); };
        AddDocument(document);
        document.IsActive = true;
        RefreshCommands();
        return designer;
    }

    private async Task RefreshSavedViewAsync(ViewDesignerView view)
    {
        static IEnumerable<CatalogNode> Walk(CatalogNode node) => node.Children.SelectMany(Walk).Prepend(node);
        foreach (var folder in ExplorerTree.Items.OfType<CatalogNode>().SelectMany(Walk).Where(n => n.Entry.Kind == CatalogKind.Views && n.Entry.Schema == view.Schema && n.Entry.Database == view.Database).ToList())
            await folder.LoadAsync(catalogCancellation.Token, refresh: true);
    }

    internal async Task<bool> CloseViewDesignerAsync(ViewDesignerView view)
    {
        if (!viewDesigners.TryGetValue(view, out var document) || !await ConfirmViewDesignerCloseAsync(view)) return false;
        acceptedViewDesignerCloses.Add(view);
        document.Close();
        return true;
    }

    private async Task<bool> ConfirmViewDesignerCloseAsync(ViewDesignerView view)
    {
        if (view.IsBusy) { MessageBox.Show(this, "Cancel the running operation before closing this view.", "PGMS"); return false; }
        await view.Designer.LeaveSqlFirstAsync();
        if (!view.IsDirty) return true;
        var answer = ViewDesignerClosePrompt?.Invoke(view) ?? MessageBox.Show(this, "Save changes to the following items?\n\n" + view.Title.TrimEnd('*'), "PGMS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && await view.SaveAsync() && !view.IsDirty);
    }

    private void DesignViewFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || (ExplorerTree.SelectedItem as CatalogNode)?.Entry is not { Schema: { } schema } entry) { Status.Text = "Connect before designing views."; return; }
        OpenViewDesigner(connection, schema, entry.Name);
    }

    private void NewViewFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before creating views."; return; }
        OpenViewDesigner(connection, (ExplorerTree.SelectedItem as CatalogNode)?.Entry.Schema ?? "public", null);
    }
}
