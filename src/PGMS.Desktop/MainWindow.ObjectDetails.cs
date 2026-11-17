using System.Windows;
using AvalonDock.Layout;
using PGMS.Desktop.ViewModels;

namespace PGMS.Desktop;

/// <summary>Object Explorer Details (F7; PGMS-052, M1085): one document tab that follows the Object Explorer selection, as in SSMS.</summary>
public partial class MainWindow
{
    private ObjectExplorerDetailsView? objectDetails;
    private LayoutDocument? objectDetailsDocument;
    internal ObjectExplorerDetailsView? ObjectDetails => objectDetails;

    private void ConfigureObjectDetails()
    {
        ExplorerTree.SelectedItemChanged += (_, _) => { if (objectDetails is { } details) _ = details.ShowAsync(ExplorerTree.SelectedItem as CatalogNode); };
    }

    private void ShowObjectExplorerDetails(object sender, RoutedEventArgs e) => ShowObjectExplorerDetails();

    /// <summary>Opens (or activates) the Object Explorer Details tab on the node selected in Object Explorer.</summary>
    internal ObjectExplorerDetailsView ShowObjectExplorerDetails()
    {
        if (objectDetails is null || objectDetailsDocument is null)
        {
            var view = new ObjectExplorerDetailsView { SelectInExplorer = node => node.IsSelected = true };
            var document = new LayoutDocument { Title = view.Title, ContentId = "objectdetails-" + Guid.NewGuid().ToString("N"), Content = view };
            document.Closed += (_, _) => { objectDetails = null; objectDetailsDocument = null; RefreshCommands(); };
            objectDetails = view; objectDetailsDocument = document;
            AddDocument(document);
        }
        objectDetailsDocument.IsActive = true;
        _ = objectDetails.ShowAsync(ExplorerTree.SelectedItem as CatalogNode);
        RefreshCommands();
        return objectDetails;
    }
}
