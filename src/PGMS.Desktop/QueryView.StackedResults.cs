using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>Query Results ▸ Results to Grid ▸ "Show multiple result sets stacked in one tab" (PGMS-082, M1122): result grids one under the other,
/// as SSMS 22.11 shows them (M1110 lacuna), instead of the Result 1, Result 2… tabs. The tab items stay, so the selected result keeps working.</summary>
public partial class QueryView
{
    private ControlTemplate? tabbedResultsTemplate;
    private Style? tabbedResultItemStyle;
    private static ControlTemplate? stackedResultsTemplate;
    private static Style? stackedResultItemStyle;

    /// <summary>True when the result grids of the last grid execution are stacked.</summary>
    public bool AreResultSetsStacked { get; private set; }

    /// <summary>Called when a grid execution starts, with no result set shown, so no grid changes its parent.</summary>
    private void ApplyResultSetLayout()
    {
        bool stacked = QueryOptions.GridStackResults;
        if (stacked == AreResultSetsStacked) return;
        tabbedResultsTemplate ??= ResultSets.Template;
        tabbedResultItemStyle ??= ResultSets.ItemContainerStyle;
        tabbedItemsPanel ??= ResultSets.ItemsPanel;
        ResultSets.ItemsPanel = stacked ? StackedItemsPanel : tabbedItemsPanel;
        if (stacked)
        {
            ResultSets.Template = stackedResultsTemplate ??= CreateStackedTemplate();
            ResultSets.ItemContainerStyle = stackedResultItemStyle ??= CreateStackedItemStyle();
            ResultSets.PreviewGotKeyboardFocus += SelectStackedResult;
            ResultSets.PreviewMouseDown += SelectStackedResultByMouse;
        }
        else
        {
            ResultSets.Template = tabbedResultsTemplate;
            ResultSets.ItemContainerStyle = tabbedResultItemStyle;
            ResultSets.PreviewGotKeyboardFocus -= SelectStackedResult;
            ResultSets.PreviewMouseDown -= SelectStackedResultByMouse;
        }
        AreResultSetsStacked = stacked;
    }

    private static ControlTemplate CreateStackedTemplate()
    {
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, false);
        scroll.SetValue(UIElement.FocusableProperty, false);
        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        scroll.AppendChild(presenter);
        var template = new ControlTemplate(typeof(TabControl)) { VisualTree = scroll };
        template.Seal();
        return template;
    }

    private static Style CreateStackedItemStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 4));
        border.SetResourceReference(Border.BorderBrushProperty, SystemColors.ControlBrushKey);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        border.AppendChild(content);
        var style = new Style(typeof(TabItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(TabItem)) { VisualTree = border }));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        style.Seal();
        return style;
    }

    private void SelectStackedResult(object sender, KeyboardFocusChangedEventArgs e) => SelectResultContaining(e.NewFocus as DependencyObject);
    private void SelectStackedResultByMouse(object sender, MouseButtonEventArgs e) => SelectResultContaining(e.OriginalSource as DependencyObject);

    private void SelectResultContaining(DependencyObject? element)
    {
        for (var node = element; node is not null && node != ResultSets; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is TabItem tab && ResultSets.Items.Contains(tab)) { if (!ReferenceEquals(ResultSets.SelectedItem, tab)) ResultSets.SelectedItem = tab; return; }
    }

    private static ItemsPanelTemplate? stackedItemsPanel;
    private ItemsPanelTemplate? tabbedItemsPanel;
    private static ItemsPanelTemplate StackedItemsPanel
    {
        get
        {
            if (stackedItemsPanel is null) { stackedItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackedResultsPanel))); stackedItemsPanel.Seal(); }
            return stackedItemsPanel;
        }
    }
}

/// <summary>Result grids one under the other: each gets an equal share of the visible height, never less than <see cref="MinimumRowHeight"/>;
/// past that the results area scrolls. Every grid has a finite height, so its rows stay virtualized.</summary>
public sealed class StackedResultsPanel : Panel
{
    public const double MinimumRowHeight = 160;
    private ScrollViewer? viewer;

    public StackedResultsPanel()
    {
        Loaded += (_, _) => { viewer = FindViewport(); if (viewer is not null) viewer.SizeChanged += ViewportChanged; InvalidateMeasure(); };
        Unloaded += (_, _) => { if (viewer is not null) viewer.SizeChanged -= ViewportChanged; viewer = null; };
    }

    private void ViewportChanged(object sender, SizeChangedEventArgs e) { if (e.HeightChanged) InvalidateMeasure(); }

    private double RowHeight(Size available)
    {
        double viewport = double.IsInfinity(available.Height) ? (FindViewport()?.ViewportHeight ?? 0) : available.Height;
        if (viewport <= 0 || double.IsNaN(viewport)) viewport = MinimumRowHeight * 2;
        int count = Math.Max(1, InternalChildren.Count);
        return Math.Max(MinimumRowHeight, Math.Floor(viewport / count));
    }

    private ScrollViewer? FindViewport()
    {
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer viewer) return viewer;
        return null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double row = RowHeight(availableSize);
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        foreach (UIElement child in InternalChildren) { child.Measure(new Size(availableSize.Width, row)); width = Math.Max(width, double.IsInfinity(availableSize.Width) ? child.DesiredSize.Width : 0); }
        return new Size(width, row * InternalChildren.Count);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double viewport = FindViewport()?.ViewportHeight ?? finalSize.Height;
        double row = RowHeight(new Size(finalSize.Width, viewport > 0 ? viewport : finalSize.Height));
        double y = 0;
        foreach (UIElement child in InternalChildren) { child.Arrange(new Rect(0, y, finalSize.Width, row)); y += row; }
        return new Size(finalSize.Width, y);
    }
}
