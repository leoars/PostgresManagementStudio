using System.Data;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>Text column of the SSMS-look results grids (M1132). It does the work the cell style used to do with bindings on every cell: the NULL
/// background and the dotted rectangle of the current cell are set once, when the cell is generated, and the text has no automation peer of its own
/// (the cell already exposes the same text as its name and value, so screen readers read it once).</summary>
public sealed class ResultTextColumn : DataGridTextColumn
{
    /// <summary>Column of the <see cref="DataRowView"/> whose NULL turns the cell yellow; null for none.</summary>
    public string? NullColumn { get; init; }

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var text = new PlainText { Style = ElementStyle };
        foreach (var property in SyncedProperties)
            if (ReadLocalValue(property) != DependencyProperty.UnsetValue) text.SetValue(property, GetValue(property));
        if (Binding is not null) BindingOperations.SetBinding(text, TextBlock.TextProperty, Binding);
        Mark(cell, dataItem);
        // The grid reuses rows for other items while scrolling: the cell then keeps this text and only its data context changes.
        text.DataContextChanged += (_, e) => Mark(cell, e.NewValue);
        return text;
    }

    private void Mark(DataGridCell cell, object? item)
    {
        if (NullColumn is not null && item is DataRowView row && row.Row.Table.Columns.Contains(NullColumn) && row.Row[NullColumn] is DBNull) cell.Background = ResultGridStyle.NullBackground;
        else cell.ClearValue(Control.BackgroundProperty);
        ResultCellChrome.SetIsCurrentCell(cell, DataGridOwner is { } grid && ReferenceEquals(grid.CurrentColumn, this) && item is not null && ReferenceEquals(grid.CurrentItem, item));
    }

    private static readonly DependencyProperty[] SyncedProperties = [FontFamilyProperty, FontSizeProperty, FontStyleProperty, FontWeightProperty, ForegroundProperty];
}

/// <summary>Background, grid lines and the dotted current-cell rectangle of a results cell (and the background and lines of a row header), drawn by one element instead of a panel, a border and a
/// rectangle per cell (M1132). The drawing is the one of the previous template: lines on the right and bottom edges and the rectangle one pixel inside them.</summary>
public sealed class ResultCellChrome : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(ResultCellChrome),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(nameof(BorderBrush), typeof(Brush), typeof(ResultCellChrome),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CurrentBrushProperty = DependencyProperty.Register(nameof(CurrentBrush), typeof(Brush), typeof(ResultCellChrome),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.Register(nameof(IsCurrent), typeof(bool), typeof(ResultCellChrome),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public Brush? CurrentBrush { get => (Brush?)GetValue(CurrentBrushProperty); set => SetValue(CurrentBrushProperty, value); }
    public bool IsCurrent { get => (bool)GetValue(IsCurrentProperty); set => SetValue(IsCurrentProperty, value); }

    /// <summary>On a <see cref="DataGridCell"/>: the cell at the grid's current item and column. SSMS outlines it with a dotted rectangle, focused or not.</summary>
    public static readonly DependencyProperty IsCurrentCellProperty = DependencyProperty.RegisterAttached("IsCurrentCell", typeof(bool), typeof(ResultCellChrome), new PropertyMetadata(false));
    public static bool GetIsCurrentCell(DependencyObject element) => (bool)element.GetValue(IsCurrentCellProperty);
    public static void SetIsCurrentCell(DependencyObject element, bool value) { if (GetIsCurrentCell(element) != value) element.SetValue(IsCurrentCellProperty, value); }

    private static readonly Dictionary<Brush, Pen> currentPens = [];

    private static readonly Dictionary<(Brush, double), Pen> linePens = [];

    private static Pen LinePen(Brush brush, double thickness)
    {
        if (linePens.TryGetValue((brush, thickness), out var pen)) return pen;
        pen = new Pen(brush, thickness);
        if (brush.IsFrozen) { pen.Freeze(); linePens[(brush, thickness)] = pen; }
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        if (Background is { } background) dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
        if (BorderBrush is { } border)
        {
            // As Border draws BorderThickness 0,0,1,1: one line per edge, centred inside it, the width rounded to device pixels by layout rounding.
            var dpi = VisualTreeHelper.GetDpi(this);
            double x = UseLayoutRounding ? Math.Round(dpi.DpiScaleX) / dpi.DpiScaleX : 1, y = UseLayoutRounding ? Math.Round(dpi.DpiScaleY) / dpi.DpiScaleY : 1;
            if (x > 0) dc.DrawLine(LinePen(border, x), new Point(width - x / 2, 0), new Point(width - x / 2, height));
            if (y > 0) dc.DrawLine(LinePen(border, y), new Point(0, height - y / 2), new Point(width, height - y / 2));
        }
        if (IsCurrent && CurrentBrush is { } current && width > 2 && height > 2)
        {
            if (!currentPens.TryGetValue(current, out var pen))
            {
                pen = new Pen(current, 1) { DashStyle = new DashStyle([1, 1], 0), DashCap = PenLineCap.Flat };
                if (current.IsFrozen) { pen.Freeze(); currentPens[current] = pen; }
            }
            // As the Rectangle of the previous template (margin 0,0,1,1, snapped to device pixels at its own edges) drew it.
            dc.PushGuidelineSet(new GuidelineSet([0, width - 1], [0, height - 1]));
            dc.DrawRoundedRectangle(null, pen, new Rect(0.5, 0.5, width - 2, height - 2), 0, 0);
            dc.Pop();
        }
    }
}

/// <summary>Text of a results grid element that already exposes the same text to UI Automation (a cell or a row header): no peer of its own (M1132).</summary>
public sealed class PlainText : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
