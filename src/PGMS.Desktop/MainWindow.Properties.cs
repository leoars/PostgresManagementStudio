using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;

namespace PGMS.Desktop;

/// <summary>
/// View ▸ Properties Window (F4, PGMS-070, M1093): one docked tool window, on the right as in SSMS 22.11, listing the properties of the active
/// document. Observed in SSMS 22.11: with a query window it lists Aggregate Status, Connection and Connection Details, categorized or alphabetical,
/// with a description box; selecting Object Explorer nodes did not change it. PGMS does the same: it follows the last active document tab and is
/// empty for documents without properties.
/// </summary>
public partial class MainWindow
{
    private const string PropertiesWindowId = "properties";
    private DockPanel? propertiesContent;
    private DesignerPropertyGrid? propertiesGrid;
    private object? propertiesSource;

    internal DesignerPropertyGrid PropertiesGrid { get { _ = PropertiesContent; return propertiesGrid!; } }
    /// <summary>The document whose properties are shown (a QueryView), or null.</summary>
    internal object? PropertiesSource => propertiesSource;

    internal DockPanel PropertiesContent
    {
        get
        {
            if (propertiesContent is not null) return propertiesContent;
            propertiesGrid = new DesignerPropertyGrid { IsReadOnly = true, ShowDescription = true, Margin = new Thickness(2) };
            AutomationProperties.SetName(propertiesGrid, "Properties");
            propertiesContent = new DockPanel { Children = { propertiesGrid } };
            propertiesContent.SetResourceReference(Panel.BackgroundProperty, "Pgms.ContentBackground");
            return propertiesContent;
        }
    }

    private void ConfigurePropertiesWindow()
    {
        DockManager.ActiveContentChanged += (_, _) => { RefreshPropertiesWindow(); UpdateConnectionTexts(); };
    }

    private void ShowPropertiesWindow(object sender, RoutedEventArgs e) => ShowPropertiesWindow();

    /// <summary>Docks the Properties window on the right (once) and activates it, keeping the document it describes.</summary>
    internal void ShowPropertiesWindow()
    {
        var anchorable = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == PropertiesWindowId);
        if (anchorable is null)
        {
            anchorable = new LayoutAnchorable { Title = "Properties", ContentId = PropertiesWindowId, Content = PropertiesContent, CanClose = false, CanHide = true, FloatingWidth = 320 };
            anchorable.AddToLayout(DockManager, AnchorableShowStrategy.Right);
        }
        else if (anchorable.Content is null) anchorable.Content = PropertiesContent;
        RefreshPropertiesWindow(force: true);
        anchorable.Show(); anchorable.IsActive = true;
        SizeNewPane(anchorable);
        FocusToolContent(PropertiesContent as FrameworkElement);
    }

    /// <summary>F4 opens the Properties window, except in a combo box, where F4 opens the list as in Windows.</summary>
    private bool HandlePropertiesShortcut(KeyEventArgs e)
    {
        if (e.Key != Key.F4 || Keyboard.Modifiers != ModifierKeys.None) return false;
        for (var d = e.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ComboBox) return false;
        ShowPropertiesWindow();
        e.Handled = true;
        return true;
    }

    /// <summary>Shows the last active document's properties. A tool window becoming active (Object Explorer, Properties itself) keeps them.</summary>
    internal void RefreshPropertiesWindow(bool force = false)
    {
        if (propertiesGrid is null) return;
        object? source = DockManager.Layout?.ActiveContent is LayoutDocument document ? document.Content : propertiesSource;
        if (source is QueryView closed && !documents.ContainsKey(closed)) source = null;
        if (!force && ReferenceEquals(source, propertiesSource)) { propertiesGrid.RefreshValues(); return; }
        if (propertiesSource is QueryView previous) { previous.StateChanged -= PropertiesSourceChanged; previous.ExecutionNoted -= PropertiesSourceChanged; }
        propertiesSource = source;
        if (source is QueryView query) { query.StateChanged += PropertiesSourceChanged; query.ExecutionNoted += PropertiesSourceChanged; propertiesGrid.Show(query.PropertyItems()); }
        else propertiesGrid.Show([]);
    }

    private void PropertiesSourceChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, propertiesSource)) propertiesGrid?.RefreshValues();
    }
}
